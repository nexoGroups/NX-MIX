using NxMix.App.Model;
using NxMix.App.Ui;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Karte "Eingaenge" oder "Ausgaenge": Kopf mit Zahl und Hinzufuegen-Taste, Kanalstreifen, Leerzustand, waagrechtes Scrollen.</summary>
internal sealed class GroupPanel : Widget
{
    readonly AppModel _m;
    readonly bool _input;
    public readonly List<ChannelStrip> Strips = new();
    public readonly Button AddButton;
    public readonly Button EmptyMono, EmptyPair;
    RectF _viewport;
    float _scroll;
    float _stripW = 80;
    RectF _barTrack, _barThumb;
    bool _dragBar;
    float _dragBarOff;
    const float HeadH = 52f;

    public GroupPanel(AppModel m, bool input)
    {
        _m = m; _input = input;
        string noun = input ? "Eingang" : "Ausgang";
        AddButton = Add(new Button("Hinzufügen", ButtonStyle.Tinted, Icon.Plus) { Font = Theme.CalloutMedium, Tooltip = $"{noun} oder Stereo-Paar hinzufügen" });
        AddButton.Clicked += ShowAddMenu;
        EmptyMono = Add(new Button(noun, ButtonStyle.Filled, Icon.Plus));
        EmptyPair = Add(new Button("Stereo-Paar", ButtonStyle.Gray, Icon.Plus));
        EmptyMono.Clicked += AddMono;
        EmptyPair.Clicked += AddPair;
        EmptyMono.Tooltip = $"Einen {noun} hinzufügen";
        EmptyPair.Tooltip = $"Zwei {noun}-Kanäle als Stereo-Paar (L/R) hinzufügen";
    }

    void AddMono() => _m.AddChannels(_input, 1);
    void AddPair() => _m.AddChannels(_input, 2, _m.NextPairNames(_input));

    void ShowAddMenu()
    {
        var root = Root; if (root == null) return;
        string noun = _input ? "Eingang" : "Ausgang";
        PopupMenu.Show(root, AddButton.Bounds, new List<MenuItem>
        {
            new($"{noun} (mono)", null, AddMono) { Icon = _input ? Icon.Mic : Icon.Speaker },
            new("Stereo-Paar (L/R)", null, AddPair) { Icon = Icon.Plus },
        }, 220);
    }

    public int Count => _input ? _m.InputCount : _m.OutputCount;
    public string Title => _input ? "Eingänge" : "Ausgänge";

    public void Rebuild()
    {
        foreach (var s in Strips) Children.Remove(s);
        Strips.Clear();
        for (int i = 0; i < Count; i++)
        {
            var s = new ChannelStrip(_m, _input, i);
            Strips.Add(s);
            Children.Insert(0, s);
            s.Parent = this;
            s.Sync();
        }
    }

    public void SyncAll() { foreach (var s in Strips) s.Sync(); }
    public void SetStripWidth(float w) => _stripW = w;
    float ContentWidth => Count * _stripW + Math.Max(0, Count - 1) * Theme.StripGap;
    public static float Inner => 12f;

