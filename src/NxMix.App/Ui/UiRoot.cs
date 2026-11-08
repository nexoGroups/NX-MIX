using System.Diagnostics;
using NxMix.App.Model;
using NxMix.App.Pages;
using NxMix.Ipc;

namespace NxMix.App.Ui;

internal interface IShell
{
    void Minimize();
    void ToggleMaximize();
    void Close();
    bool IsMaximized { get; }
    void SetCapture(bool on);
    string? ShowSaveDialog(string title, string filter, string defaultName);
    string? ShowOpenDialog(string title, string filter);
    WindowGeometry? WindowState { get; }
    bool SystemPrefersDark { get; }
    bool ReduceMotion { get; }
    void ThemeChanged();
    void UiScaleChanged();
    void PrepareExit();
}

internal sealed class NullShell : IShell
{
    public string? SaveResult { get; set; }
    public string? OpenResult { get; set; }
    public bool SystemDark { get; set; } = true;
    public void Minimize() { }
    public void ToggleMaximize() { }
    public void Close() { }
    public bool IsMaximized => false;
    public void SetCapture(bool on) { }
    public string? ShowSaveDialog(string title, string filter, string defaultName) => SaveResult;
    public string? ShowOpenDialog(string title, string filter) => OpenResult;
    public WindowGeometry? WindowState => null;
    public bool SystemPrefersDark => SystemDark;
    public bool ReduceMotion { get; set; } = true;
    public void ThemeChanged() { }
    public void UiScaleChanged() { ScaleChanges++; }
    public int ScaleChanges;
    public int ExitPrepared;
    public void PrepareExit() => ExitPrepared++;
}

/// <summary>Wurzel der NX-MIX Live-Konsole mit 8 Bereichen:
/// 0=Mixer, 1=Decks, 2=Geräte, 3=Patching Matrix, 4=Virtual I/O, 5=Outputs / DSP, 6=Snapshots, 7=Engine.</summary>
internal sealed class UiRoot : Widget
{
    public const int TabMixer = 0;
    public const int TabDecks = 1;
    public const int TabDevices = 2;
    public const int TabMatrix = 3;
    public const int TabVirtualIo = 4;
    public const int TabOutputsDsp = 5;
    public const int TabSnapshots = 6;
    public const int TabEngine = 7;

    public AppModel Model { get; }
    public IShell Shell { get; }
    public UiPrefs Prefs { get; }
    public override UiRoot? Root => this;

    public float W { get; private set; }
    public float H { get; private set; }
    public double Time { get; private set; }
    public bool Dirty { get; private set; } = true;
    public bool ReduceMotion { get; private set; }

    public Widget? Hovered { get; private set; }
    public Widget? Captured { get; private set; }
    int _captureButton;

    public readonly Sidebar Side;
    public readonly TopBar Top;
    public readonly Toasts Notify;
    public readonly Page[] Pages;
    public readonly TextEditor Editor;
    readonly List<Overlay> _overlays = new();
    public Overlay? Modal => _overlays.Count > 0 ? _overlays[^1] : null;
    public IReadOnlyList<Overlay> Overlays => _overlays;
    public int PageIndex { get; private set; }

    float _mx = -1, _my = -1;
    string? _tipText;
    double _tipSince;
    bool _tipVisible;
    Motion _pageReveal, _tooltipReveal;

    public AppUpdater Updater { get; }

