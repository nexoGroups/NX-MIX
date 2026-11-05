using NxMix.App.Model;
using NxMix.App.Ui;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Raster Eingaenge x Ausgaenge: Klick = Verbindung an/aus, Ziehen/Mausrad = dB, Doppelklick = 0 dB, Rechtsklick = trennen.</summary>
internal sealed class MatrixView : Widget
{
    readonly AppModel _m;
    public const float RowHeaderW = 176f;
    public const float ColHeaderH = 82f;
    const float SbSize = 6f;
    const float MaxCellW = 128f, MaxCellH = 68f;
    public static float MinCellW => Theme.Touch ? 76f : 64f;
    public static float MinCellH => Theme.Touch ? 52f : 42f;

    float _cellW = 80, _cellH = 48;
    float _sx, _sy;
    int _hi = -1, _ho = -1;
    int _di = -1, _do = -1;
    float _downY;
    double _downDb;
    bool _moved, _ignoreUp;
    int _dragBar;
    float _barOff;

    public MatrixView(AppModel m) { _m = m; }
    public override bool AcceptsMouse => true;

    /// <summary>Kartenrand: Inhalt beginnt 12 DIPs innen.</summary>
    RectF Inner => Bounds.Deflate(12);
    RectF CellArea => new(Inner.X + RowHeaderW, Inner.Y + ColHeaderH, Math.Max(0, Inner.W - RowHeaderW), Math.Max(0, Inner.H - ColHeaderH));
    float ContentW => _m.OutputCount * _cellW;
    float ContentH => _m.InputCount * _cellH;

    public override void Layout()
    {
        int ni = _m.InputCount, no = _m.OutputCount;
        var a = CellArea;
        _cellW = no > 0 ? Math.Clamp(a.W / no, MinCellW, MaxCellW) : 80;
        _cellH = ni > 0 ? Math.Clamp(a.H / ni, MinCellH, MaxCellH) : 48;
        ClampScroll();
    }

    /// <summary>Groesse, die das Raster braucht (hoechstens der verfuegbare Platz).</summary>
    public (float W, float H) PreferredSize(float availW, float availH)
    {
        int ni = _m.InputCount, no = _m.OutputCount;
        float cw = no > 0 ? Math.Clamp((availW - 24 - RowHeaderW) / no, MinCellW, MaxCellW) : 80;
        float ch = ni > 0 ? Math.Clamp((availH - 24 - ColHeaderH) / ni, MinCellH, MaxCellH) : 48;
        return (Math.Min(availW, 24 + RowHeaderW + no * cw + 4), Math.Min(availH, 24 + ColHeaderH + ni * ch + 4));
    }

    void ClampScroll()
    {
        var a = CellArea;
        _sx = Math.Clamp(_sx, 0, Math.Max(0, ContentW - a.W));
        _sy = Math.Clamp(_sy, 0, Math.Max(0, ContentH - a.H));
    }

    bool NeedV => ContentH > CellArea.H + 0.5f;
    bool NeedH => ContentW > CellArea.W + 0.5f;

    public RectF CellRect(int i, int o)
    {
        var a = CellArea;
        return new RectF(a.X + o * _cellW - _sx, a.Y + i * _cellH - _sy, _cellW, _cellH);
    }

    bool CellAt(float x, float y, out int i, out int o)
    {
        i = o = -1;
        var a = CellArea;
        if (!a.Contains(x, y)) return false;
        o = (int)((x - a.X + _sx) / _cellW);
        i = (int)((y - a.Y + _sy) / _cellH);
        return i >= 0 && i < _m.InputCount && o >= 0 && o < _m.OutputCount;
    }

    double MaxFor(int o) => AppModel.IsProtected(_m.Settings.Outputs[o]) ? Theme.ProtectedMatrixMaxDb : Theme.MatrixMaxDb;

    (RectF Track, RectF Thumb) VBar()
    {
        var a = CellArea;
        var track = new RectF(a.Right - SbSize - 2, a.Y + 2, SbSize, a.H - 4 - (NeedH ? SbSize + 2 : 0));
        float th = Math.Max(30, track.H * a.H / ContentH);
        float maxS = Math.Max(1, ContentH - a.H);
        return (track, new RectF(track.X, track.Y + (track.H - th) * (_sy / maxS), SbSize, th));
    }

