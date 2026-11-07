using System.Globalization;
using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Engine;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

internal sealed class NameField : Widget
{
    public string Text = "";
    public bool Placeholder;
    public event Action? Clicked;
    public NameField() { Cursor = CursorKind.IBeam; Tooltip = "Klick: Kanal umbenennen"; }
    public override bool AcceptsMouse => Visible;
    public override void OnMouseEnter() => Invalidate();
    public override void OnMouseLeave() => Invalidate();
    public override void OnMouseUp(in MouseEvent e) { if (e.Button == 0 && HitOnRelease(e)) Clicked?.Invoke(); }
    public override void Paint(Renderer g)
    {
        var b = Bounds;
        if (IsHovered) g.FillRound(b.Inflate(4, 2), Theme.RadiusSmall, Theme.ControlHover.With(0.7f));
        float tw = Math.Min(b.W - 24, Renderer.MeasureWidth(Text, Theme.Title));
        g.Text(Text, new RectF(b.X, b.Y, b.W - 22, b.H), Theme.Title, Placeholder ? Theme.TextTertiary : Theme.Text, HAlign.Left, VAlign.Middle, true);
        if (IsHovered) Icons.Draw(g, Icon.Edit, b.X + tw + 14, b.CY, 15, Theme.TextSecondary);
    }
}

/// <summary>Kanal-Inspektor im rechten Seitenblatt.
/// Eingänge bieten Trim, Low-Cut (HPF), Pan, Phase, Laufzeit (0..1000 ms), Stereo-Link, Recall-Safe &amp; App-Link.
/// Ausgänge verweisen für EQ/Crossover/REW klar auf den dedizierten Tab „Outputs / DSP“.</summary>
internal sealed class ChannelSheet : SheetOverlay
{
    readonly AppModel _m;
    public bool IsInput { get; }
    public int Index { get; private set; }

    readonly IconButton _prev = new(Icon.ChevronLeft, "Vorheriger Kanal (←)") { IconSize = 16 };
    readonly IconButton _next = new(Icon.ChevronRight, "Nächster Kanal (→)") { IconSize = 16 };
    readonly IconButton _close = new(Icon.Close, "Schließen (Esc)") { IconSize = 16 };
    public readonly NameField Name = new();

    public readonly Slider Gain = new() { Min = Theme.FaderMinDb, Max = Theme.FaderMaxDb, Default = 0, Step = 0.1, WheelStep = 1 };
    public readonly ValueBox GainBox = new();
    public readonly Slider Trim = new() { Min = -24, Max = 24, Default = 0, Step = 0.1, WheelStep = 0.5 };
    public readonly ValueBox TrimBox = new();
    public readonly Slider Pan = new() { Min = -1.0, Max = 1.0, Default = 0, Step = 0.02, WheelStep = 0.05 };
    public readonly ValueBox PanBox = new();
    public readonly Segmented LowCut = new("Aus", "60 Hz", "80 Hz", "100 Hz", "120 Hz");

    public readonly Switch MuteSw = new(), SoloSw = new(), PhaseSw = new(), PairLinkSw = new(), RecallSafeSw = new();
    public readonly Slider Delay = new() { Min = 0, Max = 50.0, Default = 0, Step = 0.05, WheelStep = 0.2 };
    public readonly ValueBox DelayBox = new() { EditHint = "Doppelklick: Laufzeit 0..1000 ms eintippen" };
    public readonly Segmented Lock = new("Aus", "0", "−6", "−12", "−20") { Tooltip = "Gain-Sperre: höchster erlaubter Pegel in dB" };
    public readonly Segmented Ceiling = new("−1", "−3", "−6", "−12") { Tooltip = "Brickwall-Limiter-Grenze des Ausgangs in dBFS" };

    readonly Button _openOutDsp = new("In Outputs / DSP öffnen (Crossover, PEQ & REW)", ButtonStyle.Filled, Icon.Dsp);
    public readonly Button LinkBind = new("Programm binden …", ButtonStyle.Tinted, Icon.Link);
    public readonly Button LinkRemove = new("Lösen", ButtonStyle.DestructiveTinted);
    readonly Button _left = new("Nach links", ButtonStyle.Gray, Icon.ChevronLeft);
    readonly Button _right = new("Nach rechts", ButtonStyle.Gray, Icon.ChevronRight);
    public readonly Button Delete = new("Kanal löschen", ButtonStyle.DestructiveTinted, Icon.Trash);

