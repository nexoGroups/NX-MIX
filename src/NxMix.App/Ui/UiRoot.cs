using NxMix.App.Model;
using NxMix.App.Pages;

namespace NxMix.App.Ui;

/// <summary>Anbindung ans Fenster (oder Platzhalter fuer Screenshot/Selbsttest).</summary>
internal interface IShell
{
    void Minimize();
    void ToggleMaximize();
    void Close();
    bool IsMaximized { get; }
    void SetCapture(bool on);
    string? ShowSaveDialog(string title, string filter, string defaultName);
    string? ShowOpenDialog(string title, string filter);
    WindowDto? WindowState { get; }
    /// <summary>Windows steht auf "dunkler App-Modus".</summary>
    bool SystemPrefersDark { get; }
    /// <summary>Farbmodus hat gewechselt (Fensterrahmen anpassen).</summary>
    void ThemeChanged();
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
    public WindowDto? WindowState => null;
    public bool SystemPrefersDark => SystemDark;
    public void ThemeChanged() { }
}

/// <summary>Wurzel der Oberflaeche: Seitenleiste, Kopfleiste, Seiten, Ueberlagerungen (Stapel), Meldungen, Eingabeverteilung.</summary>
internal sealed class UiRoot : Widget
{
    public AppModel Model { get; }
    public IShell Shell { get; }
    public UiPrefs Prefs { get; }
    public override UiRoot? Root => this;

    public float W { get; private set; }
    public float H { get; private set; }
    public double Time { get; private set; }
    public bool Dirty { get; private set; } = true;

    public Widget? Hovered { get; private set; }
    public Widget? Captured { get; private set; }

    public readonly Sidebar Side;
    public readonly TopBar Top;
    public readonly Toasts Notify;
    public readonly Page[] Pages;
    public readonly TextEditor Editor;
    readonly List<Overlay> _overlays = new();
    /// <summary>Oberste Ueberlagerung (bekommt alle Eingaben) oder null.</summary>
    public Overlay? Modal => _overlays.Count > 0 ? _overlays[^1] : null;
    public IReadOnlyList<Overlay> Overlays => _overlays;
    public int PageIndex { get; private set; }

    float _mx = -1, _my = -1;
    string? _tipText;
    double _tipSince;
    bool _tipVisible;
    double _saveDue = -1;

    public UiRoot(AppModel model, IShell shell, UiPrefs? prefs = null)
    {
        Model = model;
        Shell = shell;
        Prefs = prefs ?? new UiPrefs();
        ApplyThemeFromPrefs(false);
        Pages = new Page[] { new MixerPage(model), new MatrixPage(model), new DevicesPage(model), new SettingsPage(model) };
        foreach (var p in Pages) Add(p);
        Side = Add(new Sidebar { Expanded = Prefs.SidebarExpanded });
        Top = Add(new TopBar());
        Notify = Add(new Toasts());
        Editor = new TextEditor { Parent = this };
        model.Changed += () => _saveDue = Time + 0.8;
        SwitchPage(0);
    }

    public new void Invalidate() => Dirty = true;
    public bool ConsumeDirty() { bool d = Dirty; Dirty = false; return d; }

    // ---- Darstellung -------------------------------------------------------------------------
    public bool EffectiveDark => Prefs.Theme switch { ThemeMode.Dark => true, ThemeMode.Light => false, _ => Shell.SystemPrefersDark };

    void ApplyThemeFromPrefs(bool relayout)
    {
        bool wasDark = Theme.IsDark;
        Theme.Apply(EffectiveDark, Prefs.Density);
        if (wasDark != Theme.IsDark) Shell.ThemeChanged();
        if (relayout) Resize(W, H);
        Dirty = true;
    }

    public void SetThemeMode(ThemeMode mode) { Prefs.Theme = mode; Prefs.Save(); ApplyThemeFromPrefs(true); }
    public void SetDensity(Density d) { Prefs.Density = d; Prefs.Save(); ApplyThemeFromPrefs(true); }
    public void SetSidebarExpanded(bool on) { Prefs.SidebarExpanded = on; Prefs.Save(); Side.Expanded = on; Resize(W, H); }
    /// <summary>Windows hat den App-Modus gewechselt (nur relevant bei "System").</summary>
    public void SystemThemeChanged() { if (Prefs.Theme == ThemeMode.System) ApplyThemeFromPrefs(true); }

