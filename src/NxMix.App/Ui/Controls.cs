using Vortice.Mathematics;

namespace NxMix.App.Ui;

internal enum ButtonStyle { Secondary, Primary, Ghost, Danger }

/// <summary>Text-Taste (Standard, Primaer mit Akzent, Ghost, Gefahr). Optional mit Plus-Symbol.</summary>
internal class TextButton : Widget
{
    public string Text { get; set; }
    public ButtonStyle Style { get; set; }
    public bool PlusIcon { get; set; }
    public TextStyle Font { get; set; } = Theme.BodyBold;
    public float Radius { get; set; } = 8f;
    public event Action? Clicked;

    public TextButton(string text, ButtonStyle style = ButtonStyle.Secondary, bool plus = false)
    {
        Text = text; Style = style; PlusIcon = plus; Cursor = CursorKind.Hand;
    }

    public override bool AcceptsMouse => Visible;

    public override void OnMouseEnter() => Invalidate();
    public override void OnMouseLeave() => Invalidate();
    public override void OnMouseDown(in MouseEvent e) { if (e.Button == 0 && Enabled) Invalidate(); }
    public override void OnMouseUp(in MouseEvent e)
    {
        Invalidate();
        if (e.Button == 0 && Enabled && Bounds.Contains(e.X, e.Y)) Clicked?.Invoke();
    }

    protected void Fire() => Clicked?.Invoke();

    public override void Paint(Renderer g)
    {
        bool hot = IsHovered && Enabled, down = IsPressed && Enabled && hot;
        Color4 bg, fg, border = default;
        bool hasBorder = false;
        switch (Style)
        {
            case ButtonStyle.Primary:
                bg = Theme.Accent; fg = Theme.OnAccent;
                if (hot) bg = Theme.AccentHi;
                if (down) bg = Theme.Mix(Theme.Accent, Theme.Black, 0.18f);
                break;
            case ButtonStyle.Danger:
                bg = Theme.Mute.With(0.16f); fg = Theme.Mute; border = Theme.Mute.With(0.5f); hasBorder = true;
                if (hot) bg = Theme.Mute.With(0.28f);
                break;
            case ButtonStyle.Ghost:
                bg = hot ? Theme.Rgb(0xFFFFFF, 0.07f) : default; fg = hot ? Theme.Text : Theme.TextDim;
                if (down) bg = Theme.Rgb(0xFFFFFF, 0.12f);
                break;
            default:
                bg = hot ? Theme.RaisedHi : Theme.Raised; fg = Theme.Text; border = Theme.BorderStrong; hasBorder = true;
                if (down) bg = Theme.Mix(Theme.Raised, Theme.Black, 0.2f);
                break;
        }
        if (!Enabled)
        {
            bg = Style == ButtonStyle.Primary ? Theme.Raised : Theme.Card; fg = Theme.TextFaint; border = Theme.Border; hasBorder = Style != ButtonStyle.Ghost;
        }
        var b = Bounds;
        if (bg.A > 0) g.FillRound(b, Radius, bg);
        if (hasBorder) g.StrokeRound(b, Radius, border);
        float tw = Renderer.MeasureWidth(Text, Font);
        if (PlusIcon)
        {
            float total = 12 + 8 + tw;
            float x0 = b.CX - total / 2;
            Icons.Plus(g, x0 + 6, b.CY, 11, fg, 1.8f);
            g.Text(Text, new RectF(x0 + 20, b.Y, tw + 4, b.H), Font, fg, HAlign.Left, VAlign.Middle, false);
        }
        else g.Text(Text, b.Deflate(6, 0), Font, fg, HAlign.Center);
    }
}

internal enum IconKind { More, Lock, LockOpen, Plus, Cross, Trash, Refresh, Left, Right, Down }

/// <summary>Kleine quadratische Symboltaste. Active faerbt das Symbol (z. B. aktive Gain-Sperre).</summary>
internal sealed class IconButton : Widget
{
    public IconKind Icon { get; set; }
    public bool Active { get; set; }
    public Color4 ActiveColor { get; set; } = Theme.Accent;
    public float IconSize { get; set; } = 13f;
    public event Action<RectF>? Clicked;
    public bool Filled { get; set; }

    public IconButton(IconKind icon) { Icon = icon; Cursor = CursorKind.Hand; }
    public override bool AcceptsMouse => Visible;
    public override void OnMouseEnter() => Invalidate();
    public override void OnMouseLeave() => Invalidate();
    public override void OnMouseDown(in MouseEvent e) => Invalidate();
    public override void OnMouseUp(in MouseEvent e)
    {
        Invalidate();
        if (e.Button == 0 && Enabled && Bounds.Contains(e.X, e.Y)) Clicked?.Invoke(Bounds);
    }

