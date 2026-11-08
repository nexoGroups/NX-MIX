using Vortice.Mathematics;

namespace NxMix.App.Ui;

internal enum ButtonStyle { Filled, Tinted, Gray, Plain, Destructive, DestructiveTinted }

/// <summary>Taste im Apple-Stil: gefuellt (Akzent), getoent, grau, schlicht oder destruktiv; optional mit Symbol davor.</summary>
internal class Button : Widget
{
    public string Text { get; set; }
    public ButtonStyle Style { get; set; }
    public Icon Icon { get; set; }
    public TextStyle Font { get; set; } = Theme.BodyMedium;
    public bool Pill { get; set; }
    public HAlign ContentAlign { get; set; } = HAlign.Center;
    public event Action? Clicked;

    public Button(string text, ButtonStyle style = ButtonStyle.Gray, Icon icon = Icon.None)
    {
        Text = text; Style = style; Icon = icon; Cursor = CursorKind.Hand;
    }

    public override bool AcceptsMouse => Visible;
    public override void OnMouseEnter() => Invalidate();
    public override void OnMouseLeave() => Invalidate();
    public override void OnMouseDown(in MouseEvent e) { if (e.Button == 0 && Enabled) Invalidate(); }
    public override void OnMouseUp(in MouseEvent e)
    {
        Invalidate();
        if (e.Button == 0 && Enabled && HitOnRelease(e)) Clicked?.Invoke();
    }
    public override CursorKind CursorAt(float x, float y) => Enabled ? CursorKind.Hand : CursorKind.Arrow;

    /// <summary>Empfohlene Breite fuer den Text (mit Innenabstand und Symbol).</summary>
    public float PreferredWidth => Renderer.MeasureWidth(Text, Font) + (Icon != Icon.None ? 22 : 0) + (Theme.Touch ? 36 : 28);

    public static (Color4 Bg, Color4 Fg) Colors(ButtonStyle style, bool enabled, bool hot, bool down)
    {
        if (!enabled)
            return style == ButtonStyle.Plain ? (Theme.Clear, Theme.TextTertiary) : (Theme.Control.With(0.6f), Theme.TextTertiary);
        return style switch
        {
            ButtonStyle.Filled => (down ? Theme.AccentPressed : hot ? Theme.AccentHover : Theme.Accent, Theme.OnAccent),
            ButtonStyle.Tinted => (Theme.Accent.With((Theme.IsDark ? 0.18f : 0.11f) + (down ? 0.12f : hot ? 0.07f : 0)), Theme.AccentText),
            ButtonStyle.Plain => (down ? Theme.ControlPressed.With(0.7f) : hot ? Theme.Control.With(0.8f) : Theme.Clear, Theme.AccentText),
            ButtonStyle.Destructive => (Theme.Mix(Theme.Red, down ? Theme.Black : Theme.White, down ? 0.18f : hot ? 0.10f : 0f), Theme.White),
            ButtonStyle.DestructiveTinted => (Theme.Red.With((Theme.IsDark ? 0.16f : 0.10f) + (down ? 0.12f : hot ? 0.07f : 0)), Theme.Red),
            _ => (down ? Theme.ControlPressed : hot ? Theme.ControlHover : Theme.Control, Theme.Text),
        };
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        if (b.IsEmpty) return;
        float hover = HoverAmount(IsHovered && Enabled);
        float press = PressAmount(IsPressed && IsHovered && Enabled);
        var (restBg, fg) = Colors(Style, Enabled, false, false);
        var (hotBg, _) = Colors(Style, Enabled, true, false);
        var (downBg, _) = Colors(Style, Enabled, true, true);
        float radius = Pill ? b.H / 2 : Theme.RadiusControl;
        g.FillRound(b, radius, Theme.Mix(Theme.Mix(restBg, hotBg, hover), downBg, press));
        float iconS = Font.Size >= 13 ? 16 : 14;
        if (Icon != Icon.None && Text.Length == 0)
        {
            Icons.Draw(g, Icon, b.CX, b.CY, iconS, fg);
            return;
        }
        if (Icon != Icon.None)
        {
            float tw = Math.Min(Renderer.MeasureWidth(Text, Font), Math.Max(0, b.W - iconS - 22));
            float total = iconS + 6 + tw;
            float x0 = g.Snap(ContentAlign == HAlign.Left ? b.X + 10 : b.CX - total / 2);
            Icons.Draw(g, Icon, x0 + iconS / 2, b.CY, iconS, fg);
            g.Text(Text, new RectF(x0 + iconS + 6, b.Y, tw + 2, b.H), Font, fg, HAlign.Left, VAlign.Middle, true);
        }
        else g.Text(Text, b.Deflate(8, 0), Font, fg, ContentAlign);
    }
}

