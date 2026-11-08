using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Audio;
using NxMix.Ipc;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Eine Hardware-Geräte-Karte auf dem Tab „Geräte“:
/// Treiber-Badge (ASIO / WASAPI), Online/Offline-Status, ASIO-Control-Panel oder WASAPI-Exclusive,
/// Geräte-Master-Pegel + Dim/Mute sowie ausklappbare digitale Einzelkanal-Regler &amp; Stereo-Kopplung.</summary>
internal sealed class DeviceUnitCard : Widget
{
    readonly AppModel _m;
    public readonly Guid NodeId;
    bool _expanded;

    readonly Switch _enabledSw = new() { Tooltip = "Gerät aktivieren / deaktivieren" };
    readonly Button _driverBtn = new("ASIO Panel", ButtonStyle.Gray, Icon.Settings) { Font = Theme.CaptionMedium };
    readonly Switch _exclSw = new() { Tooltip = "WASAPI-Exclusive für dieses Gerät" };
    readonly Toggle _muteBtn = new("M", () => Theme.Red) { Tooltip = "Geräte-Master stummschalten" };
    readonly Toggle _dimBtn = new("DIM", () => Theme.Orange) { Font = Theme.MicroStrong, Tooltip = "Dim (−20 dB)" };
    readonly Slider _masterSlider = new() { Min = Theme.FaderMinDb, Max = Theme.FaderMaxDb, Step = 0.1, WheelStep = 1 };
    readonly ValueBox _masterBox = new();
    readonly Button _expandBtn = new("▸ Kanäle", ButtonStyle.Tinted) { Font = Theme.CaptionStrong };
    readonly Button _removeBtn = new("Entfernen", ButtonStyle.DestructiveTinted, Icon.Trash) { Font = Theme.CaptionMedium };

    readonly List<(int ChIdx, Slider Slider, ValueBox Box, Button LinkBtn)> _chRows = new();

    public DeviceUnitCard(AppModel m, Guid nodeId)
    {
        _m = m;
        NodeId = nodeId;
        Add(_enabledSw); Add(_driverBtn); Add(_exclSw);
        Add(_muteBtn); Add(_dimBtn); Add(_masterSlider); Add(_masterBox);
        Add(_expandBtn); Add(_removeBtn);

        _masterSlider.ToNorm = db => Db.IsOff(db) ? 0 : FaderScale.ToT(db);
        _masterSlider.FromNorm = t => { double db = FaderScale.ToDb(t); return Db.IsOff(db) ? db : Math.Round(db * 10) / 10; };

        _enabledSw.Changed += on => _m.UpdateHardwareDevice(NodeId, d => d with { Enabled = on });
        _driverBtn.Clicked += () =>
        {
            if (Node is { } n) _m.ShowAsioControlPanel(n.EndpointId);
        };
        _exclSw.Changed += on => _m.UpdateHardwareDevice(NodeId, d => d with { ExclusiveOverride = on });
        _muteBtn.Toggled += on =>
        {
            if (Node is { } n) _m.UpdateDeviceMaster(NodeId, dm => dm with { Mute = on }, n.DisplayTitle, n.Flow == DeviceFlow.Capture);
        };
        _dimBtn.Toggled += on =>
        {
            if (Node is { } n) _m.UpdateDeviceMaster(NodeId, dm => dm with { Dim = on }, n.DisplayTitle, n.Flow == DeviceFlow.Capture);
        };
        _masterSlider.Changed += v =>
        {
            if (Node is { } n) _m.UpdateDeviceMaster(NodeId, dm => dm with { GainDb = v }, n.DisplayTitle, n.Flow == DeviceFlow.Capture);
        };
        _masterBox.Typed += s =>
        {
            if (Node is { } n && Db.TryParse(s, out double db))
                _m.UpdateDeviceMaster(NodeId, dm => dm with { GainDb = Math.Clamp(db, -120, Theme.FaderMaxDb) }, n.DisplayTitle, n.Flow == DeviceFlow.Capture);
        };
        _expandBtn.Clicked += () =>
        {
            _expanded = !_expanded;
            RebuildChannelRows();
            Parent?.Layout();
            Invalidate();
        };
        _removeBtn.Clicked += () =>
        {
            string name = Node?.DisplayTitle ?? "Gerät";
            Root?.Confirm($"Gerät „{name}“ entfernen?", "Die Hardware-Zuordnung wird aus der Systemkonfiguration gelöst.",
                "Entfernen", () => _m.RemoveHardwareDevice(NodeId), danger: true);
        };

        RebuildChannelRows();
    }

