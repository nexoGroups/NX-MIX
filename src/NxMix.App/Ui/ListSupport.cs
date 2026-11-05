using Vortice.Mathematics;

namespace NxMix.App.Ui;

/// <summary>Senkrechtes Scrollen mit schmalem Balken fuer selbst gezeichnete Listen.</summary>
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

    public bool Needed => ContentH > View.H + 0.5f;
    public RectF Track => new(View.Right - 10, View.Y + 2, 8, Math.Max(0, View.H - 4));

    public RectF Thumb
    {
        get
        {
            var t = Track;
            float th = Math.Max(28, t.H * View.H / Math.Max(1, ContentH));
            float max = Math.Max(1, ContentH - View.H);
            return new RectF(t.X, t.Y + (t.H - th) * (Offset / max), t.W, th);
        }
    }

    public bool Wheel(float notches)
    {
        if (!Needed) return false;
        Offset = Math.Clamp(Offset - notches * 54, 0, Math.Max(0, ContentH - View.H));
        return true;
    }

    public bool Down(float x, float y)
    {
        if (!Needed || !Track.Inflate(4).Contains(x, y)) return false;
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
        g.FillRound(Track, 4, Theme.Well);
        g.FillRound(Thumb, 4, Dragging ? Theme.Accent : Theme.RaisedHi);
    }
}

/// <summary>Auswahlfeld: zeigt den aktuellen Wert, klappt beim Klick ein Menue auf.</summary>
internal sealed class Dropdown : Widget
{
    public Func<string> Text = () => "";
    public Func<List<MenuItem>> Items = () => new();
    public string? Caption;

    public Dropdown() { Cursor = CursorKind.Hand; }
    public override bool AcceptsMouse => Visible;
    public override void OnMouseEnter() => Invalidate();
    public override void OnMouseLeave() => Invalidate();
    public override void OnMouseDown(in MouseEvent e) => Invalidate();
    public override void OnMouseUp(in MouseEvent e)
    {
        Invalidate();
        if (e.Button == 0 && Enabled && Bounds.Contains(e.X, e.Y) && Root != null)
            PopupMenu.Show(Root, Bounds, Items(), Bounds.W);
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        bool hot = IsHovered && Enabled;
        g.FillRound(b, Theme.RadiusControl + 1, !Enabled ? Theme.Card : hot ? Theme.RaisedHi : Theme.Raised);
        g.StrokeRound(b, Theme.RadiusControl + 1, Theme.BorderStrong.With(Enabled ? 1f : 0.5f));
        g.Text(Text(), new RectF(b.X + 10, b.Y, b.W - 32, b.H), Theme.Body, Enabled ? Theme.Text : Theme.TextFaint, HAlign.Left, VAlign.Middle, true);
        Icons.Chevron(g, b.Right - 14, b.CY, 8, Enabled ? Theme.TextDim : Theme.TextFaint, 0);
    }
}