/// <summary>Symboltaste: rundes Hover-Feld, Symbol in Sekundaerfarbe (aktiv: eigene Farbe).</summary>
internal sealed class IconButton : Widget
{
    public Icon Icon { get; set; }
    public bool Active { get; set; }
    public Color4? ActiveColor { get; set; }
    public float IconSize { get; set; } = 18f;
    public bool Circle { get; set; }
    public event Action<RectF>? Clicked;

    public IconButton(Icon icon, string? tooltip = null) { Icon = icon; Tooltip = tooltip; Cursor = CursorKind.Hand; }
    public override bool AcceptsMouse => Visible;
    public override void OnMouseEnter() => Invalidate();
    public override void OnMouseLeave() => Invalidate();
    public override void OnMouseDown(in MouseEvent e) => Invalidate();
    public override void OnMouseUp(in MouseEvent e)
    {
        Invalidate();
        if (e.Button == 0 && Enabled && HitOnRelease(e)) Clicked?.Invoke(Bounds);
    }

    public override void Paint(Renderer g)
    {
        float hover = HoverAmount(IsHovered && Enabled);
        float press = PressAmount(IsPressed && IsHovered && Enabled);
        var b = Bounds;
        float rad = Circle ? Math.Min(b.W, b.H) / 2 : Theme.RadiusControl;
        var ac = ActiveColor ?? Theme.AccentText;
        var bg = Theme.Mix(Active ? Theme.Soft(ac) : Theme.ControlHover.With(0), Theme.ControlHover, hover);
        g.FillRound(b, rad, Theme.Mix(bg, Theme.ControlPressed, press));
        var c = !Enabled ? Theme.TextTertiary : Active ? ac : Theme.Mix(Theme.TextSecondary, Theme.Text, hover);
        Icons.Draw(g, Icon, b.CX, b.CY, IconSize, c);
    }
}

/// <summary>Rastende Taste fuer Mute/Solo im Kanalstreifen (aktiv: volle Funktionsfarbe).</summary>
internal sealed class Toggle : Widget
{
    public string Text { get; set; }
    public Func<Color4> ActiveColor { get; set; }
    public Func<Color4> ActiveText { get; set; } = () => Theme.White;
    public bool Checked { get; private set; }
    public TextStyle Font { get; set; } = Theme.CalloutStrong;
    public event Action<bool>? Toggled;
    Motion _checkedMotion;

    public Toggle(string text, Func<Color4> activeColor) { Text = text; ActiveColor = activeColor; Cursor = CursorKind.Hand; }

    public void SetChecked(bool on) { if (on != Checked) { Checked = on; Invalidate(); } }

    public override bool AcceptsMouse => Visible;
    public override void OnMouseEnter() => Invalidate();
    public override void OnMouseLeave() => Invalidate();
    public override void OnMouseDown(in MouseEvent e) => Invalidate();
    public override void OnMouseUp(in MouseEvent e)
    {
        Invalidate();
        if (e.Button == 0 && Enabled && HitOnRelease(e))
        {
            Checked = !Checked;
            Toggled?.Invoke(Checked);
        }
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        float hover = HoverAmount(IsHovered && Enabled);
        float press = PressAmount(IsPressed && IsHovered && Enabled);
        float selected = _checkedMotion.At(Root, Checked ? 1 : 0, Motion.Selection);
        var active = Theme.Mix(ActiveColor(), Theme.White, hover * 0.12f);
        var bg = Theme.Mix(Theme.Mix(Theme.Control, Theme.ControlHover, hover), active, selected);
        bg = Theme.Mix(bg, Theme.Black, press * 0.15f);
        var fg = Theme.Mix(Theme.Mix(Theme.TextSecondary, Theme.Text, hover), ActiveText(), selected);
        g.FillRound(b, Theme.RadiusSmall, bg);
        g.Text(Text, b, Font, fg, HAlign.Center);
    }
}

/// <summary>Ein/Aus-Schalter wie bei iOS (Akzent = an).</summary>
internal sealed class Switch : Widget
{
    public bool On { get; private set; }
    public event Action<bool>? Changed;
    Motion _selection;

