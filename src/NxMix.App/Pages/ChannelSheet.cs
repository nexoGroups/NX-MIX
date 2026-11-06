using NxMix.App.Model;
using NxMix.App.Native;
using NxMix.App.Ui;
using NxMix.Audio;
using NxMix.Engine;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Kanalname im Kopf des Blatts: Klick = umbenennen (Stift erscheint beim Darueberfahren).</summary>
internal sealed class NameField : Widget
{
    public string Text = "";
    public bool Placeholder;
    public event Action? Clicked;
    public NameField() { Cursor = CursorKind.IBeam; Tooltip = "Klick: umbenennen"; }
    public override bool AcceptsMouse => Visible;
    public override void OnMouseEnter() => Invalidate();
    public override void OnMouseLeave() => Invalidate();
    public override void OnMouseUp(in MouseEvent e) { if (e.Button == 0 && Bounds.Contains(e.X, e.Y)) Clicked?.Invoke(); }
    public override void Paint(Renderer g)
    {
        var b = Bounds;
        if (IsHovered) g.FillRound(b.Inflate(4, 2), Theme.RadiusSmall, Theme.ControlHover.With(0.7f));
        float tw = Math.Min(b.W - 24, Renderer.MeasureWidth(Text, Theme.Title));
        g.Text(Text, new RectF(b.X, b.Y, b.W - 22, b.H), Theme.Title, Placeholder ? Theme.TextTertiary : Theme.Text, HAlign.Left, VAlign.Middle, true);
        if (IsHovered) Icons.Draw(g, Icon.Edit, b.X + tw + 14, b.CY, 15, Theme.TextSecondary);
    }
}

/// <summary>
/// Kanal-Blatt (wie die Kanal-Detailansicht bei Mixing Station): Name, Pegel, Mute/Solo/Phase, Laufzeit,
/// Gain-Sperre, Limiter-Grenze, App-Link, Verschieben/Loeschen. Pfeile wechseln zum Nachbarkanal.
/// </summary>
internal sealed class ChannelSheet : SheetOverlay
{
    readonly AppModel _m;
    public bool IsInput { get; }
    public int Index { get; private set; }
    public bool EqView { get; private set; }
    protected override float PreferredWidth => EqView ? 680 : base.PreferredWidth;

    readonly IconButton _prev = new(Icon.ChevronLeft, "Vorheriger Kanal (←)") { IconSize = 16 };
    readonly IconButton _next = new(Icon.ChevronRight, "Nächster Kanal (→)") { IconSize = 16 };
    readonly IconButton _close = new(Icon.Close, "Schließen (Esc)") { IconSize = 16 };
    public readonly NameField Name = new();
    readonly Segmented _view = new("Kanal", "EQ & Spektrum");
    readonly Toggle _showEq = new("EQ-Kurve", () => EqSpectrumGraph.EqColor) { ActiveText = () => Theme.IsDark ? Theme.Black : Theme.White };
    readonly Toggle _showBefore = new("Vor EQ", () => EqSpectrumGraph.BeforeColor) { ActiveText = () => Theme.Black };
    readonly Toggle _showAfter = new("Nach EQ", () => EqSpectrumGraph.AfterColor) { ActiveText = () => Theme.Black };
    readonly EqSpectrumGraph _graph = new();