    // ---- Layout --------------------------------------------------------------------------------
    public RectF ContentRect => new(Side.Width, Theme.TopBarH, Math.Max(0, W - Side.Width), Math.Max(0, H - Theme.TopBarH));

    public void Resize(float w, float h)
    {
        W = Math.Max(1, w); H = Math.Max(1, h);
        Bounds = new RectF(0, 0, W, H);
        Side.Bounds = new RectF(0, 0, Side.Width, H);
        Top.Bounds = new RectF(Side.Width, 0, Math.Max(0, W - Side.Width), Theme.TopBarH);
        Notify.Bounds = ContentRect;
        var content = ContentRect;
        foreach (var p in Pages) { p.Bounds = content; p.Layout(); }
        foreach (var o in _overlays) { o.Bounds = Bounds; o.Layout(); }
        Dirty = true;
    }

    public void SwitchPage(int i)
    {
        i = Math.Clamp(i, 0, Pages.Length - 1);
        if (Editor.Active) Editor.Commit();
        PageIndex = i;
        for (int k = 0; k < Pages.Length; k++) Pages[k].Visible = k == i;
        Pages[i].OnShow();
        SetHover(null);
        Dirty = true;
    }

    public Page CurrentPage => Pages[PageIndex];

    /// <summary>Was liegt an (x, y) fuer das Fenster (ziehen, Maximieren-Knopf ...)?</summary>
    public TitleHit CaptionHit(float x, float y)
    {
        if (Side.Bounds.Contains(x, y)) return Side.HitKind(x, y) == TitleHit.Drag ? TitleHit.Drag : TitleHit.None;
        if (Modal != null && Modal is not SheetOverlay) return TitleHit.None;
        return Top.HitAt(x, y).Kind;
    }

    // ---- Zeichnen ------------------------------------------------------------------------------
    public override void Paint(Renderer g)
    {
        g.Clear(Theme.Window);
        CurrentPage.Paint(g);
        Top.Paint(g);
        Side.Paint(g);
        foreach (var o in _overlays) o.Paint(g);
        if (Editor.Active) Editor.Paint(g);
        Notify.Paint(g);
        PaintTooltip(g);
    }

    void PaintTooltip(Renderer g)
    {
        if (!_tipVisible || string.IsNullOrEmpty(_tipText) || Modal is PopupMenu or ModalDialog) return;
        float tw = Renderer.MeasureWidth(_tipText, Theme.Callout);
        const float maxW = 360;
        bool wrap = tw + 20 > maxW;
        float w = Math.Min(tw + 20, maxW), h = 28;
        if (wrap) h = Renderer.TextHeight(_tipText, Theme.Callout, w - 20) + 14;
        float x = Math.Clamp(_mx + 14, 4, W - w - 4);
        float y = _my + 22 + h > H - 4 ? _my - h - 8 : _my + 22;
        var r = new RectF(x, y, w, h);
        g.Shadow(r, 8, 8, 1f, 2);
        g.FillRound(r, 8, Theme.IsDark ? Theme.Rgb(0x34363C) : Theme.Rgb(0x2A2C31));
        var tc = Theme.Rgb(0xF2F3F5);
        if (wrap) g.Text(_tipText, r.Deflate(10, 7), Theme.Callout, tc, HAlign.Left, VAlign.Top, false, true);
        else g.Text(_tipText, r.Deflate(10, 0), Theme.Callout, tc);
    }

    // ---- Takt ----------------------------------------------------------------------------------
    public void Tick(float dt)
    {
        Time += dt;
        if (Model.Tick(dt)) Dirty = true;
        CurrentPage.Tick(dt);
        if (Notify.Tick(dt)) Dirty = true;
        if (Editor.Active && Editor.Tick(dt)) Dirty = true;
        foreach (var o in _overlays.ToArray()) if (o.Tick(dt)) Dirty = true;

        if (!_tipVisible && _tipText != null && Time - _tipSince > Theme.TooltipDelaySeconds && Captured == null)
        {
            _tipVisible = true; Dirty = true;
        }

        if (_saveDue > 0 && Time >= _saveDue) { _saveDue = -1; SaveNow(); }
    }

