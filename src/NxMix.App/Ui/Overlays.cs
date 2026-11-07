using NxMix.App.Native;
using Vortice.Mathematics;

namespace NxMix.App.Ui;

internal sealed class MenuItem
{
    public string Text;
    public string? Shortcut;
    public Action? Action;
    public bool Enabled = true;
    public bool IsSeparator;
    /// <summary>Graue Zwischenueberschrift (nicht waehlbar).</summary>
    public bool IsHeader;
    public bool Checked;
    public bool Danger { get; set; }
    public Icon Icon;

    public MenuItem(string text, string? shortcut, Action? action, bool enabled = true) { Text = text; Shortcut = shortcut; Action = action; Enabled = enabled; }
    public static MenuItem Sep => new("", null, null) { IsSeparator = true };
    public static MenuItem Header(string text) => new(text, null, null, false) { IsHeader = true };
}

/// <summary>Aufklappmenue (Apple-Stil, schwebend mit Schatten). Schliesst bei Klick daneben oder Esc.</summary>
internal sealed class PopupMenu : Overlay
{
    readonly List<MenuItem> _items;
    readonly RectF _anchor;
    readonly float _minWidth;
    RectF _box;
    int _hover = -1, _press = -1;
    readonly float[] _rowOffsets;
    readonly bool _hasIcons;
    readonly VScroll _scroll = new();
    RectF ItemView => _box.Deflate(Pad, Pad, _scroll.Needed ? 14 : Pad, Pad);
    static float ItemH => Theme.MenuItemH;
    const float SepH = 9f, Pad = 5f;
    float HeaderH => Theme.Touch ? 30 : 24;

    PopupMenu(RectF anchor, List<MenuItem> items, float minWidth)
    {
        _anchor = anchor;
        _items = items.Count > 0 ? items : new List<MenuItem> { MenuItem.Header("Keine Einträge") };
        _minWidth = minWidth;
        _rowOffsets = new float[_items.Count];
        _hasIcons = _items.Any(i => i.Icon != Icon.None || i.Checked);
    }

    public static PopupMenu Show(UiRoot root, RectF anchor, List<MenuItem> items, float minWidth = 0)
    {
        var m = new PopupMenu(anchor, items, minWidth);
        root.ShowOverlay(m);
        return m;
    }

    public IReadOnlyList<MenuItem> Items => _items;

