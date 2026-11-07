using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Audio;
using NxMix.Ipc;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Dedizierter Fader-Deck-Manager (Mixing-Station-Prinzip):
/// Benutzerdefinierte Ansichten referenzieren beliebige Geräte-Master, Einzelkanäle, Busse und VAIO-Slots per ID.
/// Dasselbe Objekt darf auf beliebig vielen Decks liegen, ohne jemals Audio-Pfade zu verdoppeln.</summary>
internal sealed class DecksPage : Page
{
    readonly AppModel _m;
    Guid? _selectedDeckId;
    int _catalogFilter; // 0=Alle, 1=Geräte-Master, 2=Eingänge, 3=Ausgänge, 4=Busse

    readonly Button _newDeck = new("Neues Deck", ButtonStyle.Filled, Icon.Plus);
    readonly Button _dupDeck = new("Duplizieren", ButtonStyle.Gray, Icon.Copy);
    readonly Button _renDeck = new("Umbenennen", ButtonStyle.Gray, Icon.Edit);
    readonly Button _delDeck = new("Löschen", ButtonStyle.DestructiveTinted, Icon.Trash);
    readonly Button _openOnMixer = new("Im Mixer öffnen", ButtonStyle.Tinted, Icon.Mixer);
    readonly Button _addSpacer = new("+ Trennlinie", ButtonStyle.Gray, Icon.Plus);
    readonly Switch _topBarSwitch = new();
    readonly Segmented _densitySeg = new("Detailliert", "Kompakt") { Font = Theme.CaptionMedium };
    readonly Segmented _catSeg = new("Alle", "Master", "In", "Out", "Bus") { Font = Theme.CaptionMedium };

    readonly VScroll _deckScroll = new(), _slotScroll = new(), _catScroll = new();
    RectF _leftCard, _midCard, _rightCard;
    RectF _deckListRect, _slotListRect, _catListRect, _topSwitchLabelRect;

    readonly record struct CatalogEntry(DeckItemKind Kind, Guid Id, string Badge, string Title, Color4 Color);
    List<CatalogEntry> _catalog = new();

    int _hotDeck = -1, _hotSlot = -1, _hotSlotBtn = -1, _hotCat = -1;

    public override string Title => "Decks";
    public override Icon Icon => Icon.Decks;
    public override string? Subtitle => $"{_m.Decks.Decks.Count} eigene Fader-Decks  ·  Referenzbasiert ohne Signal-Verdopplung";

