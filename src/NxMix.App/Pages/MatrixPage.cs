using NxMix.App.Model;
using NxMix.App.Ui;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Raster Eingaenge x Ausgaenge: Klick = Verbindung an/aus, Ziehen/Mausrad = dB, Doppelklick = 0 dB, Rechtsklick = trennen.</summary>
internal sealed class MatrixView : Widget
{
    readonly AppModel _m;
    const float RowHeaderW = 168f;
    const float ColHeaderH = 76f;
    const float SbSize = 8f;
    const float MaxCellW = 132f, MaxCellH = 72f;

    float _cellW = 80, _cellH = 48;
    float _sx, _sy;
    int _hi = -1, _ho = -1;           // Hover-Zelle
    // Mausdruck
    int _di = -1, _do = -1;
    float _downY;
    double _downDb;
    bool _moved, _ignoreUp;
    // Scrollbalken
    int _dragBar;                      // 0 keiner, 1 vertikal, 2 horizontal
    float _barOff;

    public MatrixView(AppModel m) { _m = m; }
    public override bool AcceptsMouse => true;

    RectF CellArea => new(Bounds.X + RowHeaderW, Bounds.Y + ColHeaderH, Math.Max(0, Bounds.W - RowHeaderW), Math.Max(0, Bounds.H - ColHeaderH));
    float ContentW => _m.OutputCount * _cellW;
    float ContentH => _m.InputCount * _cellH;

    public override void Layout()
    {
        int ni = _m.InputCount, no = _m.OutputCount;
        var a = CellArea;
        _cellW = no > 0 ? Math.Clamp(a.W / no, 62f, MaxCellW) : 80;
        _cellH = ni > 0 ? Math.Clamp(a.H / ni, 42f, MaxCellH) : 48;
        ClampScroll();
    }

    /// <summary>Groesse, die das Raster braucht (hoechstens der verfuegbare Platz), damit die Flaeche nicht leer ausgedehnt wird.</summary>
    public (float W, float H) PreferredSize(float availW, float availH)
    {
        int ni = _m.InputCount, no = _m.OutputCount;
        float cw = no > 0 ? Math.Clamp((availW - RowHeaderW) / no, 62f, MaxCellW) : 80;
        float ch = ni > 0 ? Math.Clamp((availH - ColHeaderH) / ni, 42f, MaxCellH) : 48;
        return (Math.Min(availW, RowHeaderW + no * cw + 12), Math.Min(availH, ColHeaderH + ni * ch + 12));
    }

    void ClampScroll()
    {
        var a = CellArea;
        _sx = Math.Clamp(_sx, 0, Math.Max(0, ContentW - a.W));
        _sy = Math.Clamp(_sy, 0, Math.Max(0, ContentH - a.H));
    }

    bool NeedV => ContentH > CellArea.H + 0.5f;
    bool NeedH => ContentW > CellArea.W + 0.5f;

    RectF CellRect(int i, int o)
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

    // ---- Scrollbalken-Geometrie -----------------------------------------------------------------
    (RectF Track, RectF Thumb) VBar()
    {
        var a = CellArea;
        var track = new RectF(a.Right - SbSize - 2, a.Y + 2, SbSize, a.H - 4 - (NeedH ? SbSize + 2 : 0));
        float ratio = a.H / ContentH;
        float th = Math.Max(30, track.H * ratio);
        float maxS = Math.Max(1, ContentH - a.H);
        return (track, new RectF(track.X, track.Y + (track.H - th) * (_sy / maxS), SbSize, th));
    }

    (RectF Track, RectF Thumb) HBar()
    {
        var a = CellArea;
        var track = new RectF(a.X + 2, a.Bottom - SbSize - 2, a.W - 4 - (NeedV ? SbSize + 2 : 0), SbSize);
        float ratio = a.W / ContentW;
        float tw = Math.Max(30, track.W * ratio);
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
            // Ziehen auf eine getrennte Zelle verbindet sie bei 0 dB
            if (Db.IsOff(_downDb)) _downDb = 0;
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
        if (NeedV) { var (t, th) = VBar(); if (t.Inflate(4).Contains(e.X, e.Y)) { _dragBar = 1; _barOff = th.Contains(e.X, e.Y) ? e.Y - th.Y : th.H / 2; DragBar(e); return; } }
        if (NeedH) { var (t, th) = HBar(); if (t.Inflate(4).Contains(e.X, e.Y)) { _dragBar = 2; _barOff = th.Contains(e.X, e.Y) ? e.X - th.X : th.W / 2; DragBar(e); return; } }
        if (!CellAt(e.X, e.Y, out int i, out int o)) return;
        if (e.Button == 1) { _m.SetMatrix(i, o, double.NegativeInfinity); _ignoreUp = true; return; }
        if (e.Button != 0) return;
        _di = i; _do = o; _downY = e.Y; _downDb = _m.Settings.MatrixDb[i][o];
        if (e.Clicks >= 2) { _m.SetMatrix(i, o, 0); _ignoreUp = true; }
        Invalidate();
    }