    public override void Paint(Renderer g)
    {
        bool hot = IsHovered && Enabled;
        var b = Bounds;
        if (hot) g.FillRound(b, 6, IsPressed ? Theme.RaisedHi : Theme.CardHover);
        else if (Filled || Active) g.FillRound(b, 6, Active ? ActiveColor.With(0.14f) : Theme.Raised);
        var c = !Enabled ? Theme.TextFaint : Active ? ActiveColor : hot ? Theme.Text : Theme.TextDim;
        float cx = b.CX, cy = b.CY, s = IconSize;
        switch (Icon)
        {
            case IconKind.More: Icons.Dots(g, cx, cy, s, c); break;
            case IconKind.Lock: Icons.Lock(g, cx, cy, s, c, true); break;
            case IconKind.LockOpen: Icons.Lock(g, cx, cy, s, c, false); break;
            case IconKind.Plus: Icons.Plus(g, cx, cy, s * 0.8f, c); break;
            case IconKind.Cross: Icons.Cross(g, cx, cy, s * 0.7f, c); break;
            case IconKind.Trash: Icons.Trash(g, cx, cy, s, c); break;
            case IconKind.Refresh: Icons.Refresh(g, cx, cy, s, c); break;
            case IconKind.Left: Icons.Chevron(g, cx, cy, s * 0.8f, c, 2); break;
            case IconKind.Right: Icons.Chevron(g, cx, cy, s * 0.8f, c, 3); break;
            case IconKind.Down: Icons.Chevron(g, cx, cy, s * 0.8f, c, 0); break;
        }
    }
}

/// <summary>Mute / Solo / Phase: rastet ein, Farbe frei waehlbar.</summary>
internal sealed class ToggleButton : Widget
{
    public string Text { get; set; }
    public Color4 ActiveColor { get; set; }
    public bool Checked { get; private set; }
    public TextStyle Font { get; set; } = Theme.SmallBold;
    public event Action<bool>? Toggled;

    public ToggleButton(string text, Color4 activeColor) { Text = text; ActiveColor = activeColor; Cursor = CursorKind.Hand; }

    public void SetChecked(bool on) { if (on != Checked) { Checked = on; Invalidate(); } }

    public override bool AcceptsMouse => Visible;
    public override void OnMouseEnter() => Invalidate();
    public override void OnMouseLeave() => Invalidate();
    public override void OnMouseDown(in MouseEvent e) => Invalidate();
    public override void OnMouseUp(in MouseEvent e)
    {
        Invalidate();
        if (e.Button == 0 && Enabled && Bounds.Contains(e.X, e.Y))
        {
            Checked = !Checked;
            Toggled?.Invoke(Checked);
        }
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        bool hot = IsHovered && Enabled;
        Color4 bg, fg;
        if (Checked) { bg = hot ? Theme.Mix(ActiveColor, Theme.White, 0.15f) : ActiveColor; fg = Theme.OnAccent; }
        else { bg = hot ? Theme.RaisedHi : Theme.Raised; fg = hot ? Theme.Text : Theme.TextDim; }
        if (IsPressed && hot) bg = Theme.Mix(bg, Theme.Black, 0.22f);
        g.FillRound(b, Theme.RadiusControl, bg);
        if (!Checked) g.StrokeRound(b, Theme.RadiusControl, Theme.BorderStrong.With(0.6f));
        g.Text(Text, b, Font, fg, HAlign.Center);
    }
}

/// <summary>Drehregler: Ziehen (senkrecht), Mausrad, Strg/Umschalt = fein, Doppelklick = Vorgabewert.</summary>
internal sealed class Knob : Widget
{
    public double Min { get; set; }
    public double Max { get; set; } = 1;
    public double Default { get; set; }
    public double Value { get; private set; }
    public event Action<double>? Changed;

    double _dragStartValue;
    float _dragStartY;
    bool _lastFine;
    const float DragPixels = 160f;

    public Knob() { Cursor = CursorKind.SizeNS; }
    public override bool AcceptsMouse => Visible;

    public void SetValue(double v)
    {
        v = Math.Clamp(v, Min, Max);
        if (v != Value) { Value = v; Invalidate(); }
    }