    public Switch() { Cursor = CursorKind.Hand; }
    public void SetOn(bool on) { if (on != On) { On = on; Invalidate(); } }

    public override bool AcceptsMouse => Visible;
    public override void OnMouseEnter() => Invalidate();
    public override void OnMouseLeave() => Invalidate();
    public override CursorKind CursorAt(float x, float y) => Enabled ? CursorKind.Hand : CursorKind.Arrow;
    public override void OnMouseUp(in MouseEvent e)
    {
        if (e.Button == 0 && Enabled && HitOnRelease(e))
        {
            On = !On;
            Invalidate();
            Changed?.Invoke(On);
        }
    }

    /// <summary>Gezeichnete Flaeche (rechtsbuendig in Bounds, senkrecht mittig).</summary>
    public RectF Track => new(Bounds.Right - Theme.SwitchW, Bounds.CY - Theme.SwitchH / 2, Theme.SwitchW, Theme.SwitchH);

    public override void Paint(Renderer g)
    {
        var t = Track;
        float hover = HoverAmount(IsHovered && Enabled);
        float selected = _selection.At(Root, On ? 1 : 0, Motion.Selection);
        Color4 off = Theme.Mix(Theme.Control, Theme.ControlHover, hover);
        Color4 track = Theme.Mix(off, Theme.Mix(Theme.Accent, Theme.AccentHover, hover), selected);
        if (!Enabled) track = track.With(0.45f);
        g.FillPill(t, track);
        float d = t.H - 4;
        float kx = t.X + 2 + selected * (t.W - d - 4);
        var knob = new RectF(kx, t.Y + 2, d, d);
        g.Shadow(knob, d / 2, 3, 0.8f, 1);
        g.FillPill(knob, Enabled ? Theme.White : Theme.Rgb(0xE8E8E8));
    }
}

/// <summary>Segment-Schalter (Apple): eine Zeile Abschnitte, der gewaehlte hebt sich als Plaettchen ab.</summary>
internal sealed class Segmented : Widget
{
    public string[] Items { get; set; }
    public int Selected { get; private set; }
    public Func<int, bool>? ItemEnabled { get; set; }
    public Func<int, string?>? ItemTooltip { get; set; }
    public TextStyle Font { get; set; } = Theme.CalloutMedium;
    public event Action<int>? Changed;
    int _hot = -1, _press = -1;
    Motion _selection;

    public Segmented(params string[] items) { Items = items; Cursor = CursorKind.Hand; }

    public void SetSelected(int i) { if (i != Selected) { Selected = i; Invalidate(); } }

    public override bool AcceptsMouse => Visible;

    public RectF SegRect(int i)
    {
        var b = Bounds.Deflate(2);
        float w = b.W / Math.Max(1, Items.Length);
        return new RectF(b.X + i * w, b.Y, w, b.H);
    }

    int IndexAt(float x, float y)
    {
        for (int i = 0; i < Items.Length; i++) if (SegRect(i).Contains(x, y)) return i;
        return -1;
    }

    bool IsEnabled(int i) => Enabled && (ItemEnabled?.Invoke(i) ?? true);

