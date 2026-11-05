using NxMix.Audio;
using Vortice.Mathematics;

namespace NxMix.App.Ui;

internal enum TitleHit { None, Drag, Nav, Collapse, Update, Audio, Menu, Min, Max, Close }

/// <summary>Seitenleiste wie im UniFi-Webinterface: Logo, Seiten als Symbole (ausgeklappt mit Text), unten Einstellungen und Einklappen.</summary>
internal sealed class Sidebar : Widget
{
    static readonly string[] s_keys = { "Strg+1", "Strg+2", "Strg+3", "Strg+4" };
    int _hot = -1;
    bool _hotCollapse;

    public bool Expanded { get; set; }
    public float Width => Expanded ? Theme.SidebarExpandedW : Theme.SidebarCollapsedW;
    public override bool AcceptsMouse => true;

    public RectF LogoRect => new(Bounds.X, Bounds.Y, Bounds.W, Theme.TopBarH);

    /// <summary>Seite 0..2 oben, Einstellungen (3) unten ueber dem Einklapp-Knopf.</summary>
    public RectF ItemRect(int i)
    {
        float h = Theme.NavItemH, x = Bounds.X + 10, w = Bounds.W - 20;
        if (i < 3) return new RectF(x, Bounds.Y + Theme.TopBarH + 8 + i * (h + 4), w, h);
        return new RectF(x, CollapseRect.Y - 8 - h, w, h);
    }

    public RectF CollapseRect => new(Bounds.X + 10, Bounds.Bottom - 12 - Theme.NavItemH, Bounds.W - 20, Theme.NavItemH);

    int ItemAt(float x, float y)
    {
        var root = Root; if (root == null) return -1;
        for (int i = 0; i < root.Pages.Length; i++) if (ItemRect(i).Contains(x, y)) return i;
        return -1;
    }

    public TitleHit HitKind(float x, float y)
    {
        if (!Bounds.Contains(x, y)) return TitleHit.None;
        if (ItemAt(x, y) >= 0) return TitleHit.Nav;
        if (CollapseRect.Contains(x, y)) return TitleHit.Collapse;
        if (LogoRect.Contains(x, y)) return TitleHit.Drag;
        return TitleHit.None;
    }

    public override CursorKind CursorAt(float x, float y) => HitKind(x, y) is TitleHit.Nav or TitleHit.Collapse ? CursorKind.Hand : CursorKind.Arrow;

    public override void OnMouseMove(in MouseEvent e)
    {
        int i = ItemAt(e.X, e.Y); bool c = CollapseRect.Contains(e.X, e.Y);
        if (i != _hot || c != _hotCollapse) { _hot = i; _hotCollapse = c; Invalidate(); }
    }

    public override void OnMouseLeave() { _hot = -1; _hotCollapse = false; Invalidate(); }

    public override void OnMouseUp(in MouseEvent e)
    {
        if (e.Button != 0 || Root == null) return;
        int i = ItemAt(e.X, e.Y);
        if (i >= 0) Root.SwitchPage(i);
        else if (CollapseRect.Contains(e.X, e.Y)) Root.SetSidebarExpanded(!Expanded);
    }

    public override string? TooltipAt(float x, float y)
    {
        int i = ItemAt(x, y);
        if (i >= 0) return Expanded ? s_keys[i] : $"{Root!.Pages[i].Title}  ·  {s_keys[i]}";
        if (CollapseRect.Contains(x, y)) return Expanded ? "Seitenleiste einklappen" : "Seitenleiste ausklappen";
        return null;
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        var root = Root!;
        g.Fill(b, Theme.Chrome);
        g.Fill(new RectF(b.Right - 1, b.Y, 1, b.H), Theme.Separator);

        // Logo: abgerundetes Quadrat im Akzent mit drei Fadern, daneben die Wortmarke
        var logo = RectF.Centered(b.X + Theme.SidebarCollapsedW / 2, b.Y + Theme.TopBarH / 2, 34, 34);
        g.FillRound(logo, 10, Theme.Accent);
        float[] ks = { 0.62f, 0.32f, 0.70f };
        for (int k = 0; k < 3; k++)
        {
            float lx = logo.X + 10.5f + k * 6.5f;
            g.Line(lx, logo.Y + 8, lx, logo.Bottom - 8, Theme.White.With(0.45f), 1.6f, true);
            g.FillRound(RectF.Centered(lx, logo.Y + 8 + (logo.H - 16) * ks[k], 5.5f, 3.5f), 1.5f, Theme.White);
        }
        if (Expanded)
            g.Text("NX-MIX", new RectF(logo.Right + 12, logo.Y, 140, logo.H), new TextStyle(16f, Vortice.DirectWrite.FontWeight.Bold), Theme.Text);

        for (int i = 0; i < root.Pages.Length; i++) PaintItem(g, ItemRect(i), root.Pages[i].Icon, root.Pages[i].Title, i == root.PageIndex, i == _hot);
        PaintItem(g, CollapseRect, Icon.Sidebar, "Einklappen", false, _hotCollapse, secondary: true);
    }