    public readonly Slider Gain = new() { Min = Theme.FaderMinDb, Max = Theme.FaderMaxDb, Default = 0, Step = 0.1, WheelStep = 1 };
    public readonly ValueBox GainBox = new();
    public readonly Switch MuteSw = new(), SoloSw = new(), PhaseSw = new();
    public readonly Slider Delay = new() { Min = 0, Max = Theme.DelayMaxMs, Default = 0, Step = 0.01, WheelStep = 0.1 };
    public readonly ValueBox DelayBox = new() { EditHint = "Doppelklick: Laufzeit in ms eintippen" };
    public readonly Segmented Lock = new("Aus", "0", "−6", "−10", "−20") { Tooltip = "Gain-Sperre: höchster erlaubter Pegel in dB" };
    readonly Button _lockCustom = new("Eigener Wert …", ButtonStyle.Plain) { Font = Theme.CalloutMedium };
    public readonly Segmented Ceiling = new("−1", "−3", "−6", "−12") { Tooltip = "Limiter-Grenze des Ausgangs in dBFS" };
    readonly Button _ceilCustom = new("Eigener Wert …", ButtonStyle.Plain) { Font = Theme.CalloutMedium };
    internal readonly Button RewImport = new("REW-Filter importieren …", ButtonStyle.Tinted, Icon.Import);
    internal readonly Button RewBypass = new("EQ umgehen", ButtonStyle.Gray);
    readonly Button _rewRemove = new("EQ entfernen", ButtonStyle.DestructiveTinted);
    public readonly Button LinkBind = new("Programm binden …", ButtonStyle.Tinted, Icon.Link);
    public readonly Button LinkRemove = new("Lösen", ButtonStyle.DestructiveTinted);
    readonly Button _left = new("Nach links", ButtonStyle.Gray, Icon.ChevronLeft);
    readonly Button _right = new("Nach rechts", ButtonStyle.Gray, Icon.ChevronRight);
    public readonly Button Delete = new("Kanal löschen", ButtonStyle.DestructiveTinted, Icon.Trash);

    static readonly double[] LockValues = { Theme.FaderMaxDb, 0, -6, -10, -20 };
    static readonly double[] CeilValues = { -1, -3, -6, -12 };

    // Layout-Ergebnis fuer das Zeichnen
    readonly List<RectF> _groups = new();
    readonly List<(string Text, RectF Rect, TextStyle Style, Func<Color4> Color, bool Wrap)> _labels = new();
    readonly List<(float X, float Y, float W)> _seps = new();
    RectF _linkStatus;

    ChannelSheet(AppModel m, bool input, int index, bool eq)
    {
        _m = m; IsInput = input; Index = index;
        Add(_prev); Add(_next); Add(_close); Add(Name);
        Add(Gain); Add(GainBox); Add(MuteSw); Add(SoloSw); Add(PhaseSw);
        Add(Delay); Add(DelayBox); Add(Lock); Add(_lockCustom);
        if (!input) { Add(Ceiling); Add(_ceilCustom); Add(RewImport); Add(RewBypass); Add(_rewRemove); }
        if (input) { Add(LinkBind); Add(LinkRemove); }
        Add(_left); Add(_right); Add(Delete);
        Add(_view); Add(_showEq); Add(_showBefore); Add(_showAfter); Add(_graph);

        _view.Changed += i => SetView(i == 1);
        _showEq.SetChecked(true); _showBefore.SetChecked(true); _showAfter.SetChecked(true);
        _showEq.Toggled += on => _graph.ShowEq = on;
        _showBefore.Toggled += on => _graph.ShowBefore = on;
        _showAfter.Toggled += on => _graph.ShowAfter = on;
        SetView(eq);

        _prev.Clicked += _ => Go(-1);
        _next.Clicked += _ => Go(+1);
        _close.Clicked += _ => Close();
        Name.Clicked += BeginRename;

        Gain.ToNorm = db => Db.IsOff(db) ? 0 : FaderScale.ToT(db);
        Gain.FromNorm = t => { double db = FaderScale.ToDb(t); return Db.IsOff(db) ? db : Math.Round(db * 10) / 10; };
        Gain.Changed += v => Upd(c => c with { GainDb = Math.Abs(v) < 0.45 && !Db.IsOff(v) ? 0 : v });
        GainBox.Typed += s => { if (Db.TryParse(s, out var db)) Upd(c => c with { GainDb = Math.Min(db, c.MaxGainDb) }); else Flash("Ungültiger Wert: " + s); };
        MuteSw.Changed += on => Upd(c => c with { Mute = on });
        SoloSw.Changed += on => Upd(c => c with { Solo = on });
        PhaseSw.Changed += on => Upd(c => c with { InvertPhase = on });
        Delay.Changed += v => Upd(c => c with { DelayMs = v });
        DelayBox.Typed += s => { if (TryMs(s, out var ms)) Upd(c => c with { DelayMs = Math.Clamp(ms, 0, Theme.DelayMaxMs) }); else Flash("Ungültiger Wert: " + s); };
        Lock.Changed += i => SetMax(LockValues[i]);
        _lockCustom.Clicked += () => EditNumber(_lockCustom.Bounds, Cur.MaxGainDb < Theme.FaderMaxDb - 0.05 ? Cur.MaxGainDb : 0, db => SetMax(Math.Clamp(db, -60, Theme.FaderMaxDb)));
        Ceiling.Changed += i => Upd(c => c with { CeilingDbfs = CeilValues[i] });
        _ceilCustom.Clicked += () => EditNumber(_ceilCustom.Bounds, Cur.CeilingDbfs, db => Upd(c => c with { CeilingDbfs = Math.Min(0, db) }));
        RewImport.Clicked += ImportRew;
        RewBypass.Clicked += () =>
        {
            if (CanEditRew && Cur.Correction is { } eq) Upd(c => c with { Correction = eq with { Bypassed = !eq.Bypassed } });
        };
        _rewRemove.Clicked += () => Root?.Confirm("REW-EQ entfernen?", "Der importierte EQ wird von diesem Ausgang entfernt.",
            "Entfernen", () => SetCorrection(null), danger: true);
        LinkBind.Clicked += () => OpenPicker();
        LinkRemove.Clicked += () =>
        {
            var l = _m.LinkAt(Index); if (l == null) return;
            if (_m.RemoveLink(l.FirstChannel)) Root?.Notify.Flash($"Verknüpfung mit {l.Title} gelöst.");
        };
        _left.Clicked += () => Move(-1);
        _right.Clicked += () => Move(+1);
        Delete.Clicked += AskDelete;
        _m.StructureChanged += OnStructure;
    }