    void Set(double v)
    {
        v = Math.Clamp(Math.Round(v, 2), Min, Max);
        if (v == Value) return;
        Value = v; Invalidate();
        Changed?.Invoke(v);
    }

    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button != 0 || !Enabled) return;
        if (e.Clicks >= 2) { Set(Default); return; }
        _dragStartValue = Value; _dragStartY = e.Y; _lastFine = e.Fine;
        Invalidate();
    }

    public override void OnMouseMove(in MouseEvent e)
    {
        if (!IsPressed || !Enabled) return;
        if (e.Fine != _lastFine) { _dragStartValue = Value; _dragStartY = e.Y; _lastFine = e.Fine; }
        double range = Max - Min;
        double per = e.Fine ? 0.1 : 1.0;
        Set(_dragStartValue + (_dragStartY - e.Y) / DragPixels * range * per);
    }

    public override void OnMouseUp(in MouseEvent e) => Invalidate();

    public override bool OnMouseWheel(in MouseEvent e)
    {
        if (!Enabled) return true;
        double step = (Max - Min) / (e.Fine ? 500.0 : 50.0);
        Set(Value + Math.Sign(e.Wheel) * step * Math.Max(1, Math.Abs(e.Wheel)));
        return true;
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        float d = Math.Min(b.W, b.H);
        float cx = b.CX, cy = b.CY;
        float rOuter = d / 2 - 1.5f;
        bool hot = (IsHovered || IsPressed) && Enabled;
        const float start = 135f, sweep = 270f;
        g.Arc(cx, cy, rOuter - 1f, start, sweep, Theme.Well, 3.2f);
        double t = (Value - Min) / (Max - Min);
        if (t > 0.002) g.Arc(cx, cy, rOuter - 1f, start, (float)(sweep * t), Enabled ? (hot ? Theme.AccentHi : Theme.Accent) : Theme.TextFaint, 3.2f);
        float rb = rOuter - 5.5f;
        g.FillCircle(cx, cy, rb, hot ? Theme.RaisedHi : Theme.Raised);
        g.StrokeCircle(cx, cy, rb - 0.5f, Theme.BorderStrong.With(0.7f), 1f);
        float a = (start + (float)(sweep * t)) * MathF.PI / 180f;
        g.Line(cx + MathF.Cos(a) * rb * 0.35f, cy + MathF.Sin(a) * rb * 0.35f, cx + MathF.Cos(a) * (rb - 1.5f), cy + MathF.Sin(a) * (rb - 1.5f),
            Theme.Text, 1.8f, true);
    }
}

/// <summary>Kanalname: mittig, bis zu zwei Zeilen; Doppelklick = umbenennen (eigener Mini-Editor).</summary>
internal sealed class NameLabel : Widget
{
    public string Text { get; set; } = "";
    public string Placeholder { get; set; } = "";
    /// <summary>Farbe des Platzhalters (Standard: blass; bei App-Links der Programmname in voller Schrift).</summary>
    public Color4 PlaceholderColor { get; set; } = Theme.TextFaint;
    public TextStyle Font { get; set; } = Theme.SmallBold;
    public int MaxLength { get; set; } = 24;
    public event Action<string>? Renamed;
    public bool Editable { get; set; } = true;

    public override bool AcceptsMouse => Visible;

    public override void OnMouseEnter() => Invalidate();
    public override void OnMouseLeave() => Invalidate();

    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button == 0 && e.Clicks >= 2 && Editable) BeginRename();
    }

    public void BeginRename()
    {
        var root = Root;
        if (root == null) return;
        var r = new RectF(Bounds.CX - Math.Max(Bounds.W, 110) / 2, Bounds.CY - 15, Math.Max(Bounds.W, 110), 30);
        r = new RectF(Math.Clamp(r.X, 4, root.W - r.W - 4), r.Y, r.W, r.H);
        root.BeginEdit(r, Text, Theme.Body, s => Renamed?.Invoke(s.Trim()), MaxLength, null, HAlign.Center);
    }

    public override string? TooltipAt(float x, float y) => Editable ? "Doppelklick: umbenennen" : null;

    public override void Paint(Renderer g)
    {
        bool empty = string.IsNullOrWhiteSpace(Text);
        string s = empty ? Placeholder : Text;
        var col = empty ? PlaceholderColor : IsHovered && Editable ? Theme.AccentHi : Theme.Text;
        g.Text(s, Bounds, Font, col, HAlign.Center, VAlign.Middle, true, true);
    }
}

/// <summary>Zahlenanzeige im Kasten; Doppelklick = Wert eintippen.</summary>
internal sealed class ReadoutBox : Widget
{
    public string Text { get; set; } = "";
    public Color4 TextColor { get; set; } = Theme.Text;
    public string EditHint { get; set; } = "Doppelklick: Wert eintippen";
    public event Action<string>? Typed;
    public bool Editable { get; set; } = true;

    public override bool AcceptsMouse => Visible;
    public override void OnMouseEnter() => Invalidate();
    public override void OnMouseLeave() => Invalidate();
    public override string? TooltipAt(float x, float y) => Editable ? EditHint : null;

    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button == 0 && e.Clicks >= 2 && Editable)
            Root?.BeginEdit(Bounds.Inflate(2), Text.Replace(Db.Minus, '-'), Theme.Value, s => Typed?.Invoke(s), 10, null, HAlign.Center);
    }

    public override void Paint(Renderer g)
    {
        g.FillRound(Bounds, 5, Theme.Well);
        if (IsHovered && Editable) g.StrokeRound(Bounds, 5, Theme.BorderStrong);
        g.Text(Text, Bounds, Theme.Value, TextColor, HAlign.Center, VAlign.Middle, false);
    }
}