    void PaintItem(Renderer g, RectF r, Icon icon, string label, bool active, bool hot, bool secondary = false)
    {
        if (active) g.FillRound(r, Theme.RadiusControl, Theme.AccentSoft);
        else if (hot) g.FillRound(r, Theme.RadiusControl, Theme.ControlHover.With(Theme.IsDark ? 0.8f : 1f));
        var ic = active ? Theme.AccentText : hot ? Theme.Text : secondary ? Theme.TextTertiary : Theme.TextSecondary;
        float icx = Expanded ? r.X + 22 : r.CX;
        Icons.Draw(g, icon, icx, r.CY, 20, ic);
        if (Expanded)
            g.Text(label, new RectF(r.X + 44, r.Y, r.W - 50, r.H), active ? Theme.BodyStrong : Theme.BodyMedium,
                active ? Theme.Text : hot ? Theme.Text : Theme.TextSecondary, HAlign.Left, VAlign.Middle, true);
    }
}

/// <summary>Kopfleiste: Seitentitel, Audio-Status (Klick: Seite Geraete), Projekt-Menue, Fensterknoepfe.</summary>
internal sealed class TopBar : Widget
{
    const float BtnW = 46f;
    TitleHit _hoverKind, _pressKind;
    /// <summary>Hover/Druck des Maximieren-Knopfs, wenn er als Nicht-Client-Bereich gemeldet wird (Snap-Layouts).</summary>
    public TitleHit NcHover { get; private set; }
    public bool NcPressed { get; private set; }

    public override bool AcceptsMouse => true;

    public void SetNcHover(TitleHit h, bool pressed = false)
    {
        if (h == NcHover && pressed == NcPressed) return;
        NcHover = h; NcPressed = pressed;
        Invalidate();
    }

    // ---- Geometrie -----------------------------------------------------------------------------
    RectF CloseRect => new(Bounds.Right - BtnW, Bounds.Y, BtnW, 40);
    RectF MaxRect => new(Bounds.Right - BtnW * 2, Bounds.Y, BtnW, 40);
    RectF MinRect => new(Bounds.Right - BtnW * 3, Bounds.Y, BtnW, 40);
    float ButtonH => Theme.Touch ? 38 : 32;

    public RectF MenuRect
    {
        get
        {
            float w = Renderer.MeasureWidth("Projekt", Theme.BodyMedium) + 62;
            return new RectF(MinRect.X - 14 - w, Bounds.CY - ButtonH / 2, w, ButtonH);
        }
    }

    string AudioText(out Color4 color)
    {
        var m = Root!.Model;
        string sr = $"{m.Settings.SampleRate / 1000.0:0.#} kHz";
        switch (m.HostState)
        {
            case HostState.Running:
                color = Theme.Green;
                return m.Stats != null ? $"Audio läuft  ·  {sr}  ·  {m.Stats.OutputLatencyMs:0.0} ms" : $"Audio läuft  ·  {sr}";
            case HostState.Starting: color = Theme.Orange; return "Audio startet …";
            case HostState.Faulted: color = Theme.Red; return "Audio-Fehler";
            default: color = Theme.TextTertiary; return (m.HostBusy ? "Audio stoppt …" : "Audio gestoppt") + $"  ·  {sr}";
        }
    }

