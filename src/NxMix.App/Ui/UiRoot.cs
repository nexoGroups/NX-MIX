using NxMix.App.Model;
using NxMix.App.Pages;
using Vortice.Mathematics;

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
}

internal sealed class NullShell : IShell
{
    public string? SaveResult, OpenResult;
    public void Minimize() { }
    public void ToggleMaximize() { }
    public void Close() { }
    public bool IsMaximized => false;
    public void SetCapture(bool on) { }
    public string? ShowSaveDialog(string title, string filter, string defaultName) => SaveResult;
    public string? ShowOpenDialog(string title, string filter) => OpenResult;
    public WindowDto? WindowState => null;
}

/// <summary>Wurzel der Oberflaeche: Titelleiste, Seiten, Statusleiste, Ueberlagerungen, Eingabeverteilung.</summary>
internal sealed class UiRoot : Widget
{
    public AppModel Model { get; }
    public IShell Shell { get; }
    public override UiRoot? Root => this;

    public float W { get; private set; }
    public float H { get; private set; }
    public double Time { get; private set; }
    public bool Dirty { get; private set; } = true;

    public Widget? Hovered { get; private set; }
    public Widget? Captured { get; private set; }

    public readonly TitleBar Title;
    public readonly StatusBar Status;
    public readonly Page[] Pages;
    public readonly TextEditor Editor;
    public Overlay? Modal { get; private set; }
    public int PageIndex { get; private set; }

    float _mx = -1, _my = -1;
    string? _tipText;
    double _tipSince;
    bool _tipVisible;
    double _saveDue = -1;
    Renderer? _lastRenderer;

    public UiRoot(AppModel model, IShell shell)
    {
        Model = model;
        Shell = shell;
        Pages = new Page[] { new MixerPage(model), new MatrixPage(model), new DevicesPage(model), new SettingsPage(model) };
        foreach (var p in Pages) Add(p);
        Title = Add(new TitleBar());
        Status = Add(new StatusBar());
        Editor = Add(new TextEditor());
        model.Changed += () => _saveDue = Time + 0.8;
        SwitchPage(0);
    }

    public new void Invalidate() => Dirty = true;
    public bool ConsumeDirty() { bool d = Dirty; Dirty = false; return d; }