    public UiRoot(AppModel model, IShell shell, UiPrefs? prefs = null, IUpdateBackend? updates = null)
    {
        Model = model;
        Shell = shell;
        ReduceMotion = shell.ReduceMotion;
        Prefs = prefs ?? new UiPrefs();
        Prefs.UiScalePercent = UiPrefs.ClampScale(Prefs.UiScalePercent);
        var resolvedUpdates = updates ?? new FakeUpdateBackend { IsInstalled = false };
        if (!Prefs.UpdateChannelChosen)
            Prefs.UpdateChannel = resolvedUpdates.InstalledChannel;
        Updater = new AppUpdater(resolvedUpdates, () => Model.Post, Prefs.UpdateChannel);
        Updater.Changed += OnUpdaterChanged;
        ApplyThemeFromPrefs(false);

        Notify = Add(new Toasts());
        model.Toast = (msg, err) => Notify.Flash(msg, err);

        Pages = new Page[]
        {
            new MixerPage(model),
            new DecksPage(model),
            new DevicesPage(model),
            new MatrixPage(model),
            new VirtualIoPage(model),
            new OutputsDspPage(model),
            new SnapshotsPage(model),
            new EnginePage(model),
        };
        foreach (var p in Pages) Add(p);

        Side = Add(new Sidebar { Expanded = Prefs.SidebarExpanded });
        Side.AdvanceWidth();
        Top = Add(new TopBar());
        // Notify als letztes Kind malen, damit Toasts über den Seiten liegen
        Children.Remove(Notify);
        Children.Add(Notify);

        Editor = new TextEditor { Parent = this };
        model.HostChanged += () => Dirty = true;
        model.SnapshotsChanged += () => Dirty = true;
        SwitchPage(0);
    }

    public new void Invalidate() => Dirty = true;
    public bool ConsumeDirty() { bool d = Dirty; Dirty = false; return d; }

    public bool EffectiveDark => Prefs.Theme switch { ThemeMode.Dark => true, ThemeMode.Light => false, _ => Shell.SystemPrefersDark };

    void ApplyThemeFromPrefs(bool relayout)
    {
        bool wasDark = Theme.IsDark;
        Theme.Apply(EffectiveDark, Prefs.Density);
        if (wasDark != Theme.IsDark) Shell.ThemeChanged();
        if (relayout) { Side.AdvanceWidth(); Resize(W, H); }
        Dirty = true;
    }

    public void SetThemeMode(ThemeMode mode) { Prefs.Theme = mode; Prefs.Save(); ApplyThemeFromPrefs(true); }
    public void SetDensity(Density d) { Prefs.Density = d; Prefs.Save(); ApplyThemeFromPrefs(true); }

    public void SetUiScale(double percent)
    {
        if (!double.IsFinite(percent)) percent = 100;
        int next = UiPrefs.ClampScale((int)(Math.Round(percent / 5) * 5));
        int old = Prefs.UiScalePercent;
        if (next == old) return;
        Prefs.UiScalePercent = next;
        float ratio = (float)old / next;
        _mx *= ratio;
        _my *= ratio;
        Shell.UiScaleChanged();
        if (Captured is Slider slider)
            slider.RebaseDrag(_mx);
        Prefs.Save();
        Dirty = true;
    }

    public void SetSidebarExpanded(bool on)
    {
        if (Side.Expanded == on) return;
        Prefs.SidebarExpanded = Side.Expanded = on;
        Prefs.Save();
        if (Side.AdvanceWidth()) LayoutWindow(allPages: false);
        Dirty = true;
    }

    public void RefreshMotionPreference()
    {
        bool reduce = Shell.ReduceMotion;
        if (ReduceMotion == reduce) return;
        ReduceMotion = reduce;
        Dirty = true;
    }

    public void SystemThemeChanged() { if (Prefs.Theme == ThemeMode.System) ApplyThemeFromPrefs(true); }

    public RectF ContentRect => new(Side.Width, Theme.TopBarH, Math.Max(0, W - Side.Width), Math.Max(0, H - Theme.TopBarH));

    public void Resize(float w, float h)
    {
        W = Math.Max(1, w); H = Math.Max(1, h);
        Bounds = new RectF(0, 0, W, H);
        LayoutWindow(allPages: true);
    }