    static readonly double[] LockValues = { Theme.FaderMaxDb, 0, -6, -12, -20 };
    static readonly double[] CeilValues = { -1, -3, -6, -12 };
    static readonly int[] LowCutHz = { 0, 60, 80, 100, 120 };

    readonly List<RectF> _cards = new();
    readonly List<(string Text, RectF Rect, TextStyle Style, Color4 Color)> _labels = new();

    ChannelSheet(AppModel m, bool input, int index)
    {
        _m = m; IsInput = input; Index = index;
        AddHeader(_prev); AddHeader(_next); AddHeader(_close); AddHeader(Name);

        Add(Gain); Add(GainBox);
        if (input) { Add(Trim); Add(TrimBox); Add(Pan); Add(PanBox); Add(LowCut); Add(LinkBind); Add(LinkRemove); }
        else { Add(_openOutDsp); Add(Ceiling); }
        Add(MuteSw); Add(SoloSw); Add(PhaseSw); Add(PairLinkSw); Add(RecallSafeSw);
        Add(Delay); Add(DelayBox); Add(Lock);
        Add(_left); Add(_right); Add(Delete);

        _prev.Clicked += _ => Go(-1);
        _next.Clicked += _ => Go(+1);
        _close.Clicked += _ => Close();
        Name.Clicked += BeginRename;

        Gain.ToNorm = db => Db.IsOff(db) ? 0 : FaderScale.ToT(db);
        Gain.FromNorm = t => { double db = FaderScale.ToDb(t); return Db.IsOff(db) ? db : Math.Round(db * 10) / 10; };
        Gain.Changed += v => Upd(c => c with { GainDb = Math.Abs(v) < 0.35 && !Db.IsOff(v) ? 0 : v });
        GainBox.Typed += s => { if (Db.TryParse(s, out var db)) Upd(c => c with { GainDb = Math.Min(db, c.MaxGainDb) }); };

        Trim.Changed += v => Upd(c => c with { TrimDb = Math.Round(v * 10) / 10 });
        TrimBox.Typed += s => { if (Db.TryParse(s, out var db)) Upd(c => c with { TrimDb = Math.Clamp(db, -24, 24) }); };

        Pan.Changed += v => Upd(c => c with { Pan = Math.Abs(v) < 0.03 ? 0 : Math.Round(v * 100) / 100 });
        PanBox.Typed += s =>
        {
            string clean = s.Trim().ToUpperInvariant();
            if (clean is "C" or "0" or "MITTE") Upd(c => c with { Pan = 0 });
            else if (clean.StartsWith('L') && double.TryParse(clean[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out var lv))
                Upd(c => c with { Pan = -Math.Clamp(lv / 100.0, 0, 1) });
            else if (clean.StartsWith('R') && double.TryParse(clean[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out var rv))
                Upd(c => c with { Pan = Math.Clamp(rv / 100.0, 0, 1) });
            else if (double.TryParse(clean.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pv))
                Upd(c => c with { Pan = Math.Clamp(pv, -1, 1) });
        };

        LowCut.Changed += idx =>
        {
            int hz = LowCutHz[idx];
            Upd(c =>
            {
                var rest = c.Filters.Where(f => f.Type != FilterType.HighPass).ToList();
                if (hz > 0) rest.Insert(0, new FilterSpec(FilterType.HighPass, hz, 0.7071, 0, Order: 2));
                return c with { Filters = rest.ToArray() };
            });
        };

        MuteSw.Changed += on => Upd(c => c with { Mute = on });
        SoloSw.Changed += on => Upd(c => c with { Solo = on });
        PhaseSw.Changed += on => Upd(c => c with { InvertPhase = on });
        PairLinkSw.Changed += _ =>
        {
            int pairStart = Index + 1 < Count ? Index : Math.Max(0, Index - 1);
            _m.TogglePairLink(IsInput, pairStart);
        };
        RecallSafeSw.Changed += on => Upd(c => c with { RecallSafe = on ? RecallSafeFlags.All : RecallSafeFlags.None });

        Delay.Changed += v => Upd(c => c with { DelayMs = Math.Round(v * 100) / 100 });
        DelayBox.Typed += s =>
        {
            string t = s.Trim().Replace("ms", "", StringComparison.OrdinalIgnoreCase).Trim().Replace(',', '.');
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) && double.IsFinite(ms))
                Upd(c => c with { DelayMs = Math.Clamp(ms, 0, 1000.0) });
        };

        Lock.Changed += i =>
        {
            double max = LockValues[i];
            Upd(c => c with { MaxGainDb = max, GainDb = Math.Min(c.GainDb, max) });
        };
        Ceiling.Changed += i => Upd(c => c with { CeilingDbfs = CeilValues[i] });

        _openOutDsp.Clicked += () =>
        {
            var r = Root;
            int idx = Index;
            Close();
            r?.OpenOutputDsp(idx);
        };

        LinkBind.Clicked += () => OpenPicker();
        LinkRemove.Clicked += () =>
        {
            var l = _m.LinkAt(Index); if (l == null) return;
            if (_m.RemoveLink(l.FirstChannel)) Root?.Notify.Flash($"Verknüpfung mit {l.Title} gelöst.");
        };

        _left.Clicked += () => { if (_m.CanMoveChannel(IsInput, Index, -1)) { _m.MoveChannel(IsInput, Index, -1); Index--; Layout(); Invalidate(); } };
        _right.Clicked += () => { if (_m.CanMoveChannel(IsInput, Index, +1)) { _m.MoveChannel(IsInput, Index, +1); Index++; Layout(); Invalidate(); } };
        Delete.Clicked += () => Root?.Confirm("Kanal löschen?", $"„{_m.DisplayName(IsInput, Index)}“ wird entfernt.", "Löschen",
            () => { int idx = Index; Close(); _m.RemoveChannel(IsInput, idx); }, danger: true);

        _m.ValuesChanged += SyncAndInvalidate;
        _m.StructureChanged += OnStructure;
    }