    public RectF AudioRect
    {
        get
        {
            string t = AudioText(out _);
            float w = Renderer.MeasureWidth(t, Theme.CalloutMedium) + 40;
            return new RectF(MenuRect.X - 10 - w, Bounds.CY - ButtonH / 2, w, ButtonH);
        }
    }

    /// <summary>Text der Update-Pille oder null, wenn nichts anzuzeigen ist.</summary>
    string? UpdateText()
    {
        var u = Root?.Updater;
        if (u == null) return null;
        return u.Stage switch
        {
            Model.UpdateStage.Available => $"Update {u.Offer?.Version}",
            Model.UpdateStage.Downloading => $"Update lädt … {u.Progress} %",
            Model.UpdateStage.Ready => u.InstallOnExitPending ? "Update beim Beenden" : "Update bereit",
            _ => null,
        };
    }

    public RectF UpdateRect
    {
        get
        {
            string? t = UpdateText();
            if (t == null) return default;
            float w = Renderer.MeasureWidth(t, Theme.CalloutStrong) + 44;
            return new RectF(AudioRect.X - 8 - w, Bounds.CY - ButtonH / 2, w, ButtonH);
        }
    }

    public (TitleHit Kind, int Index) HitAt(float x, float y)
    {
        if (!Bounds.Contains(x, y) || Root == null) return (TitleHit.None, -1);
        if (CloseRect.Contains(x, y)) return (TitleHit.Close, -1);
        if (MaxRect.Contains(x, y)) return (TitleHit.Max, -1);
        if (MinRect.Contains(x, y)) return (TitleHit.Min, -1);
        if (MenuRect.Contains(x, y)) return (TitleHit.Menu, -1);
        if (AudioRect.Contains(x, y)) return (TitleHit.Audio, -1);
        if (UpdateRect.Contains(x, y)) return (TitleHit.Update, -1);
        return (TitleHit.Drag, -1);
    }

    // ---- Maus ----------------------------------------------------------------------------------
    public override void OnMouseMove(in MouseEvent e)
    {
        var (k, _) = HitAt(e.X, e.Y);
        if (k != _hoverKind) { _hoverKind = k; Invalidate(); }
    }