    void LayoutWindow(bool allPages)
    {
        Side.Bounds = new RectF(0, 0, Side.Width, H);
        Top.Bounds = new RectF(Side.Width, 0, Math.Max(0, W - Side.Width), Theme.TopBarH);
        Notify.Bounds = ContentRect;
        var content = ContentRect;
        foreach (var p in Pages)
        {
            p.Bounds = content;
            if (allPages || p.Visible) p.Layout();
        }
        foreach (var o in _overlays) { o.Bounds = Bounds; o.Layout(); }
        if (Captured == null) RefreshHover(Ev(_mx, _my, -1, false, false, false));
        Dirty = true;
    }

    public void SwitchPage(int i)
    {
        i = Math.Clamp(i, 0, Pages.Length - 1);
        if (Editor.Active) Editor.Commit();
        if (i != PageIndex)
        {
            if (Captured != null && IsInside(Captured, CurrentPage)) CancelCapture();
            // Wenn Outputs / DSP verlassen wird, Spektral-Tap freigeben (sofern kein Kanalblatt offen ist)
            if (PageIndex == TabOutputsDsp && i != TabOutputsDsp && _overlays.Count == 0)
                Model.SubscribeSpectrum(0, -1);
            RevealPage();
        }
        PageIndex = i;
        for (int k = 0; k < Pages.Length; k++) Pages[k].Visible = k == i;
        Pages[i].OnShow();
        SetHover(null);
        Dirty = true;
    }

    public void OpenOutputDsp(int outputChannelIndex)
    {
        if (Pages[TabOutputsDsp] is OutputsDspPage dsp)
            dsp.SelectOutputChannel(outputChannelIndex);
        SwitchPage(TabOutputsDsp);
    }

    public Page CurrentPage => Pages[PageIndex];

    public void RevealPage()
    {
        _pageReveal.Start(0.45f, 1, Time);
        Dirty = true;
    }

    public TitleHit CaptionHit(float x, float y)
    {
        if (Side.Bounds.Contains(x, y)) return Side.HitKind(x, y) == TitleHit.Drag ? TitleHit.Drag : TitleHit.None;
        if (Modal != null && Modal is not SheetOverlay) return TitleHit.None;
        return Top.HitAt(x, y).Kind;
    }

    public override void Paint(Renderer g)
    {
        g.Clear(Theme.Window);
        float opacity = _pageReveal.At(this, 1, Motion.Reveal);
        g.PushClip(ContentRect);
        if (opacity < 1) g.PushOpacity(ContentRect, opacity);
        CurrentPage.Paint(g);
        if (opacity < 1) g.PopOpacity();
        g.PopClip();
        Top.Paint(g);
        Side.Paint(g);
        foreach (var o in _overlays) o.PaintTransition(g);
        if (Editor.Active) Editor.Paint(g);
        Notify.Paint(g);
        PaintTooltip(g);
    }

    void PaintTooltip(Renderer g)
    {
        if (!_tipVisible || string.IsNullOrEmpty(_tipText) || Modal is PopupMenu or ModalDialog) return;
        float tw = Renderer.MeasureWidth(_tipText, Theme.Callout);
        float maxW = Math.Min(380, W - 8);
        if (maxW <= 20) return;
        bool wrap = tw + 20 > maxW;
        float w = Math.Min(tw + 20, maxW), h = 28;
        if (wrap) h = Renderer.TextHeight(_tipText, Theme.Callout, w - 20) + 14;
        float x = Math.Clamp(_mx + 14, 4, W - w - 4);
        h = Math.Min(h, Math.Max(0, H - 8));
        float y = _my + 22 + h > H - 4 ? _my - h - 8 : _my + 22;
        y = Math.Clamp(y, 4, Math.Max(4, H - h - 4));
        var r = new RectF(x, y, w, h);
        float opacity = _tooltipReveal.At(this, 1, Motion.Feedback);
        if (opacity < 1) g.PushOpacity(Bounds, opacity);
        g.Shadow(r, 8, 8, 1f, 2);
        g.FillRound(r, 8, Theme.IsDark ? Theme.Rgb(0x34363C) : Theme.Rgb(0x2A2C31));
        var tc = Theme.Rgb(0xF2F3F5);
        if (wrap) g.Text(_tipText, r.Deflate(10, 7), Theme.Callout, tc, HAlign.Left, VAlign.Top, false, true);
        else g.Text(_tipText, r.Deflate(10, 0), Theme.Callout, tc);
        if (opacity < 1) g.PopOpacity();
    }