    int Count => IsInput ? _m.InputCount : _m.OutputCount;
    ChannelDsp Cur => _m.Channel(IsInput, Index);

    public static ChannelSheet Show(UiRoot root, AppModel m, bool input, int index, bool eq = false)
    {
        if (!input && eq)
        {
            root.OpenOutputDsp(index);
            return null!;
        }
        foreach (var o in root.Overlays.ToArray()) if (o is ChannelSheet) root.CloseOverlay(o);
        var s = new ChannelSheet(m, input, index);
        root.ShowOverlay(s);
        return s;
    }

    public override void OnClosed()
    {
        _m.ValuesChanged -= SyncAndInvalidate;
        _m.StructureChanged -= OnStructure;
    }

    void OnStructure()
    {
        if (Count == 0) { Close(); return; }
        Index = Math.Clamp(Index, 0, Count - 1);
        Layout();
        Invalidate();
    }

    void SyncAndInvalidate() { if (Count > 0) { Layout(); Invalidate(); } }
    void Upd(Func<ChannelDsp, ChannelDsp> f) => _m.UpdateChannel(IsInput, Index, f);

    void Go(int dir)
    {
        int next = Index + dir;
        if (next < 0 || next >= Count) return;
        Index = next;
        ResetScroll();
        Layout();
        Invalidate();
    }

    public void BeginRename()
    {
        if (Root == null || Count == 0) return;
        Root.BeginEdit(Name.Bounds, Cur.Name, Theme.Title, s => Upd(c => c with { Name = s.Trim() }), 32);
    }

    public void OpenPicker(bool manual = false)
    {
        if (Root == null || !IsInput) return;
        AppLinkPicker.Show(Root, _m, Index, LinkBind.Bounds, manual);
    }

    static string FormatPan(double pan)
    {
        if (Math.Abs(pan) < 0.015) return "Mitte";
        return pan < 0 ? $"L {Math.Abs(pan) * 100:0}" : $"R {pan * 100:0}";
    }