    public static ChannelSheet Show(UiRoot root, AppModel m, bool input, int index, bool eq = false)
    {
        // nur ein Kanal-Blatt gleichzeitig
        foreach (var o in root.Overlays.ToArray()) if (o is ChannelSheet) root.CloseOverlay(o);
        var s = new ChannelSheet(m, input, index, eq);
        root.ShowOverlay(s);
        s.RefreshSpectrum(0);
        return s;
    }

    public override void OnClosed()
    {
        _m.StructureChanged -= OnStructure;
        _graph.Stop();
    }

    void SetView(bool eq)
    {
        EqView = eq;
        _view.SetSelected(eq ? 1 : 0);
        _graph.Visible = _showEq.Visible = _showBefore.Visible = _showAfter.Visible = eq;
        foreach (var w in new Widget[] { Gain, GainBox, MuteSw, SoloSw, PhaseSw, Delay, DelayBox, Lock, _lockCustom,
                     Ceiling, _ceilCustom, LinkBind, _left, _right, Delete })
            w.Visible = !eq;
        if (!eq) _graph.Stop();
        ResetScroll();
        if (Root != null) { RefreshSpectrum(0); Layout(); Invalidate(); }
    }

    bool RefreshSpectrum(float dt) => EqView && Index < Count && _graph.Update(Cur, _m.Settings.SampleRate, IsInput, Index,
        _m.HostState == HostState.Running ? _m.Engine as MixerEngine : null, dt);

    public override bool Tick(float dt) => RefreshSpectrum(dt) | base.Tick(dt);

    void OnStructure()
    {
        if (Index >= Count) { Close(); return; }
        Layout(); Invalidate();
    }

    int Count => IsInput ? _m.InputCount : _m.OutputCount;
    ChannelDsp Cur => _m.Channel(IsInput, Index);
    void Upd(Func<ChannelDsp, ChannelDsp> f)
    {
        if (Index < Count) { _m.UpdateChannel(IsInput, Index, f); SyncValues(); }
        Invalidate();
    }
    void Flash(string s) => Root?.Notify.Flash(s, true);