    public override void OnMouseUp(in MouseEvent e)
    {
        if (_dragBar != 0) { _dragBar = 0; return; }
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
        // sonst scrollen (Alt = waagrecht)
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

    public override string? HintAt(float x, float y) =>
        CellAt(x, y, out _, out _) ? "Klick: verbinden/trennen · Ziehen oder Mausrad: Pegel · Strg/Umschalt: fein · Doppelklick: 0 dB · Rechtsklick: trennen" : null;

    // ---- Zeichnen ------------------------------------------------------------------------------
    public override void Paint(Renderer g)
    {
        var b = Bounds;
        var a = CellArea;
        int ni = _m.InputCount, no = _m.OutputCount;
        g.FillRound(b, Theme.RadiusPanel, Theme.Panel);
        g.StrokeRound(b, Theme.RadiusPanel, Theme.Border);

        // Zellen
        g.PushClip(a);
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

        // Spaltenkoepfe
        g.PushClip(new RectF(a.X, b.Y, a.W, ColHeaderH));
        for (int o = 0; o < no; o++)
        {
            float x = a.X + o * _cellW - _sx;
            if (x + _cellW < a.X || x > a.Right) continue;
            var r = new RectF(x, b.Y + 6, _cellW, ColHeaderH - 8);
            bool hot = o == _ho;
            var oc = _m.Settings.Outputs[o];
            bool prot = AppModel.IsProtected(oc);
            g.Text((o + 1).ToString("00"), new RectF(r.X + 4, r.Y, r.W - 8, 14), Theme.TinyBold, hot ? Theme.Accent : Theme.TextFaint, HAlign.Center, VAlign.Middle, false);
            g.Text(_m.DisplayName(false, o), new RectF(r.X + 5, r.Y + 15, r.W - 10, 34), Theme.SmallBold,
                string.IsNullOrWhiteSpace(oc.Name) ? Theme.TextFaint : hot ? Theme.Text : Theme.TextDim, HAlign.Center, VAlign.Middle, true, true);
            if (prot) Icons.Lock(g, r.CX, r.Bottom - 8, 11, Theme.Protect);
            else Icons.Chevron(g, r.CX, r.Bottom - 7, 7, hot ? Theme.Accent : Theme.TextFaint, 0, 1.3f);
        }
        g.PopClip();

        // Zeilenkoepfe
        g.PushClip(new RectF(b.X, a.Y, RowHeaderW, a.H));
        for (int i = 0; i < ni; i++)
        {
            float y = a.Y + i * _cellH - _sy;
            if (y + _cellH < a.Y || y > a.Bottom) continue;
            var r = new RectF(b.X + 10, y, RowHeaderW - 18, _cellH);
            bool hot = i == _hi;
            var ic = _m.Settings.Inputs[i];
            g.Text((i + 1).ToString("00"), new RectF(r.X, r.Y, 22, r.H), Theme.TinyBold, hot ? Theme.Accent : Theme.TextFaint, HAlign.Left, VAlign.Middle, false);
            g.Text(_m.DisplayName(true, i), new RectF(r.X + 26, r.Y, r.W - 30, r.H), Theme.SmallBold,
                string.IsNullOrWhiteSpace(ic.Name) ? Theme.TextFaint : hot ? Theme.Text : Theme.TextDim, HAlign.Left, VAlign.Middle, true, true);
            Icons.Chevron(g, r.Right - 2, r.CY, 7, hot ? Theme.Accent : Theme.TextFaint, 3, 1.3f);
        }
        g.PopClip();

        // Trennlinien der Koepfe
        g.Fill(new RectF(a.X, b.Y + ColHeaderH - 1, a.W, 1), Theme.Border);
        g.Fill(new RectF(b.X + RowHeaderW - 1, a.Y, 1, a.H), Theme.Border);
        // Beschriftung der Ecke
        g.Text("Eingang ↓   Ausgang →", new RectF(b.X + 12, b.Y + 8, RowHeaderW - 16, ColHeaderH - 12), Theme.Tiny, Theme.TextFaint, HAlign.Left, VAlign.Bottom, false);

        // Scrollbalken
        if (NeedV) { var (t, th) = VBar(); g.FillRound(t, 4, Theme.Well); g.FillRound(th, 4, _dragBar == 1 ? Theme.Accent : Theme.RaisedHi); }
        if (NeedH) { var (t, th) = HBar(); g.FillRound(t, 4, Theme.Well); g.FillRound(th, 4, _dragBar == 2 ? Theme.Accent : Theme.RaisedHi); }
    }

    void PaintCell(Renderer g, RectF cell, int i, int o)
    {
        var r = cell.Deflate(3);
        double v = _m.Settings.MatrixDb[i][o];
        bool on = !Db.IsOff(v);
        bool hover = i == _hi && o == _ho;
        bool cross = i == _hi || o == _ho;
        bool prot = AppModel.IsProtected(_m.Settings.Outputs[o]);
        if (on)
        {
            float level = (float)Math.Clamp((v - Theme.MatrixMinDb) / (Theme.MatrixMaxDb - Theme.MatrixMinDb), 0, 1);
            g.FillRound(r, 7, Theme.Accent.With(0.14f + 0.10f * level));
            g.StrokeRound(r, 7, hover ? Theme.AccentHi : Theme.Accent.With(0.75f), hover ? 1.8f : 1.2f);
            string t = Math.Abs(v) < 0.05 ? "0" : Db.Format(v);
            g.Text(t, new RectF(r.X, r.Y, r.W, r.H - 6), Theme.Cell, v > 0.05 ? Theme.Solo : Theme.Text, HAlign.Center, VAlign.Middle, false);
            // Pegelbalken unten
            var track = new RectF(r.X + 9, r.Bottom - 8, r.W - 18, 3);
            g.FillRound(track, 1.5f, Theme.Well.With(0.8f));
            g.FillRound(new RectF(track.X, track.Y, Math.Max(3, track.W * level), 3), 1.5f, v > 0.05 ? Theme.Solo : Theme.Accent);
        }
        else
        {
            g.FillRound(r, 7, hover ? Theme.CardHover : cross ? Theme.Card.With(0.9f) : Theme.Card.With(0.55f));
            g.StrokeRound(r, 7, hover ? Theme.BorderStrong : Theme.Border.With(0.8f));
            if (hover) Icons.Plus(g, r.CX, r.CY, 10, Theme.TextDim, 1.6f);
            else g.FillCircle(r.CX, r.CY, 1.8f, Theme.TextFaint.With(0.55f));
        }
        if (prot && on && v >= Theme.ProtectedMatrixMaxDb - 0.05)
            Icons.Lock(g, r.Right - 9, r.Y + 9, 9, Theme.Protect);
    }
}

internal sealed class MatrixPage : Page
{
    readonly AppModel _m;
    readonly MatrixView _view;
    readonly TextButton _addIn, _addOut, _addInPair, _addOutPair;

