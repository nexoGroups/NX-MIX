using NxMix.Audio;
using Vortice.Mathematics;

namespace NxMix.App.Ui;

internal enum TitleHit { None, Drag, Nav, Collapse, Update, Audio, Snapshots, PanicMute, Min, Max, Close }

/// <summary>Seitenleiste der Live-Konsole mit 8 Bereichen (Strg+1 .. Strg+8):
/// Mixer, Decks, Geräte, Patching Matrix, Virtual I/O, Outputs / DSP, Snapshots und unten Engine.</summary>
internal sealed class Sidebar : Widget
{
    static readonly string[] s_keys = { "Strg+1", "Strg+2", "Strg+3", "Strg+4", "Strg+5", "Strg+6", "Strg+7", "Strg+8" };
    int _hot = -1;
    bool _hotCollapse;
    int _pressed = -1;
    Motion _expansion;
    readonly Motion[] _navHover = new Motion[9];
    readonly Motion[] _navActive = new Motion[8];

    public bool Expanded { get; set; }
    public float Width { get; private set; }
    float _labelOpacity;

    public bool AdvanceWidth()
    {
        float amount = _expansion.At(Root, Expanded ? 1 : 0, Motion.Reveal);
        float next = Theme.SidebarCollapsedW + (Theme.SidebarExpandedW - Theme.SidebarCollapsedW) * amount;
        _labelOpacity = Math.Clamp((amount - 0.25f) / 0.75f, 0, 1);
        if (Width == next) return false;
        Width = next;
        return true;
    }

    public override bool AcceptsMouse => true;
    public RectF LogoRect => new(Bounds.X, Bounds.Y, Bounds.W, Theme.TopBarH);

    (float ItemH, float Gap) Metrics()
    {
        int n = Root?.Pages.Length ?? 8;
        float topY = Bounds.Y + Theme.TopBarH + 6;
        float botY = Bounds.Bottom - 10;
        float avail = Math.Max(120, botY - topY);
        // n Nav-Einträge + 1 Einklappen-Knopf + Abstände
        float desiredH = Theme.NavItemH;
        float gap = 3f;
        float needed = (n + 1) * desiredH + (n + 2) * gap;
        if (needed > avail)
        {
            gap = 2f;
            desiredH = Math.Clamp((avail - (n + 2) * gap) / (n + 1), 24f, Theme.NavItemH);
        }
        return (desiredH, gap);
    }

    public RectF CollapseRect
    {
        get
        {
            var (h, _) = Metrics();
            return new RectF(Bounds.X + 10, Bounds.Bottom - 10 - h, Bounds.W - 20, h);
        }
    }

    public RectF ItemRect(int i)
    {
        int n = Root?.Pages.Length ?? 8;
        var (h, gap) = Metrics();
        float x = Bounds.X + 10, w = Bounds.W - 20;
        if (i == n - 1)
            return new RectF(x, CollapseRect.Y - gap * 2 - h, w, h);
        return new RectF(x, Bounds.Y + Theme.TopBarH + 6 + i * (h + gap), w, h);
    }

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

    int ControlAt(float x, float y) => CollapseRect.Contains(x, y) ? 99 : ItemAt(x, y);

    public override void OnMouseDown(in MouseEvent e) => _pressed = e.Button == 0 ? ControlAt(e.X, e.Y) : -1;

    public override void OnMouseUp(in MouseEvent e)
    {
        int i = ControlAt(e.X, e.Y);
        int pressed = _pressed;
        _pressed = -1;
        if (e.Button != 0 || Root == null || i < 0 || i != pressed) return;
        if (i == 99) Root.SetSidebarExpanded(!Expanded);
        else Root.SwitchPage(i);
    }