    static bool TryMs(string s, out double ms)
    {
        s = s.Trim().Replace(',', '.');
        if (s.EndsWith("ms", StringComparison.OrdinalIgnoreCase)) s = s[..^2].Trim();
        return double.TryParse(s, System.Globalization.NumberStyles.Float, Db.Inv, out ms) && !double.IsNaN(ms) && !double.IsInfinity(ms);
    }

    void Go(int d)
    {
        int n = Index + d;
        if (n < 0 || n >= Count) return;
        if (Root?.Editor.Active == true) Root.Editor.Commit();
        Index = n;
        Layout(); Invalidate();
    }

    void Move(int d)
    {
        if (!_m.CanMoveChannel(IsInput, Index, d)) return;
        _m.MoveChannel(IsInput, Index, d);
        Index += d;
        Layout(); Invalidate();
    }

    void AskDelete()
    {
        var root = Root; if (root == null) return;
        string nm = _m.DisplayName(IsInput, Index);
        int idx = Index;
        root.Confirm("Kanal löschen?", $"„{nm}“ wird mit seinen Matrix-Verbindungen entfernt. Nachfolgende Kanäle rücken auf.",
            "Löschen", () =>
            {
                Close();
                if (idx < Count) _m.RemoveChannel(IsInput, idx);
                root.Notify.Flash($"„{nm}“ gelöscht");
            }, danger: true);
    }

    public void BeginRename()
    {
        var root = Root; if (root == null) return;
        var r = Name.Bounds.Inflate(6, 4);
        root.BeginEdit(r, Cur.Name, Theme.Title, s => Upd(c => c with { Name = s.Trim() }), 24);
    }

    public void OpenPicker(bool manual = false)
    {
        var root = Root; if (root == null) return;
        if (_m.HostRunning) { root.Notify.Flash("App-Links lassen sich nur bei gestopptem Audio ändern – zuerst Audio stoppen.", true); return; }
        AppLinkPicker.Show(root, _m, Index, LinkBind.Bounds, manual);
    }

    bool CanEditRew => !IsInput && Index < Count && !_m.HostRunning && !_m.HostBusy;

    void SetCorrection(OutputCorrection? correction)
    {
        if (!CanEditRew) return;
        Upd(c => c with { Correction = correction });
        Layout();
    }

    void ImportRew()
    {
        var root = Root;
        if (root == null || !CanEditRew) return;
        string? path = root.Shell.ShowOpenDialog("REW-Filter importieren", "REW-Filter (*.txt)\0*.txt\0\0");
        if (string.IsNullOrEmpty(path)) return;

        OutputCorrection eq;
        try { eq = RewFilterImport.Read(path, _m.Settings.SampleRate); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            ModalDialog.Show(root, "REW-Import fehlgeschlagen", ex.Message, new[] { "OK" }, 0, -1, _ => { });
            return;
        }

        string name = _m.DisplayName(false, Index);
        string replace = Cur.Correction == null ? "" : " Der bisherige REW-EQ dieses Ausgangs wird ersetzt.";
        root.Confirm("REW-EQ übernehmen?", $"„{eq.SourceName}“ enthält {eq.Filters.Count} aktive Filter für „{name}“.{replace}",
            "Übernehmen", () => SetCorrection(eq));
    }

    void SetMax(double newMax)
    {
        var root = Root; if (root == null) return;
        var c = Cur;
        void Apply() => Upd(ch => ch with { MaxGainDb = newMax });
        bool wasLocked = c.MaxGainDb < Theme.FaderMaxDb - 0.05;
        if (wasLocked && newMax > c.MaxGainDb + 0.05)
        {
            string nm = _m.DisplayName(IsInput, Index);
            root.Confirm("Gain-Sperre lockern?",
                $"„{nm}“ darf danach bis {(newMax >= Theme.FaderMaxDb - 0.05 ? "+12 dB (keine Sperre)" : Db.Format(newMax) + " dB")} laut werden (bisher max. {Db.Format(c.MaxGainDb)} dB). Das kann Lautsprecher und Gehör gefährden.",
                "Sperre lockern", Apply, danger: true);
        }
        else Apply();
        Invalidate();
    }

