using NxMix.Audio;
using Vortice.Mathematics;

namespace NxMix.App.Ui;

internal enum TitleHit { None, Drag, Tab, Menu, Min, Max, Close }

/// <summary>Eigene Titelleiste: Logo, Tab-Leiste, Projekt-Menue, Minimieren/Maximieren/Schliessen.</summary>
internal sealed class TitleBar : Widget
{
    const float BtnW = 48f;
    static readonly string[] s_tabKeys = { "Strg+1", "Strg+2", "Strg+3", "Strg+4" };

    TitleHit _hoverKind, _pressKind;
    int _hoverIdx = -1, _pressIdx = -1;
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
    float TabsX => 22 + 28 + Renderer.MeasureWidth("NX-MIX", Theme.Brand) + 30;

    RectF TabRect(int i)
    {
        var pages = Root!.Pages;
        float x = Bounds.X + TabsX;
        for (int k = 0; k < i; k++) x += Renderer.MeasureWidth(pages[k].Title, Theme.Tab) + 40;
        return new RectF(x, Bounds.Y, Renderer.MeasureWidth(pages[i].Title, Theme.Tab) + 40, Bounds.H);
    }

    RectF CloseRect => new(Bounds.Right - BtnW, Bounds.Y, BtnW, Bounds.H);
    RectF MaxRect => new(Bounds.Right - BtnW * 2, Bounds.Y, BtnW, Bounds.H);
    RectF MinRect => new(Bounds.Right - BtnW * 3, Bounds.Y, BtnW, Bounds.H);
    public RectF MenuRect
    {
        get
        {
            float w = Renderer.MeasureWidth("Projekt", Theme.Body) + 52;
            return new RectF(Bounds.Right - BtnW * 3 - 10 - w, Bounds.Y + 8, w, Bounds.H - 16);
        }
    }

    public (TitleHit Kind, int Index) HitAt(float x, float y)
    {
        if (y < Bounds.Y || y >= Bounds.Bottom || Root == null) return (TitleHit.None, -1);
        if (CloseRect.Contains(x, y)) return (TitleHit.Close, -1);
        if (MaxRect.Contains(x, y)) return (TitleHit.Max, -1);
        if (MinRect.Contains(x, y)) return (TitleHit.Min, -1);
        if (MenuRect.Contains(x, y)) return (TitleHit.Menu, -1);
        for (int i = 0; i < Root.Pages.Length; i++) if (TabRect(i).Contains(x, y)) return (TitleHit.Tab, i);
        return (TitleHit.Drag, -1);
    }

    // ---- Maus ----------------------------------------------------------------------------------
    public override void OnMouseMove(in MouseEvent e)
    {
        var (k, i) = HitAt(e.X, e.Y);
        if (k != _hoverKind || i != _hoverIdx) { _hoverKind = k; _hoverIdx = i; Invalidate(); }
    }

