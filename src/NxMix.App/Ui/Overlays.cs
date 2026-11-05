using NxMix.App.Native;
using Vortice.Mathematics;

namespace NxMix.App.Ui;

/// <summary>Eigener Mini-Texteditor (eine Zeile): Caret, Auswahl, Maus, Zwischenablage. Ohne Windows-Edit-Steuerelement.</summary>
internal sealed class TextEditor : Widget
{
    string _text = "";
    int _caret, _anchor;
    float _scroll;
    Action<string>? _commit;
    TextStyle _style = Theme.Body;
    HAlign _align;
    int _maxLen = 40;
    Func<char, bool>? _filter;
    double _blink;
    bool _caretOn = true;

    public bool Active { get; private set; }
    /// <summary>Aktueller Inhalt (fuer Filterfelder, die bei jeder Taste mitlesen).</summary>
    public string Text => _text;
    public override bool AcceptsMouse => Active;
    public override CursorKind CursorAt(float x, float y) => CursorKind.IBeam;

    public void Begin(RectF bounds, string text, TextStyle style, Action<string> commit, int maxLen, Func<char, bool>? filter, HAlign align = HAlign.Left)
    {
        if (Active) Commit();
        Bounds = bounds;
        _text = text ?? "";
        _style = style; _commit = commit; _maxLen = maxLen; _filter = filter; _align = align;
        _anchor = 0; _caret = _text.Length;
        _scroll = 0; _blink = 0; _caretOn = true;
        Active = true;
        Visible = true;
        Invalidate();
    }

    public void Commit()
    {
        if (!Active) return;
        Active = false;
        var cb = _commit; _commit = null;
        Invalidate();
        cb?.Invoke(_text);
    }

    public void Cancel()
    {
        if (!Active) return;
        Active = false; _commit = null;
        Invalidate();
    }

    /// <summary>Caret-Blinken. true = neu zeichnen.</summary>
    public bool Tick(float dt)
    {
        _blink += dt;
        bool on = ((int)(_blink / 0.53)) % 2 == 0;
        if (on != _caretOn) { _caretOn = on; return true; }
        return false;
    }

    bool HasSel => _caret != _anchor;
    int SelStart => Math.Min(_caret, _anchor);
    int SelEnd => Math.Max(_caret, _anchor);

    void DeleteSel()
    {
        if (!HasSel) return;
        int s = SelStart, e = SelEnd;
        _text = _text.Remove(s, e - s);
        _caret = _anchor = s;
    }

    void Insert(string s)
    {
        DeleteSel();
        var sb = new System.Text.StringBuilder();
        foreach (char c in s) if (c >= ' ' && (_filter == null || _filter(c))) sb.Append(c);
        string ins = sb.ToString();
        int room = _maxLen - _text.Length;
        if (room <= 0) return;
        if (ins.Length > room) ins = ins[..room];
        _text = _text.Insert(_caret, ins);
        _caret = _anchor = _caret + ins.Length;
        ResetBlink();
    }

    void ResetBlink() { _blink = 0; _caretOn = true; }

    public void OnChar(char c)
    {
        if (c < ' ' || c == 0x7F) return;
        Insert(c.ToString());
    }