    AudioDeviceNode? Node => _m.Config.Devices.FirstOrDefault(d => d.Id == NodeId);

    List<int> MemberChannelIndices()
    {
        var n = Node;
        if (n == null) return new List<int>();
        var list = n.Flow == DeviceFlow.Capture ? _m.Settings.Inputs : _m.Settings.Outputs;
        var res = new List<int>();
        for (int i = 0; i < list.Count; i++)
            if (list[i].DeviceId == NodeId) res.Add(i);
        return res;
    }

    public void RebuildChannelRows()
    {
        foreach (var r in _chRows)
        {
            Children.Remove(r.Slider); r.Slider.Parent = null;
            Children.Remove(r.Box); r.Box.Parent = null;
            Children.Remove(r.LinkBtn); r.LinkBtn.Parent = null;
        }
        _chRows.Clear();
        if (!_expanded || Node is not { } node) return;

        bool isInput = node.Flow == DeviceFlow.Capture;
        foreach (int idx in MemberChannelIndices())
        {
            int chIdx = idx;
            var sl = Add(new Slider
            {
                Min = Theme.FaderMinDb,
                Max = Theme.FaderMaxDb,
                Step = 0.1,
                WheelStep = 1,
                ToNorm = db => Db.IsOff(db) ? 0 : FaderScale.ToT(db),
                FromNorm = t => { double db = FaderScale.ToDb(t); return Db.IsOff(db) ? db : Math.Round(db * 10) / 10; },
            });
            sl.Changed += v => _m.UpdateChannel(isInput, chIdx, c => c with { GainDb = v });

            var bx = Add(new ValueBox());
            bx.Typed += s => { if (Db.TryParse(s, out double db)) _m.UpdateChannel(isInput, chIdx, c => c with { GainDb = Math.Min(db, c.MaxGainDb) }); };

            var lb = Add(new Button("Stereo-Link", ButtonStyle.Gray, Icon.Link) { Font = Theme.MicroStrong });
            lb.Clicked += () => _m.TogglePairLink(isInput, chIdx);

            _chRows.Add((chIdx, sl, bx, lb));
        }
    }

    public float PreferredHeight()
    {
        float baseH = Theme.Touch ? 124f : 108f;
        if (!_expanded) return baseH;
        float rowH = Theme.Touch ? 40f : 34f;
        return baseH + 12f + _chRows.Count * (rowH + 4f) + 8f;
    }