    (RectF Track, RectF Thumb) HBar()
    {
        var a = CellArea;
        var track = new RectF(a.X + 2, a.Bottom - SbSize - 2, a.W - 4 - (NeedV ? SbSize + 2 : 0), SbSize);
        float tw = Math.Max(30, track.W * a.W / ContentW);
        float maxS = Math.Max(1, ContentW - a.W);
        return (track, new RectF(track.X + (track.W - tw) * (_sx / maxS), track.Y, tw, SbSize));
    }

    // ---- Maus ----------------------------------------------------------------------------------
    public override void OnMouseMove(in MouseEvent e)
    {
        if (_dragBar != 0 && IsPressed) { DragBar(e); return; }
        if (!IsPressed)
        {
            CellAt(e.X, e.Y, out int i, out int o);
            if (i != _hi || o != _ho) { _hi = i; _ho = o; Invalidate(); }
            return;
        }
        if (_di < 0) return;
        float dy = _downY - e.Y;
        if (!_moved && Math.Abs(dy) < 4) return;
        if (!_moved)
        {
            _moved = true;
            if (Db.IsOff(_downDb)) _downDb = 0;   // Ziehen auf eine getrennte Zelle verbindet sie bei 0 dB
        }
        double perPx = e.Fine ? 0.03 : 0.3;
        double v = Math.Clamp(Math.Round((_downDb + dy * perPx) * 10) / 10, Theme.MatrixMinDb, MaxFor(_do));
        if (Math.Abs(v) < 0.2 && !e.Fine) v = 0;
        _m.SetMatrix(_di, _do, v);
        Invalidate();
    }

    void DragBar(in MouseEvent e)
    {
        var a = CellArea;
        if (_dragBar == 1)
        {
            var (t, th) = VBar();
            float span = t.H - th.H;
            if (span > 0) _sy = Math.Clamp((e.Y - _barOff - t.Y) / span, 0, 1) * Math.Max(0, ContentH - a.H);
        }
        else
        {
            var (t, th) = HBar();
            float span = t.W - th.W;
            if (span > 0) _sx = Math.Clamp((e.X - _barOff - t.X) / span, 0, 1) * Math.Max(0, ContentW - a.W);
        }
        Invalidate();
    }

    public override void OnMouseLeave() { if (_hi >= 0 || _ho >= 0) { _hi = _ho = -1; Invalidate(); } }

    public override void OnMouseDown(in MouseEvent e)
    {
        _di = _do = -1; _moved = false; _ignoreUp = false; _dragBar = 0;
        if (NeedV) { var (t, th) = VBar(); if (t.Inflate(5).Contains(e.X, e.Y)) { _dragBar = 1; _barOff = th.Contains(e.X, e.Y) ? e.Y - th.Y : th.H / 2; DragBar(e); return; } }
        if (NeedH) { var (t, th) = HBar(); if (t.Inflate(5).Contains(e.X, e.Y)) { _dragBar = 2; _barOff = th.Contains(e.X, e.Y) ? e.X - th.X : th.W / 2; DragBar(e); return; } }
        if (!CellAt(e.X, e.Y, out int i, out int o)) return;
        if (e.Button == 1) { _m.SetMatrix(i, o, double.NegativeInfinity); _ignoreUp = true; return; }
        if (e.Button != 0) return;
        _di = i; _do = o; _downY = e.Y; _downDb = _m.Settings.MatrixDb[i][o];
        if (e.Clicks >= 2) { _m.SetMatrix(i, o, 0); _ignoreUp = true; }
        Invalidate();
    }

    public override void OnMouseUp(in MouseEvent e)
    {
        if (_dragBar != 0) { _dragBar = 0; Invalidate(); return; }
        if (_di >= 0 && !_moved && !_ignoreUp && e.Button == 0 && CellAt(e.X, e.Y, out int i, out int o) && i == _di && o == _do)
        {
            double cur = _m.Settings.MatrixDb[i][o];
            _m.SetMatrix(i, o, Db.IsOff(cur) ? 0.0 : double.NegativeInfinity);
        }
        _di = _do = -1;
        Invalidate();
    }