    public override void OnMouseLeave() { _hoverKind = TitleHit.None; Invalidate(); }

    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button != 0) return;
        (_pressKind, _) = HitAt(e.X, e.Y);
        Invalidate();
    }

    public override void OnMouseUp(in MouseEvent e)
    {
        var (k, _) = HitAt(e.X, e.Y);
        var pk = _pressKind;
        _pressKind = TitleHit.None;
        Invalidate();
        if (k != pk || Root == null) return;
        switch (k)
        {
            case TitleHit.Menu: Root.ShowProjectMenu(MenuRect); break;
            case TitleHit.Audio: Root.SwitchPage(2); break;
            case TitleHit.Update: Root.ShowUpdate(); break;
            case TitleHit.Min: Root.Shell.Minimize(); break;
            case TitleHit.Max: Root.Shell.ToggleMaximize(); break;
            case TitleHit.Close: Root.Shell.Close(); break;
        }
    }

    public override CursorKind CursorAt(float x, float y) =>
        HitAt(x, y).Kind is TitleHit.Menu or TitleHit.Audio or TitleHit.Update or TitleHit.Min or TitleHit.Max or TitleHit.Close ? CursorKind.Hand : CursorKind.Arrow;

    public override string? TooltipAt(float x, float y)
    {
        var (k, _) = HitAt(x, y);
        return k switch
        {
            TitleHit.Min => "Minimieren",
            TitleHit.Max => Root!.Shell.IsMaximized ? "Verkleinern" : "Maximieren",
            TitleHit.Close => "Schließen",
            TitleHit.Menu => "Projekt: neu, exportieren, importieren",
            TitleHit.Audio => "Audio-Engine: starten, stoppen, Geräte (Strg+3)",
            TitleHit.Update => "Neue Version von NX-MIX: Klick zeigt, was neu ist, und lädt sie auf Wunsch",
            _ => null,
        };
    }

    // ---- Zeichnen ------------------------------------------------------------------------------
    public override void Paint(Renderer g)
    {
        var b = Bounds;
        var root = Root!;
        var page = root.CurrentPage;

        // Titel + Untertitel
        float tx = b.X + Theme.Pad;
        float tw = Renderer.MeasureWidth(page.Title, Theme.LargeTitle);
        g.Text(page.Title, new RectF(tx, b.Y, tw + 4, b.H), Theme.LargeTitle, Theme.Text, HAlign.Left, VAlign.Middle, false);
        float right = AudioRect.X - 12;

        // Update-Pille (Akzent), nur wenn es etwas gibt
        var ur = UpdateRect;
        if (!ur.IsEmpty)
        {
            bool hot = _hoverKind == TitleHit.Update;
            g.FillPill(ur, hot ? Theme.AccentHover : Theme.Accent);
            Icons.Draw(g, Icon.Import, ur.X + 18, ur.CY, 15, Theme.OnAccent);
            g.Text(UpdateText(), new RectF(ur.X + 32, ur.Y, ur.W - 36, ur.H), Theme.CalloutStrong, Theme.OnAccent, HAlign.Left, VAlign.Middle, false);
            right = ur.X - 12;
        }

        // Hinweis-Chips (Demo, Limiter)
        var m = root.Model;
        if (m.Engine.LimiterHits > 0)
        {
            string t = $"Limiter {m.Engine.LimiterHits}";
            right -= Draw.Chip(g, right, b.CY, t, Theme.Orange, true, null, alignRight: true) + 8;
        }
        if (m.EngineIsDemo)
            right -= Draw.Chip(g, right, b.CY, "Demo-Pegel", Theme.Orange, false, null, alignRight: true) + 8;

        string? sub = page.Subtitle;
        if (!string.IsNullOrEmpty(sub))
        {
            float sx = tx + tw + 14;
            g.Text(sub, new RectF(sx, b.Y + 2, Math.Max(0, right - sx - 8), b.H), Theme.Callout, Theme.TextSecondary, HAlign.Left, VAlign.Middle, true);
        }

        // Audio-Status als Pille
        {
            var r = AudioRect;
            string t = AudioText(out var c);
            bool hot = _hoverKind == TitleHit.Audio;
            bool running = m.HostState == HostState.Running;
            var bg = running ? Theme.Soft(Theme.Green) : m.HostState == HostState.Faulted ? Theme.Soft(Theme.Red) : Theme.Control;
            if (hot) bg = running ? Theme.Green.With(Theme.IsDark ? 0.26f : 0.2f) : Theme.ControlHover;
            g.FillPill(r, bg);
            Icons.StatusDot(g, r.X + 16, r.CY, c, 3.5f, running);
            g.Text(t, new RectF(r.X + 28, r.Y, r.W - 32, r.H), Theme.CalloutMedium, running ? Theme.Text : Theme.TextSecondary, HAlign.Left, VAlign.Middle, false);
        }

        // Projekt-Menue
        {
            var r = MenuRect;
            bool hot = _hoverKind == TitleHit.Menu || root.Modal is PopupMenu;
            g.FillRound(r, Theme.RadiusControl, hot ? Theme.ControlHover : Theme.Control);
            Icons.Draw(g, Icon.Document, r.X + 18, r.CY, 16, Theme.TextSecondary);
            g.Text("Projekt", new RectF(r.X + 32, r.Y, r.W - 50, r.H), Theme.BodyMedium, Theme.Text);
            Icons.Draw(g, Icon.ChevronDown, r.Right - 16, r.CY + 0.5f, 13, Theme.TextSecondary);
        }

        PaintCaptionButton(g, MinRect, TitleHit.Min, root);
        PaintCaptionButton(g, MaxRect, TitleHit.Max, root);
        PaintCaptionButton(g, CloseRect, TitleHit.Close, root);
    }

    void PaintCaptionButton(Renderer g, RectF r, TitleHit kind, UiRoot root)
    {
        bool hover = _hoverKind == kind || NcHover == kind;
        bool press = (_pressKind == kind) || (NcPressed && NcHover == kind);
        if (hover)
        {
            var bg = kind == TitleHit.Close ? Theme.Rgb(0xE81123, press ? 0.8f : 1f) : Theme.Text.With(press ? 0.12f : 0.07f);
            g.Fill(r, bg);
        }
        var c = hover && kind == TitleHit.Close ? Theme.White : hover ? Theme.Text : Theme.TextSecondary;
        float cx = MathF.Round(r.CX) + 0.5f, cy = MathF.Round(r.CY) + 0.5f;
        switch (kind)
        {
            case TitleHit.Min:
                g.Line(cx - 5, cy, cx + 5, cy, c, 1f);
                break;
            case TitleHit.Max:
                if (root.Shell.IsMaximized)
                {
                    g.StrokeRound(new RectF(cx - 5, cy - 3, 8, 8), 1.5f, c, 1f);
                    g.Line(cx - 3, cy - 5, cx + 4, cy - 5, c, 1f);
                    g.Line(cx + 5, cy - 4, cx + 5, cy + 3, c, 1f);
                }
                else g.StrokeRound(new RectF(cx - 5, cy - 5, 10, 10), 1.5f, c, 1f);
                break;
            case TitleHit.Close:
                g.Line(cx - 5, cy - 5, cx + 5, cy + 5, c, 1f);
                g.Line(cx - 5, cy + 5, cx + 5, cy - 5, c, 1f);
                break;
        }
    }
}