    public void OnKeyDown(int vk, bool ctrl, bool shift)
    {
        ResetBlink();
        switch (vk)
        {
            case Win32.VK_RETURN: Commit(); return;
            case Win32.VK_TAB: Commit(); return;
            case Win32.VK_ESCAPE: Cancel(); return;
            case Win32.VK_LEFT:
                if (HasSel && !shift) _caret = _anchor = SelStart;
                else { _caret = ctrl ? WordLeft(_caret) : Math.Max(0, _caret - 1); if (!shift) _anchor = _caret; }
                return;
            case Win32.VK_RIGHT:
                if (HasSel && !shift) _caret = _anchor = SelEnd;
                else { _caret = ctrl ? WordRight(_caret) : Math.Min(_text.Length, _caret + 1); if (!shift) _anchor = _caret; }
                return;
            case Win32.VK_HOME: _caret = 0; if (!shift) _anchor = 0; return;
            case Win32.VK_END: _caret = _text.Length; if (!shift) _anchor = _caret; return;
            case Win32.VK_BACK:
                if (HasSel) DeleteSel();
                else if (_caret > 0) { _text = _text.Remove(_caret - 1, 1); _caret = _anchor = _caret - 1; }
                return;
            case Win32.VK_DELETE:
                if (HasSel) DeleteSel();
                else if (_caret < _text.Length) _text = _text.Remove(_caret, 1);
                return;
            case 'A' when ctrl: _anchor = 0; _caret = _text.Length; return;
            case 'C' when ctrl: if (HasSel) Win32.SetClipboardText(_text[SelStart..SelEnd]); return;
            case 'X' when ctrl:
                if (HasSel) { Win32.SetClipboardText(_text[SelStart..SelEnd]); DeleteSel(); }
                return;
            case 'V' when ctrl:
                { var t = Win32.GetClipboardText(); if (!string.IsNullOrEmpty(t)) Insert(t.Replace("\r", "").Replace("\n", " ")); }
                return;
        }
    }

    int WordLeft(int p)
    {
        while (p > 0 && _text[p - 1] == ' ') p--;
        while (p > 0 && _text[p - 1] != ' ') p--;
        return p;
    }

    int WordRight(int p)
    {
        while (p < _text.Length && _text[p] != ' ') p++;
        while (p < _text.Length && _text[p] == ' ') p++;
        return p;
    }

    // ---- Maus ----------------------------------------------------------------------------------
    int HitIndex(float x)
    {
        var g = LastRenderer;
        if (g == null) return _text.Length;
        using var layout = g.CreateLayout(_text, _style);
        float lx = x - (Bounds.X + 7) + _scroll - AlignOffset(layout);
        layout.HitTestPoint(Math.Max(0, lx), 1, out var trailing, out _, out var m);
        int idx = (int)m.TextPosition + (trailing ? 1 : 0);
        return Math.Clamp(idx, 0, _text.Length);
    }

    float AlignOffset(Vortice.DirectWrite.IDWriteTextLayout layout)
    {
        float inner = Bounds.W - 14;
        float w = layout.Metrics.Width;
        if (w >= inner) return 0;
        return _align switch { HAlign.Center => (inner - w) / 2, HAlign.Right => inner - w, _ => 0 };
    }

    Renderer? LastRenderer;

    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Clicks >= 2) { _anchor = 0; _caret = _text.Length; return; }
        _caret = HitIndex(e.X);
        if (!e.Shift) _anchor = _caret;
        ResetBlink();
    }

    public override void OnMouseMove(in MouseEvent e)
    {
        if (!IsPressed) return;
        _caret = HitIndex(e.X);
        ResetBlink();
        Invalidate();
    }

    // ---- Zeichnen ------------------------------------------------------------------------------
    public override void Paint(Renderer g)
    {
        if (!Active) return;
        LastRenderer = g;
        var b = Bounds;
        g.Shadow(b, 6, 8, 0.45f, 2);
        g.FillRound(b, 6, Theme.Well);
        g.StrokeRound(b, 6, Theme.Accent, 1.5f);
        var inner = new RectF(b.X + 7, b.Y + 1, b.W - 14, b.H - 2);

        using var layout = g.CreateLayout(_text, _style);
        float textH = layout.Metrics.Height;
        float textW = _text.Length == 0 ? 0 : layout.Metrics.Width;
        float ty = inner.Y + (inner.H - textH) / 2;

        layout.HitTestTextPosition((uint)_caret, false, out float cx, out _, out _);
        if (cx - _scroll > inner.W - 2) _scroll = cx - inner.W + 2;
        if (cx - _scroll < 0) _scroll = cx;
        if (textW <= inner.W) _scroll = 0;
        float off = textW < inner.W ? AlignOffset(layout) : 0;
        float x0 = inner.X - _scroll + off;

        g.PushClip(inner);
        if (HasSel)
        {
            layout.HitTestTextPosition((uint)SelStart, false, out float sx, out _, out _);
            layout.HitTestTextPosition((uint)SelEnd, false, out float ex, out _, out _);
            g.Fill(new RectF(x0 + sx, inner.Y + 3, ex - sx, inner.H - 6), Theme.Accent.With(0.38f));
        }
        if (_text.Length > 0) g.DrawLayout(layout, x0, ty, Theme.Text);
        if (_caretOn) g.Fill(new RectF(MathF.Floor((x0 + cx) * g.Scale) / g.Scale, inner.Y + 4, 1.4f, inner.H - 8), Theme.AccentHi);
        g.PopClip();
    }
}

