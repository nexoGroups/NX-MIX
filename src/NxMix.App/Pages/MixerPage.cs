using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Audio;
using NxMix.Engine;
using NxMix.Ipc;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Horizontale Fader-Bank (für Hauptpult-Sektionen oder ein aktives benutzerdefiniertes Fader-Deck).</summary>
internal sealed class StripBankPanel : Widget
{
    public string Title { get; set; } = "";
    public Icon EmptyIcon { get; set; } = Icon.Mixer;
    public string EmptyTitle { get; set; } = "Keine Streifen";
    public string EmptyBody { get; set; } = "";
    public readonly List<Widget> Items = new();
    public readonly List<(int FirstItem, int LastItem, string Label, Color4 Color)> Brackets = new();
    public readonly Button HeaderAction;
    public readonly List<(Widget First, Widget Second)> Pairs = new();

    RectF _viewport;
    float _scroll;
    float _stripW = 90;
    RectF _barTrack, _barThumb;
    bool _dragBar;
    float _dragBarOff;
    const float HeadH = 48f;
    public const float Inner = 12f;

    public StripBankPanel(string title, string actionText, Icon actionIcon, Action onAction)
    {
        Title = title;
        HeaderAction = Add(new Button(actionText, ButtonStyle.Tinted, actionIcon) { Font = Theme.CalloutMedium });
        HeaderAction.Clicked += onAction;
    }

    public void ClearItems()
    {
        foreach (var w in Items) { Children.Remove(w); w.Parent = null; }
        Items.Clear();
        Brackets.Clear();
        Pairs.Clear();
    }

    public T AddStrip<T>(T w) where T : Widget
    {
        Items.Add(w);
        Children.Insert(0, w);
        w.Parent = this;
        return w;
    }

    public void SetStripWidth(float w) => _stripW = w;

    float WidthOf(Widget w) => w switch
    {
        DeckSpacerWidget => 26f,
        DeviceMasterStrip device => _stripW + device.ExtraMeterWidth,
        BusStrip bus => _stripW + bus.ExtraMeterWidth,
        _ => _stripW,
    };

    public float ContentWidth
    {
        get
        {
            if (Items.Count == 0) return 0;
            float sum = 0;
            for (int i = 0; i < Items.Count; i++) sum += WidthOf(Items[i]) + (i > 0 ? Theme.StripGap : 0);
            return sum;
        }
    }

    public bool NeedsScrollbar => ContentWidth > Bounds.W - 2 * Inner + 0.5f;

    public void RevealItem(int idx)
    {
        if (idx < 0 || idx >= Items.Count) return;
        float x = 0;
        for (int i = 0; i < idx; i++) x += WidthOf(Items[i]) + Theme.StripGap;
        float w = WidthOf(Items[idx]);
        if (x < _scroll) _scroll = x;
        else if (x + w > _scroll + _viewport.W) _scroll = x + w - _viewport.W;
        Layout();
    }

    public override void Layout()
    {
        var b = Bounds;
        float bw = Math.Min(HeaderAction.PreferredWidth, Math.Max(36, b.W - 140));
        HeaderAction.Bounds = new RectF(b.Right - Theme.CardPad - bw + 4, b.Y + (HeadH - Theme.ControlHSmall) / 2, bw, Theme.ControlHSmall);

        float top = b.Y + HeadH + (Brackets.Count > 0 ? 10f : 0f);
        float barH = NeedsScrollbar ? 14f : 0f;
        _viewport = new RectF(b.X + Inner, top, Math.Max(0, b.W - 2 * Inner), Math.Max(0, b.Bottom - Inner - top - barH));
        float maxScroll = Math.Max(0, ContentWidth - _viewport.W);
        _scroll = Math.Clamp(_scroll, 0, maxScroll);

        float x = _viewport.X - _scroll;
        foreach (var s in Items)
        {
            float sw = WidthOf(s);
            s.Bounds = new RectF(x, _viewport.Y, sw, _viewport.H);
            s.Layout();
            x += sw + Theme.StripGap;
        }

        if (NeedsScrollbar && _viewport.W > 0 && ContentWidth > 0)
        {
            _barTrack = new RectF(_viewport.X, _viewport.Bottom + 5, _viewport.W, 6);
            float tw = Math.Min(_barTrack.W * 0.85f, Math.Max(36, _barTrack.W * _viewport.W / ContentWidth));
            float tx = _barTrack.X + (_barTrack.W - tw) * (maxScroll <= 0 ? 0 : _scroll / maxScroll);
            _barThumb = new RectF(tx, _barTrack.Y, tw, 6);
        }
        else _barTrack = _barThumb = default;
    }