    public override CursorKind CursorAt(float x, float y) { int i = IndexAt(x, y); return i >= 0 && IsEnabled(i) ? CursorKind.Hand : CursorKind.Arrow; }
    public override string? TooltipAt(float x, float y) { int i = IndexAt(x, y); return i >= 0 ? ItemTooltip?.Invoke(i) ?? Tooltip : Tooltip; }
    public override void OnMouseMove(in MouseEvent e) { int i = IndexAt(e.X, e.Y); if (i != _hot) { _hot = i; Invalidate(); } }
    public override void OnMouseLeave() { _hot = -1; Invalidate(); }
    public override void OnMouseDown(in MouseEvent e) { _press = e.Button == 0 ? IndexAt(e.X, e.Y) : -1; Invalidate(); }
    public override void OnMouseUp(in MouseEvent e)
    {
        int i = IndexAt(e.X, e.Y);
        if (e.Button == 0 && i >= 0 && i == _press && IsEnabled(i) && HitOnRelease(e))
        {
            Selected = i;
            Changed?.Invoke(i);
        }
        _press = -1;
        Invalidate();
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        g.FillRound(b, Theme.RadiusControl + 1, Theme.Control);
        if (Items.Length == 0) return;
        float position = Math.Clamp(_selection.At(Root, Selected, Motion.Selection), 0, Items.Length - 1);
        var first = SegRect(0);
        var thumb = first.Offset(position * first.W, 0);
        if (_hot >= 0 && _hot < Items.Length && _hot != Selected && IsEnabled(_hot))
            g.FillRound(SegRect(_hot).Deflate(1), Theme.RadiusControl - 1, Theme.ControlHover);
        for (int i = 0; i < Items.Length; i++)
        {
            var r = SegRect(i);
            if (i > 0 && i != Selected && i - 1 != Selected)
                g.Fill(new RectF(r.X - 0.5f, r.CY - r.H * 0.25f, 1, r.H * 0.5f), Theme.BorderStrong.With(0.7f));
        }
        if (!Theme.IsDark) g.Shadow(thumb, Theme.RadiusControl - 1, 2.5f, 0.7f, 1);
        g.FillRound(thumb, Theme.RadiusControl - 1, Theme.SegmentThumb);
        for (int i = 0; i < Items.Length; i++)
        {
            var r = SegRect(i);
            bool sel = i == Selected;
            var fg = !IsEnabled(i) ? Theme.TextTertiary : Theme.Mix(Theme.TextSecondary, Theme.Text, Math.Max(0, 1 - Math.Abs(position - i)));
            g.Text(Items[i], r.Deflate(4, 0), sel ? new TextStyle(Font.Size, Vortice.DirectWrite.FontWeight.SemiBold) : Font, fg, HAlign.Center);
        }
    }
}

/// <summary>Auswahlfeld (Apple-Popup-Taste): zeigt den aktuellen Wert, klappt beim Klick ein Menue auf.</summary>
internal sealed class Dropdown : Widget
{
    public Func<string> Text = () => "";
    public Func<List<MenuItem>> Items = () => new();
    public Func<Color4>? TextColor;

    public Dropdown() { Cursor = CursorKind.Hand; }
    public override bool AcceptsMouse => Visible;
    public override CursorKind CursorAt(float x, float y) => Enabled ? CursorKind.Hand : CursorKind.Arrow;
    public override void OnMouseEnter() => Invalidate();
    public override void OnMouseLeave() => Invalidate();
    public override void OnMouseDown(in MouseEvent e) => Invalidate();
    public override void OnMouseUp(in MouseEvent e)
    {
        Invalidate();
        if (e.Button == 0 && Enabled && HitOnRelease(e) && Root != null)
            PopupMenu.Show(Root, Bounds, Items(), Bounds.W);
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        float hover = HoverAmount(IsHovered && Enabled);
        float press = PressAmount(IsPressed && IsHovered && Enabled);
        var bg = Theme.Mix(Theme.Mix(Theme.Control, Theme.ControlHover, hover), Theme.ControlPressed, press);
        g.FillRound(b, Theme.RadiusControl, Enabled ? bg : Theme.Control.With(0.6f));
        var tc = !Enabled ? Theme.TextTertiary : TextColor?.Invoke() ?? Theme.Text;
        g.Text(Text(), new RectF(b.X + 12, b.Y, b.W - 40, b.H), Theme.Body, tc, HAlign.Left, VAlign.Middle, true);
        Icons.Draw(g, Icon.UpDown, b.Right - 16, b.CY, 14, Enabled ? Theme.TextSecondary : Theme.TextTertiary);
    }
}

/// <summary>Waagrechter Schieberegler (Apple): Ziehen, Mausrad, Strg/Umschalt = fein.</summary>
internal sealed class Slider : Widget
{
    public double Min { get; set; }
    public double Max { get; set; } = 1;
    public double Value { get; private set; }
    /// <summary>Raster beim Ziehen (0 = frei).</summary>
    public double Step { get; set; } = 0.01;
    public double WheelStep { get; set; } = 0.1;
    /// <summary>Wert -> Position 0..1 (Standard: linear).</summary>
    public Func<double, double>? ToNorm { get; set; }
    public Func<double, double>? FromNorm { get; set; }
    /// <summary>Obergrenze, ueber die nicht gezogen werden darf (z. B. Gain-Sperre); NaN = keine.</summary>
    public double Limit { get; set; } = double.NaN;
    public Func<Color4>? FillColor { get; set; }
    public event Action<double>? Changed;

    bool _lastFine, _dragging;
    bool _dragRebased;
    double _dragNorm0;
    float _dragX0;