// ================================================================================================

internal sealed class MenuItem
{
    public string Text;
    public string? Shortcut;
    public Action? Action;
    public bool Enabled = true;
    public bool IsSeparator;
    public bool Checked;
    public bool Danger;

    public MenuItem(string text, string? shortcut, Action? action, bool enabled = true) { Text = text; Shortcut = shortcut; Action = action; Enabled = enabled; }
    public static MenuItem Sep => new("", null, null) { IsSeparator = true };
}

/// <summary>Aufklappmenue (Kontextmenue, Auswahlliste). Schliesst bei Klick daneben oder Esc.</summary>
internal sealed class PopupMenu : Overlay
{
    readonly List<MenuItem> _items;
    RectF _anchor;
    RectF _box;
    int _hover = -1;
    float _minWidth;
    const float ItemH = 30f, SepH = 9f, Pad = 6f;

    PopupMenu(RectF anchor, List<MenuItem> items, float minWidth) { _anchor = anchor; _items = items; _minWidth = minWidth; }

    public static void Show(UiRoot root, RectF anchor, List<MenuItem> items, float minWidth = 0)
    {
        var m = new PopupMenu(anchor, items, minWidth);
        root.ShowOverlay(m);
    }

    public override void Layout()
    {
        float w = Math.Max(_minWidth, 150);
        foreach (var it in _items)
        {
            float tw = Renderer.MeasureWidth(it.Text, Theme.Body) + 36;
            if (it.Shortcut != null) tw += Renderer.MeasureWidth(it.Shortcut, Theme.Small) + 28;
            w = Math.Max(w, tw);
        }
        float h = Pad * 2 + _items.Sum(i => i.IsSeparator ? SepH : ItemH);
        float x = _anchor.X, y = _anchor.Bottom + 4;
        if (x + w > Bounds.W - 8) x = Bounds.W - 8 - w;
        if (x < 8) x = 8;
        if (y + h > Bounds.H - 8) y = Math.Max(8, _anchor.Y - 4 - h);
        _box = new RectF(x, y, w, h);
    }

    RectF ItemRect(int idx)
    {
        float y = _box.Y + Pad;
        for (int i = 0; i < idx; i++) y += _items[i].IsSeparator ? SepH : ItemH;
        return new RectF(_box.X + Pad, y, _box.W - Pad * 2, _items[idx].IsSeparator ? SepH : ItemH);
    }

    int IndexAt(float x, float y)
    {
        if (!_box.Contains(x, y)) return -1;
        for (int i = 0; i < _items.Count; i++)
            if (!_items[i].IsSeparator && ItemRect(i).Contains(x, y)) return i;
        return -1;
    }