    /// <summary>Schreibt das Projekt sofort (beim Beenden und nach der Entprellung).</summary>
    public void SaveNow()
    {
        _saveDue = -1;
        if (!ProjectStore.Enabled) return;
        ProjectStore.TrySaveAuto(ProjectStore.ToDto(Model, Shell.WindowState));
    }

    // ---- Maus ----------------------------------------------------------------------------------
    static MouseEvent Ev(float x, float y, int button, int clicks, bool ctrl, bool shift, bool alt, float wheel = 0) =>
        new() { X = x, Y = y, Button = button, Clicks = clicks, Ctrl = ctrl, Shift = shift, Alt = alt, Wheel = wheel };

    public override Widget? HitTest(float x, float y)
    {
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
        var e = Ev(x, y, 0, 1, ctrl, shift, alt);
        if (Captured != null)
        {
            if (Captured.Root != null) Captured.OnMouseMove(e);
            return;
        }
        var hit = HitTest(x, y);
        SetHover(hit);
        UpdateTip();
        hit?.OnMouseMove(e);
        if (_tipVisible && Hovered != null && _tipText != Hovered.TooltipAt(x, y)) { _tipVisible = false; Dirty = true; }
    }

    public void MouseLeave()
    {
        if (Captured == null) { SetHover(null); UpdateTip(); }
    }

    public void MouseDown(float x, float y, int button, int clicks, bool ctrl, bool shift, bool alt)
    {
        _mx = x; _my = y;
        var e = Ev(x, y, button, clicks, ctrl, shift, alt);
        if (Editor.Active && !Editor.Bounds.Contains(x, y)) Editor.Commit();
        var hit = HitTest(x, y);
        SetHover(hit);
        _tipVisible = false; _tipText = null;
        if (hit != null)
        {
            Captured = hit;
            Shell.SetCapture(true);
            hit.OnMouseDown(e);
            // Hat der Druck eine neue Ebene geoeffnet oder die eigene geschlossen, gehoert der Rest des Klicks nicht mehr dem Element
            if (hit.Root == null || (Modal != null && hit != Editor && !IsInside(hit, Modal))) { Captured = null; Shell.SetCapture(false); }
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
        _mx = x; _my = y;
        var e = Ev(x, y, button, 1, ctrl, shift, alt);
        var c = Captured;
        if (c != null)
        {
            Captured = null;
            Shell.SetCapture(false);
            if (c.Root != null) c.OnMouseUp(e);
            SetHover(HitTest(x, y));
            UpdateTip();
        }
        Dirty = true;
    }

    public void CaptureLost()
    {
        if (Captured != null) { var c = Captured; Captured = null; c.OnMouseUp(Ev(_mx, _my, 0, 1, false, false, false)); }
    }

    public void MouseWheel(float x, float y, float notches, bool ctrl, bool shift, bool alt)
    {
        _mx = x; _my = y;
        var e = Ev(x, y, 0, 1, ctrl, shift, alt, notches);
        if (Captured != null) return;
        for (var w = HitTest(x, y); w != null; w = w.Parent)
        {
            if (w.OnMouseWheel(e)) { Dirty = true; break; }
            if (w is Overlay) break;
        }
        _tipVisible = false; _tipSince = Time;
    }

    public CursorKind CurrentCursor
    {
        get
        {
            var w = Captured ?? Hovered;
            return w?.CursorAt(_mx, _my) ?? CursorKind.Arrow;
        }
    }

    // ---- Tastatur ------------------------------------------------------------------------------
    public bool KeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (Editor.Active) { Editor.OnKeyDown(vk, ctrl, shift); Dirty = true; return true; }
        if (Modal != null) { Modal.OnKeyDown(vk, ctrl, shift); Dirty = true; return true; }
        if (ctrl && !alt)
        {
            switch (vk)
            {
                case 'N': NewProjectWithConfirm(); return true;
                case 'E': ExportProject(); return true;
                case 'O': ImportProject(); return true;
                case >= '1' and <= '4': SwitchPage(vk - '1'); return true;
            }
        }
        return false;
    }

    public void Char(char c)
    {
        if (Editor.Active) { Editor.OnChar(c); Dirty = true; return; }
        if (Modal != null) { Modal.OnChar(c); Dirty = true; }
    }