    public Slider() { Cursor = CursorKind.Hand; }
    public override bool AcceptsMouse => Visible;

    double Norm(double v) => ToNorm?.Invoke(v) ?? (Max > Min ? (v - Min) / (Max - Min) : 0);
    double Val(double t) => FromNorm?.Invoke(t) ?? Min + t * (Max - Min);

    public void SetValue(double v) { if (!v.Equals(Value)) { Value = v; Invalidate(); } }

    /// <summary>Hält den Ziehpunkt stabil, wenn die UI während des Ziehens ihre Skalierung ändert.</summary>
    internal void RebaseDrag(float pointerX)
    {
        _dragNorm0 = Math.Clamp(Norm(Value), 0, 1);
        _dragX0 = pointerX;
        _dragRebased = true;
    }

    RectF TrackRect => new(Bounds.X + KnobD / 2, Bounds.CY - 2, Math.Max(1, Bounds.W - KnobD), 4);
    static float KnobD => Theme.Touch ? 26 : 20;

    void Set(double v, bool fine)
    {
        if (Step > 0 && !double.IsInfinity(v)) v = Math.Round(v / (fine ? Step / 10 : Step)) * (fine ? Step / 10 : Step);
        if (!double.IsNegativeInfinity(v)) v = Math.Clamp(v, Min, Max);
        if (!double.IsNaN(Limit) && v > Limit) v = Limit;
        if (v.Equals(Value)) return;
        Value = v; Invalidate();
        Changed?.Invoke(v);
    }

    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button != 0 || !Enabled) return;
        _dragging = true;
        var t = TrackRect;
        float kx = t.X + (float)Math.Clamp(Norm(Value), 0, 1) * t.W;
        bool onKnob = Math.Abs(e.X - kx) <= KnobD / 2 + 2;
        _dragRebased = false;
        if (!onKnob) Set(Val(Math.Clamp((e.X - t.X) / t.W, 0, 1)), e.Fine);
        if (!_dragRebased) { _dragNorm0 = Math.Clamp(Norm(Value), 0, 1); _dragX0 = e.X; }
        _lastFine = e.Fine;
        Invalidate();
    }

    public override void OnMouseMove(in MouseEvent e)
    {
        if (!_dragging || !IsPressed || !Enabled) return;
        if (e.Fine != _lastFine) { _dragNorm0 = Math.Clamp(Norm(Value), 0, 1); _dragX0 = e.X; _lastFine = e.Fine; }
        double k = e.Fine ? 0.1 : 1.0;
        double t = Math.Clamp(_dragNorm0 + (e.X - _dragX0) / TrackRect.W * k, 0, 1);
        Set(Val(t), e.Fine);
    }

    public override void OnMouseUp(in MouseEvent e) { _dragging = false; Invalidate(); }

    public override bool OnMouseWheel(in MouseEvent e)
    {
        if (!Enabled) return true;
        double step = e.Fine ? WheelStep / 10 : WheelStep;
        double v = double.IsNegativeInfinity(Value) ? Min : Value;
        Set(v + Math.Sign(e.Wheel) * step * Math.Max(1, Math.Abs(Math.Round(e.Wheel))), e.Fine);
        return true;
    }

    public override void Paint(Renderer g)
    {
        var t = TrackRect;
        g.FillPill(t, Theme.IsDark ? Theme.Rgb(0x3A3D44) : Theme.Rgb(0xDADDE2));
        float n = (float)Math.Clamp(Norm(Value), 0, 1);
        if (!double.IsNaN(Limit))
        {
            float lx = t.X + (float)Math.Clamp(Norm(Limit), 0, 1) * t.W;
            g.FillPill(new RectF(lx, t.Y, t.Right - lx, t.H), Theme.Orange.With(0.35f));
            g.FillRound(new RectF(lx - 1, t.Y - 4, 2, t.H + 8), 1, Theme.Orange);
        }
        var fill = FillColor?.Invoke() ?? Theme.Accent;
        if (n > 0) g.FillPill(new RectF(t.X, t.Y, n * t.W, t.H), Enabled ? fill : Theme.TextTertiary);
        float kx = t.X + n * t.W;
        var knob = RectF.Centered(kx, t.CY, KnobD, KnobD);
        float hover = HoverAmount((IsHovered || _dragging) && Enabled);
        g.Shadow(knob, KnobD / 2, 4 + hover, 1f + hover * 0.35f, 1.5f);
        g.FillPill(knob, Theme.White);
        g.StrokeRound(knob, KnobD / 2, Theme.IsDark ? Theme.Rgb(0x000000, 0.25f) : Theme.Rgb(0x000000, 0.10f));
    }
}