    public override void Layout()
    {
        var b = Bounds;
        bool empty = Count == 0;
        AddButton.Visible = !empty;
        EmptyMono.Visible = EmptyPair.Visible = empty;
        float bw = AddButton.PreferredWidth;
        AddButton.Bounds = new RectF(b.Right - Theme.CardPad - bw + 4, b.Y + (HeadH - Theme.ControlHSmall) / 2 + 2, bw, Theme.ControlHSmall);

        var emptyArea = RectF.FromLTRB(b.X, b.Y + HeadH, b.Right, b.Bottom);
        float eh = Draw.EmptyHeight(b.W, EmptyBody) + 8 + Theme.ControlH;
        float ey = emptyArea.Y + Math.Max(10, (emptyArea.H - eh) / 2);
        float btnY = ey + Draw.EmptyHeight(b.W, EmptyBody) + 4;
        float w1 = EmptyMono.PreferredWidth + 10, w2 = EmptyPair.PreferredWidth + 10;
        float x0 = b.CX - (w1 + w2 + 10) / 2;
        EmptyMono.Bounds = new RectF(x0, btnY, w1, Theme.ControlH);
        EmptyPair.Bounds = new RectF(x0 + w1 + 10, btnY, w2, Theme.ControlH);
        _emptyY = ey;

        float top = b.Y + HeadH;
        float barH = ContentWidth > b.W - 2 * Inner + 0.5f ? 14 : 0;
        _viewport = new RectF(b.X + Inner, top, b.W - 2 * Inner, Math.Max(0, b.Bottom - Inner - top - barH));
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
            _barTrack = new RectF(_viewport.X, _viewport.Bottom + 6, _viewport.W, 6);
            float tw = Math.Max(40, _barTrack.W * _viewport.W / ContentWidth);
            float tx = _barTrack.X + (_barTrack.W - tw) * (maxScroll <= 0 ? 0 : _scroll / maxScroll);
            _barThumb = new RectF(tx, _barTrack.Y, tw, 6);
        }
        else _barTrack = _barThumb = default;
    }

    float _emptyY;

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
        return this;
    }

    public override bool AcceptsMouse => true;

    public override void OnMouseDown(in MouseEvent e)
    {
        if (_barThumb.W > 0 && _barTrack.Inflate(5).Contains(e.X, e.Y))
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
    public override void OnMouseUp(in MouseEvent e) { _dragBar = false; Invalidate(); }

    public override bool OnMouseWheel(in MouseEvent e)
    {
        // Rad ueber Fadern verbrauchen diese; hier landen nur freie Stellen.
        if (ContentWidth <= _viewport.W) return false;
        _scroll = Math.Clamp(_scroll - e.Wheel * 70, 0, Math.Max(0, ContentWidth - _viewport.W));
        Layout();
        return true;
    }

    /// <summary>Scrollt so, dass Streifen i sichtbar ist.</summary>
    public void Reveal(int i)
    {
        float l = i * (_stripW + Theme.StripGap), r = l + _stripW;
        if (l < _scroll) _scroll = l;
        else if (r > _scroll + _viewport.W) _scroll = r - _viewport.W;
        Layout();
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        Draw.Card(g, b);
        Draw.CardTitle(g, new RectF(b.X + Theme.CardPad, b.Y + 2, 200, HeadH), Title, Count > 0 ? Count : null);

        if (Count == 0)
        {
            Draw.Empty(g, b, _emptyY, _input ? Icon.Mic : Icon.Speaker, _input ? "Noch keine Eingänge" : "Noch keine Ausgänge", EmptyBody);
            PaintChildrenExceptStrips(g);
            return;
        }

        g.PushClip(_viewport.Inflate(2));
        foreach (var s in Strips) if (s.Bounds.Right > _viewport.X && s.Bounds.X < _viewport.Right) s.Paint(g);
        if (_input)
        {
            // Stereo-App-Links: eine Bruecke verbindet die Koepfe des Paares
            foreach (var l in _m.AppLinks)
            {
                for (int k = l.FirstChannel; k < l.End - 1 && k + 1 < Strips.Count; k++)
                {
                    var a = Strips[k].Header.Bounds; var c = Strips[k + 1].Header.Bounds;
                    var col = _m.AppLinksEnabled ? LinkLook.Color(_m.ViewOf(l).State) : Theme.BorderStrong;
                    g.FillPill(new RectF(a.Right - 2, a.Y + 3, c.X - a.Right + 4, 3), col);
                }
            }
        }
        g.PopClip();
        if (_barTrack.W > 0) g.FillPill(_barThumb, _dragBar ? Theme.TextSecondary.With(0.8f) : Theme.TextTertiary.With(0.5f));
        PaintChildrenExceptStrips(g);
    }

    void PaintChildrenExceptStrips(Renderer g)
    {
        foreach (var c in Children) if (c.Visible && c is not ChannelStrip) c.Paint(g);
    }

    string EmptyBody => _input
        ? "Eingänge sind deine Signalquellen. Lege sie hier an und weise ihnen auf der Seite „Geräte“ eine Aufnahme-Quelle zu – oder binde ein Programm per App-Link."
        : "Ausgänge gehen an Lautsprecher und Endstufen. Die Matrix verteilt die Eingänge darauf. Für Hochtöner lässt sich pro Kanal eine Gain-Sperre setzen.";
}