    public override Widget? HitTest(float x, float y)
    {
        if (!Visible || !Bounds.Contains(x, y)) return null;
        if (HeaderAction.Visible && HeaderAction.Bounds.Contains(x, y)) return HeaderAction;
        if (_viewport.Contains(x, y))
        {
            for (int i = Items.Count - 1; i >= 0; i--)
            {
                var h = Items[i].HitTest(x, y);
                if (h != null) return h;
            }
        }
        return this;
    }

    public override bool AcceptsMouse => true;

    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button == 0 && _barThumb.W > 0 && _barTrack.Inflate(5).Contains(e.X, e.Y))
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

    public override bool OnHorizontalWheel(in MouseEvent e) => ScrollBy(e.Wheel);

    public override bool OnMouseWheel(in MouseEvent e) => ScrollBy(-e.Wheel);

    bool ScrollBy(float notches)
    {
        if (ContentWidth <= _viewport.W) return false;
        _scroll = Math.Clamp(_scroll + notches * 70, 0, Math.Max(0, ContentWidth - _viewport.W));
        Layout();
        return true;
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        Draw.Card(g, b);
        Draw.CardTitle(g, new RectF(b.X + Theme.CardPad, b.Y + 2, Math.Max(60, HeaderAction.Bounds.X - b.X - 24), HeadH),
            Title, Items.Count > 0 ? Items.Count : null);

        if (Items.Count == 0)
        {
            float ey = b.Y + HeadH + Math.Max(12, (b.H - HeadH - Draw.EmptyHeight(b.W, EmptyBody)) / 2);
            Draw.Empty(g, b, ey, EmptyIcon, EmptyTitle, EmptyBody);
            if (HeaderAction.Visible) HeaderAction.Paint(g);
            return;
        }

        g.PushClip(new RectF(_viewport.X - 2, _viewport.Y - 12, _viewport.W + 4, _viewport.H + 14));
        foreach (var br in Brackets)
        {
            if (br.FirstItem < 0 || br.LastItem >= Items.Count || br.FirstItem > br.LastItem) continue;
            float x1 = Items[br.FirstItem].Bounds.X + 4;
            float x2 = Items[br.LastItem].Bounds.Right - 4;
            if (x2 > x1)
                g.FillPill(new RectF(x1, _viewport.Y - 7, x2 - x1, 3.5f), br.Color.With(0.75f));
        }
        foreach (var s in Items)
            if (s.Bounds.Right > _viewport.X && s.Bounds.X < _viewport.Right)
                s.Paint(g);
        foreach (var (first, second) in Pairs)
        {
            float x = (first.Bounds.Right + second.Bounds.X) / 2;
            float y = _viewport.Y + 6 + Theme.StripHeaderH + 8;
            var mark = new RectF(x - 11, y - 10, 22, 20);
            g.FillPill(mark, Theme.Well);
            g.StrokeRound(mark, 10, Theme.Accent);
            Icons.Draw(g, Icon.Link, x, y, 13, Theme.AccentText);
        }
        g.PopClip();

        if (_barTrack.W > 0)
            g.FillPill(_barThumb, _dragBar ? Theme.TextSecondary.With(0.8f) : Theme.TextTertiary.With(0.5f));
        if (HeaderAction.Visible) HeaderAction.Paint(g);
    }
}

internal sealed class DeckSpacerWidget : Widget
{
    readonly string? _label;
    public DeckSpacerWidget(string? label = null) => _label = label;
    public override void Paint(Renderer g)
    {
        var b = Bounds;
        g.FillRound(new RectF(b.CX - 1, b.Y + 14, 2, Math.Max(10, b.H - 28)), 1, Theme.Separator);
        if (!string.IsNullOrWhiteSpace(_label))
            g.Text(_label!, new RectF(b.X, b.Y + 2, b.W, 14), Theme.Micro, Theme.TextTertiary, HAlign.Center);
    }
}