    public override void OnMouseLeave() { _hoverKind = TitleHit.None; _hoverIdx = -1; Invalidate(); }

    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button != 0) return;
        (_pressKind, _pressIdx) = HitAt(e.X, e.Y);
        Invalidate();
    }

    public override void OnMouseUp(in MouseEvent e)
    {
        var (k, i) = HitAt(e.X, e.Y);
        var pk = _pressKind; var pi = _pressIdx;
        _pressKind = TitleHit.None; _pressIdx = -1;
        Invalidate();
        if (k != pk || i != pi || Root == null) return;
        switch (k)
        {
            case TitleHit.Tab: Root.SwitchPage(i); break;
            case TitleHit.Menu: Root.ShowProjectMenu(MenuRect); break;
            case TitleHit.Min: Root.Shell.Minimize(); break;
            case TitleHit.Max: Root.Shell.ToggleMaximize(); break;
            case TitleHit.Close: Root.Shell.Close(); break;
        }
    }

    public override CursorKind CursorAt(float x, float y) =>
        HitAt(x, y).Kind is TitleHit.Tab or TitleHit.Menu or TitleHit.Min or TitleHit.Max or TitleHit.Close ? CursorKind.Hand : CursorKind.Arrow;

    public override string? TooltipAt(float x, float y)
    {
        var (k, i) = HitAt(x, y);
        return k switch
        {
            TitleHit.Tab => s_tabKeys[Math.Min(i, s_tabKeys.Length - 1)],
            TitleHit.Min => "Minimieren",
            TitleHit.Max => Root!.Shell.IsMaximized ? "Verkleinern" : "Maximieren",
            TitleHit.Close => "Schließen",
            TitleHit.Menu => "Projekt: neu, exportieren, importieren",
            _ => null,
        };
    }

    // ---- Zeichnen ------------------------------------------------------------------------------
    public override void Paint(Renderer g)
    {
        var b = Bounds;
        var root = Root!;
        g.Fill(b, Theme.Chrome);
        g.Fill(new RectF(b.X, b.Bottom - 1, b.W, 1), Theme.Border);

        // Logo + Wortmarke
        var logo = RectF.Centered(b.X + 22 + 13, b.CY, 26, 26);
        g.FillRound(logo, 7, Theme.Accent);
        float[] ys = { 0.55f, 0.30f, 0.65f };
        for (int k = 0; k < 3; k++)
        {
            float lx = logo.X + 7.5f + k * 5.5f;
            g.Line(lx, logo.Y + 6, lx, logo.Bottom - 6, Theme.OnAccent.With(0.55f), 1.6f, true);
            g.FillRound(RectF.Centered(lx, logo.Y + 6 + (logo.H - 12) * ys[k], 5, 3), 1.2f, Theme.OnAccent);
        }
        g.Text("NX-MIX", new RectF(b.X + 22 + 34, b.Y, 120, b.H), Theme.Brand, Theme.Text);

        // Tabs
        for (int i = 0; i < root.Pages.Length; i++)
        {
            var r = TabRect(i);
            bool active = i == root.PageIndex;
            bool hover = _hoverKind == TitleHit.Tab && _hoverIdx == i;
            if (hover && !active) g.FillRound(r.Deflate(4, 8), 7, Theme.CardHover);
            g.Text(root.Pages[i].Title, r, Theme.Tab, active ? Theme.Text : hover ? Theme.Text : Theme.TextDim, HAlign.Center);
            if (active)
            {
                g.FillRound(new RectF(r.X + 14, b.Bottom - 3, r.W - 28, 3), 1.5f, Theme.Accent);
            }
        }

        // Demo-Hinweis
        float right = MenuRect.X - 14;
        if (root.Model.EngineIsDemo)
        {
            string t = "DEMO-PEGEL";
            float w = Renderer.MeasureWidth(t, Theme.TinyBold) + 20;
            var chip = new RectF(right - w, b.CY - 10, w, 20);
            g.FillRound(chip, 10, Theme.Solo.With(0.14f));
            g.StrokeRound(chip, 10, Theme.Solo.With(0.45f));
            g.Text(t, chip, Theme.TinyBold, Theme.Solo, HAlign.Center);
        }

        // Projekt-Menue
        {
            var m = MenuRect;
            bool hot = _hoverKind == TitleHit.Menu || root.Modal is PopupMenu;
            g.FillRound(m, 7, hot ? Theme.CardHover : Theme.Card);
            g.StrokeRound(m, 7, Theme.Border);
            Icons.Hamburger(g, m.X + 18, m.CY, 12, Theme.TextDim);
            g.Text("Projekt", new RectF(m.X + 34, m.Y, m.W - 40, m.H), Theme.Body, Theme.Text);
        }

        // Fenster-Knoepfe
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
            var bg = kind == TitleHit.Close ? Theme.Rgb(0xE5484D, press ? 0.8f : 1f) : Theme.Rgb(0xFFFFFF, press ? 0.12f : 0.07f);
            g.Fill(r, bg);
        }
        var c = hover && kind == TitleHit.Close ? Theme.Rgb(0xFFFFFF) : hover ? Theme.Text : Theme.TextDim;
        float cx = r.CX, cy = r.CY;
        switch (kind)
        {
            case TitleHit.Min:
                g.Line(cx - 5, cy + 0.5f, cx + 5, cy + 0.5f, c, 1f);
                break;
            case TitleHit.Max:
                if (root.Shell.IsMaximized)
                {
                    g.StrokeRect(new RectF(cx - 4.5f, cy - 2.5f, 7, 7), c, 1f);
                    g.Line(cx - 2.5f, cy - 4.5f, cx + 5.5f, cy - 4.5f, c, 1f);
                    g.Line(cx + 5.5f, cy - 4.5f, cx + 5.5f, cy + 3.5f, c, 1f);
                }
                else g.StrokeRect(new RectF(cx - 5, cy - 5, 10, 10), c, 1f);
                break;
            case TitleHit.Close:
                g.Line(cx - 5, cy - 5, cx + 5, cy + 5, c, 1f);
                g.Line(cx - 5, cy + 5, cx + 5, cy - 5, c, 1f);
                break;
        }
    }
}