    float H(MenuItem it) => it.IsSeparator ? SepH : it.IsHeader ? HeaderH : ItemH;
    public override void Layout()
    {
        float w = Math.Max(_minWidth, 180);
        float yOffset = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            _rowOffsets[i] = yOffset;
            yOffset += H(it);
            float tw = Renderer.MeasureWidth(it.Text, it.IsHeader ? Theme.CaptionStrong : Theme.Body) + 24 + (_hasIcons ? 26 : 0);
            if (it.Shortcut != null) tw += Renderer.MeasureWidth(it.Shortcut, Theme.Callout) + 28;
            w = Math.Max(w, tw);
        }
        var area = Bounds.Deflate(8);
        w = Math.Min(w, area.W);
        float h = Math.Min(Pad * 2 + yOffset, area.H);
        float x = _anchor.X, y = _anchor.Bottom + 6;
        x = Math.Clamp(x, area.X, area.Right - w);
        if (y + h > area.Bottom) y = _anchor.Y - 6 - h;
        y = Math.Clamp(y, area.Y, area.Bottom - h);
        _box = new RectF(x, y, w, h);
        _scroll.Update(_box.Deflate(0, Pad), yOffset);
    }

    RectF ItemRect(int idx)
    {
        var view = ItemView;
        float y = view.Y + _rowOffsets[idx] - _scroll.Offset;
        return new RectF(view.X, y, view.W, H(_items[idx]));
    }

    public int IndexOf(string text) => _items.FindIndex(i => i.Text == text);
    public RectF RectOf(int idx) => ItemRect(idx);

    int IndexAt(float x, float y)
    {
        if (!ItemView.Contains(x, y)) return -1;
        int i = RowAt(y - ItemView.Y + _scroll.Offset);
        return !_items[i].IsSeparator && !_items[i].IsHeader && ItemRect(i).Contains(x, y) ? i : -1;
    }

    int RowAt(float offset)
    {
        int i = Array.BinarySearch(_rowOffsets, offset);
        return i >= 0 ? i : Math.Max(0, ~i - 1);
    }

    public override CursorKind CursorAt(float x, float y) { int i = IndexAt(x, y); return i >= 0 && _items[i].Enabled ? CursorKind.Hand : CursorKind.Arrow; }

    public override void OnMouseMove(in MouseEvent e)
    {
        if (_scroll.Dragging) { _scroll.Drag(e.Y); Invalidate(); return; }
        int i = IndexAt(e.X, e.Y);
        if (i != _hover) { _hover = i; Invalidate(); }
    }

    public override void OnMouseDown(in MouseEvent e)
    {
        if (!_box.Contains(e.X, e.Y)) Close();
        else if (e.Button == 0)
        {
            if (_scroll.Down(e.X, e.Y)) _hover = -1;
            else _press = IndexAt(e.X, e.Y);
            Invalidate();
        }
    }

    public override bool OnMouseWheel(in MouseEvent e)
    {
        if (!_box.Contains(e.X, e.Y)) return true;
        _scroll.Wheel(e.Wheel);
        _hover = IndexAt(e.X, e.Y);
        Invalidate(); return true;
    }

    public override void OnMouseUp(in MouseEvent e)
    {
        if (_scroll.Dragging) { _scroll.Up(); Invalidate(); return; }
        int i = IndexAt(e.X, e.Y);
        int pressed = _press;
        _press = -1;
        if (e.Button != 0 || i < 0 || i != pressed || !_items[i].Enabled) return;
        Activate(i);
    }

    void Activate(int i)
    {
        var root = Root;
        var action = _items[i].Action;
        Close();
        action?.Invoke();
        root?.Invalidate();
    }

    public override void OnKeyDown(int vk, bool ctrl, bool shift)
    {
        switch (vk)
        {
            case Win32.VK_ESCAPE: Close(); break;
            case Win32.VK_DOWN: Step(1); break;
            case Win32.VK_UP: Step(-1); break;
            case Win32.VK_RETURN: if (_hover >= 0 && _items[_hover].Enabled) Activate(_hover); break;
        }
    }

    void Step(int d)
    {
        if (_items.Count == 0) return;
        int i = _hover;
        for (int n = 0; n < _items.Count; n++)
        {
            i = ((i < 0 ? (d > 0 ? -1 : 0) : i) + d + _items.Count) % _items.Count;
            if (!_items[i].IsSeparator && !_items[i].IsHeader && _items[i].Enabled)
            {
                _hover = i;
                float top = _rowOffsets[i], bottom = top + H(_items[i]);
                if (top < _scroll.Offset) _scroll.Offset = top;
                else if (bottom > _scroll.Offset + ItemView.H)
                    _scroll.Offset = Math.Min(bottom - ItemView.H, Math.Max(0, _scroll.ContentH - ItemView.H));
                break;
            }
        }
        Invalidate();
    }

    public override void Paint(Renderer g)
    {
        var box = _box;
        g.Shadow(box, 12, 16, 1.4f, 6);
        g.FillRound(box, Theme.RadiusCard, Theme.Elevated);
        g.StrokeRound(box, Theme.RadiusCard, Theme.IsDark ? Theme.BorderStrong : Theme.Border);
        g.PushClip(ItemView);
        for (int i = RowAt(_scroll.Offset); i < _items.Count; i++)
        {
            var it = _items[i];
            var r = ItemRect(i);
            if (r.Y >= ItemView.Bottom) break;
            if (it.IsSeparator) { g.Fill(new RectF(r.X + 8, r.CY, r.W - 16, 1), Theme.Separator); continue; }
            if (it.IsHeader)
            {
                g.Text(it.Text, new RectF(r.X + 10, r.Y + 2, r.W - 20, r.H), Theme.CaptionStrong, Theme.TextTertiary, HAlign.Left, VAlign.Middle, true);
                continue;
            }
            bool hot = i == _hover && it.Enabled;
            if (hot) g.FillRound(r, 7, it.Danger ? Theme.Soft(Theme.Red) : Theme.Accent);
            var col = !it.Enabled ? Theme.TextTertiary : it.Danger ? Theme.Red : hot ? Theme.OnAccent : Theme.Text;
            float tx = r.X + 10;
            if (_hasIcons)
            {
                if (it.Checked) Icons.Draw(g, Icon.Check, r.X + 18, r.CY, 15, hot ? Theme.OnAccent : Theme.AccentText);
                else if (it.Icon != Icon.None) Icons.Draw(g, it.Icon, r.X + 18, r.CY, 16, it.Danger && it.Enabled ? Theme.Red : hot ? Theme.OnAccent : it.Enabled ? Theme.TextSecondary : Theme.TextTertiary);
                tx = r.X + 34;
            }
            float shortcutSpace = it.Shortcut == null ? 0 : Renderer.MeasureWidth(it.Shortcut, Theme.Callout) + 16;
            g.Text(it.Text, new RectF(tx, r.Y, r.Right - tx - 10 - shortcutSpace, r.H), Theme.Body, col);
            if (it.Shortcut != null) g.Text(it.Shortcut, new RectF(r.X, r.Y, r.W - 10, r.H), Theme.Callout, hot ? Theme.OnAccent.With(0.8f) : Theme.TextTertiary, HAlign.Right);
        }
        g.PopClip();
        _scroll.Paint(g);
    }
}