    public override bool OnMouseWheel(in MouseEvent e)
    {
        if (CellAt(e.X, e.Y, out int i, out int o))
        {
            double cur = _m.Settings.MatrixDb[i][o];
            if (!Db.IsOff(cur))
            {
                double step = e.Fine ? 0.1 : 1.0;
                double v = Math.Clamp(Math.Round((cur + Math.Sign(e.Wheel) * step * Math.Max(1, Math.Abs(e.Wheel))) * 10) / 10, Theme.MatrixMinDb, MaxFor(o));
                _m.SetMatrix(i, o, v);
                return true;
            }
        }
        var a = CellArea;
        if (e.Alt) _sx = Math.Clamp(_sx - e.Wheel * 60, 0, Math.Max(0, ContentW - a.W));
        else _sy = Math.Clamp(_sy - e.Wheel * 60, 0, Math.Max(0, ContentH - a.H));
        Invalidate();
        return true;
    }

    public override CursorKind CursorAt(float x, float y) => CellAt(x, y, out _, out _) ? CursorKind.Hand : CursorKind.Arrow;

    public override string? TooltipAt(float x, float y)
    {
        if (!CellAt(x, y, out int i, out int o)) return null;
        double v = _m.Settings.MatrixDb[i][o];
        string head = $"{_m.DisplayName(true, i)} → {_m.DisplayName(false, o)}";
        string state = Db.IsOff(v) ? "getrennt" : Db.Format(v) + " dB";
        string extra = AppModel.IsProtected(_m.Settings.Outputs[o]) ? "  (Gain-Sperre: max. 0 dB)" : "";
        return $"{head}: {state}{extra}";
    }

    // ---- Zeichnen ------------------------------------------------------------------------------
    public override void Paint(Renderer g)
    {
        var b = Bounds;
        Draw.Card(g, b);
        var inner = Inner;
        var a = CellArea;
        int ni = _m.InputCount, no = _m.OutputCount;

        // Fadenkreuz: Zeile/Spalte unter der Maus leicht hervorheben
        g.PushClip(a);
        if (_hi >= 0) g.FillRound(new RectF(a.X, a.Y + _hi * _cellH - _sy + 1, Math.Min(a.W, ContentW - _sx), _cellH - 2), 8, Theme.Accent.With(0.05f));
        if (_ho >= 0) g.FillRound(new RectF(a.X + _ho * _cellW - _sx + 1, a.Y, _cellW - 2, Math.Min(a.H, ContentH - _sy)), 8, Theme.Accent.With(0.05f));
        for (int i = 0; i < ni; i++)
        {
            for (int o = 0; o < no; o++)
            {
                var r = CellRect(i, o);
                if (r.Right < a.X || r.X > a.Right || r.Bottom < a.Y || r.Y > a.Bottom) continue;
                PaintCell(g, r, i, o);
            }
        }
        g.PopClip();

        // Spaltenkoepfe (Ausgaenge)
        g.PushClip(new RectF(a.X, inner.Y, a.W, ColHeaderH));
        for (int o = 0; o < no; o++)
        {
            float x = a.X + o * _cellW - _sx;
            if (x + _cellW < a.X || x > a.Right) continue;
            var r = new RectF(x + 3, inner.Y + 4, _cellW - 6, ColHeaderH - 12);
            bool hot = o == _ho;
            var oc = _m.Settings.Outputs[o];
            bool prot = AppModel.IsProtected(oc);
            if (hot) g.FillRound(r, 8, Theme.AccentSoft);
            g.Text($"OUT {o + 1:00}", new RectF(r.X, r.Y + 6, r.W, 14), Theme.MicroStrong, hot ? Theme.AccentText : Theme.TextTertiary, HAlign.Center, VAlign.Middle, false);
            g.Text(_m.DisplayName(false, o), new RectF(r.X + 4, r.Y + 21, r.W - 8, 34), Theme.CalloutStrong,
                string.IsNullOrWhiteSpace(oc.Name) ? Theme.TextTertiary : Theme.Text, HAlign.Center, VAlign.Middle, true, true);
            if (prot) Icons.Draw(g, Icon.Lock, r.CX, r.Bottom - 8, 12, Theme.Orange);
        }
        g.PopClip();

        // Zeilenkoepfe (Eingaenge)
        g.PushClip(new RectF(inner.X, a.Y, RowHeaderW, a.H));
        for (int i = 0; i < ni; i++)
        {
            float y = a.Y + i * _cellH - _sy;
            if (y + _cellH < a.Y || y > a.Bottom) continue;
            var r = new RectF(inner.X, y + 3, RowHeaderW - 10, _cellH - 6);
            bool hot = i == _hi;
            var ic = _m.Settings.Inputs[i];
            if (hot) g.FillRound(r, 8, Theme.AccentSoft);
            g.Text($"IN {i + 1:00}", new RectF(r.X + 10, r.Y, 40, r.H), Theme.MicroStrong, hot ? Theme.AccentText : Theme.TextTertiary, HAlign.Left, VAlign.Middle, false);
            bool named = !string.IsNullOrWhiteSpace(ic.Name) || _m.LinkAt(i) != null;
            g.Text(_m.DisplayName(true, i), new RectF(r.X + 52, r.Y, r.W - 58, r.H), Theme.CalloutStrong,
                named ? Theme.Text : Theme.TextTertiary, HAlign.Left, VAlign.Middle, true, true);
        }
        g.PopClip();

        g.Text("Eingang ↓  ·  Ausgang →", new RectF(inner.X + 10, inner.Y + 4, RowHeaderW - 16, ColHeaderH - 12), Theme.Caption, Theme.TextTertiary, HAlign.Left, VAlign.Bottom, false);

        if (NeedV) { var (_, th) = VBar(); g.FillPill(th, _dragBar == 1 ? Theme.TextSecondary.With(0.8f) : Theme.TextTertiary.With(0.5f)); }
        if (NeedH) { var (_, th) = HBar(); g.FillPill(th, _dragBar == 2 ? Theme.TextSecondary.With(0.8f) : Theme.TextTertiary.With(0.5f)); }
    }