    public override string? TooltipAt(float x, float y)
    {
        int i = ItemAt(x, y);
        if (i >= 0 && i < s_keys.Length) return Expanded ? s_keys[i] : $"{Root!.Pages[i].Title}  ·  {s_keys[i]}";
        if (CollapseRect.Contains(x, y)) return Expanded ? "Seitenleiste einklappen" : "Seitenleiste ausklappen";
        return null;
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        var root = Root!;
        g.Fill(b, Theme.Chrome);
        g.Fill(new RectF(b.Right - 1, b.Y, 1, b.H), Theme.Separator);

        var logo = RectF.Centered(b.X + Theme.SidebarCollapsedW / 2, b.Y + Theme.TopBarH / 2, 34, 34);
        g.FillRound(logo, 10, Theme.Accent);
        float[] ks = { 0.62f, 0.32f, 0.70f };
        for (int k = 0; k < 3; k++)
        {
            float lx = logo.X + 10.5f + k * 6.5f;
            g.Line(lx, logo.Y + 8, lx, logo.Bottom - 8, Theme.White.With(0.45f), 1.6f, true);
            g.FillRound(RectF.Centered(lx, logo.Y + 8 + (logo.H - 16) * ks[k], 5.5f, 3.5f), 1.5f, Theme.White);
        }
        bool isBeta = root.Updater.IsCurrentBetaBuild;
        if (isBeta && _labelOpacity <= 0.1f)
            g.FillCircle(logo.Right - 3, logo.Y + 3, 4.5f, Theme.Orange);

        if (_labelOpacity > 0)
        {
            g.Text("NX-MIX", new RectF(logo.Right + 12, logo.Y, b.Right - logo.Right - 22, logo.H),
                new TextStyle(16f, Vortice.DirectWrite.FontWeight.Bold), Theme.Text.With(_labelOpacity));
            if (isBeta && _labelOpacity > 0.65f && b.W >= 188f)
                Draw.Chip(g, b.Right - 12, logo.CY, "BETA", Theme.Orange, dot: false, Theme.MicroStrong, alignRight: true);
        }

        for (int i = 0; i < root.Pages.Length; i++)
        {
            float active = _navActive[i].At(root, i == root.PageIndex ? 1 : 0, Motion.Selection);
            float hov = _navHover[i].At(root, i == _hot ? 1 : 0, Motion.Feedback);
            PaintItem(g, ItemRect(i), root.Pages[i].Icon, root.Pages[i].Title, active, hov);
        }
        PaintItem(g, CollapseRect, Icon.Sidebar, "Einklappen", 0, _navHover[8].At(root, _hotCollapse ? 1 : 0, Motion.Feedback), secondary: true);
    }

    void PaintItem(Renderer g, RectF r, Icon icon, string label, float active, float hot, bool secondary = false)
    {
        var bg = Theme.ControlHover.With(hot * (Theme.IsDark ? 0.8f : 1f));
        g.FillRound(r, Theme.RadiusControl, Theme.Mix(bg, Theme.AccentSoft, active));
        if (active > 0) g.FillPill(new RectF(r.X, r.CY - Math.Min(9, r.H * 0.35f), 3, Math.Min(18, r.H * 0.7f)), Theme.AccentText.With(active));
        var ic = Theme.Mix(Theme.Mix(secondary ? Theme.TextTertiary : Theme.TextSecondary, Theme.Text, hot), Theme.AccentText, active);
        float icx = r.X + (Theme.SidebarCollapsedW - 20) / 2;
        Icons.Draw(g, icon, icx, r.CY, Math.Min(20f, r.H - 6), ic);
        if (_labelOpacity > 0)
            g.Text(label, new RectF(r.X + 44, r.Y, r.W - 50, r.H), Theme.BodyMedium,
                Theme.Mix(Theme.TextSecondary, Theme.Text, Math.Max(active, hot)).With(_labelOpacity), HAlign.Left, VAlign.Middle, true);
    }
}

/// <summary>Kopfleiste der Live-Konsole: Seitentitel, Engine/Audio-Status-Pille, Snapshot-Schnellwahl, Panic-Mute und Fensterknöpfe.</summary>
internal sealed class TopBar : Widget
{
    const float BtnW = 46f;
    TitleHit _hoverKind, _pressKind;
    public TitleHit NcHover { get; private set; }
    public bool NcPressed { get; private set; }

    public override bool AcceptsMouse => true;

    public void SetNcHover(TitleHit h, bool pressed = false)
    {
        if (h == NcHover && pressed == NcPressed) return;
        NcHover = h; NcPressed = pressed;
        Invalidate();
    }

    RectF CloseRect => new(Bounds.Right - BtnW, Bounds.Y, BtnW, 40);
    RectF MaxRect => new(Bounds.Right - BtnW * 2, Bounds.Y, BtnW, 40);
    RectF MinRect => new(Bounds.Right - BtnW * 3, Bounds.Y, BtnW, 40);
    float ButtonH => Theme.Touch ? 38 : 32;

    string PanicText => Root?.Model.PanicMuted == true ? "MUTE AKTIV" : "Panic";

    public RectF PanicRect
    {
        get
        {
            float w = Renderer.MeasureWidth(PanicText, Theme.CalloutStrong) + 42;
            return new RectF(MinRect.X - 12 - w, Bounds.CY - ButtonH / 2, w, ButtonH);
        }
    }

