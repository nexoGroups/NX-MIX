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
    public bool Danger;
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
    int _hover = -1;
    float _t;
    static float ItemH => Theme.MenuItemH;
    const float SepH = 9f, Pad = 5f;
    float HeaderH => Theme.Touch ? 30 : 24;

    PopupMenu(RectF anchor, List<MenuItem> items, float minWidth) { _anchor = anchor; _items = items; _minWidth = minWidth; }

    public static PopupMenu Show(UiRoot root, RectF anchor, List<MenuItem> items, float minWidth = 0)
    {
        var m = new PopupMenu(anchor, items, minWidth);
        root.ShowOverlay(m);
        return m;
    }

    public IReadOnlyList<MenuItem> Items => _items;

    float H(MenuItem it) => it.IsSeparator ? SepH : it.IsHeader ? HeaderH : ItemH;
    bool HasIcons => _items.Any(i => i.Icon != Icon.None || i.Checked);

    public override void Layout()
    {
        float w = Math.Max(_minWidth, 180);
        foreach (var it in _items)
        {
            float tw = Renderer.MeasureWidth(it.Text, it.IsHeader ? Theme.CaptionStrong : Theme.Body) + 24 + (HasIcons ? 26 : 0);
            if (it.Shortcut != null) tw += Renderer.MeasureWidth(it.Shortcut, Theme.Callout) + 28;
            w = Math.Max(w, tw);
        }
        float h = Pad * 2 + _items.Sum(H);
        float x = _anchor.X, y = _anchor.Bottom + 6;
        if (x + w > Bounds.W - 8) x = Bounds.W - 8 - w;
        if (x < 8) x = 8;
        if (y + h > Bounds.H - 8) y = Math.Max(8, _anchor.Y - 6 - h);
        _box = new RectF(x, y, w, h);
    }

    RectF ItemRect(int idx)
    {
        float y = _box.Y + Pad;
        for (int i = 0; i < idx; i++) y += H(_items[i]);
        return new RectF(_box.X + Pad, y, _box.W - Pad * 2, H(_items[idx]));
    }

    public int IndexOf(string text) => _items.FindIndex(i => i.Text == text);
    public RectF RectOf(int idx) => ItemRect(idx);

    int IndexAt(float x, float y)
    {
        if (!_box.Contains(x, y)) return -1;
        for (int i = 0; i < _items.Count; i++)
            if (!_items[i].IsSeparator && !_items[i].IsHeader && ItemRect(i).Contains(x, y)) return i;
        return -1;
    }

    public override CursorKind CursorAt(float x, float y) { int i = IndexAt(x, y); return i >= 0 && _items[i].Enabled ? CursorKind.Hand : CursorKind.Arrow; }

    public override void OnMouseMove(in MouseEvent e)
    {
        int i = IndexAt(e.X, e.Y);
        if (i != _hover) { _hover = i; Invalidate(); }
    }

    public override void OnMouseDown(in MouseEvent e)
    {
        if (!_box.Contains(e.X, e.Y)) Close();
    }

    public override void OnMouseUp(in MouseEvent e)
    {
        int i = IndexAt(e.X, e.Y);
        if (i < 0 || !_items[i].Enabled) return;
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
            if (!_items[i].IsSeparator && !_items[i].IsHeader && _items[i].Enabled) { _hover = i; break; }
        }
        Invalidate();
    }

    public override bool Tick(float dt)
    {
        if (_t >= 1) return false;
        _t = Math.Min(1, _t + dt / 0.12f);
        return true;
    }

    public override void Paint(Renderer g)
    {
        float a = 1 - MathF.Pow(1 - _t, 3);
        var box = _box.Offset(0, (1 - a) * -4);
        g.Shadow(box, 12, 16, 1.4f * a, 6);
        g.FillRound(box, 12, Theme.Elevated);
        g.StrokeRound(box, 12, Theme.IsDark ? Theme.BorderStrong : Theme.Border);
        bool icons = HasIcons;
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            var r = ItemRect(i).Offset(0, box.Y - _box.Y);
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
            if (icons)
            {
                if (it.Checked) Icons.Draw(g, Icon.Check, r.X + 18, r.CY, 15, hot ? Theme.OnAccent : Theme.AccentText);
                else if (it.Icon != Icon.None) Icons.Draw(g, it.Icon, r.X + 18, r.CY, 16, it.Danger && it.Enabled ? Theme.Red : hot ? Theme.OnAccent : it.Enabled ? Theme.TextSecondary : Theme.TextTertiary);
                tx = r.X + 34;
            }
            g.Text(it.Text, new RectF(tx, r.Y, r.Right - tx - 10, r.H), Theme.Body, col);
            if (it.Shortcut != null) g.Text(it.Shortcut, new RectF(r.X, r.Y, r.W - 10, r.H), Theme.Callout, hot ? Theme.OnAccent.With(0.8f) : Theme.TextTertiary, HAlign.Right);
        }
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
    float _t;

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
    public override void OnMouseMove(in MouseEvent e) { int h = HitBtn(e.X, e.Y); if (h != _hover) { _hover = h; Invalidate(); } }
    public override void OnMouseDown(in MouseEvent e) { _press = HitBtn(e.X, e.Y); Invalidate(); }
    public override void OnMouseUp(in MouseEvent e)
    {
        int h = HitBtn(e.X, e.Y);
        if (h >= 0 && h == _press) Finish(h);
        _press = -1;
    }

    public override void OnKeyDown(int vk, bool ctrl, bool shift)
    {
        if (vk == Win32.VK_ESCAPE) Finish(0);
        else if (vk == Win32.VK_RETURN) Finish(_default);
    }

    public override bool Tick(float dt)
    {
        if (_t >= 1) return false;
        _t = Math.Min(1, _t + dt / 0.16f);
        return true;
    }

    public override void Layout()
    {
        var b = Bounds;
        float btnSum = _buttons.Sum(t => Math.Max(96, Renderer.MeasureWidth(t, Theme.BodyMedium) + 32) + 10) + 38;
        float w = Math.Min(Math.Max(Theme.Touch ? 500 : 460, btnSum), b.W - 40);
        float textW = w - 48;
        float mh = Renderer.TextHeight(_message, Theme.Body, textW);
        float bh = Theme.ControlH;
        float h = 24 + (_icon != Icon.None ? 52 : 0) + 26 + 10 + mh + 24 + bh + 20;
        _box = RectF.Centered(b.CX, b.CY, w, Math.Min(h, b.H - 40));
        _btnRects = new RectF[_buttons.Length];
        float x = _box.Right - 24;
        float by = _box.Bottom - 20 - bh;
        for (int i = _buttons.Length - 1; i >= 0; i--)
        {
            float bw = Math.Max(96, Renderer.MeasureWidth(_buttons[i], Theme.BodyMedium) + 32);
            x -= bw;
            _btnRects[i] = new RectF(x, by, bw, bh);
            x -= 10;
        }
    }

    public override void Paint(Renderer g)
    {
        float a = 1 - MathF.Pow(1 - _t, 3);
        g.Fill(Bounds, Theme.Scrim.With(Theme.Scrim.A * a));
        var box = _box;
        g.Shadow(box, 16, 28, 1.6f * a, 10);
        g.FillRound(box, 16, Theme.Elevated);
        g.StrokeRound(box, 16, Theme.IsDark ? Theme.BorderStrong : Theme.Border);
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
        y += 36;
        float mh = Renderer.TextHeight(_message, Theme.Body, textW);
        g.Text(_message, new RectF(box.X + 24, y, textW, mh + 4), Theme.Body, Theme.TextSecondary, HAlign.Left, VAlign.Top, false, true);

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
    float _t;
    readonly VScroll _sc = new();
    protected RectF Panel { get; private set; }
    protected RectF Body { get; private set; }
    protected float ScrollOffset => _sc.Offset;
    protected const float HeaderH = 74f;
    protected virtual float PreferredWidth => Theme.SheetW;
    protected void ResetScroll() => _sc.Offset = 0;

    public override void Layout()
    {
        var b = Bounds;
        float top = Root?.Top.Bounds.Bottom ?? 0;
        float w = Math.Min(PreferredWidth, b.W - 40);
        Panel = new RectF(b.Right - w - 12, top + 8, w, Math.Max(100, b.Bottom - top - 20));
        Body = new RectF(Panel.X, Panel.Y + HeaderH, Panel.W, Math.Max(0, Panel.H - HeaderH));
        float contentH = LayoutBody(Body.X + 20, Body.Y + 4 - _sc.Offset, Body.W - 40);
        _sc.Update(Body, contentH + 24);
        if (_sc.Offset > 0 && _sc.Offset > Math.Max(0, contentH + 24 - Body.H))
        {
            _sc.Update(Body, contentH + 24);
            LayoutBody(Body.X + 20, Body.Y + 4 - _sc.Offset, Body.W - 40);
        }
    }

    /// <summary>Verteilt die Kinder ab (x, y) mit Breite w und liefert die Inhaltshoehe.</summary>
    protected abstract float LayoutBody(float x, float y, float w);
    protected abstract void PaintHeader(Renderer g, RectF header);
    protected abstract void PaintBody(Renderer g);

    public override Widget? HitTest(float x, float y)
    {
        if (!Visible) return null;
        if (Body.Contains(x, y) || Panel.Contains(x, y))
        {
            for (int i = Children.Count - 1; i >= 0; i--)
            {
                var c = Children[i];
                if (!c.Visible) continue;
                // Kinder im Inhaltsbereich nur innerhalb des sichtbaren Ausschnitts
                if (c.Bounds.Y >= Body.Y - 1 && !Body.Contains(x, y)) continue;
                var h = c.HitTest(x, y);
                if (h != null) return h;
            }
        }
        return this;
    }

    public override void OnMouseDown(in MouseEvent e)
    {
        if (!Panel.Contains(e.X, e.Y)) { Close(); return; }
        if (_sc.Down(e.X, e.Y)) { Layout(); Invalidate(); }
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

    public override bool Tick(float dt)
    {
        if (_t >= 1) return false;
        _t = Math.Min(1, _t + dt / 0.18f);
        return true;
    }

    public override void Paint(Renderer g)
    {
        float a = 1 - MathF.Pow(1 - _t, 3);
        g.Fill(Bounds, Theme.Scrim.With(Theme.Scrim.A * 0.55f * a));
        var p = Panel;
        g.Shadow(p, 16, 24, 1.5f * a, 8);
        g.FillRound(p, 16, Theme.Elevated);
        g.StrokeRound(p, 16, Theme.IsDark ? Theme.BorderStrong : Theme.Border);
        PaintHeader(g, new RectF(p.X, p.Y, p.W, HeaderH));
        g.Fill(new RectF(p.X, p.Y + HeaderH - 1, p.W, 1), Theme.Separator);
        // Kopf-Elemente (Name, Pfeile, Schliessen) ungeschnitten, Inhalt im scrollbaren Ausschnitt
        foreach (var c in Children) if (c.Visible && c.Bounds.Bottom <= Body.Y + 1 && c.Bounds.Y >= p.Y) c.Paint(g);
        g.PushClip(Body.Deflate(1, 0, 1, 8));
        PaintBody(g);
        foreach (var c in Children) if (c.Visible && !(c.Bounds.Bottom <= Body.Y + 1 && c.Bounds.Y >= p.Y)) c.Paint(g);
        g.PopClip();
        _sc.Paint(g);
    }
}
