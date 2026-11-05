using NxMix.App.Model;
using NxMix.App.Ui;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Gruppenflaeche "Eingaenge" oder "Ausgaenge" mit Kanalstreifen, Kopfzeile, Leerzustand und waagrechtem Scrollen.</summary>
internal sealed class GroupPanel : Widget
{
    readonly AppModel _m;
    readonly bool _input;
    public readonly List<ChannelStrip> Strips = new();
    readonly TextButton _addMono, _addPair, _bigMono, _bigPair;
    RectF _viewport;
    float _scroll;
    float _stripW = 70;
    // Scrollbalken
    RectF _barTrack, _barThumb;
    bool _dragBar;
    float _dragBarOff;

    public GroupPanel(AppModel m, bool input)
    {
        _m = m; _input = input;
        string noun = input ? "Eingang" : "Ausgang";
        _addMono = Add(new TextButton("+ " + noun, ButtonStyle.Secondary) { Font = Theme.SmallBold, Radius = 7 });
        _addPair = Add(new TextButton("+ Stereo-Paar", ButtonStyle.Secondary) { Font = Theme.SmallBold, Radius = 7 });
        _bigMono = Add(new TextButton(noun, ButtonStyle.Primary, true) { Radius = 10 });
        _bigPair = Add(new TextButton("Stereo-Paar", ButtonStyle.Secondary, true) { Radius = 10 });
        _addMono.Clicked += AddMono; _bigMono.Clicked += AddMono;
        _addPair.Clicked += AddPair; _bigPair.Clicked += AddPair;
        _addMono.Tooltip = _bigMono.Tooltip = $"Einen {noun} am Ende hinzufügen";
        _addPair.Tooltip = _bigPair.Tooltip = $"Zwei {noun}-Kanäle als Stereo-Paar (L/R) hinzufügen";
    }

    void AddMono() => _m.AddChannels(_input, 1);
    void AddPair() => _m.AddChannels(_input, 2, _m.NextPairNames(_input));

    public int Count => _input ? _m.InputCount : _m.OutputCount;

    public void Rebuild()
    {
        foreach (var s in Strips) Children.Remove(s);
        Strips.Clear();
        for (int i = 0; i < Count; i++)
        {
            var s = new ChannelStrip(_m, _input, i);
            Strips.Add(s);
            Add(s);
            s.Sync();
        }
    }

    public void SyncAll() { foreach (var s in Strips) s.Sync(); }

    public void SetStripWidth(float w) => _stripW = w;

    float ContentWidth => Count * _stripW + Math.Max(0, Count - 1) * Theme.StripGap;

    public override void Layout()
    {
        var b = Bounds;
        bool empty = Count == 0;
        _addMono.Visible = _addPair.Visible = !empty;
        _bigMono.Visible = _bigPair.Visible = empty;

        float btnH = 26;
        float hx = b.Right - Theme.PanelInner;
        float w2 = Renderer.MeasureWidth(_addPair.Text, Theme.SmallBold) + 22;
        float w1 = Renderer.MeasureWidth(_addMono.Text, Theme.SmallBold) + 22;
        _addPair.Bounds = new RectF(hx - w2, b.Y + 9, w2, btnH); hx -= w2 + 6;
        _addMono.Bounds = new RectF(hx - w1, b.Y + 9, w1, btnH);

        var (_, _, btnY) = EmptyMetrics();
        float cx = b.CX;
        float bw = Math.Min(150, (b.W - 60) / 2);
        _bigMono.Bounds = new RectF(cx - bw - 6, btnY, bw, 46);
        _bigPair.Bounds = new RectF(cx + 6, btnY, bw, 46);

        float top = b.Y + Theme.PanelHeaderHeight + 6;
        float barH = ContentWidth > b.W - 2 * Theme.PanelInner + 0.5f ? 12 : 0;
        _viewport = new RectF(b.X + Theme.PanelInner, top, b.W - 2 * Theme.PanelInner, Math.Max(0, b.Bottom - Theme.PanelInner - top - barH));
        float maxScroll = Math.Max(0, ContentWidth - _viewport.W);
        _scroll = Math.Clamp(_scroll, 0, maxScroll);
        float x = _viewport.X - _scroll;
        foreach (var s in Strips)
        {
            s.Bounds = new RectF(x, _viewport.Y, _stripW, _viewport.H);
            s.Layout();
            x += _stripW + Theme.StripGap;
        }
        if (barH > 0)
        {
            _barTrack = new RectF(_viewport.X, _viewport.Bottom + 4, _viewport.W, 6);
            float ratio = _viewport.W / ContentWidth;
            float tw = Math.Max(30, _barTrack.W * ratio);
            float tx = _barTrack.X + (_barTrack.W - tw) * (maxScroll <= 0 ? 0 : _scroll / maxScroll);
            _barThumb = new RectF(tx, _barTrack.Y, tw, 6);
        }
        else _barTrack = _barThumb = default;
    }