/// <summary>Hinweis-/Bestaetigungsdialog (modal, Apple-Alert): Titel, Text, Tasten. Enter = Vorgabe, Esc = erste Taste.</summary>
internal sealed class ModalDialog : Overlay
{
    readonly string _title, _message;
    readonly string[] _buttons;
    readonly int _default, _danger;
    readonly Action<int> _result;
    readonly Icon _icon;
    RectF _box;
    RectF[] _btnRects = Array.Empty<RectF>();
    int _hover = -1, _press = -1;
    RectF _messageView;
    readonly VScroll _messageScroll = new();

    ModalDialog(string title, string message, string[] buttons, int def, int danger, Action<int> result, Icon icon)
    { _title = title; _message = message; _buttons = buttons; _default = def; _danger = danger; _result = result; _icon = icon; }

    public static void Show(UiRoot root, string title, string message, string[] buttons, int defaultButton, int dangerButton, Action<int> result, Icon icon = Icon.None) =>
        root.ShowOverlay(new ModalDialog(title, message, buttons, defaultButton, dangerButton, result, icon));

    public string TitleText => _title;

    void Finish(int idx)
    {
        Close();
        _result(idx);
    }

    int HitBtn(float x, float y)
    {
        for (int i = 0; i < _btnRects.Length; i++) if (_btnRects[i].Contains(x, y)) return i;
        return -1;
    }

