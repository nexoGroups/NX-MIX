using System.Numerics;
using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Engine;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

internal sealed class EqSpectrumGraph : Widget
{
    const float SpectrumTop = 12;
    static readonly int[] s_levelTicks = { 12, 0, -20, -40, -60, -80, -100 };
    static readonly int[] s_frequencyTicks = { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };

    readonly float[] _eq = new float[512];
    readonly float[] _before = new float[SpectrumAnalyzer.Points], _after = new float[SpectrumAnalyzer.Points];
    readonly float[] _levels = new float[SpectrumAnalyzer.Points];
    IReadOnlyList<FilterSpec>? _filters;
    OutputCorrection? _correction;
    int _curveRate;
    float _eqBottom = -24, _eqTop = 24;

    MixerEngine? _engine;
    SpectrumCapture? _capture;
    SpectrumAnalyzer? _analyzer;
    float[] _beforeSamples = Array.Empty<float>(), _afterSamples = Array.Empty<float>();
    int _channel, _analyzerRate;
    bool _input, _ready;
    float _elapsed;
    long _frame;

    public static Color4 EqColor => Theme.AccentText;
    public static Color4 BeforeColor => Theme.Orange;
    public static Color4 AfterColor => Theme.Teal;
    public bool ShowEq { get; set; } = true;
    public bool ShowBefore { get; set; } = true;
    public bool ShowAfter { get; set; } = true;

    public void Stop()
    {
        if (_capture != null) _engine!.StopSpectrum(_capture);
        _capture = null;
        _engine = null;
        _frame = 0;
        _ready = false;
        _elapsed = 0;
    }

    public bool Update(ChannelDsp channel, int rate, bool input, int index, MixerEngine? engine, float dt)
    {
        bool changed = RefreshCurve(channel, rate, input);
        if (!ShowBefore && !ShowAfter) engine = null;
        if (engine != _engine || (engine != null && (input != _input || index != _channel)))
        {
            Stop();
            if (engine != null)
            {
                _engine = engine;
                _input = input;
                _channel = index;
                _capture = engine.StartSpectrum(input, index);
                if (_analyzer == null || _analyzerRate != _capture.SampleRate)
                {
                    _analyzerRate = _capture.SampleRate;
                    _analyzer = new SpectrumAnalyzer(_capture.WindowSize, _analyzerRate);
                    _beforeSamples = new float[_capture.WindowSize];
                    _afterSamples = new float[_capture.WindowSize];
                }
            }
            changed = true;
        }

        if (_capture == null) return changed;
        _elapsed += dt;
        if (_elapsed < 0.045f || !_capture.TryRead(_beforeSamples, _afterSamples, ref _frame)) return changed;
        Analyze(_beforeSamples, _before);
        Analyze(_afterSamples, _after);
        _ready = true;
        _elapsed = 0;
        return true;
    }

    void Analyze(float[] samples, float[] trace)
    {
        _analyzer!.Analyze(samples, _levels);
        for (int i = 0; i < trace.Length; i++)
            trace[i] = _ready ? Math.Max(_levels[i], trace[i] - 90 * _elapsed) : _levels[i];
    }

    bool RefreshCurve(ChannelDsp channel, int rate, bool input)
    {
        if (rate == _curveRate && ReferenceEquals(_filters, channel.Filters) && ReferenceEquals(_correction, channel.Correction)) return false;
        _curveRate = rate;
        _filters = channel.Filters;
        _correction = channel.Correction;
        var filters = FilterDesign.DesignChannel(channel, rate, input);
        float min = -24, max = 24;
        for (int i = 0; i < _eq.Length; i++)
        {
            double f = SpectrumAnalyzer.Frequency((double)i / (_eq.Length - 1), rate);
            var response = Complex.One;
            foreach (var filter in filters) response *= filter.Response(f, rate);
            double db = 20 * Math.Log10(response.Magnitude);
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
        g.FillRound(b, 12, Theme.Well);
        g.StrokeRound(b, 12, Theme.Border);
        var plot = b.Deflate(47, 30, 45, 33);
        if (plot.IsEmpty) return;

        g.Text("SPEKTRUM · dBFS", new RectF(b.X + 10, b.Y + 6, 122, 18), Theme.MicroStrong, Theme.TextSecondary);
        g.Text("EQ · dB", new RectF(b.Right - 62, b.Y + 6, 52, 18), Theme.MicroStrong, EqColor, HAlign.Right);
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
            string status = _capture == null ? "Live-Spektrum bei laufendem Audio" : "Live-Spektrum startet …";
            g.Text(status, new RectF(plot.X, plot.Bottom - 24, plot.W, 22), Theme.Caption, Theme.TextSecondary, HAlign.Center);
        }
    }

    static float Y(RectF plot, float db, float bottom, float top) =>
        plot.Bottom - plot.H * Math.Clamp((db - bottom) / (top - bottom), 0, 1);

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