    public DecksPage(AppModel m)
    {
        _m = m;
        Add(_newDeck); Add(_dupDeck); Add(_renDeck); Add(_delDeck);
        Add(_openOnMixer); Add(_addSpacer); Add(_topBarSwitch); Add(_densitySeg); Add(_catSeg);

        _newDeck.Clicked += () =>
        {
            var created = _m.CreateDeck();
            _selectedDeckId = created.Id;
            RebuildCatalog();
            Layout();
            Invalidate();
        };
        _dupDeck.Clicked += () =>
        {
            if (SelectedDeck is { } d && _m.DuplicateDeck(d.Id) is { } copy)
            {
                _selectedDeckId = copy.Id;
                Layout();
                Invalidate();
            }
        };
        _renDeck.Clicked += () =>
        {
            if (SelectedDeck is { } d && Root != null)
                Root.BeginEdit(_renDeck.Bounds, d.Name, Theme.BodyMedium, s =>
                {
                    if (!string.IsNullOrWhiteSpace(s)) _m.UpdateDeck(d.Id, x => x with { Name = s.Trim() });
                }, 32);
        };
        _delDeck.Clicked += () =>
        {
            if (SelectedDeck is { } d)
                Root?.Confirm($"Deck „{d.Name}“ löschen?", "Nur die Deck-Ansicht wird gelöscht. Alle Audio-Kanäle, Busse und Routings bleiben vollständig erhalten.",
                    "Deck löschen", () => { _m.DeleteDeck(d.Id); EnsureSelection(); Layout(); Invalidate(); }, danger: true);
        };
        _openOnMixer.Clicked += () =>
        {
            if (SelectedDeck is { } d)
            {
                _m.SelectDeck(d.Id);
                Root?.SwitchPage(UiRoot.TabMixer);
            }
        };
        _addSpacer.Clicked += () =>
        {
            if (SelectedDeck is { } d)
                _m.AddDeckSlot(d.Id, DeckItemKind.Spacer, Guid.Empty);
        };
        _topBarSwitch.Changed += on =>
        {
            if (SelectedDeck is { } d)
                _m.UpdateDeck(d.Id, x => x with { VisibleInTopBar = on });
        };
        _densitySeg.Changed += idx =>
        {
            if (SelectedDeck is { } d)
                _m.UpdateDeck(d.Id, x => x with { Density = idx == 1 ? DeckDensity.Compact : DeckDensity.Detailed });
        };
        _catSeg.Changed += idx => { _catalogFilter = idx; _catScroll.Offset = 0; RebuildCatalog(); Layout(); Invalidate(); };

        m.DecksChanged += () => { EnsureSelection(); Layout(); Invalidate(); };
        m.StructureChanged += () => { RebuildCatalog(); Layout(); Invalidate(); };
        m.ValuesChanged += () => { RebuildCatalog(); Invalidate(); };

        EnsureSelection();
        RebuildCatalog();
    }

    UiDeckDefinition? SelectedDeck =>
        _selectedDeckId is { } id ? _m.Decks.Decks.FirstOrDefault(d => d.Id == id) : null;

    void EnsureSelection()
    {
        if (_selectedDeckId != null && _m.Decks.Decks.Any(d => d.Id == _selectedDeckId)) return;
        _selectedDeckId = _m.Decks.ActiveDeckId ?? _m.Decks.Decks.FirstOrDefault()?.Id;
    }

    public override void OnShow()
    {
        EnsureSelection();
        RebuildCatalog();
        Layout();
    }

    void RebuildCatalog()
    {
        var list = new List<CatalogEntry>();

        if (_catalogFilter is 0 or 1)
        {
            foreach (var d in _m.Config.Devices)
                list.Add(new CatalogEntry(DeckItemKind.DeviceMaster, d.Id, d.Flow == DeviceFlow.Capture ? "DEV IN" : "DEV OUT", d.DisplayTitle, d.Flow == DeviceFlow.Capture ? Theme.Teal : Theme.Accent));
            foreach (var v in _m.Config.Vaios)
                list.Add(new CatalogEntry(DeckItemKind.VaioSlot, v.Id, "VAIO", v.Name, Theme.Teal));
            foreach (var dm in _m.Settings.DeviceMasters)
                if (list.All(x => x.Id != dm.Id))
                    list.Add(new CatalogEntry(DeckItemKind.DeviceMaster, dm.Id, dm.IsInput ? "DEV IN" : "DEV OUT", string.IsNullOrWhiteSpace(dm.Name) ? "Geräte-Master" : dm.Name, dm.IsInput ? Theme.Teal : Theme.Accent));
        }

        if (_catalogFilter is 0 or 2)
        {
            for (int i = 0; i < _m.InputCount; i++)
                list.Add(new CatalogEntry(DeckItemKind.InputChannel, _m.Settings.Inputs[i].Id, $"IN {i + 1:00}", _m.DisplayName(true, i), Theme.Green));
        }

        if (_catalogFilter is 0 or 3)
        {
            for (int o = 0; o < _m.OutputCount; o++)
                list.Add(new CatalogEntry(DeckItemKind.OutputChannel, _m.Settings.Outputs[o].Id, $"OUT {o + 1:00}", _m.DisplayName(false, o), Theme.AccentText));
        }

        if (_catalogFilter is 0 or 4)
        {
            foreach (var b in _m.Settings.Buses)
                list.Add(new CatalogEntry(DeckItemKind.MixBus, b.Id, $"BUS ({b.Channels}ch)", b.Name, Theme.Purple));
        }

        _catalog = list;
    }