    // ---- Layout --------------------------------------------------------------------------------
    public void Resize(float w, float h)
    {
        W = Math.Max(1, w); H = Math.Max(1, h);
        Bounds = new RectF(0, 0, W, H);
        Title.Bounds = new RectF(0, 0, W, Theme.TitleBarHeight);
        Status.Bounds = new RectF(0, H - Theme.StatusBarHeight, W, Theme.StatusBarHeight);
        var content = new RectF(0, Theme.TitleBarHeight, W, Math.Max(0, H - Theme.TitleBarHeight - Theme.StatusBarHeight));
        foreach (var p in Pages) { p.Bounds = content; p.Layout(); }
        if (Modal != null) { Modal.Bounds = Bounds; Modal.Layout(); }
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

    // ---- Zeichnen ------------------------------------------------------------------------------
    public override void Paint(Renderer g)
    {
        _lastRenderer = g;
        g.Clear(Theme.Window);
        PaintChildren(g);
        Modal?.Paint(g);
        PaintTooltip(g);
    }

    void PaintTooltip(Renderer g)
    {
        if (!_tipVisible || string.IsNullOrEmpty(_tipText) || Modal != null) return;
        float tw = Renderer.MeasureWidth(_tipText, Theme.Small);
        float w = Math.Min(tw + 20, 360), h = 26;
        bool wrap = tw + 20 > 360;               // lange Texte (z. B. Fehlermeldungen) umbrechen statt abschneiden
        if (wrap) h = Renderer.TextHeight(_tipText, Theme.Small, w - 20) + 14;
        float x = Math.Clamp(_mx + 14, 4, W - w - 4);
        float y = _my + 22 + h > H - 4 ? _my - h - 8 : _my + 22;
        var r = new RectF(x, y, w, h);
        g.Shadow(r, 6, 6, 0.4f, 2);
        g.FillRound(r, 6, Theme.Rgb(0x2B2F37));
        g.StrokeRound(r, 6, Theme.BorderStrong);
        if (wrap) g.Text(_tipText, r.Deflate(10, 7), Theme.Small, Theme.Text, HAlign.Left, VAlign.Top, false, true);
        else g.Text(_tipText, r.Deflate(10, 0), Theme.Small, Theme.Text);
    }

    // ---- Takt ----------------------------------------------------------------------------------
    public void Tick(float dt)
    {
        Time += dt;
        if (Model.Tick(dt)) Dirty = true;
        CurrentPage.Tick(dt);
        Status.Tick(dt);
        if (Editor.Active && Editor.Tick(dt)) Dirty = true;
        if (Modal != null && Modal.Tick(dt)) Dirty = true;

        // Tooltip
        if (!_tipVisible && _tipText != null && Time - _tipSince > Theme.TooltipDelaySeconds && Captured == null && Modal == null)
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
        Status.SetHint(Hovered?.HintAt(_mx, _my));
    }

    public void MouseMove(float x, float y, bool ctrl, bool shift, bool alt)
    {
        _mx = x; _my = y;
        var e = Ev(x, y, 0, 1, ctrl, shift, alt);
        if (Modal != null) { Modal.OnMouseMove(e); SetHover(Modal); return; }
        if (Captured != null)
        {
            if (Captured.Root != null) Captured.OnMouseMove(e);
            return;
        }
        var hit = HitTest(x, y);
        SetHover(hit);
        UpdateTip();
        hit?.OnMouseMove(e);
        if (_tipVisible && Hovered != null) { _tipVisible = false; Dirty = true; }
    }

    public void MouseLeave()
    {
        if (Captured == null) { SetHover(null); UpdateTip(); }
    }

    public void MouseDown(float x, float y, int button, int clicks, bool ctrl, bool shift, bool alt)
    {
        _mx = x; _my = y;
        var e = Ev(x, y, button, clicks, ctrl, shift, alt);
        if (Modal != null) { Modal.OnMouseDown(e); return; }
        if (Editor.Active && !Editor.Bounds.Contains(x, y)) Editor.Commit();
        var hit = HitTest(x, y);
        SetHover(hit);
        _tipVisible = false; _tipText = null;
        if (hit != null)
        {
            Captured = hit;
            Shell.SetCapture(true);
            hit.OnMouseDown(e);
        }
        Dirty = true;
    }

    public void MouseUp(float x, float y, int button, bool ctrl, bool shift, bool alt)
    {
        _mx = x; _my = y;
        var e = Ev(x, y, button, 1, ctrl, shift, alt);
        if (Modal != null) { Modal.OnMouseUp(e); return; }
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
        if (Modal != null) { Modal.OnMouseWheel(e); return; }
        if (Captured != null) return;
        for (var w = HitTest(x, y); w != null; w = w.Parent)
            if (w.OnMouseWheel(e)) { Dirty = true; break; }
        _tipVisible = false; _tipSince = Time;
    }

    public CursorKind CurrentCursor
    {
        get
        {
            if (Modal != null) return Modal.CursorAt(_mx, _my);
            var w = Captured ?? Hovered;
            return w?.CursorAt(_mx, _my) ?? CursorKind.Arrow;
        }
    }

    // ---- Tastatur ------------------------------------------------------------------------------
    public bool KeyDown(int vk, bool ctrl, bool shift, bool alt)
    {
        if (Modal != null) { Modal.OnKeyDown(vk, ctrl, shift); Dirty = true; return true; }
        if (Editor.Active) { Editor.OnKeyDown(vk, ctrl, shift); Dirty = true; return true; }
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
        if (Modal != null) { Modal.OnChar(c); return; }
        if (Editor.Active) { Editor.OnChar(c); Dirty = true; }
    }

    // ---- Ueberlagerungen -----------------------------------------------------------------------
    public void ShowOverlay(Overlay o)
    {
        if (Editor.Active) Editor.Commit();
        CloseOverlay(Modal);
        Modal = o;
        o.Bounds = Bounds;
        // Overlay hat Root ueber Parent
        o.Parent = this;
        o.Layout();
        Captured = null;
        Shell.SetCapture(false);
        _tipText = null; _tipVisible = false;
        Dirty = true;
    }

    public void CloseOverlay(Overlay? o)
    {
        if (o == null || Modal != o) return;
        Modal = null;
        o.Parent = null;
        o.OnClosed();
        Dirty = true;
        SetHover(HitTest(_mx, _my));
    }

    /// <summary>Bearbeitet einen Text direkt an Ort und Stelle (z. B. Kanalname).</summary>
    public void BeginEdit(RectF bounds, string text, TextStyle style, Action<string> commit, int maxLen = 40,
        Func<char, bool>? filter = null, HAlign align = HAlign.Left)
    {
        Editor.Begin(bounds, text, style, commit, maxLen, filter, align);
        Dirty = true;
    }

    public void Confirm(string title, string message, string okText, Action onOk, bool danger = false, string cancelText = "Abbrechen")
    {
        ModalDialog.Show(this, title, message, new[] { cancelText, okText }, 1, danger ? 1 : -1, i => { if (i == 1) onOk(); });
    }

    // ---- Projekt-Aktionen ----------------------------------------------------------------------
    public void NewProjectWithConfirm()
    {
        Confirm("Neues leeres Projekt", "Alle Kanäle, Einstellungen und Geräte-Zuordnungen werden verworfen. Das aktuelle Projekt ist danach nur noch über einen vorherigen Export wiederherstellbar.",
            "Neu beginnen", () => { Model.NewProject(); Status.Flash("Neues leeres Projekt"); }, danger: true);
    }

    public void ExportProject()
    {
        var path = Shell.ShowSaveDialog("Projekt exportieren", "NX-MIX-Projekt (*.json)\0*.json\0\0", "NX-MIX-Projekt.json");
        if (path == null) return;
        try
        {
            ProjectStore.WriteFile(path, ProjectStore.ToDto(Model, Shell.WindowState));
            Status.Flash("Projekt exportiert: " + path);
        }
        catch (Exception ex) { Status.Flash("Export fehlgeschlagen: " + ex.Message, true); }
    }

    public void ImportProject()
    {
        var path = Shell.ShowOpenDialog("Projekt importieren", "NX-MIX-Projekt (*.json)\0*.json\0\0");
        if (path == null) return;
        try
        {
            var dto = ProjectStore.ReadFile(path);
            var (s, b) = ProjectStore.FromDto(dto);
            Confirm("Projekt importieren", $"Das aktuelle Projekt wird durch „{Path.GetFileName(path)}“ ersetzt ({Fmt.Inputs(s.Inputs.Count)}, {Fmt.Outputs(s.Outputs.Count)}). Gain-Sperren der Datei werden übernommen – bitte danach prüfen.",
                "Importieren", () =>
                {
                    Model.LoadProject(s, b, dto.BlockFrames, dto.Exclusive, ProjectStore.AppLinksFromDto(dto));
                    Status.Flash("Projekt importiert: " + Path.GetFileName(path));
                });
        }
        catch (Exception ex) { Status.Flash("Import fehlgeschlagen: " + ex.Message, true); }
    }

    public void ShowProjectMenu(RectF anchor)
    {
        var items = new List<MenuItem>
        {
            new("Neues leeres Projekt", "Strg+N", NewProjectWithConfirm),
            MenuItem.Sep,
            new("Projekt exportieren …", "Strg+E", ExportProject),
            new("Projekt importieren …", "Strg+O", ImportProject),
        };
        PopupMenu.Show(this, anchor, items);
    }
}