    string SnapshotText()
    {
        var m = Root?.Model;
        if (m == null) return "Snapshots";
        if (m.ActiveSnapshotId is { } id && m.Snapshots.FirstOrDefault(s => s.Id == id) is { } active)
            return active.Name.Length > 18 ? active.Name[..17] + "…" : active.Name;
        return "Snapshots";
    }

    public RectF SnapshotRect
    {
        get
        {
            string t = SnapshotText();
            float w = Renderer.MeasureWidth(t, Theme.BodyMedium) + 56;
            float x = PanicRect.X - 8 - w;
            float titleEnd = Bounds.X + Theme.Pad + Renderer.MeasureWidth(Root!.CurrentPage.Title, Theme.LargeTitle);
            return x < titleEnd + 14 ? default : new RectF(x, Bounds.CY - ButtonH / 2, w, ButtonH);
        }
    }

    string AudioText(out Color4 color)
    {
        var m = Root!.Model;
        if (!m.IsConnected)
        {
            color = Theme.Orange;
            return "Verbinde mit Engine …";
        }
        string sr = $"{m.Settings.SampleRate / 1000.0:0.#} kHz";
        switch (m.HostState)
        {
            case HostState.Running:
                color = Theme.Green;
                return m.Stats.OutputLatencyMs > 0
                    ? $"Engine aktiv  ·  {sr}  ·  {m.Stats.OutputLatencyMs:0.0} ms"
                    : $"Engine aktiv  ·  {sr}";
            case HostState.Starting:
                color = Theme.Orange;
                return "Audio startet …";
            case HostState.Faulted:
                color = Theme.Red;
                return "Audio-Fehler";
            default:
                color = Theme.AccentText;
                return $"Engine bereit  ·  {sr}";
        }
    }

    public RectF AudioRect
    {
        get
        {
            string t = AudioText(out _);
            float w = Renderer.MeasureWidth(t, Theme.CalloutMedium) + 38;
            var snapR = SnapshotRect;
            float anchorX = snapR.IsEmpty ? PanicRect.X : snapR.X;
            float x = anchorX - 8 - w;
            float titleEnd = Bounds.X + Theme.Pad + Renderer.MeasureWidth(Root!.CurrentPage.Title, Theme.LargeTitle);
            return x < titleEnd + 14 ? default : new RectF(x, Bounds.CY - ButtonH / 2, w, ButtonH);
        }
    }

    string? UpdateText()
    {
        var u = Root?.Updater;
        if (u == null) return null;
        return u.Stage switch
        {
            Model.UpdateStage.Available => u.Offer?.IsDowngrade == true
                ? $"Stable {u.Offer.Version}"
                : $"Update {u.Offer?.Version}",
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
            float w = Renderer.MeasureWidth(t, Theme.CalloutStrong) + 42;
            var audio = AudioRect;
            var snap = SnapshotRect;
            float rightAnchor = !audio.IsEmpty ? audio.X : !snap.IsEmpty ? snap.X : PanicRect.X;
            float titleEnd = Bounds.X + Theme.Pad + Renderer.MeasureWidth(Root!.CurrentPage.Title, Theme.LargeTitle);
            float x = rightAnchor - 8 - w;
            return x < titleEnd + 14 ? default : new RectF(x, Bounds.CY - ButtonH / 2, w, ButtonH);
        }
    }