    public override CursorKind CursorAt(float x, float y) => IndexAt(x, y) >= 0 && _items[IndexAt(x, y)].Enabled ? CursorKind.Hand : CursorKind.Arrow;

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
            if (!_items[i].IsSeparator && _items[i].Enabled) { _hover = i; break; }
        }
        Invalidate();
    }

    public override void Paint(Renderer g)
    {
        g.Shadow(_box, 10, 14, 0.5f, 4);
        g.FillRound(_box, 10, Theme.Rgb(0x1F2227));
        g.StrokeRound(_box, 10, Theme.BorderStrong);
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            var r = ItemRect(i);
            if (it.IsSeparator) { g.Fill(new RectF(r.X + 6, r.CY, r.W - 12, 1), Theme.Border); continue; }
            bool hot = i == _hover && it.Enabled;
            if (hot) g.FillRound(r, 6, it.Danger ? Theme.Mute.With(0.18f) : Theme.Accent.With(0.18f));
            var col = !it.Enabled ? Theme.TextFaint : it.Danger ? Theme.Mute : Theme.Text;
            if (it.Checked) Icons.Chevron(g, r.X + 14, r.CY, 7, Theme.Accent, 0, 2f);
            g.Text(it.Text, new RectF(r.X + 28, r.Y, r.W - 36, r.H), Theme.Body, col);
            if (it.Shortcut != null) g.Text(it.Shortcut, new RectF(r.X, r.Y, r.W - 12, r.H), Theme.Small, Theme.TextFaint, HAlign.Right);
        }
    }
}

// ================================================================================================

/// <summary>Bestaetigungsdialog (modal): Titel, Text, Tasten. Enter = Vorgabe, Esc = erste Taste (Abbrechen).</summary>
internal sealed class ModalDialog : Overlay
{
    readonly string _title, _message;
    readonly string[] _buttons;
    readonly int _default, _danger;
    readonly Action<int> _result;
    RectF _box;
    RectF[] _btnRects = Array.Empty<RectF>();
    int _hover = -1, _press = -1;

    ModalDialog(string title, string message, string[] buttons, int def, int danger, Action<int> result)
    { _title = title; _message = message; _buttons = buttons; _default = def; _danger = danger; _result = result; }

    public static void Show(UiRoot root, string title, string message, string[] buttons, int defaultButton, int dangerButton, Action<int> result) =>
        root.ShowOverlay(new ModalDialog(title, message, buttons, defaultButton, dangerButton, result));

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

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        g.Fill(b, new Color4(0, 0, 0, 0.62f));
        float w = Math.Min(460, b.W - 40);
        float textW = w - 48;
        float mh = Renderer.TextHeight(_message, Theme.Body, textW);
        float h = 24 + 24 + 14 + mh + 26 + 38 + 22;
        _box = RectF.Centered(b.CX, b.CY, w, h);
        g.Shadow(_box, 14, 24, 0.6f, 8);
        g.FillRound(_box, 14, Theme.Rgb(0x1B1E23));
        g.StrokeRound(_box, 14, Theme.BorderStrong);
        g.Text(_title, new RectF(_box.X + 24, _box.Y + 20, textW, 26), Theme.Heading, Theme.Text);
        g.Text(_message, new RectF(_box.X + 24, _box.Y + 58, textW, mh + 4), Theme.Body, Theme.TextDim, HAlign.Left, VAlign.Top, false, true);

        // Tasten rechts unten
        _btnRects = new RectF[_buttons.Length];
        float x = _box.Right - 24;
        float by = _box.Bottom - 22 - 38;
        for (int i = _buttons.Length - 1; i >= 0; i--)
        {
            float bw = Math.Max(96, Renderer.MeasureWidth(_buttons[i], Theme.BodyBold) + 40);
            x -= bw;
            _btnRects[i] = new RectF(x, by, bw, 38);
            x -= 10;
        }
        for (int i = 0; i < _buttons.Length; i++)
        {
            var r = _btnRects[i];
            bool primary = i == _default;
            bool danger = i == _danger;
            bool hot = i == _hover;
            Color4 bg = danger ? Theme.Mute : primary ? Theme.Accent : Theme.Raised;
            if (hot) bg = Theme.Mix(bg, Theme.White, 0.14f);
            if (_press == i) bg = Theme.Mix(bg, Theme.Black, 0.2f);
            g.FillRound(r, 8, bg);
            g.Text(_buttons[i], r, Theme.BodyBold, primary || danger ? Theme.OnAccent : Theme.Text, HAlign.Center);
        }
    }
}