    string DescribeSlot(DeckSlotItem item, out string badge, out Color4 color)
    {
        switch (item.Kind)
        {
            case DeckItemKind.DeviceMaster:
            case DeckItemKind.VaioSlot:
                {
                    var dev = _m.Config.Devices.FirstOrDefault(d => d.Id == item.TargetId);
                    var vaio = _m.Config.Vaios.FirstOrDefault(v => v.Id == item.TargetId);
                    var dm = _m.GetDeviceMaster(item.TargetId);
                    badge = vaio != null ? "VAIO" : (dev?.Flow == DeviceFlow.Capture || dm?.IsInput == true ? "DEV IN" : "DEV OUT");
                    color = vaio != null || badge == "DEV IN" ? Theme.Teal : Theme.Accent;
                    return item.CustomLabel ?? dev?.DisplayTitle ?? vaio?.Name ?? dm?.Name ?? "Geräte-Master";
                }
            case DeckItemKind.InputChannel:
                {
                    color = Theme.Green;
                    for (int i = 0; i < _m.InputCount; i++)
                        if (_m.Settings.Inputs[i].Id == item.TargetId) { badge = $"IN {i + 1:00}"; return item.CustomLabel ?? _m.DisplayName(true, i); }
                    badge = "IN";
                    return "Entfernter Eingang";
                }
            case DeckItemKind.OutputChannel:
                {
                    color = Theme.AccentText;
                    for (int o = 0; o < _m.OutputCount; o++)
                        if (_m.Settings.Outputs[o].Id == item.TargetId) { badge = $"OUT {o + 1:00}"; return item.CustomLabel ?? _m.DisplayName(false, o); }
                    badge = "OUT";
                    return "Entfernter Ausgang";
                }
            case DeckItemKind.MixBus:
                {
                    color = Theme.Purple;
                    var bus = _m.Settings.Buses.FirstOrDefault(b => b.Id == item.TargetId);
                    badge = "BUS";
                    return item.CustomLabel ?? bus?.Name ?? "Entfernter Bus";
                }
            default:
                badge = "SPACER";
                color = Theme.TextTertiary;
                return "Optische Trennlinie";
        }
    }

    static float RowH => Theme.Touch ? 48f : 40f;