    public override string Title => "Matrix";

    public MatrixPage(AppModel m)
    {
        _m = m;
        _view = Add(new MatrixView(m));
        _addIn = Add(new TextButton("Eingang", ButtonStyle.Primary, true) { Radius = 10 });
        _addOut = Add(new TextButton("Ausgang", ButtonStyle.Primary, true) { Radius = 10 });
        _addInPair = Add(new TextButton("Stereo-Eingang", ButtonStyle.Secondary, true) { Radius = 10 });
        _addOutPair = Add(new TextButton("Stereo-Ausgang", ButtonStyle.Secondary, true) { Radius = 10 });
        _addIn.Clicked += () => m.AddChannels(true, 1);
        _addOut.Clicked += () => m.AddChannels(false, 1);
        _addInPair.Clicked += () => m.AddChannels(true, 2, m.NextPairNames(true));
        _addOutPair.Clicked += () => m.AddChannels(false, 2, m.NextPairNames(false));
        m.StructureChanged += () => { Layout(); Invalidate(); };
        m.ValuesChanged += Invalidate;
    }

    bool Empty => _m.InputCount == 0 || _m.OutputCount == 0;

    public override void OnShow() { Layout(); }

    public override void Layout()
    {
        var area = new RectF(Bounds.X + Theme.Pad, Bounds.Y + 12, Math.Max(0, Bounds.W - 2 * Theme.Pad), Math.Max(0, Bounds.H - 24));
        const float head = 54;
        _view.Visible = !Empty;
        var (pw, ph) = _view.PreferredSize(area.W, Math.Max(0, area.H - head));
        _view.Bounds = new RectF(area.X, area.Y + head, pw, ph);
        _view.Layout();
        _addIn.Visible = _addOut.Visible = _addInPair.Visible = _addOutPair.Visible = Empty;
        if (Empty)
        {
            var card = EmptyCard();
            float cx = card.CX, cy = card.Bottom - 78;
            bool needIn = _m.InputCount == 0, needOut = _m.OutputCount == 0;
            _addIn.Visible = _addInPair.Visible = needIn;
            _addOut.Visible = _addOutPair.Visible = needOut;
            var list = new List<TextButton>();
            if (needIn) { list.Add(_addIn); list.Add(_addInPair); }
            if (needOut) { list.Add(_addOut); list.Add(_addOutPair); }
            float BW(TextButton t) => Renderer.MeasureWidth(t.Text, Theme.BodyBold) + 62;
            float total = list.Sum(BW) + (list.Count - 1) * 12;
            float x = cx - total / 2;
            foreach (var bt in list)
            {
                float w = BW(bt);
                bt.Bounds = new RectF(x, cy, w, 46);
                x += w + 12;
            }
        }
    }