    public override void Layout()
    {
        var b = Bounds;
        var n = Node;
        if (n == null) return;

        float pad = Theme.CardPad;
        float ch = Theme.ControlHSmall;
        float y1 = b.Y + 12;

        _removeBtn.Bounds = new RectF(b.Right - pad - 98, y1, 98, ch);
        _enabledSw.SetOn(n.Enabled);
        _enabledSw.Bounds = new RectF(_removeBtn.Bounds.X - 10 - Theme.SwitchW, y1, Theme.SwitchW, ch);

        bool isAsio = n.Backend == AudioBackend.Asio;
        _driverBtn.Visible = isAsio;
        _exclSw.Visible = !isAsio;
        if (isAsio)
            _driverBtn.Bounds = new RectF(_enabledSw.Bounds.X - 12 - 104, y1, 104, ch);
        else
        {
            _exclSw.SetOn(n.ExclusiveOverride ?? _m.Config.DefaultWasapiExclusive);
            _exclSw.Bounds = new RectF(_enabledSw.Bounds.X - 14 - Theme.SwitchW, y1, Theme.SwitchW, ch);
        }

        // Zeile 2: Geräte-Master
        float y2 = b.Y + (Theme.Touch ? 62f : 54f);
        var dm = _m.GetDeviceMaster(NodeId);
        double mg = dm?.GainDb ?? 0.0;
        _muteBtn.SetChecked(dm?.Mute ?? false);
        _dimBtn.SetChecked(dm?.Dim ?? false);
        _masterSlider.SetValue(mg);
        _masterBox.Text = Db.Format(mg);

        var members = MemberChannelIndices();
        _expandBtn.Text = _expanded ? $"▾ {members.Count} Kanäle" : $"▸ {members.Count} Kanäle";
        _expandBtn.Style = _expanded ? ButtonStyle.Filled : ButtonStyle.Tinted;

        float expW = 108f;
        _expandBtn.Bounds = new RectF(b.Right - pad - expW, y2, expW, ch + 4);
        _masterBox.Bounds = new RectF(_expandBtn.Bounds.X - 8 - 68, y2 + 2, 68, ch);
        _dimBtn.Bounds = new RectF(b.X + pad + 130, y2 + 2, 42, ch);
        _muteBtn.Bounds = new RectF(_dimBtn.Bounds.Right + 4, y2 + 2, 34, ch);
        _masterSlider.Bounds = new RectF(_muteBtn.Bounds.Right + 12, y2, Math.Max(60, _masterBox.Bounds.X - _muteBtn.Bounds.Right - 22), ch + 4);

        if (_expanded)
        {
            bool isInput = n.Flow == DeviceFlow.Capture;
            float ry = y2 + ch + 18f;
            float rowH = Theme.Touch ? 40f : 34f;
            foreach (var row in _chRows)
            {
                if (row.ChIdx >= (isInput ? _m.InputCount : _m.OutputCount)) continue;
                var c = _m.Channel(isInput, row.ChIdx);
                row.Slider.Limit = c.MaxGainDb < Theme.FaderMaxDb - 0.05 ? c.MaxGainDb : double.NaN;
                row.Slider.SetValue(c.GainDb);
                row.Box.Text = Db.Format(c.GainDb);
                row.LinkBtn.Style = c.LinkGroupId.HasValue ? ButtonStyle.Tinted : ButtonStyle.Gray;
                row.LinkBtn.Visible = (row.ChIdx % 2 == 0) && row.ChIdx + 1 < (isInput ? _m.InputCount : _m.OutputCount);

                float rightX = b.Right - pad;
                if (row.LinkBtn.Visible)
                {
                    row.LinkBtn.Bounds = new RectF(rightX - 102, ry + 3, 102, rowH - 6);
                    rightX = row.LinkBtn.Bounds.X - 8;
                }
                row.Box.Bounds = new RectF(rightX - 68, ry + 4, 68, rowH - 8);
                row.Slider.Bounds = new RectF(b.X + pad + 190, ry, Math.Max(60, row.Box.Bounds.X - (b.X + pad + 200)), rowH);
                ry += rowH + 4f;
            }
        }
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        var n = Node;
        if (n == null) return;
        Draw.Card(g, b);

        float pad = Theme.CardPad;
        float cy1 = b.Y + 12 + Theme.ControlHSmall / 2f;
        var info = _m.FindDevice(n.EndpointId);
        bool online = info != null && string.IsNullOrEmpty(info.UnavailableReason);

        float x = b.X + pad;
        float wFlow = Draw.Chip(g, x, cy1, n.Loopback ? "LOOPBACK" : n.Flow == DeviceFlow.Capture ? "EINGANG" : "AUSGANG",
            n.Flow == DeviceFlow.Capture ? Theme.Teal : Theme.Accent, dot: false, Theme.MicroStrong);
        x += wFlow + 6;
        float wDrv = Draw.Chip(g, x, cy1, n.Backend == AudioBackend.Asio ? "ASIO" : "WASAPI",
            n.Backend == AudioBackend.Asio ? Theme.Purple : Theme.TextSecondary, dot: false, Theme.MicroStrong);
        x += wDrv + 6;
        float wOn = Draw.Chip(g, x, cy1, online ? "Online" : "Offline", online ? Theme.Green : Theme.Orange, dot: true, Theme.MicroStrong);
        x += wOn + 10;

        float rightLimit = (n.Backend == AudioBackend.Asio ? _driverBtn.Bounds.X : _exclSw.Bounds.X - 68) - 10;
        g.Text($"{n.DisplayTitle}  ·  {n.ChannelCount} Kanäle (Engine {n.EngineFirstChannel + 1}..{n.EngineFirstChannel + n.ChannelCount})",
            new RectF(x, b.Y + 10, Math.Max(40, rightLimit - x), 30), Theme.Headline, Theme.Text);

        if (n.Backend != AudioBackend.Asio)
            g.Text("Exklusiv", new RectF(_exclSw.Bounds.X - 64, b.Y + 12, 58, Theme.ControlHSmall), Theme.CaptionMedium, Theme.TextSecondary, HAlign.Right);

        float y2 = b.Y + (Theme.Touch ? 62f : 54f);
        g.Text("Geräte-Master", new RectF(b.X + pad, y2, 124, Theme.ControlHSmall + 4), Theme.BodyStrong, Theme.Text);

        if (_expanded && _chRows.Count > 0)
        {
            Draw.HLine(g, b.X + pad, y2 + Theme.ControlHSmall + 10, b.W - 2 * pad);
            bool isInput = n.Flow == DeviceFlow.Capture;
            foreach (var row in _chRows)
            {
                if (row.ChIdx >= (isInput ? _m.InputCount : _m.OutputCount)) continue;
                string chName = _m.DisplayName(isInput, row.ChIdx);
                g.Text($"Ch {row.ChIdx + 1:00}  ·  {chName}",
                    new RectF(b.X + pad + 8, row.Slider.Bounds.Y, 176, row.Slider.Bounds.H),
                    Theme.CalloutMedium, Theme.TextSecondary);
            }
        }

        PaintChildren(g);
    }
}