    public override CursorKind CursorAt(float x, float y) => HitBtn(x, y) >= 0 ? CursorKind.Hand : CursorKind.Arrow;
    public override void OnMouseMove(in MouseEvent e)
    {
        if (_messageScroll.Dragging) { _messageScroll.Drag(e.Y); Invalidate(); return; }
        int h = HitBtn(e.X, e.Y);
        if (h != _hover) { _hover = h; Invalidate(); }
    }
    public override void OnMouseLeave() { _hover = -1; Invalidate(); }
    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button != 0) return;
        if (!_messageScroll.Down(e.X, e.Y)) _press = HitBtn(e.X, e.Y);
        Invalidate();
    }
    public override void OnMouseUp(in MouseEvent e)
    {
        _messageScroll.Up();
        int h = HitBtn(e.X, e.Y);
        int pressed = _press;
        _press = -1;
        if (e.Button == 0 && h >= 0 && h == pressed) Finish(h);
    }

    public override bool OnMouseWheel(in MouseEvent e)
    {
        if (_box.Contains(e.X, e.Y) && _messageScroll.Wheel(e.Wheel)) Invalidate();
        return true;
    }

    public override void OnKeyDown(int vk, bool ctrl, bool shift)
    {
        if (vk == Win32.VK_ESCAPE) Finish(0);
        else if (vk == Win32.VK_RETURN) Finish(_default);
    }

    public override void Layout()
    {
        var b = Bounds.Deflate(20);
        var widths = _buttons.Select(t => Math.Max(96, Renderer.MeasureWidth(t, Theme.BodyMedium) + 32)).ToArray();
        float btnSum = widths.Sum();
        float gaps = (_buttons.Length - 1) * 10;
        float w = Math.Min(Math.Max(Theme.Touch ? 500 : 460, btnSum + gaps + 48), b.W);
        float textW = Math.Max(0, w - 48 - 12);
        float mh = Renderer.TextHeight(_message, Theme.Body, textW) + 4;
        float bh = Theme.ControlH;
        float head = 24 + (_icon != Icon.None ? 52 : 0) + 36;
        float h = Math.Min(head + mh + 20 + bh + 20, b.H);
        _box = RectF.Centered(b.CX, b.CY, w, h);
        _btnRects = new RectF[_buttons.Length];
        float x = _box.Right - 24;
        float by = _box.Bottom - 20 - bh;
        float fit = Math.Clamp((w - 48 - gaps) / btnSum, 0, 1);
        for (int i = _buttons.Length - 1; i >= 0; i--)
        {
            float bw = widths[i] * fit;
            x -= bw;
            _btnRects[i] = new RectF(x, by, bw, bh);
            x -= 10;
        }
        float y = _box.Y + head;
        _messageView = RectF.FromLTRB(_box.X + 24, y, _box.Right - 24, by - 20);
        _messageScroll.Update(_messageView, mh);
    }

    public override void Paint(Renderer g)
    {
        g.Fill(Bounds, Theme.Scrim);
        var box = _box;
        g.Shadow(box, 16, 28, 1.6f, 10);
        g.FillRound(box, Theme.RadiusCard, Theme.Elevated);
        g.StrokeRound(box, Theme.RadiusCard, Theme.IsDark ? Theme.BorderStrong : Theme.Border);
        float y = box.Y + 24;
        if (_icon != Icon.None)
        {
            var ic = _danger >= 0 ? Theme.Red : _icon == Icon.Warn ? Theme.Orange : Theme.AccentText;
            g.FillRound(new RectF(box.X + 24, y, 40, 40), 10, Theme.Soft(ic));
            Icons.Draw(g, _icon, box.X + 44, y + 20, 22, ic);
            y += 52;
        }
        float textW = box.W - 48;
        g.Text(_title, new RectF(box.X + 24, y, textW, 26), Theme.Title, Theme.Text);
        g.PushClip(_messageView);
        g.Text(_message, new RectF(_messageView.X, _messageView.Y - _messageScroll.Offset, _messageView.W - 12, _messageScroll.ContentH),
            Theme.Body, Theme.TextSecondary, HAlign.Left, VAlign.Top, false, true);
        g.PopClip();
        _messageScroll.Paint(g);

        for (int i = 0; i < _buttons.Length; i++)
        {
            var style = i == _danger ? ButtonStyle.Destructive : i == _default ? ButtonStyle.Filled : ButtonStyle.Gray;
            Draw.ButtonLook(g, _btnRects[i], _buttons[i], style, true, i == _hover, _press == i, font: Theme.BodyMedium);
        }
    }
}

/// <summary>
/// Seitenblatt rechts (Inspektor wie bei UniFi/Apple): schwebt ueber dem Inhalt, Klick daneben oder Esc schliesst.
/// Unterklassen setzen ihre Bedienelemente als Kinder in <see cref="Panel"/>.
/// </summary>
internal abstract class SheetOverlay : Overlay
{
    readonly VScroll _sc = new();
    readonly List<Widget> _header = new();
    Motion _bodyReveal;
    protected RectF Panel { get; private set; }
    protected RectF Body { get; private set; }
    RectF BodyClip => Body.Deflate(1, 0, 13, 8);
    protected float ScrollOffset => _sc.Offset;
    protected const float HeaderH = 74f;
    protected virtual float PreferredWidth => Theme.SheetW;
    /// <summary>Breite Kanalbearbeitung innerhalb des App-Arbeitsbereichs.</summary>
    protected virtual bool FullPage => false;
    protected void ResetScroll() => _sc.Offset = 0;
    protected void AddHeader(Widget widget) => _header.Add(Add(widget));
    protected void RevealBody()
    {
        if (Root is not { } root) return;
        _bodyReveal.Start(0.55f, 1, root.Time);
        Invalidate();
    }