    void EditNumber(RectF at, double current, Action<double> apply)
    {
        var root = Root; if (root == null) return;
        root.BeginEdit(at.Inflate(2), Db.Format(current).Replace(Db.Minus, '-'), Theme.Value, s =>
        {
            if (Db.TryParse(s, out double v)) apply(v); else root.Notify.Flash("Ungültiger Wert: " + s, true);
        }, 10, null, HAlign.Center);
    }

    // ---- Werte aus dem Modell ------------------------------------------------------------------
    void SyncValues()
    {
        if (Index >= Count) return;
        var c = Cur;
        var link = IsInput ? _m.LinkAt(Index) : null;
        Name.Text = !string.IsNullOrWhiteSpace(c.Name) ? c.Name : link != null ? link.Title : AppModel.DefaultName(IsInput, Index);
        Name.Placeholder = string.IsNullOrWhiteSpace(c.Name);
        _prev.Enabled = Index > 0; _next.Enabled = Index < Count - 1;
        bool locked = c.MaxGainDb < Theme.FaderMaxDb - 0.05;
        Gain.Limit = locked ? c.MaxGainDb : double.NaN;
        Gain.SetValue(c.GainDb);
        GainBox.Text = Db.Format(c.GainDb) + " dB";
        GainBox.TextColor = !Db.IsOff(c.GainDb) && c.GainDb > 0.05 ? Theme.Yellow : null;
        MuteSw.SetOn(c.Mute); SoloSw.SetOn(c.Solo); PhaseSw.SetOn(c.InvertPhase);
        Delay.SetValue(c.DelayMs);
        DelayBox.Text = $"{c.DelayMs:0.00} ms";
        int li = locked ? Array.FindIndex(LockValues, v => Math.Abs(v - c.MaxGainDb) < 0.05) : 0;
        Lock.SetSelected(li);
        Ceiling.SetSelected(Array.FindIndex(CeilValues, v => Math.Abs(v - c.CeilingDbfs) < 0.05));
        RewImport.Enabled = RewBypass.Enabled = _rewRemove.Enabled = CanEditRew;
        RewBypass.Visible = _rewRemove.Visible = c.Correction != null;
        RewBypass.Text = c.Correction?.Bypassed == true ? "EQ aktivieren" : "EQ umgehen";
        bool run = _m.HostRunning;
        LinkBind.Text = link != null ? "Ändern …" : "Programm binden …";
        LinkBind.Enabled = !run;
        LinkRemove.Visible = !EqView && link != null;
        LinkRemove.Enabled = !run;
        _left.Enabled = Index > 0 && _m.CanMoveChannel(IsInput, Index, -1);
        _right.Enabled = Index < Count - 1 && _m.CanMoveChannel(IsInput, Index, +1);
    }

    // ---- Layout --------------------------------------------------------------------------------
    public override void Layout()
    {
        SyncValues();
        base.Layout();
        var p = Panel;
        float bs = 32;
        _close.Bounds = new RectF(p.Right - 16 - bs, p.Y + 20, bs, bs);
        _next.Bounds = new RectF(_close.Bounds.X - 6 - bs, p.Y + 20, bs, bs);
        _prev.Bounds = new RectF(_next.Bounds.X - 2 - bs, p.Y + 20, bs, bs);
        Name.Bounds = new RectF(p.X + 20, p.Y + 34, Math.Max(40, _prev.Bounds.X - 12 - p.X - 20), 28);
    }

    void Label(string t, RectF r, TextStyle st, Func<Color4> c, bool wrap = false) => _labels.Add((t, r, st, c, wrap));

    float Section(float x, float y, float w, string title)
    {
        Label(title, new RectF(x + 4, y, w, 20), Theme.Overline, () => Theme.TextTertiary);
        return y + 24;
    }

    float Footer(float x, float y, float w, string text)
    {
        float h = Renderer.TextHeight(text, Theme.Caption, w - 8);
        Label(text, new RectF(x + 4, y + 6, w - 8, h + 2), Theme.Caption, () => Theme.TextTertiary, true);
        return y + 6 + h + 4;
    }