    public override Widget? HitTest(float x, float y)
    {
        if (!Visible || !Bounds.Contains(x, y)) return null;
        for (int i = Children.Count - 1; i >= 0; i--)
        {
            var c = Children[i];
            if (c is ChannelStrip && !_viewport.Contains(x, y)) continue;
            var h = c.HitTest(x, y);
            if (h != null) return h;
        }
        return AcceptsMouse ? this : null;
    }

    public override bool AcceptsMouse => true;

    public override void OnMouseDown(in MouseEvent e)
    {
        if (_barThumb.W > 0 && _barTrack.Inflate(4).Contains(e.X, e.Y))
        {
            _dragBar = true;
            _dragBarOff = _barThumb.Contains(e.X, e.Y) ? e.X - _barThumb.X : _barThumb.W / 2;
            DragBarTo(e.X);
        }
    }

    void DragBarTo(float x)
    {
        float span = _barTrack.W - _barThumb.W;
        if (span <= 0) return;
        float t = Math.Clamp((x - _dragBarOff - _barTrack.X) / span, 0, 1);
        _scroll = t * Math.Max(0, ContentWidth - _viewport.W);
        Layout();
        Invalidate();
    }

    public override void OnMouseMove(in MouseEvent e) { if (_dragBar && IsPressed) DragBarTo(e.X); }
    public override void OnMouseUp(in MouseEvent e) => _dragBar = false;

    public override bool OnMouseWheel(in MouseEvent e)
    {
        // Rad ueber Fader/Knopf wird von diesen verbraucht; hier landen nur freie Stellen.
        if (ContentWidth <= _viewport.W) return false;
        _scroll = Math.Clamp(_scroll - e.Wheel * 60, 0, Math.Max(0, ContentWidth - _viewport.W));
        Layout();
        return true;
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        g.FillRound(b, Theme.RadiusPanel, Theme.Panel);
        g.StrokeRound(b, Theme.RadiusPanel, Theme.Border);
        string title = _input ? "EINGÄNGE" : "AUSGÄNGE";
        g.Text(title, new RectF(b.X + Theme.PanelInner + 4, b.Y + 4, 140, Theme.PanelHeaderHeight), Theme.Caps, Theme.TextDim, HAlign.Left, VAlign.Middle, false);
        float tw = Renderer.MeasureWidth(title, Theme.Caps);
        if (Count > 0)
        {
            string n = Count.ToString();
            float cw = Renderer.MeasureWidth(n, Theme.TinyBold) + 12;
            var chip = new RectF(b.X + Theme.PanelInner + 4 + tw + 8, b.Y + 4 + (Theme.PanelHeaderHeight - 17) / 2, cw, 17);
            g.FillRound(chip, 8.5f, Theme.Raised);
            g.Text(n, chip, Theme.TinyBold, Theme.TextDim, HAlign.Center, VAlign.Middle, false);
        }

        if (Count == 0) { PaintEmpty(g); PaintChildrenExceptStrips(g); return; }

        g.PushClip(_viewport);
        foreach (var s in Strips) if (s.Bounds.Right > _viewport.X && s.Bounds.X < _viewport.Right) s.Paint(g);
        if (_input)
        {
            // Stereo-App-Links: eine kleine Bruecke verbindet die beiden Link-Felder des Paares
            foreach (var l in _m.AppLinks)
            {
                for (int k = l.FirstChannel; k < l.End - 1 && k + 1 < Strips.Count; k++)
                {
                    var a = Strips[k].LinkRect; var c = Strips[k + 1].LinkRect;
                    if (a.IsEmpty || c.IsEmpty) continue;
                    g.Fill(new RectF(a.Right, a.CY - 2, c.X - a.Right, 4), _m.AppLinksEnabled ? Theme.Accent.With(0.85f) : Theme.BorderStrong);
                }
            }
        }
        g.PopClip();
        if (_barTrack.W > 0)
        {
            g.FillRound(_barTrack, 3, Theme.Well);
            g.FillRound(_barThumb, 3, _dragBar ? Theme.Accent : Theme.RaisedHi);
        }
        PaintChildrenExceptStrips(g);
    }