/// <summary>Editierbare Zahlenanzeige.</summary>
internal sealed class ValueBox : Widget
{
    public string Text { get; set; } = "";
    public Color4? TextColor { get; set; }
    public string EditHint { get; set; } = "Klick: Wert eintippen";
    public event Action<string>? Typed;
    public bool Editable { get; set; } = true;

    public override bool AcceptsMouse => Visible;
    public override void OnMouseEnter() => Invalidate();
    public override void OnMouseLeave() => Invalidate();
    public override string? TooltipAt(float x, float y) => Editable ? EditHint : Tooltip;

    public override void OnMouseUp(in MouseEvent e)
    {
        if (e.Button == 0 && Editable && HitOnRelease(e))
            Root?.BeginEdit(Bounds.Inflate(2), Text.Replace(Db.Minus, '-'), Theme.Value, s => Typed?.Invoke(s), 10, null, HAlign.Center);
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        g.FillRound(b, Theme.RadiusSmall, Theme.IsDark ? Theme.Well : Theme.Control);
        float hover = HoverAmount(IsHovered && Editable);
        if (hover > 0) g.StrokeRound(b, Theme.RadiusSmall, Theme.BorderStrong.With(hover));
        g.Text(Text, b, Theme.Value, TextColor ?? Theme.Text, HAlign.Center, VAlign.Middle, false);
    }
}

/// <summary>Senkrechtes Scrollen mit schmalem, schwebendem Balken (macOS-Stil) fuer selbst gezeichnete Listen.</summary>
internal sealed class VScroll
{
    public float Offset;
    public float ContentH;
    public RectF View;
    public bool Dragging;
    float _grab;

    public void Update(RectF view, float contentH)
    {
        View = view; ContentH = contentH;
        Offset = Math.Clamp(Offset, 0, Math.Max(0, ContentH - View.H));
    }

    public bool Needed => !View.IsEmpty && ContentH > View.H + 0.5f;
    public RectF Track => new(View.Right - 9, View.Y + 3, 6, Math.Max(0, View.H - 6));
    public bool HitTrack(float x, float y) => Needed && Track.Inflate(4, 0).Intersect(View).Contains(x, y);

    public RectF Thumb
    {
        get
        {
            var t = Track;
            float th = Math.Min(t.H - Math.Min(8, t.H / 4), Math.Max(32, t.H * View.H / Math.Max(1, ContentH)));
            float max = Math.Max(1, ContentH - View.H);
            return new RectF(t.X, t.Y + (t.H - th) * (Offset / max), t.W, th);
        }
    }

    public bool Wheel(float notches)
    {
        if (!Needed) return false;
        float before = Offset;
        Offset = Math.Clamp(Offset - notches * 60, 0, Math.Max(0, ContentH - View.H));
        return before != Offset;
    }

    public bool Down(float x, float y)
    {
        if (!HitTrack(x, y)) return false;
        var th = Thumb;
        _grab = th.Contains(x, y) ? y - th.Y : th.H / 2;
        Dragging = true;
        Drag(y);
        return true;
    }

    public void Drag(float y)
    {
        var t = Track; var th = Thumb;
        float span = t.H - th.H;
        if (span <= 0) return;
        Offset = Math.Clamp((y - _grab - t.Y) / span, 0, 1) * Math.Max(0, ContentH - View.H);
    }

    public void Up() => Dragging = false;

    public void Paint(Renderer g)
    {
        if (!Needed) return;
        g.FillPill(Thumb, Dragging ? Theme.TextSecondary.With(0.8f) : Theme.TextTertiary.With(0.55f));
    }
}

/// <summary>Gemeinsame Zeichenbausteine: Karte, Chip, Abschnittstitel, Trennlinie.</summary>
internal static class Draw
{
    /// <summary>Karte im UniFi/Apple-Stil: im Dunkeln flach mit feinem Rand, im Hellen weiss mit weichem Schatten.</summary>
    public static void Card(Renderer g, RectF r, float radius = Theme.RadiusCard, Color4? fill = null)
    {
        if (!Theme.IsDark) g.Shadow(r, radius, 6, 0.35f, 1);
        g.FillRound(r, radius, fill ?? Theme.Surface);
        g.StrokeRound(r, radius, Theme.Border);
    }