    protected override float LayoutBody(float x, float y0, float w)
    {
        _groups.Clear(); _labels.Clear(); _seps.Clear();
        float y = y0 + 12;
        float rowH = Theme.Touch ? 52 : 44;
        float ip = 14; // Innenabstand der Gruppen
        float boxW = 92, boxH = Theme.Touch ? 32 : 26;
        var c = Index < Count ? Cur : new ChannelDsp();

        _view.Bounds = new RectF(x, y, w, Theme.ControlHSmall + 4);
        y = _view.Bounds.Bottom + 14;
        if (EqView)
        {
            float bw = (w - 12) / 3;
            float bh = Theme.ControlHSmall + 4;
            _showEq.Bounds = new RectF(x, y, bw, bh);
            _showBefore.Bounds = new RectF(_showEq.Bounds.Right + 6, y, bw, bh);
            _showAfter.Bounds = new RectF(_showBefore.Bounds.Right + 6, y, bw, bh);
            _graph.Bounds = new RectF(x, y + bh + 8, w, 252);
            y = Footer(x, _graph.Bounds.Bottom, w, "Live-Abgriff direkt vor und nach der Filterkette.") + 12;
            if (!IsInput) y = LayoutRew(c, x, y, w, ip);
            return y - y0;
        }

        // ---- Pegel ----
        y = Section(x, y, w, "PEGEL");
        float g0 = y;
        Label("Gain", new RectF(x + ip, y + 10, 120, boxH), Theme.BodyMedium, () => Theme.Text);
        GainBox.Bounds = new RectF(x + w - ip - boxW, y + 10, boxW, boxH);
        Gain.Bounds = new RectF(x + ip - 4, y + 10 + boxH + 4, w - 2 * ip + 8, 32);
        y = Gain.Bounds.Bottom + 6;
        foreach (var (sw, text) in new[] { (MuteSw, "Stumm"), (SoloSw, "Solo"), (PhaseSw, "Phase invertieren (180°)") })
        {
            _seps.Add((x + ip, y, w - ip));
            Label(text, new RectF(x + ip, y, w - 2 * ip - 60, rowH), Theme.Body, () => Theme.Text);
            sw.Bounds = new RectF(x + w - ip - 60, y, 60, rowH);
            y += rowH;
        }
        _groups.Add(new RectF(x, g0, w, y - g0));
        y += 22;

        // ---- Laufzeit ----
        y = Section(x, y, w, "LAUFZEIT");
        g0 = y;
        Label("Verzögerung", new RectF(x + ip, y + 10, 160, boxH), Theme.BodyMedium, () => Theme.Text);
        DelayBox.Bounds = new RectF(x + w - ip - boxW, y + 10, boxW, boxH);
        Delay.Bounds = new RectF(x + ip - 4, y + 10 + boxH + 4, w - 2 * ip + 8, 32);
        y = Delay.Bounds.Bottom + 8;
        _groups.Add(new RectF(x, g0, w, y - g0));
        y = Footer(x, y, w, "Verzögert diesen Kanal um bis zu 20 ms, z. B. um Lautsprecher zeitlich auszurichten. Doppelklick auf den Regler = 0.");
        y += 16;

        // ---- Schutz ----
        y = Section(x, y, w, "SCHUTZ");
        g0 = y;
        float lw = Renderer.MeasureWidth("Eigener Wert …", Theme.CalloutMedium) + 20;
        Label("Gain-Sperre", new RectF(x + ip, y + 10, w - 2 * ip - lw, boxH), Theme.BodyMedium, () => Theme.Text);
        _lockCustom.Bounds = new RectF(x + w - ip - lw + 8, y + 10, lw, boxH);
        Lock.Bounds = new RectF(x + ip, y + 10 + boxH + 6, w - 2 * ip, Theme.ControlH);
        y = Lock.Bounds.Bottom + 14;
        if (!IsInput)
        {
            _seps.Add((x + ip, y, w - ip));
            Label("Limiter-Grenze (dBFS)", new RectF(x + ip, y + 10, w - 2 * ip - lw, boxH), Theme.BodyMedium, () => Theme.Text);
            _ceilCustom.Bounds = new RectF(x + w - ip - lw + 8, y + 10, lw, boxH);
            Ceiling.Bounds = new RectF(x + ip, y + 10 + boxH + 6, w - 2 * ip, Theme.ControlH);
            y = Ceiling.Bounds.Bottom + 14;
        }
        _groups.Add(new RectF(x, g0, w, y - g0));
        bool locked = c.MaxGainDb < Theme.FaderMaxDb - 0.05;
        string lockText = locked
            ? $"Gesperrt bei {Db.Format(c.MaxGainDb)} dB: Fader, Eingabe und Mausrad gehen nicht darüber." + (AppModel.IsProtected(c) ? " Matrix-Punkte auf diesen Ausgang bleiben bei höchstens 0 dB." : "")
            : "Keine Sperre. Für empfindliche Hochtöner einen Höchstwert wählen – der Fader lässt sich dann nicht darüber ziehen.";
        if (!IsInput) lockText += $" Limiter-Grenze: {Db.Format(c.CeilingDbfs)} dBFS (Marke am Meter).";
        y = Footer(x, y, w, lockText);
        y += 16;

        if (!IsInput) y = LayoutRew(c, x, y, w, ip);

        // ---- App-Link ----
        if (IsInput)
        {
            y = Section(x, y, w, "APP-LINK");
            g0 = y;
            _linkStatus = new RectF(x + ip, y + 10, w - 2 * ip, 40);
            y += 58;
            float bh = Theme.ControlHSmall + 4;
            bool linked = _m.LinkAt(Index) != null;
            if (linked)
            {
                float rw = Math.Max(80, LinkRemove.PreferredWidth);
                LinkRemove.Bounds = new RectF(x + w - ip - rw, y, rw, bh);
                LinkBind.Bounds = new RectF(x + ip, y, LinkRemove.Bounds.X - 8 - x - ip, bh);
            }
            else LinkBind.Bounds = new RectF(x + ip, y, w - 2 * ip, bh);
            y += bh + 14;
            _groups.Add(new RectF(x, g0, w, y - g0));
            string note = _m.HostRunning
                ? "Zum Ändern zuerst Audio stoppen."
                : "Bindet ein Programm (z. B. Spotify) an diesen Eingang: Sein Ton läuft dann nur noch über NX-MIX.";
            y = Footer(x, y, w, note);
            y += 16;
        }

        // ---- Kanal ----
        y = Section(x, y, w, "KANAL");
        g0 = y;
        float hb = Theme.ControlHSmall + 4;
        float half = (w - 2 * ip - 8) / 2;
        _left.Bounds = new RectF(x + ip, y + 14, half, hb);
        _right.Bounds = new RectF(x + ip + half + 8, y + 14, half, hb);
        y = _left.Bounds.Bottom + 10;
        Delete.Bounds = new RectF(x + ip, y, w - 2 * ip, hb);
        y = Delete.Bounds.Bottom + 14;
        _groups.Add(new RectF(x, g0, w, y - g0));
        return y - y0;
    }