    void PaintChildrenExceptStrips(Renderer g)
    {
        foreach (var c in Children) if (c.Visible && c is not ChannelStrip) c.Paint(g);
    }

    string EmptyBody => _input
        ? "Eingänge sind deine Signalquellen. Lege sie hier an und weise ihnen später auf der Seite „Geräte“ eine Aufnahme-Quelle zu."
        : "Ausgänge gehen an Lautsprecher und Endstufen. Lege sie hier an; die Matrix verteilt die Eingänge darauf. Für Hochtöner lässt sich pro Kanal eine Gain-Sperre setzen.";

    /// <summary>Senkrechte Aufteilung des Leerzustands (Symbol, Textblock, Tasten) - fuer Layout und Zeichnen gleich.</summary>
    (float IconY, float BodyH, float BtnY) EmptyMetrics()
    {
        var b = Bounds;
        float y = b.Y + Theme.PanelHeaderHeight + (b.H - Theme.PanelHeaderHeight) / 2 - 90;
        float bh = Renderer.TextHeight(EmptyBody, Theme.Body, Math.Max(40, b.W - 80));
        return (y, bh, y + 70 + bh + 22);
    }

    void PaintEmpty(Renderer g)
    {
        var b = Bounds;
        float cx = b.CX;
        var (y, bh, _) = EmptyMetrics();
        // Symbol: Kreis mit Pfeil
        g.StrokeRect(new RectF(cx - 26, y - 26, 52, 52), Theme.BorderStrong, 1.6f);
        if (_input) Icons.InArrow(g, cx, y, 22, Theme.TextDim); else Icons.OutArrow(g, cx, y, 22, Theme.TextDim);
        string head = _input ? "Noch keine Eingänge" : "Noch keine Ausgänge";
        g.Text(head, new RectF(b.X + 24, y + 38, b.W - 48, 26), Theme.Heading, Theme.Text, HAlign.Center);
        g.Text(EmptyBody, new RectF(b.X + 40, y + 70, b.W - 80, bh + 4), Theme.Body, Theme.TextDim, HAlign.Center, VAlign.Top, false, true);
    }
}

internal sealed class MixerPage : Page
{
    readonly AppModel _m;
    readonly GroupPanel _in, _out;
    readonly Panel _masterPanel = new();
    readonly ChannelStrip _master;
    float[] _inPeaks = Array.Empty<float>();
    float[] _outPeaks = Array.Empty<float>();

    public override string Title => "Mixer";
    internal GroupPanel Inputs => _in;
    internal GroupPanel Outputs => _out;

    sealed class Panel : Widget
    {
        public override void Paint(Renderer g)
        {
            g.FillRound(Bounds, Theme.RadiusPanel, Theme.Panel);
            g.StrokeRound(Bounds, Theme.RadiusPanel, Theme.Border);
            g.Text("MASTER", new RectF(Bounds.X + Theme.PanelInner + 4, Bounds.Y + 4, 100, Theme.PanelHeaderHeight), Theme.Caps, Theme.TextDim, HAlign.Left, VAlign.Middle, false);
            PaintChildren(g);
        }
    }

    public MixerPage(AppModel m)
    {
        _m = m;
        _in = Add(new GroupPanel(m, true));
        _out = Add(new GroupPanel(m, false));
        Add(_masterPanel);
        _master = _masterPanel.Add(new ChannelStrip(m, false, 0, master: true));
        m.StructureChanged += Rebuild;
        m.ValuesChanged += SyncAll;
        Rebuild();
    }

    void Rebuild()
    {
        _in.Rebuild();
        _out.Rebuild();
        _master.Sync();
        _inPeaks = new float[_m.InputCount];
        _outPeaks = new float[_m.OutputCount];
        LayoutPanels();
        Invalidate();
    }

    void SyncAll()
    {
        _in.SyncAll();
        _out.SyncAll();
        _master.Sync();
        Invalidate();
    }