    void PaintCell(Renderer g, RectF cell, int i, int o)
    {
        var r = cell.Deflate(3);
        double v = _m.Settings.MatrixDb[i][o];
        bool on = !Db.IsOff(v);
        bool hover = i == _hi && o == _ho;
        bool prot = AppModel.IsProtected(_m.Settings.Outputs[o]);
        if (on)
        {
            float level = (float)Math.Clamp((v - Theme.MatrixMinDb) / (Theme.MatrixMaxDb - Theme.MatrixMinDb), 0, 1);
            bool boost = v > 0.05;
            g.FillRound(r, 8, hover ? (boost ? Theme.Yellow.With(0.32f) : Theme.AccentHover) : (boost ? Theme.Soft(Theme.Yellow) : Theme.Accent));
            var fg = boost ? (Theme.IsDark ? Theme.Yellow : Theme.Rgb(0x8A6500)) : Theme.OnAccent;
            string t = Math.Abs(v) < 0.05 ? "0 dB" : Db.Format(v);
            g.Text(t, new RectF(r.X, r.Y, r.W, r.H - 6), Theme.CalloutStrong, fg, HAlign.Center, VAlign.Middle, false);
            var track = new RectF(r.X + 10, r.Bottom - 8, r.W - 20, 3);
            g.FillPill(track, boost ? Theme.Yellow.With(0.25f) : Theme.White.With(0.25f));
            g.FillPill(new RectF(track.X, track.Y, Math.Max(3, track.W * level), 3), boost ? Theme.Yellow : Theme.White.With(0.9f));
        }
        else
        {
            g.FillRound(r, 8, hover ? Theme.ControlHover : Theme.Surface2);
            if (hover) Icons.Draw(g, Icon.Plus, r.CX, r.CY, 15, Theme.TextSecondary);
            else g.FillCircle(r.CX, r.CY, 2f, Theme.TextTertiary.With(0.5f));
        }
        if (prot && on && v >= Theme.ProtectedMatrixMaxDb - 0.05)
            Icons.Draw(g, Icon.Lock, r.Right - 9, r.Y + 9, 10, Theme.IsDark ? Theme.Orange : Theme.White);
    }
}

internal sealed class MatrixPage : Page
{
    readonly AppModel _m;
    readonly MatrixView _view;
    readonly Button _addIn, _addOut, _addInPair, _addOutPair;