    float LayoutRew(ChannelDsp c, float x, float y, float w, float ip)
    {
        y = Section(x, y, w, "REW-EQ");
        float start = y;
        float bh = Theme.ControlHSmall + 4;
        RewImport.Bounds = new RectF(x + ip, y + 12, w - 2 * ip, bh);
        y = RewImport.Bounds.Bottom;
        if (c.Correction is { } eq)
        {
            float bw = (w - 2 * ip - 8) / 2;
            RewBypass.Bounds = new RectF(x + ip, y + 8, bw, bh);
            _rewRemove.Bounds = new RectF(RewBypass.Bounds.Right + 8, y + 8, bw, bh);
            y = RewBypass.Bounds.Bottom;
            string state = eq.Bypassed ? "Bypass" : "Aktiv";
            Label($"{eq.SourceName} · {eq.Filters.Count} Filter · {state}", new RectF(x + ip, y + 6, w - 2 * ip, 20),
                Theme.Caption, () => Theme.TextSecondary);
            y += 24;
        }
        y += 12;
        _groups.Add(new RectF(x, start, w, y - start));
        return Footer(x, y, w, CanEditRew ? "REW Generic: bis zu 20 parametrische Filter (PK)." : "Zum Ändern zuerst Audio stoppen.") + 16;
    }

