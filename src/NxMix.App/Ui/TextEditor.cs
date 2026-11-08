using NxMix.App.Native;

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
    Renderer? _lastRenderer;

    public bool Active { get; private set; }
    /// <summary>Kein eigener Hintergrund (das Feld zeichnet der Besitzer, z. B. ein Suchfeld).</summary>
    public bool Bare { get; set; }
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
    float PadX => Bare ? 0 : 10;

    int HitIndex(float x)
    {
        var g = _lastRenderer;
        if (g == null) return _text.Length;
        using var layout = g.CreateLayout(_text, _style);
        float lx = x - (Bounds.X + PadX) + _scroll - AlignOffset(layout);
        layout.HitTestPoint(Math.Max(0, lx), 1, out var trailing, out _, out var m);
        int idx = (int)m.TextPosition + (trailing ? 1 : 0);
        return Math.Clamp(idx, 0, _text.Length);
    }

    float AlignOffset(Vortice.DirectWrite.IDWriteTextLayout layout)
    {
        float inner = Bounds.W - 2 * PadX;
        float w = layout.Metrics.Width;
        if (w >= inner) return 0;
        return _align switch { HAlign.Center => (inner - w) / 2, HAlign.Right => inner - w, _ => 0 };
    }

    public override void OnMouseDown(in MouseEvent e)
    {
        _caret = HitIndex(e.X);
        if (!e.Shift) _anchor = _caret;
        ResetBlink();
    }

    /// <summary>Auswahl ziehen, wenn der Besitzer (z. B. ein Popup) die Maus haelt.</summary>
    public void DragTo(float x) { _caret = HitIndex(x); ResetBlink(); Invalidate(); }

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
        _lastRenderer = g;
        var b = Bounds;
        if (!Bare)
        {
            // Apple-Fokusring: weicher Akzent-Hof + Rand
            g.FillRound(b.Inflate(3), Theme.RadiusControl + 3, Theme.Accent.With(0.25f));
            g.FillRound(b, Theme.RadiusControl, Theme.IsDark ? Theme.Well : Theme.White);
            g.StrokeRound(b, Theme.RadiusControl, Theme.Accent, 1.5f);
        }
        var inner = new RectF(b.X + PadX, b.Y + 1, b.W - 2 * PadX, b.H - 2);

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
            g.FillRound(new RectF(x0 + sx, inner.Y + 4, ex - sx, inner.H - 8), 3, Theme.Accent.With(0.35f));
        }
        if (_text.Length > 0) g.DrawLayout(layout, x0, ty, Theme.Text);
        if (_caretOn) g.Fill(new RectF(MathF.Floor((x0 + cx) * g.Scale) / g.Scale, inner.Y + 5, 1.5f, inner.H - 10), Theme.Accent);
        g.PopClip();
    }
}