/// <summary>Statusleiste: Hinweis zum Element unter der Maus, Meldungen, Audio-Zustand, Limiter.</summary>
internal sealed class StatusBar : Widget
{
    string? _hint, _flash;
    bool _flashError;
    float _flashLeft;
    long _lastHits = -1;

    public void SetHint(string? h) { if (h != _hint) { _hint = h; Invalidate(); } }

    public void Flash(string message, bool error = false)
    {
        _flash = message; _flashError = error; _flashLeft = error ? 8f : 4.5f;
        Invalidate();
    }

    public void Tick(float dt)
    {
        if (_flash != null)
        {
            _flashLeft -= dt;
            if (_flashLeft <= 0) { _flash = null; Invalidate(); }
        }
        var root = Root;
        if (root == null) return;
        long hits = root.Model.Engine.LimiterHits;
        if (hits != _lastHits) { _lastHits = hits; Invalidate(); }
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        var root = Root!;
        var m = root.Model;
        g.Fill(b, Theme.Chrome);
        g.Fill(new RectF(b.X, b.Y, b.W, 1), Theme.Border);

        // rechts: Segmente von rechts nach links
        float x = b.Right - Theme.Pad;
        void Seg(string text, Color4 color, Color4? dot = null)
        {
            float w = Renderer.MeasureWidth(text, Theme.Small);
            float dw = dot != null ? 14 : 0;
            var r = new RectF(x - w - dw, b.Y, w + dw, b.H);
            if (dot != null) g.FillCircle(r.X + 4, b.CY, 3.5f, dot.Value);
            g.Text(text, new RectF(r.X + dw, r.Y, w + 2, r.H), Theme.Small, color, HAlign.Left, VAlign.Middle, false);
            x = r.X - 18;
        }
        Seg(m.EngineIsDemo ? "Demo-Engine" : "Engine bereit", Theme.TextFaint);
        Seg($"Limiter: {m.Engine.LimiterHits}", m.Engine.LimiterHits > 0 ? Theme.Solo : Theme.TextDim);
        Seg($"{m.Settings.SampleRate / 1000.0:0.0##} kHz", Theme.TextDim);
        var st = m.HostState;
        string host = st switch
        {
            HostState.Running => m.Stats != null ? $"Audio läuft · {m.Stats.OutputLatencyMs:0.0} ms" : "Audio läuft",
            HostState.Starting => "Audio startet …",
            HostState.Faulted => "Audio-Fehler",
            _ => "Audio gestoppt",
        };
        Seg(host, st == HostState.Running ? Theme.Text : st == HostState.Faulted ? Theme.Mute : Theme.TextDim,
            st == HostState.Running ? Theme.Rgb(0x3FD08A) : st == HostState.Faulted ? Theme.Mute : Theme.TextFaint);

        // links: Meldung > Hinweis > Zusammenfassung
        string left;
        Color4 lc = Theme.TextDim;
        if (_flash != null) { left = _flash; lc = _flashError ? Theme.Mute : Theme.Text; }
        else if (!string.IsNullOrEmpty(_hint)) left = _hint;
        else left = $"{Fmt.Inputs(m.InputCount)}  ·  {Fmt.Outputs(m.OutputCount)}  ·  {Fmt.Count(m.Bindings.Count, "Geräte-Zuordnung", "Geräte-Zuordnungen")}";
        g.Text(left, new RectF(b.X + Theme.Pad, b.Y, Math.Max(0, x - b.X - Theme.Pad), b.H), Theme.Small, lc);
    }
}