    public override void OnShow() { SyncAll(); }

    /// <summary>Nur fuer Screenshots: Menue/Editor eines Streifens oeffnen.</summary>
    public void DebugOpen(bool input, int index, string what)
    {
        var strips = (input ? _in : _out).Strips;
        if (index >= 0 && index < strips.Count) strips[index].DebugOpen(what);
    }

    public override void Layout() => LayoutPanels();

    void LayoutPanels()
    {
        var area = new RectF(Bounds.X + Theme.Pad, Bounds.Y + 12, Math.Max(0, Bounds.W - 2 * Theme.Pad), Math.Max(0, Bounds.H - 24));
        const float gap = 12f;
        int ni = _m.InputCount, no = _m.OutputCount;
        float masterStrip = 78f;
        float masterW = masterStrip + 2 * Theme.PanelInner;
        float free = area.W - masterW - 2 * gap;
        const float emptyW = 360f;
        float inner = Theme.PanelInner, sg = Theme.StripGap;

        float s, win, wout;
        if (ni == 0 && no == 0)
        {
            s = 70; win = wout = free / 2;
        }
        else
        {
            float fixedW = (ni == 0 ? emptyW : 0) + (no == 0 ? emptyW : 0);
            float overhead = (ni > 0 ? 2 * inner + (ni - 1) * sg : 0) + (no > 0 ? 2 * inner + (no - 1) * sg : 0);
            int count = ni + no;
            s = (free - fixedW - overhead) / count;
            s = Math.Clamp(s, Theme.StripMinWidth - 6, Theme.StripMaxWidth);
            // Mindestbreite je Gruppe: Kopfzeile mit Titel und beiden Hinzufuegen-Tasten muss hineinpassen
            const float minPanel = 330f;
            win = ni > 0 ? Math.Max(minPanel, 2 * inner + ni * s + (ni - 1) * sg) : emptyW;
            wout = no > 0 ? Math.Max(minPanel, 2 * inner + no * s + (no - 1) * sg) : emptyW;
            float total = win + wout;
            if (total > free)
            {
                // zu viele Kanaele: die Gruppen teilen sich den Platz im Verhaeltnis, innen wird gescrollt
                win = Math.Max(Math.Min(200, free / 2), free * win / total);
                wout = free - win;
            }
            else if (total < free)
            {
                float left = free - total;
                if (ni == 0) win += left; else if (no == 0) wout += left;
            }
        }
        _in.SetStripWidth(s);
        _out.SetStripWidth(s);
        // Bleibt Platz uebrig (sehr breite Fenster), sitzt die ganze Gruppe mittig statt einsam am Rand.
        float used = win + wout + masterW + 2 * gap;
        float startX = area.X + Math.Max(0, (area.W - used) / 2);
        _in.Bounds = new RectF(startX, area.Y, Math.Max(0, win), area.H);
        _out.Bounds = new RectF(_in.Bounds.Right + gap, area.Y, Math.Max(0, wout), area.H);
        _masterPanel.Bounds = new RectF(_out.Bounds.Right + gap, area.Y, masterW, area.H);
        _master.Bounds = new RectF(_masterPanel.Bounds.X + inner, area.Y + Theme.PanelHeaderHeight + 6, masterStrip, area.H - Theme.PanelHeaderHeight - 6 - inner);
        _in.Layout(); _out.Layout(); _master.Layout();
    }

    public override void Tick(float dt)
    {
        if (!Visible) return;
        bool changed = false;
        var eng = _m.Engine;
        if (_inPeaks.Length > 0)
        {
            eng.ReadInputPeaks(_inPeaks);
            for (int i = 0; i < _in.Strips.Count && i < _inPeaks.Length; i++) changed |= _in.Strips[i].FeedMeter(_inPeaks[i], dt);
        }
        float max = 0;
        if (_outPeaks.Length > 0)
        {
            eng.ReadOutputPeaks(_outPeaks);
            for (int i = 0; i < _out.Strips.Count && i < _outPeaks.Length; i++)
            {
                changed |= _out.Strips[i].FeedMeter(_outPeaks[i], dt);
                max = Math.Max(max, _outPeaks[i]);
            }
        }
        changed |= _master.FeedMeter(max, dt);
        if (changed) Invalidate();
    }
}