    protected override float LayoutBody(float x, float y, float w)
    {
        _cards.Clear();
        _labels.Clear();
        if (Count == 0) return 0;

        var c = Cur;
        Name.Text = _m.DisplayName(IsInput, Index);
        Name.Placeholder = string.IsNullOrWhiteSpace(c.Name);
        _prev.Enabled = Index > 0;
        _next.Enabled = Index + 1 < Count;

        float start = y;
        float pad = 14f;
        float rowH = Theme.Touch ? 38f : 32f;

        // Karte 1: Pegel, Trim & Panorama
        float c1Top = y;
        y += pad;
        _labels.Add(("Arbeits-Fader (dB)", new RectF(x + pad, y, w - 2 * pad, 18), Theme.CaptionStrong, Theme.TextSecondary));
        y += 20;
        Gain.Limit = c.MaxGainDb < Theme.FaderMaxDb - 0.05 ? c.MaxGainDb : double.NaN;
        Gain.SetValue(c.GainDb);
        Gain.Bounds = new RectF(x + pad, y, w - 2 * pad - 76, rowH);
        GainBox.Text = Db.Format(c.GainDb);
        GainBox.Bounds = new RectF(x + w - pad - 68, y + (rowH - 26) / 2, 68, 26);
        y += rowH + 8;

        if (IsInput)
        {
            _labels.Add(("Digitaler Vorverstärkungs-Trim (−24 .. +24 dB)", new RectF(x + pad, y, w - 2 * pad, 18), Theme.CaptionStrong, Theme.TextSecondary));
            y += 20;
            Trim.SetValue(c.TrimDb);
            Trim.Bounds = new RectF(x + pad, y, w - 2 * pad - 76, rowH);
            TrimBox.Text = Db.Format(c.TrimDb);
            TrimBox.Bounds = new RectF(x + w - pad - 68, y + (rowH - 26) / 2, 68, 26);
            y += rowH + 8;

            _labels.Add(("Panorama / Stereo-Balance", new RectF(x + pad, y, w - 2 * pad, 18), Theme.CaptionStrong, Theme.TextSecondary));
            y += 20;
            Pan.SetValue(c.Pan);
            Pan.Bounds = new RectF(x + pad, y, w - 2 * pad - 76, rowH);
            PanBox.Text = FormatPan(c.Pan);
            PanBox.Bounds = new RectF(x + w - pad - 68, y + (rowH - 26) / 2, 68, 26);
            y += rowH + 10;

            _labels.Add(("Trittschall-Filter (Low-Cut 12 dB/Okt.)", new RectF(x + pad, y, w - 2 * pad, 18), Theme.CaptionStrong, Theme.TextSecondary));
            y += 22;
            var hp = c.Filters.FirstOrDefault(f => f.Type == FilterType.HighPass && f.Enabled);
            int lcIdx = hp == null ? 0 : Array.FindIndex(LowCutHz, hz => hz > 0 && Math.Abs(hp.FrequencyHz - hz) < 5);
            LowCut.SetSelected(lcIdx < 0 ? 2 : lcIdx);
            LowCut.Bounds = new RectF(x + pad, y, w - 2 * pad, Theme.ControlHSmall);
            y += Theme.ControlHSmall + pad;
        }
        else
        {
            _openOutDsp.Bounds = new RectF(x + pad, y + 4, w - 2 * pad, Theme.ControlH);
            y += Theme.ControlH + pad + 4;
        }
        _cards.Add(new RectF(x, c1Top, w, y - c1Top));
        y += 10;

        // Karte 2: Schalter (Mute, Solo, Phase, Stereo-Kopplung, Recall-Safe)
        float c2Top = y;
        y += pad;
        void SwitchRow(string label, Switch sw, bool val, bool enabled = true)
        {
            sw.Enabled = enabled;
            sw.SetOn(val);
            _labels.Add((label, new RectF(x + pad, y, w - 2 * pad - 60, rowH), Theme.Body, Theme.Text));
            sw.Bounds = new RectF(x + w - pad - Theme.SwitchW, y, Theme.SwitchW, rowH);
            y += rowH + 4;
        }
        SwitchRow("Stumm (Mute)", MuteSw, c.Mute);
        SwitchRow("Solo", SoloSw, c.Solo);
        SwitchRow("Phase drehen (180°)", PhaseSw, c.InvertPhase);
        SwitchRow("Stereo-Paar koppeln (relativ)", PairLinkSw, c.LinkGroupId.HasValue, Count >= 2);
        SwitchRow("Recall-Safe (vor Snapshots schützen)", RecallSafeSw, c.RecallSafe != RecallSafeFlags.None);
        y += pad - 4;
        _cards.Add(new RectF(x, c2Top, w, y - c2Top));
        y += 10;

        // Karte 3: Laufzeit (Delay 0..1000 ms) & Schutz
        float c3Top = y;
        y += pad;
        double meters = c.DelayMs * 0.343;
        _labels.Add(($"Laufzeit / Delay  ·  {meters:0.00} m Distanz", new RectF(x + pad, y, w - 2 * pad, 18), Theme.CaptionStrong, Theme.TextSecondary));
        y += 20;
        Delay.SetValue(Math.Min(50.0, c.DelayMs));
        Delay.Bounds = new RectF(x + pad, y, w - 2 * pad - 82, rowH);
        DelayBox.Text = $"{c.DelayMs:0.##} ms";
        DelayBox.Bounds = new RectF(x + w - pad - 74, y + (rowH - 26) / 2, 74, 26);
        y += rowH + 10;

        _labels.Add(("Gain-Sperre (MaxGainDb)", new RectF(x + pad, y, w - 2 * pad, 18), Theme.CaptionStrong, Theme.TextSecondary));
        y += 20;
        int lockIdx = Array.FindIndex(LockValues, v => Math.Abs(v - c.MaxGainDb) < 0.25);
        Lock.SetSelected(lockIdx >= 0 ? lockIdx : 0);
        Lock.Bounds = new RectF(x + pad, y, w - 2 * pad, Theme.ControlHSmall);
        y += Theme.ControlHSmall + 10;

        if (!IsInput)
        {
            _labels.Add(("Brickwall-Limiter Grenze (dBFS)", new RectF(x + pad, y, w - 2 * pad, 18), Theme.CaptionStrong, Theme.TextSecondary));
            y += 20;
            int ceilIdx = Array.FindIndex(CeilValues, v => Math.Abs(v - c.CeilingDbfs) < 0.25);
            Ceiling.SetSelected(ceilIdx >= 0 ? ceilIdx : 0);
            Ceiling.Bounds = new RectF(x + pad, y, w - 2 * pad, Theme.ControlHSmall);
            y += Theme.ControlHSmall + 6;
        }
        y += pad - 4;
        _cards.Add(new RectF(x, c3Top, w, y - c3Top));
        y += 10;

        // Karte 4 (nur Eingang): App-Link
        if (IsInput)
        {
            float c4Top = y;
            y += pad;
            var link = _m.LinkAt(Index);
            string info = link != null ? LinkLook.Describe(link, _m.ViewOf(link)) : "Kein Programm direkt an diesen Eingang gebunden.";
            _labels.Add(("App-Audio-Quelle (ProcessLoopback)", new RectF(x + pad, y, w - 2 * pad, 18), Theme.CaptionStrong, Theme.TextSecondary));
            y += 20;
            _labels.Add((info, new RectF(x + pad, y, w - 2 * pad, 22), Theme.Callout, Theme.Text));
            y += 28;
            if (link != null)
            {
                LinkBind.Text = "Programm wechseln …";
                LinkBind.Bounds = new RectF(x + pad, y, (w - 2 * pad - 8) * 0.65f, Theme.ControlH);
                LinkRemove.Visible = true;
                LinkRemove.Bounds = new RectF(LinkBind.Bounds.Right + 8, y, (w - 2 * pad - 8) * 0.35f, Theme.ControlH);
            }
            else
            {
                LinkBind.Text = "Programm binden …";
                LinkBind.Bounds = new RectF(x + pad, y, w - 2 * pad, Theme.ControlH);
                LinkRemove.Visible = false;
            }
            y += Theme.ControlH + pad;
            _cards.Add(new RectF(x, c4Top, w, y - c4Top));
            y += 10;
        }

        // Reihenfolge & Löschen
        float half = (w - 8) / 2;
        _left.Enabled = _m.CanMoveChannel(IsInput, Index, -1);
        _right.Enabled = _m.CanMoveChannel(IsInput, Index, +1);
        _left.Bounds = new RectF(x, y, half, Theme.ControlH);
        _right.Bounds = new RectF(x + half + 8, y, half, Theme.ControlH);
        y += Theme.ControlH + 8;
        Delete.Bounds = new RectF(x, y, w, Theme.ControlH);
        y += Theme.ControlH;

        return y - start;
    }