    public override void Layout()
    {
        if (Bounds.W < 20 || Bounds.H < 20) return;
        var area = Bounds.Deflate(Theme.Pad, 10, Theme.Pad, Theme.Pad);
        float gap = Theme.Gap;
        float availW = Math.Max(360f, area.W - 2 * gap);
        float leftW = Math.Clamp(MathF.Floor(availW * 0.28f), 148f, 320f);
        float rightW = Math.Clamp(MathF.Floor(availW * 0.34f), 172f, 400f);
        float midW = Math.Max(148f, availW - leftW - rightW);

        _leftCard = new RectF(area.X, area.Y, leftW, area.H);
        _midCard = new RectF(_leftCard.Right + gap, area.Y, midW, area.H);
        _rightCard = new RectF(_midCard.Right + gap, area.Y, rightW, area.H);

        float pad = Theme.CardPad;
        float ch = Theme.ControlHSmall;

        // Linke Karte: Deck-Liste + Aktionen unten
        float newW = Math.Min(124f, Math.Max(72f, _leftCard.W - 2 * pad - 48f));
        _newDeck.Bounds = new RectF(_leftCard.Right - pad - newW, _leftCard.Y + 11, newW, ch);
        float botY = _leftCard.Bottom - pad;
        float halfL = Math.Max(40f, (_leftCard.W - 2 * pad - 6) / 2);
        _delDeck.Bounds = new RectF(_leftCard.X + pad + halfL + 6, botY - ch, halfL, ch);
        _renDeck.Bounds = new RectF(_leftCard.X + pad, botY - ch, halfL, ch);
        botY -= ch + 6;
        _dupDeck.Bounds = new RectF(_leftCard.X + pad, botY - ch, _leftCard.W - 2 * pad, ch);
        botY -= ch + 10;

        bool hasSel = SelectedDeck != null;
        _dupDeck.Enabled = _renDeck.Enabled = _delDeck.Enabled = _openOnMixer.Enabled = _addSpacer.Enabled = _topBarSwitch.Enabled = _densitySeg.Enabled = hasSel;

        _deckListRect = RectF.FromLTRB(_leftCard.X + pad, _leftCard.Y + 50, _leftCard.Right - pad, Math.Max(_leftCard.Y + 60, botY));
        _deckScroll.Update(_deckListRect, _m.Decks.Decks.Count * (RowH + 4));

        // Mittlere Karte: Deck-Eigenschaften + zugewiesene Streifen
        float my = _midCard.Y + 11;
        float openW = Math.Min(140f, Math.Max(84f, _midCard.W - 2 * pad - 54f));
        _openOnMixer.Bounds = new RectF(_midCard.Right - pad - openW, my, openW, ch);
        _addSpacer.Bounds = new RectF(_openOnMixer.Bounds.X - 6 - 100, my, 100, ch);
        _addSpacer.Visible = _addSpacer.Bounds.X >= _midCard.X + pad + 48;

        float optsY = _midCard.Y + 52;
        if (SelectedDeck is { } sd)
        {
            _densitySeg.SetSelected(sd.Density == DeckDensity.Compact ? 1 : 0);
            _topBarSwitch.SetOn(sd.VisibleInTopBar);
        }
        _topBarSwitch.Bounds = new RectF(_midCard.Right - pad - Theme.SwitchW, optsY, Theme.SwitchW, ch);
        float densW = Math.Min(180f, Math.Max(86f, _topBarSwitch.Bounds.X - (_midCard.X + pad) - 8f));
        _densitySeg.Bounds = new RectF(_midCard.X + pad, optsY, densW, ch);
        _topSwitchLabelRect = new RectF(_densitySeg.Bounds.Right + 10, optsY, Math.Max(0, _topBarSwitch.Bounds.X - _densitySeg.Bounds.Right - 14), ch);

        _slotListRect = RectF.FromLTRB(_midCard.X + pad, optsY + ch + 12, _midCard.Right - pad, _midCard.Bottom - pad);
        _slotScroll.Update(_slotListRect, (SelectedDeck?.Items.Count ?? 0) * (RowH + 4));

        // Rechte Karte: Objekt-Katalog
        _catSeg.Bounds = new RectF(_rightCard.X + pad, _rightCard.Y + 48, _rightCard.W - 2 * pad, ch);
        _catListRect = RectF.FromLTRB(_rightCard.X + pad, _catSeg.Bounds.Bottom + 10, _rightCard.Right - pad, _rightCard.Bottom - pad);
        _catScroll.Update(_catListRect, _catalog.Count * (RowH + 4));
    }

    public override bool AcceptsMouse => true;