    // ---- Ueberlagerungen -----------------------------------------------------------------------
    public void ShowOverlay(Overlay o)
    {
        if (Editor.Active) Editor.Commit();
        _overlays.Add(o);
        o.Bounds = Bounds;
        o.Parent = this;
        o.Layout();
        Captured = null;
        Shell.SetCapture(false);
        _tipText = null; _tipVisible = false;
        SetHover(null);
        Dirty = true;
    }

    /// <summary>Schliesst o (und alles, was darueber liegt).</summary>
    public void CloseOverlay(Overlay? o)
    {
        if (o == null) return;
        int idx = _overlays.IndexOf(o);
        if (idx < 0) return;
        if (Editor.Active && IsInside(Editor, o)) Editor.Cancel();
        for (int i = _overlays.Count - 1; i >= idx; i--)
        {
            var ov = _overlays[i];
            _overlays.RemoveAt(i);
            ov.Parent = null;
            ov.OnClosed();
        }
        if (Captured != null && Captured.Root == null) { Captured = null; Shell.SetCapture(false); }
        Dirty = true;
        SetHover(HitTest(_mx, _my));
    }

    /// <summary>Bearbeitet einen Text direkt an Ort und Stelle (z. B. Kanalname, Zahlenwert).</summary>
    public void BeginEdit(RectF bounds, string text, TextStyle style, Action<string> commit, int maxLen = 40,
        Func<char, bool>? filter = null, HAlign align = HAlign.Left)
    {
        Editor.Begin(bounds, text, style, commit, maxLen, filter, align);
        Dirty = true;
    }

    public void Confirm(string title, string message, string okText, Action onOk, bool danger = false, string cancelText = "Abbrechen")
    {
        ModalDialog.Show(this, title, message, new[] { cancelText, okText }, 1, danger ? 1 : -1, i => { if (i == 1) onOk(); },
            danger ? Icon.Warn : Icon.None);
    }

    // ---- Projekt-Aktionen ----------------------------------------------------------------------
    public void NewProjectWithConfirm()
    {
        Confirm("Neues leeres Projekt?", "Alle Kanäle, Einstellungen und Geräte-Zuordnungen werden verworfen. Das aktuelle Projekt ist danach nur noch über einen vorherigen Export wiederherstellbar.",
            "Neu beginnen", () => { Model.NewProject(); Notify.Flash("Neues leeres Projekt"); }, danger: true);
    }

    public void ExportProject()
    {
        var path = Shell.ShowSaveDialog("Projekt exportieren", "NX-MIX-Projekt (*.json)\0*.json\0\0", "NX-MIX-Projekt.json");
        if (path == null) return;
        try
        {
            ProjectStore.WriteFile(path, ProjectStore.ToDto(Model, Shell.WindowState));
            Notify.Flash("Projekt exportiert: " + Path.GetFileName(path));
        }
        catch (Exception ex) { Notify.Flash("Export fehlgeschlagen: " + ex.Message, true); }
    }

    public void ImportProject()
    {
        var path = Shell.ShowOpenDialog("Projekt importieren", "NX-MIX-Projekt (*.json)\0*.json\0\0");
        if (path == null) return;
        try
        {
            var dto = ProjectStore.ReadFile(path);
            var (s, b) = ProjectStore.FromDto(dto);
            Confirm("Projekt importieren?", $"Das aktuelle Projekt wird durch „{Path.GetFileName(path)}“ ersetzt ({Fmt.Inputs(s.Inputs.Count)}, {Fmt.Outputs(s.Outputs.Count)}). Gain-Sperren der Datei werden übernommen – bitte danach prüfen.",
                "Importieren", () =>
                {
                    Model.LoadProject(s, b, dto.BlockFrames, dto.Exclusive, ProjectStore.AppLinksFromDto(dto));
                    Notify.Flash("Projekt importiert: " + Path.GetFileName(path));
                });
        }
        catch (Exception ex) { Notify.Flash("Import fehlgeschlagen: " + ex.Message, true); }
    }

    public void ShowProjectMenu(RectF anchor)
    {
        var items = new List<MenuItem>
        {
            new("Neues leeres Projekt", "Strg+N", NewProjectWithConfirm) { Icon = Icon.Document, Danger = true },
            MenuItem.Sep,
            new("Projekt exportieren …", "Strg+E", ExportProject) { Icon = Icon.Export },
            new("Projekt importieren …", "Strg+O", ImportProject) { Icon = Icon.Import },
        };
        PopupMenu.Show(this, anchor, items, 240);
    }
}