/// <summary>Kurzmeldungen unten mittig (Apple-Toast) statt einer Statusleiste.</summary>
internal sealed class Toasts : Widget
{
    sealed class Item { public string Text = ""; public bool Error; public float Left, Age; }
    readonly List<Item> _items = new();

    /// <summary>Letzte Meldung (fuer Selbsttest).</summary>
    public string? Last { get; private set; }
    public bool LastWasError { get; private set; }

    public void Flash(string message, bool error = false)
    {
        Last = message; LastWasError = error;
        _items.RemoveAll(i => i.Text == message);
        _items.Add(new Item { Text = message, Error = error, Left = error ? 8f : 4.5f });
        while (_items.Count > 3) _items.RemoveAt(0);
        Invalidate();
    }

    public bool Tick(float dt)
    {
        if (_items.Count == 0) return false;
        foreach (var i in _items) { i.Left -= dt; i.Age += dt; }
        _items.RemoveAll(i => i.Left <= 0);
        return true;
    }

    public override void Paint(Renderer g)
    {
        var area = Bounds;
        float y = area.Bottom - 24;
        for (int k = _items.Count - 1; k >= 0; k--)
        {
            var it = _items[k];
            float a = Math.Clamp(Math.Min(it.Age / 0.15f, it.Left / 0.35f), 0, 1);
            float maxW = Math.Min(640, area.W - 40);
            float tw = Renderer.MeasureWidth(it.Text, Theme.BodyMedium);
            bool wrap = tw + 64 > maxW;
            float w = Math.Min(maxW, tw + 64);
            float h = wrap ? Renderer.TextHeight(it.Text, Theme.BodyMedium, w - 64) + 22 : 42;
            var r = new RectF(area.CX - w / 2, y - h + (1 - a) * 8, w, h);
            g.Shadow(r, 14, 18, 1.4f * a, 6);
            g.FillRound(r, 14, Theme.Elevated.With(a));
            g.StrokeRound(r, 14, (Theme.IsDark ? Theme.BorderStrong : Theme.Border).With(a));
            var c = it.Error ? Theme.Red : Theme.Green;
            g.FillCircle(r.X + 22, r.Y + 21, 11, Theme.Soft(c).With(Theme.Soft(c).A * a));
            Icons.Draw(g, it.Error ? Icon.Warn : Icon.Check, r.X + 22, r.Y + 21, 14, c.With(a));
            g.Text(it.Text, new RectF(r.X + 42, r.Y + (wrap ? 11 : 0), r.W - 58, wrap ? h - 22 : h), Theme.BodyMedium, Theme.Text.With(a),
                HAlign.Left, wrap ? VAlign.Top : VAlign.Middle, false, wrap);
            y = r.Y - 8;
        }
    }
}