    public override string Title => "Matrix";
    public override Icon Icon => Icon.Matrix;
    public override string? Subtitle => Empty ? null
        : $"{Fmt.Inputs(_m.InputCount)} × {Fmt.Outputs(_m.OutputCount)}  ·  Klick verbindet/trennt  ·  Ziehen oder Mausrad: Pegel  ·  Doppelklick: 0 dB  ·  Rechtsklick: trennen";
    internal MatrixView View => _view;

    public MatrixPage(AppModel m)
    {
        _m = m;
        _view = Add(new MatrixView(m));
        _addIn = Add(new Button("Eingang", ButtonStyle.Filled, Icon.Plus));
        _addOut = Add(new Button("Ausgang", ButtonStyle.Filled, Icon.Plus));
        _addInPair = Add(new Button("Stereo-Eingang", ButtonStyle.Gray, Icon.Plus));
        _addOutPair = Add(new Button("Stereo-Ausgang", ButtonStyle.Gray, Icon.Plus));
        _addIn.Clicked += () => m.AddChannels(true, 1);
        _addOut.Clicked += () => m.AddChannels(false, 1);
        _addInPair.Clicked += () => m.AddChannels(true, 2, m.NextPairNames(true));
        _addOutPair.Clicked += () => m.AddChannels(false, 2, m.NextPairNames(false));
        m.StructureChanged += () => { Layout(); Invalidate(); };
        m.ValuesChanged += Invalidate;
    }

    bool Empty => _m.InputCount == 0 || _m.OutputCount == 0;

    public override void OnShow() { Layout(); }

    RectF Area => new(Bounds.X + Theme.Pad, Bounds.Y + 2, Math.Max(0, Bounds.W - 2 * Theme.Pad), Math.Max(0, Bounds.H - Theme.Pad - 2));

    public override void Layout()
    {
        var area = Area;
        _view.Visible = !Empty;
        var (pw, ph) = _view.PreferredSize(area.W, area.H);
        _view.Bounds = new RectF(area.X, area.Y, pw, ph);
        _view.Layout();
        _addIn.Visible = _addOut.Visible = _addInPair.Visible = _addOutPair.Visible = false;
        if (Empty)
        {
            var card = EmptyCard();
            bool needIn = _m.InputCount == 0, needOut = _m.OutputCount == 0;
            _addIn.Visible = _addInPair.Visible = needIn;
            _addOut.Visible = _addOutPair.Visible = needOut;
            var list = new List<Button>();
            if (needIn) { list.Add(_addIn); list.Add(_addInPair); }
            if (needOut) { list.Add(_addOut); list.Add(_addOutPair); }
            float total = list.Sum(b => b.PreferredWidth + 10) + (list.Count - 1) * 10;
            float x = card.CX - total / 2;
            float y = card.Bottom - 40 - Theme.ControlH;
            foreach (var bt in list)
            {
                float w = bt.PreferredWidth + 10;
                bt.Bounds = new RectF(x, y, w, Theme.ControlH);
                x += w + 10;
            }
        }
    }

    public override void Paint(Renderer g)
    {
        if (Empty) PaintEmpty(g);
        PaintChildren(g);
    }

    const string EmptyBodyText = "Hier verteilst du jeden Eingang auf beliebige Ausgänge – mit eigenem Pegel je Kreuzungspunkt. Lege dafür zuerst Kanäle an (auch direkt hier), ohne Namen und ohne Vorbelegung.";

    RectF EmptyCard()
    {
        var a = Area;
        float h = Math.Min(a.H, Draw.EmptyHeight(640, EmptyBodyText) + 60 + Theme.ControlH + 40);
        return RectF.Centered(a.CX, a.Y + a.H / 2, Math.Max(100, Math.Min(680, a.W)), Math.Max(100, h));
    }

    void PaintEmpty(Renderer g)
    {
        var card = EmptyCard();
        Draw.Card(g, card);
        string need = _m.InputCount == 0 && _m.OutputCount == 0 ? "Eingänge und Ausgänge fehlen" : _m.InputCount == 0 ? "Es fehlen Eingänge" : "Es fehlen Ausgänge";
        Draw.Empty(g, card, card.Y + 36, Icon.Matrix, "Die Matrix wartet auf Kanäle", EmptyBodyText + " " + need + ".");
    }
}