/// <summary>Tab „Geräte“ (Strg+3): ausschließlich physische Audio-Hardware (ASIO &amp; WASAPI).
/// Takt/Puffer liegen sauber getrennt im Tab „Engine“, virtuelle Kabel und App-Quellen im Tab „Virtual I/O“.</summary>
internal sealed class DevicesPage : Page
{
    readonly AppModel _m;
    readonly Button _addHwBtn = new("Hardware-Gerät einbinden …", ButtonStyle.Filled, Icon.Plus);
    readonly Button _scanBtn = new("Geräte neu scannen", ButtonStyle.Gray, Icon.Refresh);
    readonly Button _toVaioBtn = new("Virtual I/O & Apps …", ButtonStyle.Gray, Icon.Vaio);
    readonly Button _toEngineBtn = new("Takt & Puffer (Engine) …", ButtonStyle.Gray, Icon.Engine);
    readonly List<DeviceUnitCard> _cards = new();
    readonly VScroll _scroll = new();
    RectF _listArea;

    public override string Title => "Geräte";
    public override Icon Icon => Icon.Devices;
    public override string? Subtitle =>
        $"{_m.Config.Devices.Count} eingebundene Hardware-Geräte  ·  {_m.PhysicalDevices.Count} physische Endpunkte erkannt";