/// <summary>Haupt-Mischpult-Seite im Mixing-Station-Stil:
/// Oben Deck-Schnellwahl („Hauptpult (Geräte)“ + eigene Fader-Decks + Kompakt/Detailliert).
/// Im Hauptpult erscheint pro Mehrkanal-Gerät / VAIO genau 1 DeviceMasterStrip mit Inline-Ausklappung der Einzelkanäle.</summary>
internal sealed class MixerPage : Page
{
    readonly AppModel _m;
    readonly StripBankPanel _inBank;
    readonly StripBankPanel _busBank;
    readonly StripBankPanel _outBank;
    readonly StripBankPanel _customDeckBank;

    readonly List<Button> _deckButtons = new();
    readonly Button _manageDecksBtn = new("Decks …", ButtonStyle.Gray, Icon.Decks) { Font = Theme.CalloutMedium };
    readonly Segmented _densitySeg = new("Detailliert", "Kompakt") { Font = Theme.CaptionMedium };
    bool _compactMain;

    public override string Title => "Mixer";
    public override Icon Icon => Icon.Mixer;
    public override string? Subtitle =>
        $"{Fmt.Inputs(_m.Settings.Inputs.Count(_m.IsChannelVisible))}  ·  {_m.BusCount} Busse  ·  {Fmt.Outputs(_m.Settings.Outputs.Count(_m.IsChannelVisible))}";

    internal IReadOnlyList<ChannelStrip> InputChannelStrips => _inBank.Items.OfType<ChannelStrip>().ToList();
    internal IReadOnlyList<ChannelStrip> OutputChannelStrips => _outBank.Items.OfType<ChannelStrip>().ToList();

    public MixerPage(AppModel m)
    {
        _m = m;
        _inBank = Add(new StripBankPanel("Eingänge & Quellen", "+ Kanal", Icon.Plus, () => ShowAddChannelMenu(true))
        {
            EmptyIcon = Icon.Mic,
            EmptyTitle = "Noch keine Eingänge",
            EmptyBody = "Binde auf „Geräte“ ein Audio-Interface ein, aktiviere unter „Virtual I/O“ ein App-Capture oder lege einen Kanal an.",
        });
        _busBank = Add(new StripBankPanel("Busse", "+ Bus", Icon.Plus, () => _m.AddBus(2))
        {
            EmptyIcon = Icon.Matrix,
            EmptyTitle = "Keine Busse",
            EmptyBody = "Lege Stereo- oder Subgruppen-Busse für gemeinsames Routing an.",
        });
        _outBank = Add(new StripBankPanel("Ausgänge", "+ Ausgang", Icon.Plus, () => ShowAddChannelMenu(false))
        {
            EmptyIcon = Icon.Speaker,
            EmptyTitle = "Noch keine Ausgänge",
            EmptyBody = "Wähle unter „Geräte“ dein Ausgabegerät (ASIO oder WASAPI), um sofort einen Geräte-Master samt Einzelkanälen zu erhalten.",
        });
        _customDeckBank = Add(new StripBankPanel("Deck", "Deck bearbeiten", Icon.Edit, () => Root?.SwitchPage(UiRoot.TabDecks))
        {
            EmptyIcon = Icon.Decks,
            EmptyTitle = "Dieses Fader-Deck ist noch leer",
            EmptyBody = "Füge im Tab „Decks“ beliebige Geräte-Master, Einzelkanäle, Busse oder Virtual-I/O-Slots hinzu.",
        });


        Add(_manageDecksBtn);
        Add(_densitySeg);
        _manageDecksBtn.Clicked += () => Root?.SwitchPage(UiRoot.TabDecks);
        _densitySeg.Changed += idx =>
        {
            if (_m.ActiveCustomDeck is { } cd)
                _m.UpdateDeck(cd.Id, d => d with { Density = idx == 1 ? DeckDensity.Compact : DeckDensity.Detailed });
            else
            {
                _compactMain = idx == 1;
                Layout();
                Invalidate();
            }
        };

        m.StructureChanged += RebuildAll;
        m.DecksChanged += RebuildAll;
        m.ValuesChanged += SyncAll;
        m.HostChanged += SyncAll;
        RebuildAll();
    }

