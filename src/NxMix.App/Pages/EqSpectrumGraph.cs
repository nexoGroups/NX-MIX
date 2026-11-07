using System.Numerics;
using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Engine;
using NxMix.Ipc;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Live-Spektrum (Pre-DSP orange gestrichelt, Post-DSP türkis) + exakte mathematische Summen-Filterkurve (inkl. REW &amp; Preamp).</summary>
internal sealed class EqSpectrumGraph : Widget
{
    const float SpectrumTop = 12;
    static readonly int[] s_levelTicks = { 12, 0, -20, -40, -60, -80, -100 };
    static readonly int[] s_frequencyTicks = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };

    readonly float[] _eq = new float[512];
    readonly float[] _before = new float[SpectrumAnalyzer.Points];
    readonly float[] _after = new float[SpectrumAnalyzer.Points];

    IReadOnlyList<FilterSpec>? _filters;
    OutputCorrection? _correction;
    int _curveRate = 48000;
    float _eqBottom = -24, _eqTop = 24;
    long _lastSeq = -1;
    bool _ready;

    public static Color4 EqColor => Theme.AccentText;
    public static Color4 BeforeColor => Theme.Orange;
    public static Color4 AfterColor => Theme.Teal;
    public bool ShowEq { get; set; } = true;
    public bool ShowBefore { get; set; } = true;
    public bool ShowAfter { get; set; } = true;

    public EqSpectrumGraph()
    {
        Array.Fill(_before, SpectrumAnalyzer.FloorDb);
        Array.Fill(_after, SpectrumAnalyzer.FloorDb);
    }

    public void Stop(AppModel? model = null)
    {
        model?.SubscribeSpectrum(0, -1);
        _ready = false;
        _lastSeq = -1;
    }

    public bool Update(ChannelDsp channel, int rate, bool input, int index, AppModel model, float dt)
    {
        bool changed = RefreshCurve(channel, rate, input);
        if (!ShowBefore && !ShowAfter)
        {
            model.SubscribeSpectrum(0, -1);
            return changed;
        }

        int mode = input ? 1 : 2;
        model.SubscribeSpectrum(mode, index);

        if (!model.HostRunning)
        {
            if (_ready) { _ready = false; return true; }
            return changed;
        }

        if (model.TelemetryFrameSeq != _lastSeq &&
            model.Telemetry.SpectrumMode == mode &&
            model.Telemetry.SpectrumChannel == index)
        {
            _lastSeq = model.TelemetryFrameSeq;
            var pre = model.SpectrumPreDb;
            var post = model.SpectrumPostDb;
            bool hasSignal = false;
            for (int i = 0; i < _before.Length && i < pre.Length; i++)
            {
                if (pre[i] > SpectrumAnalyzer.FloorDb + 0.5f || post[i] > SpectrumAnalyzer.FloorDb + 0.5f)
                    hasSignal = true;
                _before[i] = _ready ? Math.Max(pre[i], _before[i] - 85f * dt) : pre[i];
                _after[i] = _ready ? Math.Max(post[i], _after[i] - 85f * dt) : post[i];
            }
            if (hasSignal || _ready)
            {
                _ready = true;
                changed = true;
            }
        }
        return changed;
    }

    public bool RefreshCurve(ChannelDsp channel, int rate, bool input)
    {
        int sr = rate > 0 ? rate : 48000;
        if (sr == _curveRate && ReferenceEquals(_filters, channel.Filters) && Equals(_correction, channel.Correction))
            return false;

        _curveRate = sr;
        _filters = channel.Filters;
        _correction = channel.Correction;

        var sections = FilterDesign.DesignChannel(channel, sr, input);
        double preampDb = !input && channel.Correction is { Bypassed: false } corr && double.IsFinite(corr.PreampDb)
            ? corr.PreampDb : 0.0;

        float min = -24, max = 24;
        for (int i = 0; i < _eq.Length; i++)
        {
            double f = SpectrumAnalyzer.Frequency((double)i / (_eq.Length - 1), sr);
            var response = Complex.One;
            foreach (var sec in sections) response *= sec.Response(f, sr);
            double db = 20.0 * Math.Log10(Math.Max(1e-12, response.Magnitude)) + preampDb;
            _eq[i] = double.IsNaN(db) ? 0 : (float)Math.Clamp(db, -120, 120);
            min = Math.Min(min, _eq[i]);
            max = Math.Max(max, _eq[i]);
        }
        _eqBottom = 12 * MathF.Floor(min / 12);
        _eqTop = 12 * MathF.Ceiling(max / 12);
        return true;
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        g.FillRound(b, Theme.RadiusInner, Theme.Well);
        g.StrokeRound(b, Theme.RadiusInner, Theme.Border);
        var plot = b.Deflate(47, 30, 45, 33);
        if (plot.IsEmpty) return;

        g.Text("SPEKTRUM · dBFS", new RectF(b.X + 10, b.Y + 6, 132, 18), Theme.MicroStrong, Theme.TextSecondary);
        g.Text("SUMMEN-DSP · dB", new RectF(b.Right - 115, b.Y + 6, 105, 18), Theme.MicroStrong, EqColor, HAlign.Right);
        foreach (int db in s_levelTicks)
        {
            float y = Y(plot, db, SpectrumAnalyzer.FloorDb, SpectrumTop);
            g.Line(plot.X, y, plot.Right, y, db == 0 ? Theme.BorderStrong : Theme.Separator);
            g.Text(db.ToString(), new RectF(b.X + 4, y - 8, 37, 16), Theme.Micro, Theme.TextSecondary, HAlign.Right);
        }
        foreach (float db in (ReadOnlySpan<float>)[_eqTop, 0, _eqBottom])
        {
            float y = Y(plot, db, _eqBottom, _eqTop);
            g.Text(db > 0 ? $"+{db:0}" : $"{db:0}", new RectF(plot.Right + 6, y - 8, 35, 16), Theme.Micro, EqColor);
        }

        double maxHz = SpectrumAnalyzer.MaxHz(_curveRate);
        double span = Math.Log(maxHz / SpectrumAnalyzer.MinHz);
        foreach (int hz in s_frequencyTicks)
        {
            if (hz > maxHz) break;
            float x = plot.X + plot.W * (float)(Math.Log(hz / SpectrumAnalyzer.MinHz) / span);
            g.Line(x, plot.Y, x, plot.Bottom, Theme.Separator);
            string label = hz >= 1000 ? $"{hz / 1000}k" : hz.ToString();
            g.Text(label, new RectF(x - 16, plot.Bottom + 4, 32, 17), Theme.Micro, Theme.TextSecondary, HAlign.Center);
        }
        g.Text("Hz", new RectF(plot.Right + 16, plot.Bottom + 4, 23, 17), Theme.Micro, Theme.TextSecondary);

        if (_ready && ShowAfter) Trace(g, plot, _after, SpectrumAnalyzer.FloorDb, SpectrumTop, AfterColor, 1.6f);
        if (_ready && ShowBefore) Trace(g, plot, _before, SpectrumAnalyzer.FloorDb, SpectrumTop, BeforeColor, 1.4f, dashed: true);
        if (ShowEq) Trace(g, plot, _eq, _eqBottom, _eqTop, EqColor, 2.2f);
        if (!_ready && (ShowBefore || ShowAfter))
        {
            g.Text("Live-Spektrum aktiv sobald Audio läuft", new RectF(plot.X, plot.Bottom - 24, plot.W, 22),
                Theme.Caption, Theme.TextSecondary, HAlign.Center);
        }
    }

    static float Y(RectF plot, float db, float bottom, float top) =>
        plot.Bottom - plot.H * Math.Clamp((db - bottom) / Math.Max(1f, top - bottom), 0, 1);

    static void Trace(Renderer g, RectF plot, float[] values, float bottom, float top, Color4 color, float width, bool dashed = false)
    {
        float step = plot.W / (values.Length - 1);
        float y = Y(plot, values[0], bottom, top);
        for (int i = 1; i < values.Length; i++)
        {
            float next = Y(plot, values[i], bottom, top);
            if (!dashed || i % 4 < 3) g.Line(plot.X + (i - 1) * step, y, plot.X + i * step, next, color, width);
            y = next;
        }
    }
}
