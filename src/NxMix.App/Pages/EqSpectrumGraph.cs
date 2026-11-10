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
    SpectrumPrefs _options = new();
    Guid _channelId;
    int _channelIndex = -1;

    public void SetOptions(SpectrumPrefs options)
    {
        if (_options.WindowSize != options.WindowSize || _options.ShowPre != options.ShowPre || _options.ShowPost != options.ShowPost)
            ResetTrace();
        _options = options;
    }
    static readonly int[] s_levelTicks = { 12, 0, -20, -40, -60, -80, -100 };
    static readonly int[] s_frequencyTicks = { 20, 50, 100, 200, 250, 500, 1000, 2000, 4000, 5000, 10000, 20000 };

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
    bool ShowEq => _options.ShowEq;
    bool ShowBefore => _options.ShowPre;
    bool ShowAfter => _options.ShowPost;

    public EqSpectrumGraph()
    {
        Array.Fill(_before, SpectrumAnalyzer.FloorDb);
        Array.Fill(_after, SpectrumAnalyzer.FloorDb);
    }

    public void Stop(AppModel? model = null)
    {
        model?.SubscribeSpectrum(0, -1);
        ResetTrace();
    }

    void ResetTrace()
    {
        _ready = false;
        _lastSeq = -1;
    }

    public bool Update(ChannelDsp channel, int rate, bool input, int index, AppModel model, float dt)
    {
        bool selectionChanged = channel.Id != _channelId || index != _channelIndex;
        if (selectionChanged || (rate > 0 && rate != _curveRate))
        {
            _channelId = channel.Id;
            _channelIndex = index;
            ResetTrace();
        }
        bool changed = RefreshCurve(channel, rate, input) || selectionChanged;
        if (!ShowBefore && !ShowAfter)
        {
            model.SubscribeSpectrum(0, -1);
            return changed;
        }

        int mode = input ? 1 : 2;
        model.SubscribeSpectrum(mode, index, _options.WindowSize);

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

    public IReadOnlyList<FilterSpec> Bands { get; set; } = Array.Empty<FilterSpec>();
    public int SelectedBand { get; set; }
    public Action<int>? SelectBand { get; set; }
    public Action<int, double, double>? MoveBand { get; set; }
    int _dragBand = -1;
    float _dragBottom, _dragTop, _grabX, _grabY;

    RectF Plot => Bounds.Deflate(47, 30, 45, 33);
    float EqBottom => _dragBand >= 0 ? _dragBottom : _eqBottom;
    float EqTop => _dragBand >= 0 ? _dragTop : _eqTop;

    (double DataMax, double Low, double High, double Span) FrequencyRange()
    {
        double dataMaxHz = SpectrumAnalyzer.MaxHz(_curveRate);
        (double lowHz, double highHz) = _options.Range switch
        {
            1 => (20, 250), 2 => (100, 4000), 3 => (2000, 20000), _ => (20, 20000),
        };
        if (_options.CustomFrequency is { Min: >= 20, Max: <= 20000 } frequency && frequency.Max > frequency.Min)
            (lowHz, highHz) = (frequency.Min, frequency.Max);
        highHz = Math.Min(highHz, dataMaxHz);
        lowHz = lowHz < highHz ? lowHz : SpectrumAnalyzer.MinHz;
        double span = _options.Linear ? highHz - lowHz : Math.Log(highHz / lowHz);
        return (dataMaxHz, lowHz, highHz, span);
    }

    static bool HasGain(FilterSpec band) => band.Type is FilterType.Peak or FilterType.LowShelf or FilterType.HighShelf;

    (float X, float Y) BandPoint(FilterSpec band)
    {
        var plot = Plot;
        var (_, low, high, span) = FrequencyRange();
        double hz = Math.Clamp(band.FrequencyHz, low, high);
        double offset = _options.Linear ? hz - low : Math.Log(hz / low);
        return (plot.X + plot.W * (float)(offset / span),
            Y(plot, HasGain(band) ? (float)band.GainDb : 0f, EqBottom, EqTop));
    }

    bool BandIsVisible(FilterSpec band)
    {
        var (_, low, high, _) = FrequencyRange();
        return band.FrequencyHz >= low - 0.5 && band.FrequencyHz <= high + 0.5;
    }

    int HitBand(float x, float y)
    {
        if (!ShowEq || MoveBand == null || Plot.IsEmpty) return -1;
        int best = -1;
        float distance = 13 * 13;
        for (int i = 0; i < Bands.Count; i++)
        {
            if (!BandIsVisible(Bands[i])) continue;
            var p = BandPoint(Bands[i]);
            float dx = x - p.X, dy = y - p.Y;
            float squared = dx * dx + dy * dy;
            if (squared < distance || (squared == distance && i == SelectedBand))
            {
                distance = squared;
                best = i;
            }
        }
        return best;
    }

    public override bool AcceptsMouse => MoveBand != null;
    public override CursorKind CursorAt(float x, float y) =>
        _dragBand >= 0 || HitBand(x, y) >= 0 ? CursorKind.Hand : CursorKind.Arrow;

    public override string? TooltipAt(float x, float y)
    {
        int index = HitBand(x, y);
        if (index < 0) return null;
        var band = Bands[index];
        string gain = HasGain(band) ? $" · {Db.Format(band.GainDb)} dB" : "";
        return $"PEQ {index + 1} · {band.FrequencyHz:0} Hz{gain} · Q {band.Q:0.##}";
    }

    public void EndBandDrag() => _dragBand = -1;

    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button != 0) return;
        int index = HitBand(e.X, e.Y);
        if (index < 0) return;
        var p = BandPoint(Bands[index]);
        _grabX = e.X - p.X;
        _grabY = e.Y - p.Y;
        _dragBottom = _eqBottom;
        _dragTop = _eqTop;
        _dragBand = index;
        SelectBand?.Invoke(index);
        Invalidate();
    }

    public override void OnMouseMove(in MouseEvent e)
    {
        if (_dragBand < 0 || _dragBand >= Bands.Count || Plot.IsEmpty) return;
        var plot = Plot;
        var (_, low, _, span) = FrequencyRange();
        double x = Math.Clamp((e.X - _grabX - plot.X) / plot.W, 0, 1);
        double y = Math.Clamp((plot.Bottom - e.Y + _grabY) / plot.H, 0, 1);
        double hz = _options.Linear ? low + x * span : low * Math.Exp(x * span);
        double gain = Math.Clamp(EqBottom + y * (EqTop - EqBottom), -24, 18);
        MoveBand?.Invoke(_dragBand, Math.Round(hz), Math.Round(gain, 1));
    }

    public override void OnMouseUp(in MouseEvent e)
    {
        EndBandDrag();
        Invalidate();
    }

    void PaintBands(Renderer g)
    {
        for (int i = 0; i < Bands.Count; i++)
        {
            var band = Bands[i];
            if (!BandIsVisible(band)) continue;
            var p = BandPoint(band);
            var rect = new RectF(p.X - 9, p.Y - 9, 18, 18);
            var color = band.Enabled ? EqColor : Theme.TextSecondary;
            g.FillPill(rect, i == SelectedBand ? color : Theme.Well);
            g.StrokeRound(rect, 9, color, 1.5f);
            g.Text((i + 1).ToString(), rect, Theme.MicroStrong, i == SelectedBand ? Theme.Well : Theme.Text, HAlign.Center);
        }
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        g.FillRound(b, Theme.RadiusInner, Theme.Well);
        g.StrokeRound(b, Theme.RadiusInner, Theme.Border);
        var plot = Plot;
        if (plot.IsEmpty) return;

        g.Text("SPEKTRUM · dBFS", new RectF(b.X + 10, b.Y + 6, 132, 18), Theme.MicroStrong, Theme.TextSecondary);
        g.Text("SUMMEN-DSP · dB", new RectF(b.Right - 115, b.Y + 6, 105, 18), Theme.MicroStrong, EqColor, HAlign.Right);
        (float bottom, float top) = _options.LevelRange switch { 1 => (-80, 0), 2 => (-60, 0), 3 => (-40, 0), _ => (-100, 12) };
        bool customLevels = _options.CustomLevels is { Min: >= -100, Max: <= 12 } customLevel && customLevel.Min < customLevel.Max;
        if (customLevels) (bottom, top) = (_options.CustomLevels!.Min, _options.CustomLevels.Max);
        for (int i = 0; i < s_levelTicks.Length; i++)
        {
            float db = customLevels ? MathF.Round(top - i * (top - bottom) / (s_levelTicks.Length - 1)) : s_levelTicks[i];
            if (db < bottom || db > top) continue;
            float y = Y(plot, db, bottom, top);
            g.Line(plot.X, y, plot.Right, y, db == 0 ? Theme.BorderStrong : Theme.Separator);
            g.Text($"{db:0}", new RectF(b.X + 4, y - 8, 37, 16), Theme.Micro, Theme.TextSecondary, HAlign.Right);
        }
        foreach (float db in (ReadOnlySpan<float>)[EqTop, 0, EqBottom])
        {
            float y = Y(plot, db, EqBottom, EqTop);
            g.Text(db > 0 ? $"+{db:0}" : $"{db:0}", new RectF(plot.Right + 6, y - 8, 35, 16), Theme.Micro, EqColor);
        }

        var (dataMaxHz, lowHz, highHz, span) = FrequencyRange();
        double stepHz = Math.Max(1, Math.Pow(10, Math.Floor(Math.Log10(span / 4))));
        if (_options.Linear) stepHz *= Math.Max(1, Math.Floor(span / (4 * stepHz)));
        int tickCount = _options.Linear ? (int)Math.Ceiling(highHz / stepHz) : s_frequencyTicks.Length;
        float previousLabel = float.NegativeInfinity;
        for (int i = _options.Linear ? (int)Math.Ceiling(lowHz / stepHz) : 0; i < tickCount; i++)
        {
            double hz = _options.Linear ? i * stepHz : s_frequencyTicks[i];
            if (hz < lowHz || hz > highHz) continue;
            double offset = _options.Linear ? hz - lowHz : Math.Log(hz / lowHz);
            float x = plot.X + plot.W * (float)(offset / span);
            g.Line(x, plot.Y, x, plot.Bottom, Theme.Separator);
            if (x - previousLabel < 34 || x > plot.Right - 25) continue;
            previousLabel = x;
            string label = hz >= 1000 ? $"{hz / 1000:0.#}k" : $"{hz:0}";
            g.Text(label, new RectF(x - 16, plot.Bottom + 4, 32, 17), Theme.Micro, Theme.TextSecondary, HAlign.Center);
        }
        g.Text(highHz >= 1000 ? $"{highHz / 1000:0.#}k" : $"{highHz:0}",
            new RectF(plot.Right - 16, plot.Bottom + 4, 32, 17), Theme.Micro, Theme.TextSecondary, HAlign.Center);
        g.Text("Hz", new RectF(plot.Right + 16, plot.Bottom + 4, 23, 17), Theme.Micro, Theme.TextSecondary);

        int displayPoints = _options.EffectiveDisplayPoints;
        if (_ready && ShowAfter) Trace(_after, displayPoints, bottom, top, AfterColor, 1.6f, _options.Bars);
        if (_ready && ShowBefore) Trace(_before, displayPoints, bottom, top, BeforeColor, 1.4f, _options.Bars, dashed: true);
        if (ShowEq)
        {
            Trace(_eq, _eq.Length, EqBottom, EqTop, EqColor, 2.2f, false);
            PaintBands(g);
        }
        if (!_ready && (ShowBefore || ShowAfter))
        {
            g.Text("Live-Spektrum aktiv sobald Audio läuft", new RectF(plot.X, plot.Bottom - 24, plot.W, 22),
                Theme.Caption, Theme.TextSecondary, HAlign.Center);
        }
        void Trace(float[] values, int count, float min, float max, Color4 color, float width, bool bars, bool dashed = false)
        {
            double logDataSpan = Math.Log(dataMaxHz / SpectrumAnalyzer.MinHz);
            int last = values.Length - 1;
            double divisions = bars ? count : count - 1;
            float step = plot.W / (float)divisions;
            float previous = plot.Bottom;
            double leftEdge = DataIndex(0);
            for (int i = 0; i < count; i++)
            {
                double fraction = (i + (bars ? 0.5 : 0)) / divisions;
                float db = LevelAt(DataIndex(fraction));
                if (bars)
                {
                    double rightEdge = DataIndex((i + 1) / divisions);
                    for (int band = (int)Math.Ceiling(leftEdge); band < rightEdge; band++)
                        db = Math.Max(db, values[band]);
                    if (i == count - 1) db = Math.Max(db, LevelAt(rightEdge));
                    leftEdge = rightEdge;
                }
                float y = Y(plot, db, min, max);
                float x = plot.X + plot.W * (float)fraction;
                if (bars)
                    g.Line(x, plot.Bottom, x, y, color.With(dashed ? 0.55f : 0.75f), Math.Clamp(step * 0.7f, 1f, 6f));
                else if (i > 0 && (!dashed || i % 4 < 3))
                    g.Line(x - step, previous, x, y, color, width);
                previous = y;
            }

            double DataIndex(double fraction)
            {
                double offset = span * fraction;
                double hz = _options.Linear ? lowHz + offset : lowHz * Math.Exp(offset);
                return Math.Clamp(Math.Log(hz / SpectrumAnalyzer.MinHz) / logDataSpan * last, 0, last);
            }

            float LevelAt(double index)
            {
                int left = Math.Min((int)index, last - 1);
                return values[left] + (values[left + 1] - values[left]) * (float)(index - left);
            }
        }
    }

    static float Y(RectF plot, float db, float bottom, float top) =>
        plot.Bottom - plot.H * Math.Clamp((db - bottom) / Math.Max(1f, top - bottom), 0, 1);

}