    void ShowAddChannelMenu(bool input)
    {
        if (Root is not { } root) return;
        string noun = input ? "Eingang" : "Ausgang";
        PopupMenu.Show(root, (input ? _inBank : _outBank).HeaderAction.Bounds, new List<MenuItem>
        {
            new($"{noun} (Mono)", null, () => _m.AddChannels(input, 1)) { Icon = input ? Icon.Mic : Icon.Speaker },
            new($"Stereo-Paar (L/R)", null, () => _m.AddChannels(input, 2, _m.NextPairNames(input))) { Icon = Icon.Plus },
            MenuItem.Sep,
            new("Hardware-Gerät einbinden …", "Strg+3", () => root.SwitchPage(UiRoot.TabDevices)) { Icon = Icon.Devices },
            new("App / Virtual Cable einbinden …", "Strg+5", () => root.SwitchPage(UiRoot.TabVirtualIo)) { Icon = Icon.Vaio },
        }, 240);
    }

    public ChannelSheet? OpenSheet(bool input, int index)
    {
        if (Root == null) return null;
        int count = input ? _m.InputCount : _m.OutputCount;
        if (count == 0) return null;
        index = Math.Clamp(index, 0, count - 1);
        return ChannelSheet.Show(Root, _m, input, index);
    }

    void RebuildAll()
    {
        RebuildDeckBarButtons();
        if (_m.ActiveCustomDeck is { } customDeck)
            RebuildCustomDeck(customDeck);
        else
            RebuildMainConsole();
        SyncAll();
        Layout();
        Invalidate();
    }

    void RebuildDeckBarButtons()
    {
        foreach (var b in _deckButtons) { Children.Remove(b); b.Parent = null; }
        _deckButtons.Clear();

        bool mainActive = _m.ActiveCustomDeck == null;
        var mainBtn = Add(new Button("Hauptpult (Geräte)", mainActive ? ButtonStyle.Filled : ButtonStyle.Gray) { Font = Theme.CalloutMedium });
        mainBtn.Clicked += () => _m.SelectDeck(null);
        _deckButtons.Add(mainBtn);

        foreach (var d in _m.Decks.Decks.Where(x => x.VisibleInTopBar))
        {
            var deck = d;
            bool active = _m.Decks.ActiveDeckId == deck.Id;
            var btn = Add(new Button(deck.Name, active ? ButtonStyle.Filled : ButtonStyle.Gray) { Font = Theme.CalloutMedium });
            btn.Clicked += () => _m.SelectDeck(deck.Id);
            _deckButtons.Add(btn);
        }

        var plusBtn = Add(new Button("+ Deck", ButtonStyle.Tinted) { Font = Theme.CalloutMedium, Tooltip = "Neues Fader-Deck anlegen" });
        plusBtn.Clicked += () =>
        {
            _m.CreateDeck();
            Root?.SwitchPage(UiRoot.TabDecks);
        };
        _deckButtons.Add(plusBtn);
    }

    void RebuildMainConsole()
    {
        _inBank.ClearItems();
        _busBank.ClearItems();
        _outBank.ClearItems();

        PopulateDeviceGroupedBank(_inBank, isInput: true);
        PopulateDeviceGroupedBank(_outBank, isInput: false);

        foreach (var bus in _m.Settings.Buses)
            _busBank.AddStrip(new BusStrip(_m, bus.Id));
    }