/// <summary>Mixer: Eingaenge, Ausgaenge und Master nebeneinander (Anordnung wie Mixing Station).</summary>
internal sealed class MixerPage : Page
{
    readonly AppModel _m;
    readonly GroupPanel _in, _out;
    readonly MasterPanel _masterPanel = new();
    readonly ChannelStrip _master;
    float[] _inPeaks = Array.Empty<float>();
    float[] _outPeaks = Array.Empty<float>();

    public override string Title => "Mixer";
    public override Icon Icon => Icon.Mixer;
    public override string? Subtitle => _m.InputCount + _m.OutputCount == 0 ? null
        : $"{Fmt.Inputs(_m.InputCount)}  ·  {Fmt.Outputs(_m.OutputCount)}  ·  Klick auf einen Kanalnamen öffnet seine Einstellungen";
    internal GroupPanel Inputs => _in;
    internal GroupPanel Outputs => _out;
    internal ChannelStrip Master => _master;

    sealed class MasterPanel : Widget
    {
        public override void Paint(Renderer g)
        {
            Draw.Card(g, Bounds);
            Draw.CardTitle(g, new RectF(Bounds.X + Theme.CardPad, Bounds.Y + 2, 100, 52), "Master");
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
        m.HostChanged += SyncAll;
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

    public override void OnShow() { LayoutPanels(); SyncAll(); }

    /// <summary>Oeffnet das Kanal-Blatt (Screenshots, Selbsttest).</summary>
    public ChannelSheet? OpenSheet(bool input, int index)
    {
        var g = input ? _in : _out;
        if (index < 0 || index >= g.Strips.Count || Root == null) return null;
        g.Reveal(index);
        return ChannelSheet.Show(Root, _m, input, index);
    }

    public override void Layout() => LayoutPanels();

    void LayoutPanels()
    {
        var area = new RectF(Bounds.X + Theme.Pad, Bounds.Y + 2, Math.Max(0, Bounds.W - 2 * Theme.Pad), Math.Max(0, Bounds.H - Theme.Pad - 2));
        float gap = Theme.Gap;
        int ni = _m.InputCount, no = _m.OutputCount;
        float inner = GroupPanel.Inner, sg = Theme.StripGap;
        float masterStrip = Theme.Touch ? 104f : 92f;
        float masterW = masterStrip + 2 * inner;
        float free = area.W - masterW - 2 * gap;
        const float emptyW = 380f;

        float s, win, wout;
        if (ni == 0 && no == 0)
        {
            s = 80; win = wout = free / 2;
        }
        else
        {
            float fixedW = (ni == 0 ? emptyW : 0) + (no == 0 ? emptyW : 0);
            float overhead = (ni > 0 ? 2 * inner + (ni - 1) * sg : 0) + (no > 0 ? 2 * inner + (no - 1) * sg : 0);
            int count = ni + no;
            s = (free - fixedW - overhead) / count;
            s = Math.Clamp(s, Theme.StripMinW, Theme.StripMaxW);
            const float minPanel = 260f;
            win = ni > 0 ? Math.Max(minPanel, 2 * inner + ni * s + (ni - 1) * sg) : emptyW;
            wout = no > 0 ? Math.Max(minPanel, 2 * inner + no * s + (no - 1) * sg) : emptyW;
            float total = win + wout;
            if (total > free)
            {
                // zu viele Kanaele: die Gruppen teilen sich den Platz im Verhaeltnis, innen wird gescrollt
                win = Math.Max(Math.Min(260, free / 2), free * win / total);
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
        float used = win + wout + masterW + 2 * gap;
        float startX = area.X + Math.Max(0, (area.W - used) / 2);
        _in.Bounds = new RectF(startX, area.Y, Math.Max(0, win), area.H);
        _out.Bounds = new RectF(_in.Bounds.Right + gap, area.Y, Math.Max(0, wout), area.H);
        _masterPanel.Bounds = new RectF(_out.Bounds.Right + gap, area.Y, masterW, area.H);
        _master.Bounds = new RectF(_masterPanel.Bounds.X + inner, area.Y + 52, masterStrip, area.H - 52 - inner);
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