    /// <summary>Kleine Statuspille: getoenter Grund, farbiger Text, optional Punkt davor.</summary>
    public static float Chip(Renderer g, float x, float cy, string text, Color4 color, bool dot = true, TextStyle? font = null, bool alignRight = false)
    {
        var f = font ?? Theme.CaptionStrong;
        float tw = Renderer.MeasureWidth(text, f);
        float h = Theme.Touch ? 24 : 22;
        float w = tw + (dot ? 26 : 18);
        var r = new RectF(alignRight ? x - w : x, cy - h / 2, w, h);
        g.FillPill(r, Theme.Soft(color));
        if (dot) g.FillCircle(r.X + 11, r.CY, 3.5f, color);
        g.Text(text, new RectF(r.X + (dot ? 19 : 9), r.Y, tw + 2, r.H), f, color, HAlign.Left, VAlign.Middle, false);
        return w;
    }

    public static float ChipWidth(string text, bool dot = true, TextStyle? font = null) =>
        Renderer.MeasureWidth(text, font ?? Theme.CaptionStrong) + (dot ? 26 : 18);

    /// <summary>Kopf einer Karte: Titel (Headline), optional Zahl als graue Pille.</summary>
    public static void CardTitle(Renderer g, RectF r, string title, int? count = null)
    {
        g.Text(title, r, Theme.Headline, Theme.Text, HAlign.Left, VAlign.Middle, false);
        if (count is int n)
        {
            float tw = Renderer.MeasureWidth(title, Theme.Headline);
            string s = n.ToString();
            float cw = Math.Max(20, Renderer.MeasureWidth(s, Theme.CaptionStrong) + 12);
            var chip = new RectF(r.X + tw + 8, r.CY - 10, cw, 20);
            g.FillPill(chip, Theme.Control);
            g.Text(s, chip, Theme.CaptionStrong, Theme.TextSecondary, HAlign.Center, VAlign.Middle, false);
        }
    }

    public static void HLine(Renderer g, float x, float y, float w) => g.Fill(new RectF(x, y, w, 1), Theme.Separator);

    /// <summary>Taste ohne eigenes Widget (fuer Listen): Hintergrund + Text/Symbol.</summary>
    public static void ButtonLook(Renderer g, RectF r, string text, ButtonStyle style, bool enabled, bool hot, bool down = false, Icon icon = Icon.None, TextStyle? font = null)
    {
        var (bg, fg) = Button.Colors(style, enabled, hot, down);
        g.FillRound(r, Theme.RadiusControl, bg);
        var f = font ?? Theme.CalloutMedium;
        if (icon != Icon.None)
        {
            float tw = Renderer.MeasureWidth(text, f);
            float total = 14 + (text.Length > 0 ? 5 + tw : 0);
            float x0 = r.CX - total / 2;
            Icons.Draw(g, icon, x0 + 7, r.CY, 14, fg);
            if (text.Length > 0) g.Text(text, new RectF(x0 + 19, r.Y, tw + 4, r.H), f, fg, HAlign.Left, VAlign.Middle, false);
        }
        else g.Text(text, r, f, fg, HAlign.Center, VAlign.Middle, false);
    }

    /// <summary>Leerzustand: Symbol im getoenten Kreis, Ueberschrift, Text (zentriert ab y).</summary>
    public static float Empty(Renderer g, RectF area, float y, Icon icon, string head, string body, Color4? tint = null)
    {
        var c = tint ?? Theme.AccentText;
        float cx = area.CX;
        g.FillCircle(cx, y + 28, 28, Theme.Soft(c));
        Icons.Draw(g, icon, cx, y + 28, 26, c);
        y += 70;
        g.Text(head, new RectF(area.X + 20, y, area.W - 40, 24), Theme.Title, Theme.Text, HAlign.Center);
        y += 30;
        float bw = Math.Min(460, area.W - 60);
        float bh = Renderer.TextHeight(body, Theme.Body, bw);
        g.Text(body, new RectF(cx - bw / 2, y, bw, bh + 4), Theme.Body, Theme.TextSecondary, HAlign.Center, VAlign.Top, false, true);
        return y + bh + 20;
    }

    public static float EmptyHeight(float width, string body) =>
        70 + 30 + Renderer.TextHeight(body, Theme.Body, Math.Min(460, width - 60)) + 20;
}