    public override void OnMouseMove(in MouseEvent e)
    {
        if (_deckScroll.Dragging) { _deckScroll.Drag(e.Y); Invalidate(); return; }
        if (_slotScroll.Dragging) { _slotScroll.Drag(e.Y); Invalidate(); return; }
        if (_catScroll.Dragging) { _catScroll.Drag(e.Y); Invalidate(); return; }

        int hd = HitRow(_deckListRect, _deckScroll.Offset, _m.Decks.Decks.Count, e.X, e.Y);
        int hs = HitRow(_slotListRect, _slotScroll.Offset, SelectedDeck?.Items.Count ?? 0, e.X, e.Y);
        int hsb = hs >= 0 ? HitSlotAction(hs, e.X, e.Y) : -1;
        int hc = HitRow(_catListRect, _catScroll.Offset, _catalog.Count, e.X, e.Y);
        if (hd != _hotDeck || hs != _hotSlot || hsb != _hotSlotBtn || hc != _hotCat)
        {
            _hotDeck = hd; _hotSlot = hs; _hotSlotBtn = hsb; _hotCat = hc;
            Invalidate();
        }
    }

    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button != 0) return;
        if (_deckScroll.Down(e.X, e.Y) || _slotScroll.Down(e.X, e.Y) || _catScroll.Down(e.X, e.Y))
        {
            Invalidate();
            return;
        }

        int hd = HitRow(_deckListRect, _deckScroll.Offset, _m.Decks.Decks.Count, e.X, e.Y);
        if (hd >= 0 && hd < _m.Decks.Decks.Count)
        {
            _selectedDeckId = _m.Decks.Decks[hd].Id;
            Layout();
            Invalidate();
            return;
        }

        if (SelectedDeck is { } deck)
        {
            int hs = HitRow(_slotListRect, _slotScroll.Offset, deck.Items.Count, e.X, e.Y);
            if (hs >= 0 && hs < deck.Items.Count)
            {
                var slot = deck.Items[hs];
                int btn = HitSlotAction(hs, e.X, e.Y);
                if (btn == 0) _m.MoveDeckSlot(deck.Id, slot.ItemId, -1);
                else if (btn == 1) _m.MoveDeckSlot(deck.Id, slot.ItemId, +1);
                else if (btn == 2 && slot.Kind is DeckItemKind.DeviceMaster or DeckItemKind.VaioSlot)
                    _m.ToggleDeckSlotExpanded(deck.Id, slot.ItemId);
                else if (btn == 3) _m.RemoveDeckSlot(deck.Id, slot.ItemId);
                return;
            }
        }

        int hc = HitRow(_catListRect, _catScroll.Offset, _catalog.Count, e.X, e.Y);
        if (hc >= 0 && hc < _catalog.Count)
        {
            var targetDeck = SelectedDeck ?? _m.CreateDeck();
            _selectedDeckId = targetDeck.Id;
            var entry = _catalog[hc];
            _m.AddDeckSlot(targetDeck.Id, entry.Kind, entry.Id);
        }
    }

    public override void OnMouseUp(in MouseEvent e)
    {
        _deckScroll.Up(); _slotScroll.Up(); _catScroll.Up();
    }

    public override bool OnMouseWheel(in MouseEvent e)
    {
        if (_deckListRect.Contains(e.X, e.Y) && _deckScroll.Wheel(e.Wheel)) { Invalidate(); return true; }
        if (_slotListRect.Contains(e.X, e.Y) && _slotScroll.Wheel(e.Wheel)) { Invalidate(); return true; }
        if (_catListRect.Contains(e.X, e.Y) && _catScroll.Wheel(e.Wheel)) { Invalidate(); return true; }
        return false;
    }

    static int HitRow(RectF view, float scroll, int count, float x, float y)
    {
        if (!view.Contains(x, y) || count <= 0) return -1;
        int idx = (int)((y - view.Y + scroll) / (RowH + 4));
        return idx >= 0 && idx < count ? idx : -1;
    }

    RectF SlotRowRect(int idx) => new(_slotListRect.X, _slotListRect.Y + idx * (RowH + 4) - _slotScroll.Offset, _slotListRect.W - (_slotScroll.Needed ? 10 : 0), RowH);

    (RectF Up, RectF Down, RectF Exp, RectF Del) SlotActionRects(RectF r, bool canExpand)
    {
        float bh = r.H - 10;
        float x = r.Right - 6;
        var del = new RectF(x - 28, r.CY - bh / 2, 28, bh); x = del.X - 4;
        var down = new RectF(x - 28, r.CY - bh / 2, 28, bh); x = down.X - 4;
        var up = new RectF(x - 28, r.CY - bh / 2, 28, bh); x = up.X - 6;
        var exp = canExpand ? new RectF(x - 74, r.CY - bh / 2, 74, bh) : default;
        return (up, down, exp, del);
    }

    int HitSlotAction(int idx, float x, float y)
    {
        if (SelectedDeck is not { } d || idx < 0 || idx >= d.Items.Count) return -1;
        var r = SlotRowRect(idx);
        bool canExp = d.Items[idx].Kind is DeckItemKind.DeviceMaster or DeckItemKind.VaioSlot;
        var (up, down, exp, del) = SlotActionRects(r, canExp);
        if (up.Contains(x, y)) return 0;
        if (down.Contains(x, y)) return 1;
        if (!exp.IsEmpty && exp.Contains(x, y)) return 2;
        if (del.Contains(x, y)) return 3;
        return -1;
    }

    public override CursorKind CursorAt(float x, float y) =>
        (_hotDeck >= 0 || _hotSlotBtn >= 0 || _hotCat >= 0) ? CursorKind.Hand : CursorKind.Arrow;

    public override void Paint(Renderer g)
    {
        Draw.Card(g, _leftCard);
        Draw.Card(g, _midCard);
        Draw.Card(g, _rightCard);

        Draw.CardTitle(g, new RectF(_leftCard.X + Theme.CardPad, _leftCard.Y + 2, 130, 46), "Fader-Decks", _m.Decks.Decks.Count);
        Draw.CardTitle(g, new RectF(_midCard.X + Theme.CardPad, _midCard.Y + 2, Math.Max(80, _addSpacer.Bounds.X - _midCard.X - 24), 46),
            SelectedDeck != null ? $"Streifen: {SelectedDeck.Name}" : "Deck-Streifen", SelectedDeck?.Items.Count);
        Draw.CardTitle(g, new RectF(_rightCard.X + Theme.CardPad, _rightCard.Y + 2, _rightCard.W - 32, 46), "Objekt-Katalog (+ Hinzufügen)", _catalog.Count);

        if (!_topSwitchLabelRect.IsEmpty)
            g.Text("In Top-Leiste", _topSwitchLabelRect, Theme.CaptionMedium, Theme.TextSecondary, HAlign.Right);

        // Linke Liste: Decks
        g.PushClip(_deckListRect);
        if (_m.Decks.Decks.Count == 0)
        {
            g.Text("Noch kein eigenes Deck.\nKlicke oben auf „Neues Deck“.", _deckListRect.Deflate(6), Theme.Callout, Theme.TextSecondary, HAlign.Center, VAlign.Middle, false, true);
        }
        else
        {
            for (int i = 0; i < _m.Decks.Decks.Count; i++)
            {
                var d = _m.Decks.Decks[i];
                var r = new RectF(_deckListRect.X, _deckListRect.Y + i * (RowH + 4) - _deckScroll.Offset, _deckListRect.W - (_deckScroll.Needed ? 10 : 0), RowH);
                if (r.Bottom < _deckListRect.Y || r.Y > _deckListRect.Bottom) continue;
                bool sel = d.Id == _selectedDeckId;
                bool hot = i == _hotDeck;
                g.FillRound(r, Theme.RadiusControl, sel ? Theme.AccentSoft : hot ? Theme.ControlHover : Theme.Surface2);
                if (sel) g.StrokeRound(r, Theme.RadiusControl, Theme.Accent.With(0.7f));
                g.Text(d.Name, new RectF(r.X + 12, r.Y, r.W - 84, r.H), Theme.BodyMedium, Theme.Text);
                g.Text($"{d.Items.Count} Slots", new RectF(r.Right - 72, r.Y, 62, r.H), Theme.Caption, Theme.TextTertiary, HAlign.Right);
            }
        }
        g.PopClip();
        _deckScroll.Paint(g);

        // Mittlere Liste: Slots im gewählten Deck
        g.PushClip(_slotListRect);
        if (SelectedDeck is not { } deck)
        {
            g.Text("Links ein Deck auswählen oder neu anlegen.", _slotListRect, Theme.Body, Theme.TextSecondary, HAlign.Center);
        }
        else if (deck.Items.Count == 0)
        {
            g.Text("Rechts im Katalog auf einen Eintrag klicken,\num ihn als Streifen diesem Deck hinzuzufügen.", _slotListRect.Deflate(12), Theme.Body, Theme.TextSecondary, HAlign.Center, VAlign.Middle, false, true);
        }
        else
        {
            for (int i = 0; i < deck.Items.Count; i++)
            {
                var slot = deck.Items[i];
                var r = SlotRowRect(i);
                if (r.Bottom < _slotListRect.Y || r.Y > _slotListRect.Bottom) continue;
                g.FillRound(r, Theme.RadiusControl, i == _hotSlot ? Theme.ControlHover.With(0.55f) : Theme.Surface2);

                string title = DescribeSlot(slot, out string badge, out var col);
                float cw = Draw.Chip(g, r.X + 8, r.CY, badge, col, dot: false, Theme.MicroStrong);
                bool canExp = slot.Kind is DeckItemKind.DeviceMaster or DeckItemKind.VaioSlot;
                var (up, down, exp, del) = SlotActionRects(r, canExp);
                float rightEdge = (canExp ? exp.X : up.X) - 8;
                g.Text(title, new RectF(r.X + 14 + cw, r.Y, Math.Max(20, rightEdge - (r.X + 14 + cw)), r.H), Theme.BodyMedium, Theme.Text);

                if (canExp)
                    Draw.ButtonLook(g, exp, slot.ExpandedInline ? "▾ Kanäle" : "▸ Master", slot.ExpandedInline ? ButtonStyle.Tinted : ButtonStyle.Gray, true, i == _hotSlot && _hotSlotBtn == 2, font: Theme.MicroStrong);
                Draw.ButtonLook(g, up, "", ButtonStyle.Gray, i > 0, i == _hotSlot && _hotSlotBtn == 0, icon: Icon.ChevronUp);
                Draw.ButtonLook(g, down, "", ButtonStyle.Gray, i + 1 < deck.Items.Count, i == _hotSlot && _hotSlotBtn == 1, icon: Icon.ChevronDown);
                Draw.ButtonLook(g, del, "", ButtonStyle.DestructiveTinted, true, i == _hotSlot && _hotSlotBtn == 3, icon: Icon.Close);
            }
        }
        g.PopClip();
        _slotScroll.Paint(g);

        // Rechte Liste: Katalog
        g.PushClip(_catListRect);
        for (int i = 0; i < _catalog.Count; i++)
        {
            var entry = _catalog[i];
            var r = new RectF(_catListRect.X, _catListRect.Y + i * (RowH + 4) - _catScroll.Offset, _catListRect.W - (_catScroll.Needed ? 10 : 0), RowH);
            if (r.Bottom < _catListRect.Y || r.Y > _catListRect.Bottom) continue;
            bool hot = i == _hotCat;
            g.FillRound(r, Theme.RadiusControl, hot ? Theme.AccentSoft : Theme.Surface2);
            float cw = Draw.Chip(g, r.X + 8, r.CY, entry.Badge, entry.Color, dot: false, Theme.MicroStrong);
            g.Text(entry.Title, new RectF(r.X + 14 + cw, r.Y, Math.Max(20, r.W - cw - 52), r.H), Theme.Body, Theme.Text);
            Icons.Draw(g, Icon.Plus, r.Right - 20, r.CY, 16, hot ? Theme.AccentText : Theme.TextSecondary);
        }
        g.PopClip();
        _catScroll.Paint(g);

        PaintChildren(g);
    }
}