    void PopulateDeviceGroupedBank(StripBankPanel bank, bool isInput)
    {
        var channels = isInput ? _m.Settings.Inputs : _m.Settings.Outputs;
        var handled = new bool[channels.Count];

        // Alle bekannten Geräte-Master in Richtung (Hardware + VAIO)
        var groups = new List<(Guid Id, string Title)>();
        foreach (var d in _m.Config.Devices.Where(d => (d.Flow == DeviceFlow.Capture) == isInput))
            groups.Add((d.Id, d.DisplayTitle));
        foreach (var v in _m.Config.Vaios.Where(v => (v.Flow == DeviceFlow.Capture) == isInput))
            groups.Add((v.Id, v.Name));
        foreach (var dm in _m.Settings.DeviceMasters.Where(dm => dm.IsInput == isInput))
            if (groups.All(g => g.Id != dm.Id))
                groups.Add((dm.Id, string.IsNullOrWhiteSpace(dm.Name) ? "Gerät" : dm.Name));

        foreach (var (devId, title) in groups)
        {
            var memberIndices = new List<int>();
            for (int i = 0; i < channels.Count; i++)
            {
                if (channels[i].DeviceId == devId)
                {
                    if (_m.IsChannelVisible(channels[i])) memberIndices.Add(i);
                    handled[i] = true;
                }
            }
            if (memberIndices.Count == 0 && _m.Config.Devices.All(d => d.Id != devId) && _m.Config.Vaios.All(v => v.Id != devId))
                continue;

            int firstSlot = bank.Items.Count;
            Guid capturedId = devId;
            bank.AddStrip(new DeviceMasterStrip(
                _m,
                capturedId,
                title,
                isInput,
                memberIndices,
                () => _m.IsMainDeviceExpanded(capturedId),
                () => _m.ToggleMainDeviceExpanded(capturedId)));

            if (_m.IsMainDeviceExpanded(capturedId) && memberIndices.Count > 0)
            {
                foreach (int chIdx in memberIndices)
                    bank.AddStrip(new ChannelStrip(_m, isInput, chIdx));
                int lastSlot = bank.Items.Count - 1;
                bank.Brackets.Add((firstSlot, lastSlot, title, isInput ? Theme.Teal : Theme.Accent));
            }
        }

        // Übrige Kanäle ohne Geräte-Zugehörigkeit (z. B. App-Links oder manuelle Einzelkanäle)
        for (int i = 0; i < channels.Count; i++)
        {
            if (!handled[i] && _m.IsChannelVisible(channels[i]))
                bank.AddStrip(new ChannelStrip(_m, isInput, i));
        }
    }