    public override void Layout()
    {
        var b = Bounds;
        float top = Root?.Top.Bounds.Bottom ?? 0;
        float w = Math.Min(PreferredWidth, b.W - 40);
        Panel = FullPage && Root is { } root
            ? new RectF(root.ContentRect.X + 8, top + 8, Math.Max(100, root.ContentRect.W - 16), Math.Max(100, b.Bottom - top - 16))
            : new RectF(b.Right - w - 12, top + 8, w, Math.Max(100, b.Bottom - top - 20));
        Body = new RectF(Panel.X, Panel.Y + HeaderH, Panel.W, Math.Max(0, Panel.H - HeaderH));
        float oldOffset = _sc.Offset;
        float contentH = LayoutBody(Body.X + 20, Body.Y + 4 - oldOffset, Body.W - 40);
        _sc.Update(Body.Deflate(0, 0, 0, 8), contentH + 16);
        if (oldOffset != _sc.Offset)
            LayoutBody(Body.X + 20, Body.Y + 4 - _sc.Offset, Body.W - 40);
    }

    /// <summary>Verteilt die Kinder ab (x, y) mit Breite w und liefert die Inhaltshoehe.</summary>
    protected abstract float LayoutBody(float x, float y, float w);
    protected abstract void PaintHeader(Renderer g, RectF header);
    protected abstract void PaintBody(Renderer g);

    public override Widget? HitTest(float x, float y)
    {
        if (!Visible) return null;
        if (BodyClip.Contains(x, y))
        {
            for (int i = Children.Count - 1; i >= 0; i--)
            {
                var c = Children[i];
                if (_header.Contains(c)) continue;
                var h = c.HitTest(x, y);
                if (h != null) return h;
            }
        }
        else if (Panel.Contains(x, y) && y < Body.Y)
            foreach (var c in _header)
                if (c.HitTest(x, y) is { } h) return h;
        return this;
    }

    public override void OnMouseDown(in MouseEvent e)
    {
        if (!Panel.Contains(e.X, e.Y)) { Close(); return; }
        if (e.Button == 0 && _sc.Down(e.X, e.Y)) { Layout(); Invalidate(); }
    }

    public override void OnMouseMove(in MouseEvent e)
    {
        if (_sc.Dragging && IsPressed) { _sc.Drag(e.Y); Layout(); Invalidate(); }
    }

    public override void OnMouseUp(in MouseEvent e) { if (_sc.Dragging) { _sc.Up(); Invalidate(); } }

    public override bool OnMouseWheel(in MouseEvent e)
    {
        if (_sc.Wheel(e.Wheel)) { Layout(); Invalidate(); }
        return true;
    }

    public override void OnKeyDown(int vk, bool ctrl, bool shift)
    {
        if (vk == Win32.VK_ESCAPE) Close();
    }

    public override void Paint(Renderer g)
    {
        if (!FullPage) g.Fill(Bounds, Theme.Scrim.With(Theme.Scrim.A * 0.55f));
        var p = Panel;
        if (FullPage) g.Fill(p, Theme.Elevated);
        else
        {
            g.Shadow(p, Theme.RadiusCard, 24, 1.5f, 8);
            g.FillRound(p, Theme.RadiusCard, Theme.Elevated);
            g.StrokeRound(p, Theme.RadiusCard, Theme.IsDark ? Theme.BorderStrong : Theme.Border);
        }
        PaintHeader(g, new RectF(p.X, p.Y, p.W, HeaderH));
        g.Fill(new RectF(p.X, p.Y + HeaderH - 1, p.W, 1), Theme.Separator);
        foreach (var c in _header) if (c.Visible) c.Paint(g);
        g.PushClip(BodyClip);
        PaintBody(g);
        foreach (var c in Children)
            if (c.Visible && !_header.Contains(c) && !c.Bounds.Intersect(BodyClip).IsEmpty) c.Paint(g);
        float reveal = _bodyReveal.At(Root, 1, Motion.Selection);
        if (reveal < 1) g.Fill(BodyClip, Theme.Elevated.With(1 - reveal));
        g.PopClip();
        _sc.Paint(g);
    }
}