    public (TitleHit Kind, int Index) HitAt(float x, float y)
    {
        if (!Bounds.Contains(x, y) || Root == null) return (TitleHit.None, -1);
        if (CloseRect.Contains(x, y)) return (TitleHit.Close, -1);
        if (MaxRect.Contains(x, y)) return (TitleHit.Max, -1);
        if (MinRect.Contains(x, y)) return (TitleHit.Min, -1);
        if (PanicRect.Contains(x, y)) return (TitleHit.PanicMute, -1);
        if (!SnapshotRect.IsEmpty && SnapshotRect.Contains(x, y)) return (TitleHit.Snapshots, -1);
        if (!AudioRect.IsEmpty && AudioRect.Contains(x, y)) return (TitleHit.Audio, -1);
        if (!UpdateRect.IsEmpty && UpdateRect.Contains(x, y)) return (TitleHit.Update, -1);
        return (TitleHit.Drag, -1);
    }

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
        if (e.Button != 0 || k != pk || Root == null) return;
        switch (k)
        {
            case TitleHit.PanicMute:
                Root.Model.SetPanicMute(!Root.Model.PanicMuted);
                break;
            case TitleHit.Snapshots:
                Root.ShowQuickSnapshotMenu(SnapshotRect);
                break;
            case TitleHit.Audio:
                Root.SwitchPage(7); // Engine-Tab
                break;
            case TitleHit.Update:
                Root.ShowUpdate();
                break;
            case TitleHit.Min:
                Root.Shell.Minimize();
                break;
            case TitleHit.Max:
                Root.Shell.ToggleMaximize();
                break;
            case TitleHit.Close:
                Root.Shell.Close();
                break;
        }
    }

    public override CursorKind CursorAt(float x, float y) =>
        HitAt(x, y).Kind is TitleHit.PanicMute or TitleHit.Snapshots or TitleHit.Audio or TitleHit.Update or TitleHit.Min or TitleHit.Max or TitleHit.Close
            ? CursorKind.Hand : CursorKind.Arrow;

    public override string? TooltipAt(float x, float y)
    {
        var (k, _) = HitAt(x, y);
        return k switch
        {
            TitleHit.Min => "Minimieren",
            TitleHit.Max => Root!.Shell.IsMaximized ? "Verkleinern" : "Maximieren",
            TitleHit.Close => "Oberfläche schließen (Hintergrund-Engine & Audio laufen weiter)",
            TitleHit.PanicMute => "Panic-Mute: alle Ausgänge sofort stummschalten / freigeben (Strg+M)",
            TitleHit.Snapshots => "Snapshots: schnell abrufen, speichern oder verwalten (Strg+7)",
            TitleHit.Audio => "Hintergrund-Engine, Takt, Latenz & Status (Strg+8)",
            TitleHit.Update => "Neue Version von NX-MIX verfügbar",
            _ => null,
        };
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        var root = Root!;
        var page = root.CurrentPage;
        var m = root.Model;
        g.Fill(b, Theme.Chrome);
        g.Fill(new RectF(b.X, b.Bottom - 1, b.W, 1), Theme.Separator);

        float tx = b.X + Theme.Pad;
        float tw = Renderer.MeasureWidth(page.Title, Theme.LargeTitle);
        var ar = AudioRect;
        var sr = SnapshotRect;
        var pr = PanicRect;
        float rightMost = !ar.IsEmpty ? ar.X : !sr.IsEmpty ? sr.X : pr.X;
        g.Text(page.Title, new RectF(tx, b.Y, Math.Max(40, Math.Min(tw + 4, rightMost - tx - 8)), b.H), Theme.LargeTitle, Theme.Text);

        float right = rightMost - 12;
        var ur = UpdateRect;
        if (!ur.IsEmpty)
        {
            bool hot = _hoverKind == TitleHit.Update;
            g.FillPill(ur, hot ? Theme.AccentHover : Theme.Accent);
            Icons.Draw(g, Icon.Import, ur.X + 18, ur.CY, 15, Theme.OnAccent);
            g.Text(UpdateText(), new RectF(ur.X + 32, ur.Y, ur.W - 36, ur.H), Theme.CalloutStrong, Theme.OnAccent, HAlign.Left, VAlign.Middle, false);
            right = ur.X - 12;
        }

        if (m.LimiterHits > 0)
        {
            string t = $"Limiter {m.LimiterHits}";
            if (right - Draw.ChipWidth(t) > tx + tw + 14)
                right -= Draw.Chip(g, right, b.CY, t, Theme.Orange, true, null, alignRight: true) + 8;
        }

        string? sub = page.Subtitle;
        if (!string.IsNullOrEmpty(sub))
        {
            float sx = tx + tw + 14;
            float available = right - sx - 8;
            if (available >= 90) g.Text(sub, new RectF(sx, b.Y + 2, available, b.H), Theme.Callout, Theme.TextSecondary);
        }

        // Audio / Daemon-Status-Pille
        if (!ar.IsEmpty)
        {
            string t = AudioText(out var c);
            bool hot = _hoverKind == TitleHit.Audio;
            bool running = m.HostState == HostState.Running && m.IsConnected;
            var bg = running ? Theme.Soft(Theme.Green) : m.HostState == HostState.Faulted ? Theme.Soft(Theme.Red) : Theme.Control;
            if (hot) bg = running ? Theme.Green.With(Theme.IsDark ? 0.26f : 0.2f) : Theme.ControlHover;
            g.FillPill(ar, bg);
            Icons.StatusDot(g, ar.X + 16, ar.CY, c, 3.5f, running);
            g.Text(t, new RectF(ar.X + 28, ar.Y, ar.W - 32, ar.H), Theme.CalloutMedium, running ? Theme.Text : Theme.TextSecondary, HAlign.Left, VAlign.Middle, false);
        }

        // Snapshots-Schnellwahl
        if (!sr.IsEmpty)
        {
            bool hot = _hoverKind == TitleHit.Snapshots || root.Modal is PopupMenu;
            g.FillRound(sr, Theme.RadiusControl, hot ? Theme.ControlHover : Theme.Control);
            Icons.Draw(g, Icon.Snapshots, sr.X + 17, sr.CY, 15, m.ActiveSnapshotId.HasValue ? Theme.AccentText : Theme.TextSecondary);
            g.Text(SnapshotText(), new RectF(sr.X + 31, sr.Y, sr.W - 48, sr.H), Theme.BodyMedium, Theme.Text);
            Icons.Draw(g, Icon.ChevronDown, sr.Right - 15, sr.CY + 0.5f, 13, Theme.TextSecondary);
        }

        // Panic-Mute-Schalter
        {
            bool muted = m.PanicMuted;
            bool hot = _hoverKind == TitleHit.PanicMute;
            var bg = muted
                ? (hot ? Theme.Mix(Theme.Red, Theme.White, 0.14f) : Theme.Red)
                : (hot ? Theme.Red.With(Theme.IsDark ? 0.22f : 0.15f) : Theme.Control);
            var fg = muted ? Theme.White : (hot ? Theme.Red : Theme.TextSecondary);
            g.FillRound(pr, Theme.RadiusControl, bg);
            Icons.Draw(g, Icon.Mute, pr.X + 17, pr.CY, 15, fg);
            g.Text(PanicText, new RectF(pr.X + 30, pr.Y, pr.W - 36, pr.H), Theme.CalloutStrong, fg, HAlign.Left, VAlign.Middle, false);
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

/// <summary>Kurzmeldungen unten mittig (Apple-Toast).</summary>
internal sealed class Toasts : Widget
{
    const float FadeIn = 0.15f, FadeOut = 0.35f;
    sealed class Item { public string Text = ""; public bool Error; public float Left, Age; }
    readonly List<Item> _items = new();

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
        bool animate = Root?.ReduceMotion != true;
        bool changed = false;
        foreach (var i in _items)
        {
            i.Left -= dt;
            changed |= animate && (i.Age < FadeIn || i.Left <= FadeOut);
            i.Age += dt;
        }
        return _items.RemoveAll(i => i.Left <= 0) > 0 || changed;
    }

    public override void Paint(Renderer g)
    {
        var area = Bounds;
        float y = area.Bottom - 24;
        for (int k = _items.Count - 1; k >= 0; k--)
        {
            var it = _items[k];
            float a = Root?.ReduceMotion == true ? 1 : Math.Clamp(Math.Min(it.Age / FadeIn, it.Left / FadeOut), 0, 1);
            float maxW = Math.Min(640, area.W - 40);
            if (maxW <= 64) break;
            float tw = Renderer.MeasureWidth(it.Text, Theme.BodyMedium);
            bool wrap = tw + 64 > maxW;
            float w = Math.Min(maxW, tw + 64);
            float h = wrap ? Renderer.TextHeight(it.Text, Theme.BodyMedium, w - 64) + 22 : 42;
            var r = new RectF(area.CX - w / 2, y - h + (1 - a) * 8, w, h);
            g.Shadow(r, 14, 18, 1.4f * a, 6);
            g.FillRound(r, Theme.RadiusCard, Theme.Elevated.With(a));
            g.StrokeRound(r, Theme.RadiusCard, (Theme.IsDark ? Theme.BorderStrong : Theme.Border).With(a));
            var c = it.Error ? Theme.Red : Theme.Green;
            g.FillCircle(r.X + 22, r.Y + 21, 11, Theme.Soft(c).With(Theme.Soft(c).A * a));
            Icons.Draw(g, it.Error ? Icon.Warn : Icon.Check, r.X + 22, r.Y + 21, 14, c.With(a));
            g.Text(it.Text, new RectF(r.X + 42, r.Y + (wrap ? 11 : 0), r.W - 58, wrap ? h - 22 : h), Theme.BodyMedium, Theme.Text.With(a),
                HAlign.Left, wrap ? VAlign.Top : VAlign.Middle, false, wrap);
            y = r.Y - 8;
        }
    }
}