    void RebuildCustomDeck(UiDeckDefinition deck)
    {
        _customDeckBank.Title = $"Deck: {deck.Name}";
        _customDeckBank.ClearItems();

        var pairStarts = new Dictionary<Guid, Widget>();
        var pendingMembers = new List<(bool Input, int Index)>();
        for (int slotIndex = 0; slotIndex < deck.Items.Count; slotIndex++)
        {
            var slot = deck.Items[slotIndex];
            int start = _customDeckBank.Items.Count;
            var peer = slot.FaderPairId is { } id
                ? deck.Items.FirstOrDefault(item => item.FaderPairId == id && item.ItemId != slot.ItemId)
                : null;
            bool startsPair = peer != null && slotIndex + 1 < deck.Items.Count && deck.Items[slotIndex + 1].ItemId == peer.ItemId;
            Action<string> rename = name => _m.RenameDeckSlot(deck.Id, slot.ItemId, name);
            Func<double, bool>? pairedGain = peer == null ? null : gain => _m.SetDeckFaderPairGain(slot, peer, gain);
            switch (slot.Kind)
            {
                case DeckItemKind.DeviceMaster:
                case DeckItemKind.VaioSlot:
                    {
                        var dm = _m.GetDeviceMaster(slot.TargetId);
                        var devNode = _m.Config.Devices.FirstOrDefault(d => d.Id == slot.TargetId);
                        var vaio = _m.Config.Vaios.FirstOrDefault(v => v.Id == slot.TargetId);
                        bool isInput = dm?.IsInput ?? (devNode != null ? devNode.Flow == DeviceFlow.Capture : vaio?.Flow == DeviceFlow.Capture);
                        string title = devNode?.DisplayTitle ?? vaio?.Name ?? dm?.Name ?? "Gerät";
                        var chList = isInput ? _m.Settings.Inputs : _m.Settings.Outputs;
                        var members = Enumerable.Range(0, chList.Count)
                            .Where(idx => chList[idx].DeviceId == slot.TargetId && _m.IsChannelVisible(chList[idx])).ToList();

                        int first = _customDeckBank.Items.Count;
                        _customDeckBank.AddStrip(new DeviceMasterStrip(
                            _m,
                            slot.TargetId,
                            title,
                            isInput,
                            members,
                            () => slot.ExpandedInline,
                            () => _m.ToggleDeckSlotExpanded(deck.Id, slot.ItemId))
                        {
                            ApplyPairedGain = pairedGain,
                            CustomDeckLabel = slot.CustomLabel,
                            RenameDeckSlot = rename,
                        });

                        if (slot.ExpandedInline && startsPair)
                            pendingMembers.AddRange(members.Select(index => (isInput, index)));
                        else if (slot.ExpandedInline && members.Count > 0)
                        {
                            foreach (int chIdx in members)
                                _customDeckBank.AddStrip(new ChannelStrip(_m, isInput, chIdx));
                            _customDeckBank.Brackets.Add((first, _customDeckBank.Items.Count - 1, title, isInput ? Theme.Teal : Theme.Accent));
                        }
                        break;
                    }
                case DeckItemKind.InputChannel:
                    {
                        int idx = IndexOfChannel(_m.Settings.Inputs, slot.TargetId);
                        if (idx >= 0 && _m.IsChannelVisible(_m.Settings.Inputs[idx]))
                            _customDeckBank.AddStrip(new ChannelStrip(_m, true, idx) { CustomDeckLabel = slot.CustomLabel, ApplyPairedGain = pairedGain, RenameDeckSlot = rename });
                        break;
                    }
                case DeckItemKind.OutputChannel:
                    {
                        int idx = IndexOfChannel(_m.Settings.Outputs, slot.TargetId);
                        if (idx >= 0 && _m.IsChannelVisible(_m.Settings.Outputs[idx]))
                            _customDeckBank.AddStrip(new ChannelStrip(_m, false, idx) { CustomDeckLabel = slot.CustomLabel, ApplyPairedGain = pairedGain, RenameDeckSlot = rename });
                        break;
                    }
                case DeckItemKind.MixBus:
                    if (_m.Settings.Buses.Any(b => b.Id == slot.TargetId))
                        _customDeckBank.AddStrip(new BusStrip(_m, slot.TargetId) { CustomDeckLabel = slot.CustomLabel, ApplyPairedGain = pairedGain, RenameDeckSlot = rename });
                    break;
                case DeckItemKind.Spacer:
                    _customDeckBank.AddStrip(new DeckSpacerWidget(slot.CustomLabel));
                    break;
            }

            if (slot.FaderPairId is not { } pair || peer == null) continue;
            if (_customDeckBank.Items.Count > start)
            {
                var strip = _customDeckBank.Items[start];
                if (pairStarts.Remove(pair, out var first))
                    _customDeckBank.Pairs.Add((first, strip));
                else pairStarts.Add(pair, strip);
            }
            if (!startsPair)
            {
                foreach (var (input, index) in pendingMembers)
                    _customDeckBank.AddStrip(new ChannelStrip(_m, input, index));
                pendingMembers.Clear();
            }
        }
        foreach (var (input, index) in pendingMembers)
            _customDeckBank.AddStrip(new ChannelStrip(_m, input, index));
    }

    static int IndexOfChannel(IReadOnlyList<ChannelDsp> list, Guid id)
    {
        for (int i = 0; i < list.Count; i++) if (list[i].Id == id) return i;
        return -1;
    }

    void SyncAll()
    {
        void SyncBank(StripBankPanel bank)
        {
            foreach (var w in bank.Items)
            {
                if (w is ChannelStrip cs) cs.Sync();
                else if (w is DeviceMasterStrip dms) dms.Sync();
                else if (w is BusStrip bs) bs.Sync();
            }
        }
        SyncBank(_inBank);
        SyncBank(_busBank);
        SyncBank(_outBank);
        SyncBank(_customDeckBank);
        Invalidate();
    }

    public override void OnShow() { SyncAll(); Layout(); }