    public void Tick(float dt)
    {
        Time += dt;
        if (Side.AdvanceWidth()) LayoutWindow(allPages: false);
        if (Model.Tick(dt)) Dirty = true;
        CurrentPage.Tick(dt);
        if (Notify.Tick(dt)) Dirty = true;
        if (Editor.Active && Editor.Tick(dt)) Dirty = true;
        foreach (var o in _overlays.ToArray()) if (o.Tick(dt)) Dirty = true;

        if (!_tipVisible && _tipText != null && Time - _tipSince > Theme.TooltipDelaySeconds && Captured == null)
        {
            _tipVisible = true;
            _tooltipReveal.Start(0.2f, 1, Time);
            Dirty = true;
        }
    }

    /// <summary>Speichert lokale UI-Einstellungen und Deck-Definitionen beim Schließen des Fensters.</summary>
    public void SaveNow()
    {
        Prefs.UpdateWindow(Shell.WindowState);
        Prefs.Save();
        UiDeckStore.Save(Model.Decks);
    }

    static MouseEvent Ev(float x, float y, int button, bool ctrl, bool shift, bool alt, float wheel = 0) =>
        new() { X = x, Y = y, Button = button, Ctrl = ctrl, Shift = shift, Alt = alt, Wheel = wheel };

    public override Widget? HitTest(float x, float y)
    {
        if (!Bounds.Contains(x, y)) return null;
        if (Editor.Active && Editor.Bounds.Contains(x, y)) return Editor;
        if (Modal != null) return Modal.HitTest(x, y) ?? Modal;
        if (Side.Bounds.Contains(x, y)) return Side;
        if (Top.Bounds.Contains(x, y)) return Top;
        return CurrentPage.HitTest(x, y);
    }

    void SetHover(Widget? w)
    {
        if (w == Hovered) return;
        Hovered?.OnMouseLeave();
        Hovered = w;
        Hovered?.OnMouseEnter();
        _tipVisible = false;
        _tipText = null;
        _tipSince = Time;
        Dirty = true;
    }

    void UpdateTip()
    {
        string? t = Hovered?.TooltipAt(_mx, _my);
        if (t != _tipText) { _tipText = t; _tipVisible = false; _tipSince = Time; Dirty = true; }
    }

    public void MouseMove(float x, float y, bool ctrl, bool shift, bool alt)
    {
        _mx = x; _my = y;
        var e = Ev(x, y, 0, ctrl, shift, alt);
        if (Captured != null)
        {
            if (Captured.Root != null) Captured.OnMouseMove(e);
            return;
        }
        RefreshHover(e);
    }

    void RefreshHover(in MouseEvent e)
    {
        var hit = HitTest(e.X, e.Y);
        SetHover(hit);
        hit?.OnMouseMove(e);
        UpdateTip();
    }

    public void MouseLeave()
    {
        if (Captured == null) { _mx = _my = -1; SetHover(null); }
    }

    public void MouseDown(float x, float y, int button, bool ctrl, bool shift, bool alt)
    {
        if (Captured != null) return;
        _mx = x; _my = y;
        var e = Ev(x, y, button, ctrl, shift, alt);
        if (Editor.Active && !Editor.Bounds.Contains(x, y)) Editor.Commit();
        var hit = HitTest(x, y);
        SetHover(hit);
        _tipVisible = false; _tipText = null;
        if (hit != null)
        {
            Captured = hit;
            _captureButton = button;
            Shell.SetCapture(true);
            hit.OnMouseDown(e);
            if (Captured == hit && (hit.Root == null || (Modal != null && hit != Editor && !IsInside(hit, Modal)))) CancelCapture();
        }
        Dirty = true;
    }

