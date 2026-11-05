namespace NxMix.App.Ui;

internal enum CursorKind { Arrow, Hand, IBeam, SizeNS, SizeWE, No }

internal struct MouseEvent
{
    public float X, Y;
    /// <summary>0 = links, 1 = rechts, 2 = Mitte.</summary>
    public int Button;
    public bool Ctrl, Shift, Alt;
    /// <summary>1 = einfacher Klick, 2 = Doppelklick.</summary>
    public int Clicks;
    /// <summary>Mausrad in Rastern (+ = nach oben/weg vom Nutzer).</summary>
    public float Wheel;
    public readonly bool Fine => Ctrl || Shift;
}

/// <summary>Basis aller selbst gezeichneten Elemente. Bounds sind absolute Fensterkoordinaten in DIPs.</summary>
internal abstract class Widget
{
    public RectF Bounds { get; set; }
    public Widget? Parent { get; internal set; }
    public readonly List<Widget> Children = new();
    public bool Visible { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public string? Tooltip { get; set; }
    public CursorKind Cursor { get; set; } = CursorKind.Arrow;

    public virtual UiRoot? Root => Parent?.Root;

    public T Add<T>(T w) where T : Widget { w.Parent = this; Children.Add(w); return w; }

    public void Invalidate() => Root?.Invalidate();

    public virtual void Layout() { }
    public virtual void Paint(Renderer g) => PaintChildren(g);

    protected void PaintChildren(Renderer g)
    {
        foreach (var c in Children) if (c.Visible) c.Paint(g);
    }

    public virtual bool AcceptsMouse => false;

    public virtual Widget? HitTest(float x, float y)
    {
        if (!Visible || !Bounds.Contains(x, y)) return null;
        for (int i = Children.Count - 1; i >= 0; i--)
        {
            var h = Children[i].HitTest(x, y);
            if (h != null) return h;
        }
        return AcceptsMouse ? this : null;
    }

    public virtual string? TooltipAt(float x, float y) => Tooltip;
    public virtual CursorKind CursorAt(float x, float y) => Enabled ? Cursor : CursorKind.Arrow;

    public virtual void OnMouseEnter() { }
    public virtual void OnMouseLeave() { }
    public virtual void OnMouseDown(in MouseEvent e) { }
    public virtual void OnMouseMove(in MouseEvent e) { }
    public virtual void OnMouseUp(in MouseEvent e) { }
    public virtual bool OnMouseWheel(in MouseEvent e) => false;

    public bool IsHovered => Root?.Hovered == this;
    public bool IsPressed => Root?.Captured == this;
}

/// <summary>Eine Seite der Navigation.</summary>
internal abstract class Page : Widget
{
    public abstract string Title { get; }
    /// <summary>Zweite Zeile in der Kopfleiste (z. B. Kanalzahlen).</summary>
    public virtual string? Subtitle => null;
    public abstract Icon Icon { get; }
    public virtual void OnShow() { }
    public virtual void Tick(float dt) { }
}

/// <summary>
/// Ebene ueber der Oberflaeche (Menue, Dialog, Seitenblatt). Liegt auf einem Stapel; nur die oberste bekommt Eingaben.
/// Kind-Widgets der Ebene werden wie normale Widgets bedient (Hover, Ziehen, Tooltip).
/// </summary>
internal abstract class Overlay : Widget
{
    public override bool AcceptsMouse => true;
    public virtual void OnKeyDown(int vk, bool ctrl, bool shift) { }
    public virtual void OnChar(char c) { }
    public virtual void OnClosed() { }
    /// <summary>Takt (ca. 60 Hz) solange die Ebene offen ist; true = neu zeichnen.</summary>
    public virtual bool Tick(float dt) => false;
    public void Close() => Root?.CloseOverlay(this);
}