    public DevicesPage(AppModel m)
    {
        _m = m;
        Add(_addHwBtn); Add(_scanBtn); Add(_toVaioBtn); Add(_toEngineBtn);

        _addHwBtn.Clicked += ShowAddHardwareMenu;
        _scanBtn.Clicked += () => { _m.RefreshDevices(); Root?.Notify.Flash("Hardware-Geräte werden neu eingelesen …"); };
        _toVaioBtn.Clicked += () => Root?.SwitchPage(UiRoot.TabVirtualIo);
        _toEngineBtn.Clicked += () => Root?.SwitchPage(UiRoot.TabEngine);

        m.BindingsChanged += RebuildCards;
        m.StructureChanged += RebuildCards;
        m.ValuesChanged += () => { foreach (var c in _cards) c.Layout(); Invalidate(); };
        m.HostChanged += () => { foreach (var c in _cards) c.Layout(); Invalidate(); };
        RebuildCards();
    }

    void ShowAddHardwareMenu()
    {
        if (Root is not { } root) return;
        var items = new List<MenuItem>();
        var phys = _m.PhysicalDevices;

        var asio = phys.Where(d => d.Backend == AudioBackend.Asio).ToList();
        if (asio.Count > 0)
        {
            items.Add(MenuItem.Header("ASIO-Hardware (Nativ / Master-Takt-fähig)"));
            foreach (var d in asio)
            {
                var dev = d;
                string dir = dev.Flow == DeviceFlow.Capture ? "Eingang" : "Ausgang";
                items.Add(new MenuItem($"{dev.Name}  ·  {dir} ({dev.Channels} Ch)", "ASIO", () => _m.AddHardwareDevice(dev))
                {
                    Icon = dev.Flow == DeviceFlow.Capture ? Icon.Mic : Icon.Speaker,
                    Enabled = string.IsNullOrEmpty(dev.UnavailableReason),
                });
            }
        }

        var wasapiIn = phys.Where(d => d.Backend == AudioBackend.Windows && d.Flow == DeviceFlow.Capture).ToList();
        if (wasapiIn.Count > 0)
        {
            if (items.Count > 0) items.Add(MenuItem.Sep);
            items.Add(MenuItem.Header("Windows WASAPI — Physische Eingänge"));
            foreach (var d in wasapiIn)
            {
                var dev = d;
                items.Add(new MenuItem($"{dev.Name} ({dev.Channels} Ch)", null, () => _m.AddHardwareDevice(dev)) { Icon = Icon.Mic });
            }
        }

        var wasapiOut = phys.Where(d => d.Backend == AudioBackend.Windows && d.Flow == DeviceFlow.Render).ToList();
        if (wasapiOut.Count > 0)
        {
            if (items.Count > 0) items.Add(MenuItem.Sep);
            items.Add(MenuItem.Header("Windows WASAPI — Physische Ausgänge"));
            foreach (var d in wasapiOut)
            {
                var dev = d;
                items.Add(new MenuItem($"{dev.Name} ({dev.Channels} Ch)", null, () => _m.AddHardwareDevice(dev)) { Icon = Icon.Speaker });
                items.Add(new MenuItem($"{dev.Name} (als Loopback-Aufnahme)", "Loopback", () => _m.AddHardwareDevice(dev, loopback: true)) { Icon = Icon.Loopback });
            }
        }

        PopupMenu.Show(root, _addHwBtn.Bounds, items, 340);
    }

    void RebuildCards()
    {
        foreach (var c in _cards) { Children.Remove(c); c.Parent = null; }
        _cards.Clear();
        foreach (var d in _m.Config.Devices)
        {
            var card = new DeviceUnitCard(_m, d.Id);
            _cards.Add(card);
            Children.Insert(0, card);
            card.Parent = this;
        }
        Layout();
        Invalidate();
    }

    public override void OnShow() { Layout(); Invalidate(); }