    // ---- Zeichnen ------------------------------------------------------------------------------
    protected override void PaintHeader(Renderer g, RectF h)
    {
        string kind = IsInput ? "EINGANG" : "AUSGANG";
        g.Text($"{kind} {Index + 1:00}  ·  {Index + 1} VON {Count}", new RectF(h.X + 20, h.Y + 16, h.W - 160, 16), Theme.Overline, Theme.TextTertiary, HAlign.Left, VAlign.Middle, false);
    }

    public override void Paint(Renderer g)
    {
        if (Index < Count) Layout(); // Werte und Aufteilung koennen sich durch das Modell geaendert haben
        base.Paint(g);
    }

    protected override void PaintBody(Renderer g)
    {
        foreach (var r in _groups) g.FillRound(r, 12, Theme.IsDark ? Theme.Surface2 : Theme.Surface2);
        foreach (var (x, y, w) in _seps) g.Fill(new RectF(x, y, w, 1), Theme.Separator);
        foreach (var l in _labels) g.Text(l.Text, l.Rect, l.Style, l.Color(), HAlign.Left, l.Wrap ? VAlign.Top : VAlign.Middle, !l.Wrap, l.Wrap);
        if (IsInput && !EqView && !_linkStatus.IsEmpty) PaintLinkStatus(g);
    }

    void PaintLinkStatus(Renderer g)
    {
        var r = _linkStatus;
        var l = Index < Count ? _m.LinkAt(Index) : null;
        if (l == null)
        {
            g.FillRound(new RectF(r.X, r.Y + 2, 36, 36), 10, Theme.Control);
            Icons.Draw(g, Icon.Link, r.X + 18, r.Y + 20, 18, Theme.TextTertiary);
            g.Text("Kein Programm gebunden", new RectF(r.X + 48, r.Y + 2, r.W - 48, 18), Theme.BodyMedium, Theme.Text);
            g.Text(_m.AppLinksEnabled ? "Eingang erhält sein Signal über die Geräte-Zuordnung." : "App-Links sind in den Einstellungen ausgeschaltet.",
                new RectF(r.X + 48, r.Y + 20, r.W - 48, 18), Theme.Callout, Theme.TextSecondary);
            return;
        }
        var v = _m.ViewOf(l);
        var col = LinkLook.Color(v.State);
        g.FillRound(new RectF(r.X, r.Y + 2, 36, 36), 10, Theme.Soft(col));
        string letter = l.Title.Length > 0 ? l.Title[..1].ToUpperInvariant() : "?";
        g.Text(letter, new RectF(r.X, r.Y + 2, 36, 36), Theme.Headline, col, HAlign.Center, VAlign.Middle, false);
        string head = $"{l.Title}  ·  {(l.Count == 2 ? (Index == l.FirstChannel ? "Stereo L" : "Stereo R") : "mono")}";
        g.Text(head, new RectF(r.X + 48, r.Y + 2, r.W - 48, 18), Theme.BodyMedium, Theme.Text);
        Icons.StatusDot(g, r.X + 52, r.Y + 30, col, 3, false);
        string st = LinkLook.Text(v.State) + (string.IsNullOrWhiteSpace(v.Message) ? "" : ": " + v.Message);
        g.Text(st, new RectF(r.X + 60, r.Y + 21, r.W - 60, 18), Theme.Callout, Theme.TextSecondary);
    }

    public override void OnKeyDown(int vk, bool ctrl, bool shift)
    {
        switch (vk)
        {
            case Win32.VK_LEFT: Go(-1); return;
            case Win32.VK_RIGHT: Go(+1); return;
        }
        base.OnKeyDown(vk, ctrl, shift);
    }
}