    public override void Layout()
    {
        base.Layout();
        float y = Panel.Y + (HeaderH - 32) / 2;
        _prev.Bounds = new RectF(Panel.X + 14, y, 30, 32);
        _next.Bounds = new RectF(Panel.X + 46, y, 30, 32);
        _close.Bounds = new RectF(Panel.Right - 44, y, 30, 32);
        Name.Bounds = new RectF(Panel.X + 84, y, Math.Max(60, _close.Bounds.X - (Panel.X + 92)), 32);
    }

    protected override void PaintHeader(Renderer g, RectF header)
    {
        if (Count == 0) return;
        string owner = _m.ChannelOwnerTitle(Cur);
        string tag = (IsInput ? $"EINGANG {Index + 1:00}" : $"AUSGANG {Index + 1:00}") + (owner.Length > 0 ? $"  ·  {owner}" : "");
        g.Text(tag, new RectF(Name.Bounds.X, header.Y + 6, Name.Bounds.W, 14), Theme.MicroStrong, Theme.AccentText);
    }

    protected override void PaintBody(Renderer g)
    {
        foreach (var card in _cards) Draw.Card(g, card, Theme.RadiusInner, Theme.Surface);
        foreach (var l in _labels) g.Text(l.Text, l.Rect, l.Style, l.Color, HAlign.Left, VAlign.Middle, true);
    }
}