    public override void Layout()
    {
        var area = Bounds.Deflate(Theme.Pad, 10, Theme.Pad, Theme.Pad);
        float barH = Theme.ControlH;
        float x = area.X;

        float wAdd = _addHwBtn.PreferredWidth;
        _addHwBtn.Bounds = new RectF(x, area.Y, wAdd, barH); x += wAdd + 8;
        float wScan = _scanBtn.PreferredWidth;
        _scanBtn.Bounds = new RectF(x, area.Y, wScan, barH);

        float rx = area.Right;
        float wEng = _toEngineBtn.PreferredWidth;
        _toEngineBtn.Bounds = new RectF(rx - wEng, area.Y, wEng, barH);
        _toEngineBtn.Visible = _toEngineBtn.Bounds.X > _scanBtn.Bounds.Right + 10;
        if (_toEngineBtn.Visible) rx -= wEng + 8;

        float wVaio = _toVaioBtn.PreferredWidth;
        _toVaioBtn.Bounds = new RectF(rx - wVaio, area.Y, wVaio, barH);
        _toVaioBtn.Visible = _toVaioBtn.Bounds.X > _scanBtn.Bounds.Right + 10;

        _listArea = new RectF(area.X, area.Y + barH + 12, area.W, Math.Max(0, area.H - barH - 12));
        float totalH = 0;
        foreach (var c in _cards) totalH += c.PreferredHeight() + Theme.Gap;
        _scroll.Update(_listArea, Math.Max(0, totalH - Theme.Gap));

        float cy = _listArea.Y - _scroll.Offset;
        float cw = _listArea.W - (_scroll.Needed ? 14f : 0f);
        foreach (var c in _cards)
        {
            float h = c.PreferredHeight();
            c.Bounds = new RectF(_listArea.X, cy, cw, h);
            c.Layout();
            cy += h + Theme.Gap;
        }
    }

    public override Widget? HitTest(float x, float y)
    {
        if (!Visible || !Bounds.Contains(x, y)) return null;
        foreach (var btn in new[] { _addHwBtn, _scanBtn, _toVaioBtn, _toEngineBtn })
            if (btn.Visible && btn.Bounds.Contains(x, y)) return btn;
        if (_listArea.Contains(x, y) && !_scroll.HitTrack(x, y))
        {
            for (int i = _cards.Count - 1; i >= 0; i--)
                if (_cards[i].HitTest(x, y) is { } h) return h;
        }
        return this;
    }

    public override bool AcceptsMouse => true;
    public override void OnMouseDown(in MouseEvent e) { if (e.Button == 0 && _scroll.Down(e.X, e.Y)) { Layout(); Invalidate(); } }
    public override void OnMouseMove(in MouseEvent e) { if (_scroll.Dragging && IsPressed) { _scroll.Drag(e.Y); Layout(); Invalidate(); } }
    public override void OnMouseUp(in MouseEvent e) { _scroll.Up(); }
    public override bool OnMouseWheel(in MouseEvent e)
    {
        if (_scroll.Wheel(e.Wheel)) { Layout(); Invalidate(); return true; }
        return false;
    }

    public override void Paint(Renderer g)
    {
        if (_cards.Count == 0)
        {
            Draw.Card(g, _listArea);
            const string body = "Binde hier deine physischen Audio-Interfaces (ASIO oder WASAPI) ein. Pro Mehrkanal-Gerät entsteht automatisch ein eigener Geräte-Master samt Einzelkanälen.";
            float ey = _listArea.Y + Math.Max(20, (_listArea.H - Draw.EmptyHeight(_listArea.W, body)) / 2);
            Draw.Empty(g, _listArea, ey, Icon.Devices, "Noch kein Hardware-Gerät eingebunden", body);
        }
        else
        {
            g.PushClip(_listArea);
            foreach (var c in _cards)
                if (c.Bounds.Bottom > _listArea.Y && c.Bounds.Y < _listArea.Bottom)
                    c.Paint(g);
            g.PopClip();
            _scroll.Paint(g);
        }

        foreach (var btn in new[] { _addHwBtn, _scanBtn, _toVaioBtn, _toEngineBtn })
            if (btn.Visible) btn.Paint(g);
    }
}