    static bool IsInside(Widget w, Widget container)
    {
        for (var p = w; p != null; p = p.Parent) if (p == container) return true;
        return false;
    }

    public void MouseUp(float x, float y, int button, bool ctrl, bool shift, bool alt)
    {
        if (Captured == null || button != _captureButton) return;
        _mx = x; _my = y;
        var e = Ev(x, y, button, ctrl, shift, alt);
        var c = Captured;
        if (c != null)
        {
            Captured = null;
            Shell.SetCapture(false);
            if (c.Root != null) c.OnMouseUp(e);
            RefreshHover(e);
        }
        Dirty = true;
    }

    public void CaptureLost()
    {
        if (Captured is not { } captured) return;
        Captured = null;
        if (captured.Root == this) captured.OnMouseUp(Ev(_mx, _my, -1, false, false, false));
        SetHover(null);
        Dirty = true;
    }

    void CancelCapture()
    {
        if (Captured == null) return;
        CaptureLost();
        Shell.SetCapture(false);
    }

    public void MouseWheel(float x, float y, float notches, bool ctrl, bool shift, bool alt)
    {
        _mx = x; _my = y;
        var e = Ev(x, y, 0, ctrl, shift, alt, notches);
        if (Captured != null) return;
        for (var w = HitTest(x, y); w != null; w = w.Parent)
        {
            if (w.OnMouseWheel(e)) { Dirty = true; break; }
            if (w is Overlay) break;
        }
        _tipVisible = false; _tipSince = Time;
        RefreshHover(e);
    }

    public CursorKind CurrentCursor => (Captured ?? Hovered)?.CursorAt(_mx, _my) ?? CursorKind.Arrow;