    public override void Layout()
    {
        if (Bounds.W < 20 || Bounds.H < 20) return;
        var area = new RectF(Bounds.X + Theme.Pad, Bounds.Y + 8, Math.Max(0, Bounds.W - 2 * Theme.Pad), Math.Max(0, Bounds.H - Theme.Pad - 8));
        float barH = Theme.ControlH;

        // Oben rechts: Kompakt/Detailliert + Decks verwalten
        float segW = 172f;
        float manW = _manageDecksBtn.PreferredWidth;
        _densitySeg.Bounds = new RectF(area.Right - segW, area.Y, segW, barH);
        _manageDecksBtn.Bounds = new RectF(_densitySeg.Bounds.X - 8 - manW, area.Y, manW, barH);

        bool compact = _m.ActiveCustomDeck is { } cd ? cd.Density == DeckDensity.Compact : _compactMain;
        _densitySeg.SetSelected(compact ? 1 : 0);

        float bx = area.X;
        float maxBtnRight = _manageDecksBtn.Bounds.X - 10;
        foreach (var btn in _deckButtons)
        {
            float w = Math.Min(180, btn.PreferredWidth);
            if (bx + w > maxBtnRight) { btn.Visible = false; continue; }
            btn.Visible = true;
            btn.Bounds = new RectF(bx, area.Y, w, barH);
            bx += w + 6;
        }

        var body = new RectF(area.X, area.Y + barH + 10, area.W, Math.Max(0, area.H - barH - 10));
        float stripW = compact ? (Theme.Touch ? 82f : 68f) : (Theme.Touch ? 104f : 90f);

        bool isCustom = _m.ActiveCustomDeck != null;
        _customDeckBank.Visible = isCustom;
        _inBank.Visible = _outBank.Visible = !isCustom;
        _busBank.Visible = !isCustom && (_m.BusCount > 0 || body.W >= 1280);

        float freeW = body.W;

        if (isCustom)
        {
            _customDeckBank.SetStripWidth(stripW);
            _customDeckBank.Bounds = new RectF(body.X, body.Y, freeW, body.H);
            _customDeckBank.Layout();
            return;
        }

        _inBank.SetStripWidth(stripW);
        _busBank.SetStripWidth(stripW);
        _outBank.SetStripWidth(stripW);

        if (_busBank.Visible)
        {
            float minBusW = Math.Min(132f, freeW * 0.24f);
            float maxBusW = Math.Max(minBusW, freeW * 0.26f);
            float busDesired = Math.Clamp(2 * StripBankPanel.Inner + Math.Max(stripW, _busBank.ContentWidth), minBusW, maxBusW);
            float rem = Math.Max(0, freeW - busDesired - 2 * Theme.Gap);
            float inW = rem * 0.52f;
            float outW = rem - inW;
            _inBank.Bounds = new RectF(body.X, body.Y, inW, body.H);
            _busBank.Bounds = new RectF(_inBank.Bounds.Right + Theme.Gap, body.Y, busDesired, body.H);
            _outBank.Bounds = new RectF(_busBank.Bounds.Right + Theme.Gap, body.Y, outW, body.H);
            _busBank.Layout();
        }
        else
        {
            float rem = Math.Max(0, freeW - Theme.Gap);
            float inW = rem * 0.52f;
            float outW = rem - inW;
            _inBank.Bounds = new RectF(body.X, body.Y, inW, body.H);
            _outBank.Bounds = new RectF(_inBank.Bounds.Right + Theme.Gap, body.Y, outW, body.H);
        }
        _inBank.Layout();
        _outBank.Layout();
    }

    public override void Paint(Renderer g)
    {
        PaintChildren(g);
    }

    public override void Tick(float dt)
    {
        if (!Visible) return;
        bool changed = false;

        void FeedBank(StripBankPanel bank)
        {
            foreach (var w in bank.Items)
            {
                switch (w)
                {
                    case ChannelStrip cs:
                        float cp = cs.IsInput ? _m.GetInputPeak(cs.Index) : _m.GetOutputPeak(cs.Index);
                        changed |= cs.FeedMeter(cp, dt);
                        break;
                    case DeviceMasterStrip dms:
                        changed |= dms.FeedMeters(dt);
                        break;
                    case BusStrip bs:
                        changed |= bs.FeedMeters(dt);
                        break;
                }
            }
        }

        if (_m.ActiveCustomDeck != null) FeedBank(_customDeckBank);
        else { FeedBank(_inBank); FeedBank(_busBank); FeedBank(_outBank); }


        if (changed) Invalidate();
    }
}