    public override void Paint(Renderer g)
    {
        var area = new RectF(Bounds.X + Theme.Pad, Bounds.Y + 12, Bounds.W - 2 * Theme.Pad, 44);
        g.Text("Matrix", new RectF(area.X + 2, area.Y, 200, 26), Theme.Heading, Theme.Text);
        string sub = $"{Fmt.Inputs(_m.InputCount)} × {Fmt.Outputs(_m.OutputCount)}  ·  Klick verbindet/trennt  ·  Ziehen oder Mausrad ändert den Pegel (Strg/Umschalt = fein)  ·  Doppelklick = 0 dB";
        g.Text(sub, new RectF(area.X + 2, area.Y + 26, area.W, 18), Theme.Small, Theme.TextFaint, HAlign.Left, VAlign.Middle, true);
        if (Empty) PaintEmpty(g);
        PaintChildren(g);
    }

    RectF EmptyCard() =>
        RectF.Centered(Bounds.CX, Bounds.Y + 66 + (Bounds.H - 66) / 2, Math.Max(100, Math.Min(680, Bounds.W - 2 * Theme.Pad)), Math.Max(100, Math.Min(340, Bounds.H - 90)));

    void PaintEmpty(Renderer g)
    {
        var card = EmptyCard();
        float cx = card.CX;
        g.FillRound(card, Theme.RadiusPanel, Theme.Panel);
        g.StrokeRound(card, Theme.RadiusPanel, Theme.Border);
        // kleines Raster als Symbol
        float gx = cx - 36, gy = card.Y + 34;
        for (int i = 0; i < 3; i++)
            for (int o = 0; o < 3; o++)
            {
                var c = new RectF(gx + o * 26, gy + i * 20, 22, 16);
                bool on = (i == o);
                g.FillRound(c, 4, on ? Theme.Accent.With(0.3f) : Theme.Card);
                g.StrokeRound(c, 4, on ? Theme.Accent : Theme.Border);
            }
        string head = "Die Matrix wartet auf Kanäle";
        string need = _m.InputCount == 0 && _m.OutputCount == 0 ? "Eingänge und Ausgänge" : _m.InputCount == 0 ? "Eingänge" : "Ausgänge";
        string body = $"Hier verteilst du jeden Eingang auf beliebige Ausgänge – mit eigenem Pegel je Kreuzungspunkt. Lege dafür zuerst {need} an (auch direkt hier), ohne Namen und ohne Vorbelegung.";
        g.Text(head, new RectF(card.X + 20, card.Y + 112, card.W - 40, 28), Theme.Heading, Theme.Text, HAlign.Center);
        g.Text(body, new RectF(card.X + 50, card.Y + 146, card.W - 100, 60), Theme.Body, Theme.TextDim, HAlign.Center, VAlign.Top, false, true);
    }
}