    public bool KeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (Editor.Active) { Editor.OnKeyDown(vk, ctrl, shift); Dirty = true; return true; }
        if (Modal != null) { Modal.OnKeyDown(vk, ctrl, shift); Dirty = true; return true; }
        if (ctrl && !alt)
        {
            switch (vk)
            {
                case >= '1' and <= '8':
                    SwitchPage(vk - '1');
                    return true;
                case 'Z' when !shift && CurrentPage is MatrixPage matrix:
                    matrix.UndoPatch();
                    return true;
                case 'M':
                    Model.SetPanicMute(!Model.PanicMuted);
                    return true;
                case 'S':
                    QuickSaveSnapshot();
                    return true;
            }
        }
        return false;
    }

    public void Char(char c)
    {
        if (Editor.Active) { Editor.OnChar(c); Dirty = true; return; }
        if (Modal != null) { Modal.OnChar(c); Dirty = true; }
    }

    public void ShowOverlay(Overlay o)
    {
        if (Editor.Active) Editor.Commit();
        CancelCapture();
        _overlays.Add(o);
        o.Bounds = Bounds;
        o.Parent = this;
        o.BeginReveal(Time);
        o.Layout();
        _tipText = null; _tipVisible = false;
        SetHover(null);
        Dirty = true;
    }

    public void CloseOverlay(Overlay? o)
    {
        if (o == null) return;
        int idx = _overlays.IndexOf(o);
        if (idx < 0) return;
        for (int i = _overlays.Count - 1; i >= idx; i--)
        {
            var ov = _overlays[i];
            if (Captured != null && IsInside(Captured, ov)) CancelCapture();
            if (IsInside(Editor, ov)) { Editor.Cancel(); Editor.Parent = this; }
            _overlays.RemoveAt(i);
            ov.Parent = null;
            ov.OnClosed();
        }
        Dirty = true;
        RefreshHover(Ev(_mx, _my, -1, false, false, false));
    }

    public void BeginEdit(RectF bounds, string text, TextStyle style, Action<string> commit, int maxLen = 40,
        Func<char, bool>? filter = null, HAlign align = HAlign.Left)
    {
        Editor.Begin(bounds, text, style, commit, maxLen, filter, align);
        Editor.Parent = (Widget?)Modal ?? this;
        Dirty = true;
    }

    public void Confirm(string title, string message, string okText, Action onOk, bool danger = false, string cancelText = "Abbrechen")
    {
        ModalDialog.Show(this, title, message, new[] { cancelText, okText }, 1, danger ? 1 : -1, i => { if (i == 1) onOk(); },
            danger ? Icon.Warn : Icon.None);
    }

    // ---- Snapshot-Schnellwahl in der TopBar ----------------------------------------------------
    public void QuickSaveSnapshot()
    {
        string name = $"Szene {Model.Snapshots.Count + 1}";
        Model.SaveSnapshot(null, name, "Schnell gespeichert aus Live-Mix", "#3B82F6", 150.0, SnapshotScopeFlags.All,
            s => Notify.Flash($"Snapshot „{s.Name}“ gespeichert."));
    }

    public void ShowQuickSnapshotMenu(RectF anchor)
    {
        var items = new List<MenuItem>
        {
            new("Live-Mix als neuen Snapshot speichern", "Strg+S", QuickSaveSnapshot) { Icon = Icon.Plus },
            new("Snapshots & Recall-Safe verwalten …", "Strg+7", () => SwitchPage(TabSnapshots)) { Icon = Icon.Snapshots },
        };
        if (Model.Snapshots.Count > 0)
        {
            items.Add(MenuItem.Sep);
            items.Add(MenuItem.Header("Schnellabruf (mit Standard-Fade)"));
            foreach (var s in Model.Snapshots)
            {
                var snap = s;
                items.Add(new MenuItem(
                    snap.Name,
                    $"{snap.DefaultFadeMs:0} ms",
                    () =>
                    {
                        Model.RecallSnapshot(snap.Id, snap.DefaultScope, snap.DefaultFadeMs);
                        Notify.Flash($"Snapshot „{snap.Name}“ abgerufen ({snap.DefaultFadeMs:0} ms Fade).");
                    })
                {
                    Checked = Model.ActiveSnapshotId == snap.Id,
                });
            }
        }
        PopupMenu.Show(this, anchor, items, 270);
    }

    // ---- Updates & Release-Kanäle -------------------------------------------------------------
    UpdateStage _lastStage;

    public void StartUpdateCheck() { if (Prefs.AutoUpdateCheck) Updater.Check(); }

    public void SetAutoUpdateCheck(bool on)
    {
        Prefs.AutoUpdateCheck = on;
        Prefs.Save();
        Dirty = true;
    }

    public void SetUpdateChannel(ReleaseChannel channel, bool autoCheckNow = true)
    {
        if (Prefs.UpdateChannel == channel && Prefs.UpdateChannelChosen && Updater.SelectedChannel == channel)
            return;

        Prefs.UpdateChannel = channel;
        Prefs.UpdateChannelChosen = true;
        Prefs.Save();
        Updater.SetChannel(channel, autoCheckNow);

        if (channel == ReleaseChannel.Stable && Updater.IsCurrentBetaBuild)
            Notify.Flash("Update-Kanal: Stable gewählt. Beim Wechsel auf die aktuelle Stable-Version wird vorab automatisch ein Konfigurations-Backup angelegt.");
        else if (channel == ReleaseChannel.Beta)
            Notify.Flash("Update-Kanal: Beta gewählt (Vorabversionen & neue Funktionen).");
        else
            Notify.Flash("Update-Kanal: Stable gewählt (offizielle Live-Versionen).");

        Dirty = true;
    }

    public void OpenEngineUpdatesSection()
    {
        SwitchPage(TabEngine);
        if (Pages[TabEngine] is EnginePage ep)
            ep.SelectLeftSection(2);
    }

    public void OpenChangelog()
    {
        var offer = Updater.Offer;
        string targetVer = offer?.Version ?? Updater.CurrentVersion;
        string url = offer != null
            ? $"{VelopackUpdateBackend.ReleasesPageUrl}/tag/v{offer.Version}"
            : VelopackUpdateBackend.ReleasesPageUrl;

        string notes = PlainNotes(offer?.Notes, 1100);
        if (notes.Length > 0)
        {
            string chanLabel = offer?.IsPrerelease == true ? "Beta-Prerelease" : "Stable-Release";
            ModalDialog.Show(
                this,
                $"Changelog – NX-MIX v{targetVer} ({chanLabel})",
                notes,
                new[] { "Schließen", "Auf GitHub öffnen" },
                0,
                -1,
                i => { if (i == 1) OpenBrowserUrl(url); },
                Icon.Engine);
        }
        else
        {
            ModalDialog.Show(
                this,
                $"Versionshinweise – NX-MIX v{Updater.CurrentVersion}",
                $"Aktuell installiert: v{Updater.CurrentVersion} ({ReleaseChannelInfo.DisplayName(Updater.InstalledChannel)}-Kanal).\n\n" +
                "Vollständige Release Notes für alle Stable- und Beta-Versionen sind auf der GitHub-Releases-Seite verfügbar.",
                new[] { "Schließen", "GitHub Releases öffnen" },
                1,
                -1,
                i => { if (i == 1) OpenBrowserUrl(url); },
                Icon.Engine);
        }
    }

    static void OpenBrowserUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Program.LogCrash(ex);
        }
    }

    void OnUpdaterChanged()
    {
        var st = Updater.Stage;
        if (st != _lastStage)
        {
            if (st == UpdateStage.Ready)
            {
                string kind = Updater.Offer?.IsDowngrade == true ? "Kanalwechsel auf Stable" : "Update";
                Notify.Flash($"{kind} v{Updater.Offer?.Version} geprüft & bereit – Installation wartet auf deine Freigabe.");
            }
            else if (st == UpdateStage.Failed && _lastStage == UpdateStage.Downloading)
            {
                Notify.Flash("Update-Download fehlgeschlagen: " + Updater.Error, true);
            }
            _lastStage = st;
        }
        if (Pages[TabEngine] is EnginePage ep && PageIndex == TabEngine)
            ep.Layout();
        Top.Layout();
        Dirty = true;
    }

    public void ShowUpdate()
    {
        var u = Updater;
        switch (u.Stage)
        {
            case UpdateStage.Available:
                {
                    var offer = u.Offer!;
                    string notes = PlainNotes(offer.Notes, 700);
                    string title = offer.IsDowngrade
                        ? $"Kanalwechsel auf Stable v{offer.Version}"
                        : $"NX-MIX v{offer.Version} ({ReleaseChannelInfo.DisplayName(offer.SourceChannel)}) verfügbar";
                    string body = offer.IsDowngrade
                        ? $"Installiert ist Beta v{u.CurrentVersion}. Für den Wechsel auf den offiziellen Stable-Kanal wird v{offer.Version} geladen; vor der Installation sichert NX-MIX automatisch alle Einstellungen, Decks und Snapshots."
                        : $"Installiert ist v{u.CurrentVersion}. Das Paket wird im Hintergrund geladen und per SHA256 geprüft; der laufende Audiobetrieb bleibt unberührt.";
                    if (notes.Length > 0) body += "\n\n" + notes;

                    ModalDialog.Show(this, title, body,
                        new[] { "Später", "Update-Einstellungen", "Herunterladen" }, 2, -1, i =>
                        {
                            if (i == 1) OpenEngineUpdatesSection();
                            else if (i == 2) u.Download();
                        }, Icon.Import);
                    break;
                }
            case UpdateStage.Downloading:
                Notify.Flash($"Paket v{u.Offer?.Version} wird geladen … {u.Progress} %");
                break;
            case UpdateStage.Ready:
                ConfirmAndInstallUpdate();
                break;
            case UpdateStage.Failed:
                OpenEngineUpdatesSection();
                Notify.Flash("Update-Fehler: " + u.Error, true);
                break;
            case UpdateStage.NotInstalled:
                OpenEngineUpdatesSection();
                Notify.Flash("Automatische Updates erfordern eine Setup-Installation (NX-MIX-Setup.exe oder NX-MIX-Beta-Setup.exe).");
                break;
            default:
                u.Check();
                break;
        }
    }

    public void ConfirmAndInstallUpdate()
    {
        var u = Updater;
        if (u.Stage != UpdateStage.Ready || u.Offer == null) return;
        if (u.InstallOnExitPending)
        {
            Notify.Flash($"Version v{u.Offer.Version} ist bereits für die Installation beim Beenden vorgemerkt.");
            return;
        }

        bool live = Model.HostRunning;
        bool downgrade = u.Offer.IsDowngrade;
        string title = downgrade
            ? $"Auf Stable v{u.Offer.Version} wechseln"
            : $"NX-MIX v{u.Offer.Version} installieren";

        string msg = downgrade
            ? $"Kanalwechsel von v{u.CurrentVersion} auf Stable v{u.Offer.Version}: Vor dem Austausch legt NX-MIX automatisch ein vollständiges Backup aller Routings, DSP-Filter, Decks und Snapshots an."
            : $"Paket v{u.Offer.Version} wurde heruntergeladen und per Prüfsumme verifiziert. Deine Einstellungen, Decks und Snapshots bleiben vollständig erhalten.";

        msg += live
            ? "\n\nACHTUNG – Aktiver Live-Audiobetrieb erkannt!\nEin sofortiger Update-Neustart stoppt kontrolliert NxMix.Daemon.exe und unterbricht Audio-Streams für wenige Sekunden. Empfehlung im Live-Betrieb: „Beim Beenden installieren“."
            : "\n\n„Jetzt neu starten“ speichert alle Einstellungen, beendet kontrolliert die Hintergrund-Engine (NxMix.Daemon.exe) und startet die aktualisierte Version.";

        ModalDialog.Show(
            this,
            title,
            msg,
            new[] { "Später", "Beim Beenden installieren", "Jetzt kontrolliert neu starten" },
            live ? 1 : 2,
            -1,
            i =>
            {
                if (i == 1)
                {
                    u.InstallOnExit(UiPrefs.DataDirectory);
                    Notify.Flash($"v{u.Offer.Version} wird nach dem Schließen kontrolliert installiert (Backup erstellt).");
                }
                else if (i == 2)
                {
                    RestartForUpdate();
                }
            },
            Icon.Import);
    }

    void RestartForUpdate()
    {
        SaveNow();
        Updater.CreatePreUpdateBackup(UiPrefs.DataDirectory);
        Model.ShutdownDaemonExplicitly(5000);
        Updater.InstallNow(null);
    }

    static string PlainNotes(string? md, int maxChars = 700)
    {
        if (string.IsNullOrWhiteSpace(md)) return "";
        var lines = md.Replace("\r", "").Split('\n')
            .Select(l => l.Trim().TrimStart('#').Trim().Replace("**", "").Replace("`", ""))
            .Select(l => l.StartsWith("- ") ? "• " + l[2..] : l);
        var text = string.Join("\n", lines).Trim();
        while (text.Contains("\n\n\n")) text = text.Replace("\n\n\n", "\n\n");
        return text.Length > maxChars ? text[..maxChars].TrimEnd() + " …" : text;
    }
}
