using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Engine;

namespace NxMix.App.Pages;

/// <summary>Decks are views of stable channel IDs. Editing slots never moves or rewires audio channels.</summary>
internal sealed class DeckWorkspace : Widget
{
    readonly AppModel _m;
    readonly List<Widget> _tabs = new();
    readonly List<Widget> _slots = new();
    readonly Button _new = new("+ Deck", ButtonStyle.Tinted);
    readonly Button _edit = new("Layout bearbeiten");
    readonly Button _options = new("Deck …");
    readonly Button _bus = new("+ Bus", ButtonStyle.Tinted);
    readonly Dropdown _deckSelect = new();
    readonly Slider _horizontal = new() { Min = 0, Max = 1, Step = 1, WheelStep = 70 };
    readonly VScroll _vertical = new();
    bool _editing;
    float _scrollX, _scrollY;
    RectF _viewport;
    int _drag = -1;
    float _downX, _downY;
    public const float HeaderHeight = 48;
    MixerDeck? Deck => _m.LayoutSettings.Decks.FirstOrDefault(d => d.Id == _m.LayoutSettings.ActiveDeck);
    public bool Custom => Deck != null;
    internal Widget SlotAt(int index) => _slots[index];
    internal void BeginEditing() { _editing = true; Rebuild(); }
    public DeckWorkspace(AppModel m)
    {
        _m = m;
        Add(_new); Add(_edit); Add(_options); Add(_bus); Add(_deckSelect); Add(_horizontal);
        _deckSelect.Text = () => Deck?.Name ?? "Alle Kanäle";
        _deckSelect.Items = () => new[] { new MenuItem("Alle Kanäle", null, () => m.SelectDeck(null)) }
            .Concat(m.LayoutSettings.Decks.Select(d => new MenuItem(d.Name, null, () => m.SelectDeck(d.Id)))).ToList();
        _horizontal.Changed += value => { _scrollX = (float)value; Layout(); Invalidate(); };
        _new.Clicked += () => { _editing = true; m.SaveDeck(new MixerDeck { Name = $"Deck {m.LayoutSettings.Decks.Count + 1}" }); };
        _edit.Clicked += () => { _editing = !_editing; Rebuild(); };
        _options.Clicked += Options;
        _bus.Clicked += AddBus;
        m.LayoutChanged += Rebuild;
        m.StructureChanged += Rebuild;
        Rebuild();
    }
    void AddBus()
    {
        if (Root is not { } r) return;
        void Create(int n)
        {
            Guid id = _m.AddBus(n);
            var deck = Deck ?? new MixerDeck { Name = "Busse" };
            var slots = deck.Slots.ToList();
            int empty = slots.FindIndex(s => s == null);
            var slot = new FaderSlot(RouteKind.Bus, id);
            if (empty >= 0) slots[empty] = slot; else slots.Add(slot);
            _m.SaveDeck(deck with { Slots = slots, Rows = Math.Max(deck.Rows, (slots.Count + deck.Columns - 1) / deck.Columns) });
        }
        var items = new[] { 1, 2, 4, 6, 8, 16, 32, 64 }.Select(n => new MenuItem($"Bus mit {n} Kanälen", null, () => Create(n))).ToList();
        items.Add(new MenuItem("Eigene Kanalzahl …", null, () => r.BeginEdit(_bus.Bounds, "2", Theme.Body, s =>
        {
            if (int.TryParse(s, out int n) && n is >= 1 and <= 64) Create(n);
            else r.Notify.Flash("Bitte eine Kanalzahl von 1 bis 64 eingeben.", true);
        }, 2)));
        PopupMenu.Show(r, _bus.Bounds, items);
    }
    void Options()
    {
        if (Root is not { } r || Deck is not { } d) return;
        PopupMenu.Show(r, _options.Bounds, new List<MenuItem>
        {
            new("Umbenennen …", null, () => r.BeginEdit(_options.Bounds.Inflate(40, 0), d.Name, Theme.Body, s => _m.SaveDeck(d with { Name = s.Trim() }), 30)),
            new("Duplizieren", null, () => _m.SaveDeck(d with { Id = Guid.NewGuid(), Name = d.Name + " Kopie" })),
            new("Spalten …", null, () => r.BeginEdit(_options.Bounds, d.Columns.ToString(), Theme.Body, s => { if (int.TryParse(s, out int n)) _m.SaveDeck(d with { Columns = Math.Clamp(n, 1, 32) }); }, 2)),
            new("Reihen …", null, () => r.BeginEdit(_options.Bounds, d.Rows.ToString(), Theme.Body, s => { if (int.TryParse(s, out int n)) _m.SaveDeck(d with { Rows = Math.Clamp(n, 1, 4) }); }, 1)),
            new("Deck löschen", null, () => r.Confirm("Deck löschen?", "Die Ansicht wird entfernt. Die Audiokanäle bleiben bestehen.", "Löschen", () => _m.DeleteDeck(d.Id), true)) { Danger = true },
        });
    }
    public void Rebuild()
    {
        foreach (var w in _tabs.Concat(_slots)) { Children.Remove(w); w.Parent = null; }
        _tabs.Clear(); _slots.Clear();
        void Tab(string name, Guid? id)
        {
            var b = Add(new Button(name, _m.LayoutSettings.ActiveDeck == id ? ButtonStyle.Tinted : ButtonStyle.Gray) { Font = Theme.CalloutMedium });
            b.Clicked += () => { _scrollX = _scrollY = 0; _vertical.Offset = 0; _m.SelectDeck(id); };
            _tabs.Add(b);
        }
        Tab("Alle Kanäle", null);
        foreach (var d in _m.LayoutSettings.Decks) Tab(d.Name, d.Id);
        if (Deck is { } deck)
        {
            int count = Math.Max(deck.Columns * deck.Rows, deck.Slots.Count);
            for (int k = 0; k < count; k++)
            {
                var slot = k < deck.Slots.Count ? deck.Slots[k] : null;
                Widget w;
                if (_editing) w = new SlotEditor(this, k, slot);
                else if (slot?.Kind == RouteKind.Bus && _m.Settings.Buses.Any(b => b.Id == slot.ChannelId)) w = new BusStrip(_m, slot.ChannelId);
                else if (slot != null && slot.Kind != RouteKind.Bus)
                {
                    bool input = slot.Kind == RouteKind.Input;
                    int i = (input ? _m.Settings.Inputs : _m.Settings.Outputs).ToList().FindIndex(c => c.Id == slot.ChannelId);
                    w = i >= 0 ? new ChannelStrip(_m, input, i) : new Button("Kanal fehlt", ButtonStyle.Plain) { Enabled = false };
                }
                else w = new Button("", ButtonStyle.Plain) { Enabled = false };
                Add(w); _slots.Add(w);
            }
        }
        if (Root != null) { Parent?.Layout(); Invalidate(); }
    }
    void PickSlot(int index, RectF anchor)
    {
        if (Root is not { } r || Deck is not { } d) return;
        void Set(FaderSlot? slot)
        {
            var slots = d.Slots.ToList();
            while (slots.Count <= index) slots.Add(null);
            slots[index] = slot;
            _m.SaveDeck(d with { Slots = slots });
        }
        void PickKind(RouteKind kind)
        {
            var entries = kind == RouteKind.Bus
                ? _m.Settings.Buses.Select(b => new FaderSlot(kind, b.Id))
                : (kind == RouteKind.Input ? _m.Settings.Inputs : _m.Settings.Outputs).Select(c => new FaderSlot(kind, c.Id));
            PopupMenu.Show(r, anchor, entries.Select(s => new MenuItem(_m.FaderDescription(s), null, () => Set(s))).ToList(), 250);
        }
        PopupMenu.Show(r, anchor, new List<MenuItem>
        {
            new("Eingang …", null, () => PickKind(RouteKind.Input)),
            new("Ausgang …", null, () => PickKind(RouteKind.Output)),
            new("Bus …", null, () => PickKind(RouteKind.Bus)),
            MenuItem.Sep, new("Platz leeren", null, () => Set(null)),
        });
    }
    sealed class SlotEditor : Widget
    {
        readonly DeckWorkspace _owner;
        readonly int _index;
        readonly FaderSlot? _slot;
        public SlotEditor(DeckWorkspace owner, int index, FaderSlot? slot) { _owner = owner; _index = index; _slot = slot; Cursor = CursorKind.Hand; }
        public override bool AcceptsMouse => true;
        public override void OnMouseDown(in MouseEvent e) { _owner._drag = _index; _owner._downX = e.X; _owner._downY = e.Y; }
        public override void OnMouseUp(in MouseEvent e)
        {
            if (_owner.Deck is not { } d || _owner._drag < 0) return;
            _owner._drag = -1;
            bool moved = Math.Abs(e.X - _owner._downX) + Math.Abs(e.Y - _owner._downY) > 10;
            if (!moved) { _owner.PickSlot(_index, Bounds); return; }
            float px = e.X, py = e.Y;
            int target = _owner._slots.FindIndex(w => w.Bounds.Contains(px, py));
            if (target < 0 || target == _index) return;
            var slots = d.Slots.ToList();
            while (slots.Count <= Math.Max(target, _index)) slots.Add(null);
            (slots[target], slots[_index]) = (slots[_index], slots[target]);
            _owner._m.SaveDeck(d with { Slots = slots });
        }
        public override void Paint(Renderer g)
        {
            g.FillRound(Bounds, Theme.RadiusInner, IsHovered ? Theme.ControlHover : Theme.Surface2);
            g.StrokeRound(Bounds, Theme.RadiusInner, Theme.BorderStrong);
            g.Text($"{_index + 1:00}\n\n{(_slot == null ? "+\nBelegen" : _owner._m.FaderName(_slot))}\n\nZiehen: tauschen", Bounds.Deflate(8), Theme.Callout, Theme.TextSecondary, HAlign.Center, VAlign.Middle, wrap: true);
        }
    }
    public override void Layout()
    {
        var b = Bounds;
        float right = b.Right;
        foreach (var button in new[] { _bus, _options, _edit, _new })
        {
            float w = button.PreferredWidth;
            button.Bounds = new RectF(right - w, b.Y + 6, w, 32); right -= w + 6;
        }
        _edit.Visible = _options.Visible = Custom;
        _edit.Text = _editing ? "Layout fertig" : "Layout bearbeiten";
        float x = b.X;
        foreach (Button tab in _tabs)
        {
            float w = Math.Min(160, tab.PreferredWidth);
            tab.Bounds = new RectF(x, b.Y + 6, w, 32); tab.Visible = tab.Bounds.Right <= right; x += w + 6;
        }
        _deckSelect.Visible = x > right;
        _deckSelect.Bounds = new RectF(b.X, b.Y + 6, Math.Max(70, right - b.X), 32);
        if (_deckSelect.Visible) foreach (var tab in _tabs) tab.Visible = false;
        _viewport = new RectF(b.X, b.Y + HeaderHeight, b.W, Math.Max(0, b.H - HeaderHeight));
        _horizontal.Visible = false;
        if (Deck is not { } deck) return;
        const float width = 96, gap = 8;
        float maxX = Math.Max(0, deck.Columns * (width + gap) - gap - _viewport.W);
        _horizontal.Visible = maxX > 0;
        if (_horizontal.Visible)
        {
            _viewport = new RectF(_viewport.X, _viewport.Y, _viewport.W, Math.Max(0, _viewport.H - 26));
            _horizontal.Bounds = new RectF(b.X, _viewport.Bottom, b.W, 26);
            _horizontal.Max = maxX;
        }
        int rows = Math.Max(deck.Rows, (_slots.Count + deck.Columns - 1) / deck.Columns);
        float height = rows == 1 ? Math.Max(300, _viewport.H) : 420;
        _vertical.Update(_viewport, rows * (height + gap) - gap);
        if (_vertical.Needed) _viewport = new RectF(_viewport.X, _viewport.Y, Math.Max(0, _viewport.W - 14), _viewport.H);
        _scrollX = Math.Clamp(_scrollX, 0, Math.Max(0, deck.Columns * (width + gap) - gap - _viewport.W));
        _horizontal.SetValue(_scrollX);
        _scrollY = _vertical.Offset;
        for (int i = 0; i < _slots.Count; i++)
        {
            var w = _slots[i];
            w.Bounds = new RectF(_viewport.X + i % deck.Columns * (width + gap) - _scrollX,
                _viewport.Y + i / deck.Columns * (height + gap) - _scrollY, width, height);
            w.Layout();
        }
    }
    public override Widget? HitTest(float x, float y)
    {
        if (!Bounds.Contains(x, y)) return null;
        if (_horizontal.Visible && _horizontal.HitTest(x, y) is { } bar) return bar;
        if (y < _viewport.Y)
        {
            foreach (var w in Children.Except(_slots).Reverse()) if (w.Visible && w.HitTest(x, y) is { } hit) return hit;
            return null;
        }
        if (!Custom) return null;
        if (!_viewport.Contains(x, y)) return this;
        foreach (var w in _slots) if (w.HitTest(x, y) is { } hit) return hit;
        return this;
    }
    public override bool OnMouseWheel(in MouseEvent e)
    {
        if (!Custom) return false;
        if (e.Shift || Deck!.Rows == 1) _scrollX -= e.Wheel * 70; else _vertical.Wheel(e.Wheel);
        Layout(); Invalidate(); return true;
    }
    public override void OnMouseDown(in MouseEvent e) { if (_vertical.Down(e.X, e.Y)) { Layout(); Invalidate(); } }
    public override void OnMouseMove(in MouseEvent e) { if (_vertical.Dragging && IsPressed) { _vertical.Drag(e.Y); Layout(); Invalidate(); } }
    public override void OnMouseUp(in MouseEvent e) { _vertical.Up(); Invalidate(); }
    public void FeedMeters(float[] input, float[] output, float dt)
    {
        foreach (var c in _slots.OfType<ChannelStrip>())
        {
            var peaks = c.IsInput ? input : output;
            c.Sync(); if (c.Index < peaks.Length) c.FeedMeter(peaks[c.Index], dt);
        }
    }
    public override void Paint(Renderer g)
    {
        foreach (var w in Children.Except(_slots)) if (w.Visible) w.Paint(g);
        if (!Custom) return;
        g.PushClip(_viewport);
        foreach (var w in _slots) if (w.Bounds.Right > _viewport.X && w.Bounds.X < _viewport.Right && w.Bounds.Bottom > _viewport.Y && w.Bounds.Y < _viewport.Bottom) { if (w is ChannelStrip c) c.Sync(); w.Paint(g); }
        g.PopClip();
        _vertical.Paint(g);
    }
}
