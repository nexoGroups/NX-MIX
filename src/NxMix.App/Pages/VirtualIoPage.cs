using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Audio;
using NxMix.Ipc;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Tab „Virtual I/O“ (Strg+5):
/// Saubere Trennung zwischen echten Windows-Virtual-Audio-Treibern (VB-Cable / VAC / WaveRT),
/// prozessspezifischem App-Audio-Capture (ProcessLoopback + stummer Parkplatz) und transparentem Kernel-Treiber-Status.</summary>
internal sealed class VirtualIoPage : Page
{
    readonly AppModel _m;

    readonly Button _addCableBtn = new("+ Kabel …", ButtonStyle.Filled) { Font = Theme.CaptionStrong, Tooltip = "Virtuelles Windows-Wiedergabegerät als NX-MIX-Eingang einbinden oder einen VAIO-Slot hinzufügen" };
    readonly IconButton _scanBtn = new(Icon.Refresh, "Audio-Endpunkte und laufende Programme neu einlesen") { IconSize = 15 };

    readonly Switch _appSw = new() { Tooltip = "Prozessspezifisches App-Audio-Capture aktivieren / deaktivieren" };
    readonly Button _addAppBtn = new("+ Programm …", ButtonStyle.Filled) { Font = Theme.CaptionStrong, Tooltip = "Laufendes Programm oder EXE-Namen als eigenen Mixer-Eingang einbinden" };
    readonly Toggle _redirectOptions = new("Windows-Ausgabe …", () => Theme.AccentText) { Tooltip = "Optionale Umleitung und Reset anzeigen" };
    readonly Dropdown _parkingDd = new() { Tooltip = "Stilles Ausgabegerät (Parkplatz), auf das gebundene Apps unhörbar umgeleitet werden" };
    readonly Button _restoreBtn = new("Reset", ButtonStyle.Gray, Icon.Refresh) { Font = Theme.CaptionMedium, Tooltip = "Verwaiste Windows-App-Umleitungen sofort auf Standard zurücksetzen" };

    readonly VScroll _cableScroll = new(), _appScroll = new();
    RectF _cableCard, _appCard, _kernelBox;
    RectF _cableListRect, _appListRect, _parkingLabelRect;

    int _hotCable = -1, _hotCableAction = -1; // 0=Gain, 1=Mute, 2=Enable, 3=Delete
    int _hotApp = -1, _hotAppAction = -1;     // 0=Edit, 1=Remove
    bool _loadingAppMenu;

    public override string Title => "Virtual I/O";
    public override Icon Icon => Icon.Vaio;
    public override string? Subtitle =>
        $"{_m.Config.Vaios.Count} virtuelle Kabel-Slots  ·  {_m.AppLinks.Count} App-Audio-Quellen  ·  {_m.VirtualEndpointDevices.Count} Treiber-Endpunkte erkannt";

    public VirtualIoPage(AppModel m)
    {
        _m = m;
        Add(_addCableBtn);
        Add(_scanBtn);
        Add(_appSw);
        Add(_addAppBtn);
        Add(_redirectOptions);
        Add(_parkingDd);
        Add(_restoreBtn);

        _addCableBtn.Clicked += ShowAddCableMenu;
        _scanBtn.Clicked += _ =>
        {
            _m.RefreshDevices();
            Root?.Notify.Flash("Virtuelle Endpunkte und Audioprogramme aktualisiert.");
        };

        _appSw.Changed += on => _m.SetAppLinksEnabled(on);
        _addAppBtn.Clicked += ShowAddAppMenu;
        _restoreBtn.Clicked += () =>
        {
            _m.RestoreOrphanedAppRoutes(count =>
                Root?.Notify.Flash(count > 0
                    ? $"{count} App-Audio-Umleitung(en) auf Windows-Standard zurückgesetzt."
                    : "Alle Windows-App-Routen waren bereits im Normalzustand."));
        };

        _parkingDd.Text = () =>
        {
            if (string.IsNullOrWhiteSpace(_m.ParkingDeviceId))
                return "Windows spielt parallel";
            return _m.ParkingDeviceName ?? _m.FindDevice(_m.ParkingDeviceId)?.Name ?? _m.ParkingDeviceId;
        };
        _parkingDd.Items = BuildParkingMenu;
        _redirectOptions.Toggled += _ => { Layout(); Invalidate(); };

        m.BindingsChanged += OnModelChanged;
        m.StructureChanged += OnModelChanged;
        m.ValuesChanged += () => Invalidate();
        m.HostChanged += () => Invalidate();
    }

    void OnModelChanged()
    {
        Layout();
        Invalidate();
    }

    public override void OnShow()
    {
        Layout();
    }

    List<MenuItem> BuildParkingMenu()
    {
        var items = new List<MenuItem>
        {
            MenuItem.Header("Stilles Ausgabegerät (nicht Standard-Lautsprecher)"),
            new("Kein Parkplatz (Windows-Ausgabe bleibt aktiv)", null, () => _m.SetParking(null))
            {
                Checked = string.IsNullOrWhiteSpace(_m.ParkingDeviceId),
            },
            MenuItem.Sep,
        };

        var candidates = _m.DiscoveredDevices
            .Where(d => d.Flow == DeviceFlow.Render && d.Backend == AudioBackend.Windows && !d.IsDefault)
            .OrderByDescending(d => d.IsVirtualEndpoint)
            .ThenBy(d => d.Name)
            .ToList();

        if (candidates.Count == 0)
        {
            items.Add(MenuItem.Header("Kein zweites Windows-Ausgabegerät gefunden (z. B. VB-Cable installieren)"));
        }
        else
        {
            foreach (var dev in candidates)
            {
                var d = dev;
                string tag = d.IsVirtualEndpoint ? "Virtuell" : "Hardware";
                items.Add(new MenuItem(d.Name, tag, () => _m.SetParking(d.Id, d.Name))
                {
                    Checked = string.Equals(_m.ParkingDeviceId, d.Id, StringComparison.OrdinalIgnoreCase),
                    Enabled = !_m.Config.Vaios.Any(v => v.Loopback && SameEndpoint(v.EndpointOrExe, d.Id)),
                });
            }
        }
        return items;
    }

    void ShowAddCableMenu()
    {
        if (Root is not { } root) return;
        var items = new List<MenuItem>();
        var virtuals = _m.VirtualEndpointDevices;
        var playback = virtuals.Where(d => d.Flow == DeviceFlow.Render && d.IsNxMixEndpoint).ToList();
        if (playback.Count > 0)
        {
            items.Add(MenuItem.Header("Windows-Wiedergabe an NX-MIX In senden"));
            foreach (var endpoint in playback)
            {
                bool reserved = SameEndpoint(_m.ParkingDeviceId, endpoint.Id);
                bool bound = EndpointIsBound(endpoint.Id);
                items.Add(new MenuItem(endpoint.Name,
                    reserved ? "App-Parkplatz" : bound ? "bereits eingebunden" : "als Mixer-Eingang",
                    () =>
                    {
                        var slot = _m.AddVirtualCableSlot(endpoint, loopback: true);
                        root.Notify.Flash($"Wähle „{endpoint.Name}“ in Windows oder in deiner App. In NX-MIX landet der Ton auf „{slot.Name}“.");
                    }, enabled: !reserved && !bound) { Icon = Icon.Loopback });
            }
            items.Add(MenuItem.Sep);
        }

        var recording = virtuals.Where(d => d.IsNxMixEndpoint && d.NativeOutputPort > 0).ToList();
        if (recording.Count > 0)
        {
            items.Add(MenuItem.Header("NX-MIX Out an Windows-Aufnahmegeräte senden"));
            foreach (var endpoint in recording)
            {
                bool bound = EndpointIsBound(endpoint.Id);
                items.Add(new MenuItem(endpoint.Name, bound ? "bereits eingebunden" : "als Mixer-Ausgang", () =>
                {
                    _m.AddVirtualCableSlot(endpoint, $"NX-MIX Out {endpoint.NativeOutputPort}");
                    root.Notify.Flash($"Wähle „{endpoint.Name}“ als Aufnahmegerät in deiner App.");
                }, enabled: !bound) { Icon = Icon.Speaker });
            }
            items.Add(MenuItem.Sep);
        }

        var external = virtuals.Where(d => !d.IsNxMixEndpoint).ToList();
        items.Add(MenuItem.Header("Weitere virtuelle Ein- und Ausgänge"));
        if (external.Count == 0)
        {
            items.Add(MenuItem.Header("Kein virtueller Audiotreiber (z. B. VB-CABLE / VAC) installiert"));
        }
        else
        {
            foreach (var ep in external)
            {
                var e = ep;
                bool alreadyBound = EndpointIsBound(e.Id);
                string flowLabel = e.Flow == DeviceFlow.Capture ? "VAIO In" : "VAIO Out";
                items.Add(new MenuItem(
                    e.Name,
                    alreadyBound ? "bereits eingebunden" : $"{flowLabel} · {Math.Max(2, e.Channels)}ch",
                    () =>
                    {
                        var slot = _m.AddVirtualCableSlot(e);
                        root.Notify.Flash($"Virtuelles Kabel „{slot.Name}“ eingebunden.");
                    },
                    enabled: !alreadyBound));
            }
        }

        // Erlaube zusätzlich physische Loopback-/Aux-Endpunkte als externen VAIO-Slot, falls ein Treiber sich nicht als Virtual meldet
        var others = _m.PhysicalDevices.Where(d => d.Backend == AudioBackend.Windows).ToList();
        if (others.Count > 0)
        {
            items.Add(MenuItem.Sep);
            items.Add(MenuItem.Header("Weitere Windows-Endpunkte manuell als VAIO-Slot nutzen"));
            foreach (var ep in others.Take(8))
            {
                var e = ep;
                bool alreadyBound = EndpointIsBound(e.Id);
                items.Add(new MenuItem(
                    e.Name,
                    e.Flow == DeviceFlow.Capture ? "Eingang" : "Ausgang",
                    () => _m.AddVirtualCableSlot(e),
                    enabled: !alreadyBound));
            }
        }

        PopupMenu.Show(root, _addCableBtn.Bounds, items, 320);
    }

    bool EndpointIsBound(string id) => _m.Config.Vaios.Any(v => SameEndpoint(v.EndpointOrExe, id))
        || _m.Config.Devices.Any(d => SameEndpoint(d.EndpointId, id));

    static bool SameEndpoint(string? left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    void ShowAddAppMenu()
    {
        if (_loadingAppMenu || Root is not { } root) return;
        _loadingAppMenu = true;
        _m.LoadAppPrograms((programs, error) =>
        {
            _loadingAppMenu = false;
            if (!ReferenceEquals(Root, root) || !Visible) return;
            if (error != null) root.Notify.Flash(error, true);
            ShowAppProgramMenu(root, programs);
        });
    }

    void ShowAppProgramMenu(UiRoot root, List<AppProgram> programs)
    {
        var items = new List<MenuItem>
        {
            MenuItem.Header("Aktive Programme mit Audio-Sitzung"),
        };

        if (programs.Count == 0)
        {
            items.Add(MenuItem.Header("Derzeit keine aktive Audio-Sitzung erkannt"));
        }
        else
        {
            foreach (var prog in programs)
            {
                var p = prog;
                bool bound = _m.AppLinks.Any(l => string.Equals(l.ExeName, p.ExeName, StringComparison.OrdinalIgnoreCase));
                items.Add(new MenuItem(
                    $"{p.DisplayName} ({p.ExeName})",
                    bound ? "gebunden" : (p.IsPlaying ? "spielt · Stereo" : "offen · Stereo"),
                    () =>
                    {
                        string? err = _m.BindNewAppQuick(p.ExeName, 2, p.DisplayName);
                        if (err != null) root.Notify.Flash(err, true);
                        else root.Notify.Flash($"„{p.DisplayName}“ als Stereo-App-Quelle eingebunden.");
                    },
                    enabled: !bound));
            }
        }

        items.Add(MenuItem.Sep);
        items.Add(new MenuItem("Programm per EXE-Name / Kanalwahl binden …", null, () =>
        {
            int ch = _m.NextFreeEngineChannel(DeviceFlow.Capture);
            if (ch + 2 > _m.InputCount) _m.AddChannels(true, (ch + 2) - _m.InputCount);
            AppLinkPicker.Show(root, _m, ch, _addAppBtn.Bounds, startManual: programs.Count == 0);
        })
        { Icon = Icon.Plus });

        PopupMenu.Show(root, _addAppBtn.Bounds, items, 320);
    }

    static float CableRowH => Theme.Touch ? 58f : 50f;
    static float AppRowH => Theme.Touch ? 58f : 50f;

    public override void Layout()
    {
        var area = Bounds.Deflate(Theme.Pad, 10, Theme.Pad, Theme.Pad);
        float gap = Theme.Gap;
        float leftW = MathF.Floor((area.W - gap) * 0.48f);
        float rightW = Math.Max(180f, area.W - leftW - gap);

        _cableCard = new RectF(area.X, area.Y, leftW, area.H);
        _appCard = new RectF(_cableCard.Right + gap, area.Y, rightW, area.H);

        float pad = Theme.CardPad;
        float ch = Theme.ControlHSmall;

        // Linke Karte: Virtuelle Kabel + Kernel-VAD-Statusbox unten
        float yL = _cableCard.Y + 11;
        _addCableBtn.Bounds = new RectF(_cableCard.Right - pad - 96, yL, 96, ch);
        _scanBtn.Bounds = new RectF(_addCableBtn.Bounds.X - 6 - ch, yL, ch, ch);

        float kernelH = Theme.Touch ? 104f : 92f;
        _kernelBox = new RectF(_cableCard.X + pad, _cableCard.Bottom - pad - kernelH, _cableCard.W - 2 * pad, kernelH);

        _cableListRect = RectF.FromLTRB(
            _cableCard.X + pad,
            _cableCard.Y + 64,
            _cableCard.Right - pad,
            Math.Max(_cableCard.Y + 80, _kernelBox.Y - 12));
        _cableScroll.Update(_cableListRect, _m.Config.Vaios.Count * (CableRowH + 6));

        // Rechte Karte: ProcessLoopback App-Audio-Capture
        float yR = _appCard.Y + 11;
        _appSw.SetOn(_m.AppLinksEnabled);
        _addAppBtn.Bounds = new RectF(_appCard.Right - pad - 114, yR, 114, ch);
        _appSw.Bounds = new RectF(_addAppBtn.Bounds.X - 8 - Theme.SwitchW, yR, Theme.SwitchW, ch);

        float yPark = yR + ch + 12;
        _redirectOptions.Bounds = new RectF(_appCard.X + pad, yPark, 155, ch);
        _parkingDd.Visible = _restoreBtn.Visible = _redirectOptions.Checked;
        yPark += ch + 6;
        _parkingLabelRect = new RectF(_appCard.X + pad, yPark, _appCard.W - 2 * pad, 16);
        float yDd = yPark + 18;
        float rstW = 78f;
        _restoreBtn.Bounds = new RectF(_appCard.Right - pad - rstW, yDd, rstW, ch);
        _parkingDd.Bounds = new RectF(_appCard.X + pad, yDd, Math.Max(80, _restoreBtn.Bounds.X - (_appCard.X + pad) - 8), ch);

        _appListRect = RectF.FromLTRB(
            _appCard.X + pad,
            (_redirectOptions.Checked ? _parkingDd.Bounds.Bottom : _redirectOptions.Bounds.Bottom) + 12,
            _appCard.Right - pad,
            _appCard.Bottom - pad);
        _appScroll.Update(_appListRect, _m.Config.AppLinks.Count * (AppRowH + 6));
    }

    public override bool AcceptsMouse => true;

    RectF CableRowRect(int idx)
    {
        float w = _cableListRect.W - (_cableScroll.Needed ? 12 : 0);
        return new RectF(_cableListRect.X, _cableListRect.Y + idx * (CableRowH + 6) - _cableScroll.Offset, w, CableRowH);
    }

    static (RectF Gain, RectF Mute, RectF Enable, RectF Del) CableActionRects(RectF r)
    {
        float bh = 26f;
        float cy = r.CY - bh / 2f;
        var del = new RectF(r.Right - 32, cy, 26, bh);
        var en = new RectF(del.X - 54, cy, 48, bh);
        var mute = new RectF(en.X - 34, cy, 28, bh);
        var gain = new RectF(mute.X - 66, cy, 60, bh);
        return (gain, mute, en, del);
    }

    RectF AppRowRect(int idx)
    {
        float w = _appListRect.W - (_appScroll.Needed ? 12 : 0);
        return new RectF(_appListRect.X, _appListRect.Y + idx * (AppRowH + 6) - _appScroll.Offset, w, AppRowH);
    }

    static (RectF Edit, RectF Del) AppActionRects(RectF r)
    {
        float bh = 26f;
        float cy = r.CY - bh / 2f;
        var del = new RectF(r.Right - 64, cy, 58, bh);
        var edit = new RectF(del.X - 68, cy, 62, bh);
        return (edit, del);
    }

    public override void OnMouseMove(in MouseEvent e)
    {
        if (_cableScroll.Dragging) { _cableScroll.Drag(e.Y); Invalidate(); return; }
        if (_appScroll.Dragging) { _appScroll.Drag(e.Y); Invalidate(); return; }

        int hc = -1, hca = -1;
        if (_cableListRect.Contains(e.X, e.Y) && !(_cableScroll.Needed && e.X >= _cableListRect.Right - 12))
        {
            int idx = (int)((e.Y - _cableListRect.Y + _cableScroll.Offset) / (CableRowH + 6));
            if (idx >= 0 && idx < _m.Config.Vaios.Count && CableRowRect(idx).Contains(e.X, e.Y))
            {
                hc = idx;
                var acts = CableActionRects(CableRowRect(idx));
                if (acts.Gain.Contains(e.X, e.Y)) hca = 0;
                else if (acts.Mute.Contains(e.X, e.Y)) hca = 1;
                else if (acts.Enable.Contains(e.X, e.Y)) hca = 2;
                else if (acts.Del.Contains(e.X, e.Y)) hca = 3;
            }
        }

        int ha = -1, haa = -1;
        if (_appListRect.Contains(e.X, e.Y) && !(_appScroll.Needed && e.X >= _appListRect.Right - 12))
        {
            int idx = (int)((e.Y - _appListRect.Y + _appScroll.Offset) / (AppRowH + 6));
            if (idx >= 0 && idx < _m.Config.AppLinks.Count && AppRowRect(idx).Contains(e.X, e.Y))
            {
                ha = idx;
                var acts = AppActionRects(AppRowRect(idx));
                if (acts.Edit.Contains(e.X, e.Y)) haa = 0;
                else if (acts.Del.Contains(e.X, e.Y)) haa = 1;
            }
        }

        if (hc != _hotCable || hca != _hotCableAction || ha != _hotApp || haa != _hotAppAction)
        {
            _hotCable = hc; _hotCableAction = hca;
            _hotApp = ha; _hotAppAction = haa;
            Invalidate();
        }
    }

    static RectF CableNameRect(RectF row, VaioSlotConfig slot)
    {
        string badge = slot.Flow == DeviceFlow.Capture ? "VAIO IN" : "VAIO OUT";
        float left = row.X + 16 + Draw.ChipWidth(badge, false, Theme.MicroStrong);
        return new RectF(left, row.Y + 6, Math.Max(30, CableActionRects(row).Gain.X - left - 8), 20);
    }

    static RectF AppNameRect(RectF row, LinkView view)
    {
        float left = row.X + 50;
        float statusWidth = Draw.ChipWidth(LinkLook.Text(view.State), true, Theme.MicroStrong);
        return new RectF(left, row.Y + 6, Math.Max(30, AppActionRects(row).Edit.X - 14 - statusWidth - left), 19);
    }

    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button != 0) return;
        if (_cableScroll.Down(e.X, e.Y) || _appScroll.Down(e.X, e.Y)) { Invalidate(); return; }

        if (_hotCable >= 0 && _hotCable < _m.Config.Vaios.Count)
        {
            var slot = _m.Config.Vaios[_hotCable];
            var acts = CableActionRects(CableRowRect(_hotCable));
            var dm = _m.GetDeviceMaster(slot.Id);
            var name = CableNameRect(CableRowRect(_hotCable), slot);
            if (name.Contains(e.X, e.Y))
            {
                Root?.BeginEdit(name, slot.Name, Theme.BodyMedium, text => _m.RenameDeviceFader(slot.Id, text), 48);
                return;
            }
            switch (_hotCableAction)
            {
                case 0:
                    Root?.BeginEdit(acts.Gain.Inflate(2), Db.Format(dm?.GainDb ?? 0.0).Replace(Db.Minus, '-'), Theme.Value, s =>
                    {
                        if (Db.TryParse(s, out double db))
                            _m.UpdateDeviceMaster(slot.Id, x => x with { GainDb = Math.Clamp(db, -120, Theme.FaderMaxDb) }, slot.Name, slot.Flow == DeviceFlow.Capture);
                    }, 10, null, HAlign.Center);
                    break;
                case 1:
                    _m.UpdateDeviceMaster(slot.Id, x => x with { Mute = !(x.Mute) }, slot.Name, slot.Flow == DeviceFlow.Capture);
                    break;
                case 2:
                    _m.UpdateVaioSlot(slot.Id, v => v with { Enabled = !v.Enabled });
                    break;
                case 3:
                    Root?.Confirm($"VAIO-Slot „{slot.Name}“ entfernen?",
                        "Die virtuelle Kabel-Zuordnung wird aus der Systemkonfiguration gelöst.",
                        "Entfernen", () => _m.RemoveVaioSlot(slot.Id), danger: true);
                    break;
            }
            return;
        }

        if (_hotApp >= 0 && _hotApp < _m.Config.AppLinks.Count)
        {
            var entry = _m.Config.AppLinks[_hotApp];
            var acts = AppActionRects(AppRowRect(_hotApp));
            var link = new AppLink(entry.ExeName, entry.FirstChannel, entry.Count, entry.DisplayName);
            var name = AppNameRect(AppRowRect(_hotApp), _m.ViewOf(link));
            if (name.Contains(e.X, e.Y))
                Root?.BeginEdit(name, entry.Title, Theme.BodyMedium, text => _m.RenameDeviceFader(entry.Id, text), 48);
            else if (_hotAppAction == 0 && Root != null)
            {
                AppLinkPicker.Show(Root, _m, entry.FirstChannel, acts.Edit);
            }
            else if (_hotAppAction == 1)
            {
                _m.RemoveLink(entry.FirstChannel);
                Root?.Notify.Flash($"App-Quelle „{entry.Title}“ gelöst.");
            }
        }
    }

    public override void OnMouseUp(in MouseEvent e)
    {
        _cableScroll.Up();
        _appScroll.Up();
    }

    public override bool OnMouseWheel(in MouseEvent e)
    {
        if (_cableListRect.Contains(e.X, e.Y) && _cableScroll.Wheel(e.Wheel)) { Invalidate(); return true; }
        if (_appListRect.Contains(e.X, e.Y) && _appScroll.Wheel(e.Wheel)) { Invalidate(); return true; }
        return false;
    }

    public override void Paint(Renderer g)
    {
        float pad = Theme.CardPad;

        // ---- Linke Karte: Virtuelle Audio-Kabel & Kernel-VAD-Status ----
        Draw.Card(g, _cableCard);
        Draw.CardTitle(g, new RectF(_cableCard.X + pad, _cableCard.Y + 11, Math.Max(40, _scanBtn.Bounds.X - (_cableCard.X + pad) - 8), Theme.ControlHSmall),
            "Virtuelle Kabel", _m.Config.Vaios.Count);
        g.Text("Echte Windows-Audio-Endpunkte (VB-CABLE, Virtual Audio Cable, WaveRT)",
            new RectF(_cableCard.X + pad, _cableCard.Y + 40, _cableCard.W - 2 * pad, 18), Theme.Caption, Theme.TextSecondary);

        g.PushClip(_cableListRect);
        if (_m.Config.Vaios.Count == 0)
        {
            Draw.Empty(g, _cableListRect, _cableListRect.Y + 12, Icon.Vaio,
                "Kein virtuelles Kabel aktiv",
                "Eigene NX-MIX-Eingänge erscheinen hier, sobald der native Audiotreiber verfügbar ist.");
        }
        else
        {
            for (int i = 0; i < _m.Config.Vaios.Count; i++)
            {
                var r = CableRowRect(i);
                if (r.Bottom < _cableListRect.Y || r.Y > _cableListRect.Bottom) continue;
                var slot = _m.Config.Vaios[i];
                var dm = _m.GetDeviceMaster(slot.Id);
                bool hot = i == _hotCable;

                g.FillRound(r, 9, hot ? Theme.ControlHover : Theme.Well);
                g.StrokeRound(r, 9, Theme.Border);

                var acts = CableActionRects(r);
                Draw.Chip(g, r.X + 10, r.CY - 9,
                    slot.Flow == DeviceFlow.Capture ? "VAIO IN" : "VAIO OUT",
                    slot.Flow == DeviceFlow.Capture ? Theme.Teal : Theme.Accent,
                    dot: false, Theme.MicroStrong);

                g.Text(slot.Name, CableNameRect(r, slot), Theme.BodyMedium, slot.Enabled ? Theme.Text : Theme.TextTertiary);
                int activeChannels = _m.ActiveVaioChannels(slot);
                string channels = $"{activeChannels}ch · Engine {slot.EngineFirstChannel + 1}..{slot.EngineFirstChannel + activeChannels}";
                string detail = slot.Loopback ? $"Windows: {_m.FindDevice(slot.EndpointOrExe)?.Name ?? "Gerät offline"} · {channels}" : channels;
                g.Text(detail,
                    new RectF(r.X + 10, r.Bottom - 20, Math.Max(40, acts.Gain.X - r.X - 16), 16), Theme.Caption, Theme.TextSecondary);

                double gainDb = dm?.GainDb ?? 0.0;
                bool muted = dm?.Mute ?? false;
                Draw.ButtonLook(g, acts.Gain, Db.Format(gainDb), ButtonStyle.Gray, true, hot && _hotCableAction == 0, font: Theme.MicroStrong);
                Draw.ButtonLook(g, acts.Mute, "M", muted ? ButtonStyle.Destructive : ButtonStyle.Gray, true, hot && _hotCableAction == 1, font: Theme.MicroStrong);
                Draw.ButtonLook(g, acts.Enable, slot.Enabled ? "Aktiv" : "Aus", slot.Enabled ? ButtonStyle.Tinted : ButtonStyle.Gray, true, hot && _hotCableAction == 2, font: Theme.MicroStrong);
                Draw.ButtonLook(g, acts.Del, "", ButtonStyle.DestructiveTinted, true, hot && _hotCableAction == 3, icon: Icon.Trash, font: Theme.MicroStrong);
            }
        }
        g.PopClip();
        _cableScroll.Paint(g);

        // Ehrlicher Kernel-Treiber-Statuskasten unten links
        g.FillRound(_kernelBox, Theme.RadiusInner, Theme.Well);
        g.StrokeRound(_kernelBox, Theme.RadiusInner, Theme.Border);
        var vaio = _m.VaioAvailability;
        g.Text("NX-MIX NATIVE KERNEL-VAD", new RectF(_kernelBox.X + 12, _kernelBox.Y + 8, _kernelBox.W - 140, 18), Theme.MicroStrong, Theme.TextSecondary);
        Draw.Chip(g, _kernelBox.Right - 10, _kernelBox.Y + 17,
            vaio.NativeKernelDriverInstalled ? "Treiber aktiv" : "Kein Kernel-Treiber",
            vaio.NativeKernelDriverInstalled ? Theme.Green : Theme.TextSecondary,
            dot: true, Theme.MicroStrong, alignRight: true);
        g.Text(vaio.NativeDriverMessage,
            new RectF(_kernelBox.X + 12, _kernelBox.Y + 30, _kernelBox.W - 24, _kernelBox.H - 36),
            Theme.Caption, Theme.TextSecondary, HAlign.Left, VAlign.Top, false, true);

        // ---- Rechte Karte: App-Audio-Capture (ProcessLoopback) ----
        Draw.Card(g, _appCard);
        Draw.CardTitle(g, new RectF(_appCard.X + pad, _appCard.Y + 11, Math.Max(40, _appSw.Bounds.X - (_appCard.X + pad) - 8), Theme.ControlHSmall),
            "App-Capture", _m.Config.AppLinks.Count);
        if (_redirectOptions.Checked) g.Text("OPTIONALE STILLE UMLEITUNG", _parkingLabelRect, Theme.MicroStrong, Theme.TextSecondary);

        g.PushClip(_appListRect);
        if (_m.Config.AppLinks.Count == 0)
        {
            Draw.Empty(g, _appListRect, _appListRect.Y + 14, Icon.Link,
                "Noch kein Programm eingebunden",
                "Binde Spotify, Browser, Discord oder Spiele per „+ Programm …“ direkt an eigene Mixer-Kanäle – ganz ohne zusätzliches Kabel.");
        }
        else
        {
            for (int i = 0; i < _m.Config.AppLinks.Count; i++)
            {
                var r = AppRowRect(i);
                if (r.Bottom < _appListRect.Y || r.Y > _appListRect.Bottom) continue;
                var entry = _m.Config.AppLinks[i];
                var link = new AppLink(entry.ExeName, entry.FirstChannel, entry.Count, entry.DisplayName);
                var view = _m.ViewOf(link);
                bool hot = i == _hotApp;

                g.FillRound(r, 9, hot ? Theme.ControlHover : Theme.Well);
                g.StrokeRound(r, 9, Theme.Border);

                var tile = new RectF(r.X + 10, r.CY - 15, 30, 30);
                var col = LinkLook.Color(view.State);
                g.FillRound(tile, 8, Theme.Soft(col));
                string letter = link.Title.Length > 0 ? link.Title[..1].ToUpperInvariant() : "?";
                g.Text(letter, tile, Theme.BodyStrong, col, HAlign.Center, VAlign.Middle, false);

                var acts = AppActionRects(r);
                float tx = tile.Right + 10;
                bool hasDetail = !string.IsNullOrWhiteSpace(view.Message);
                float statusY = hasDetail ? r.Y + 15 : r.CY;
                Draw.Chip(g, acts.Edit.X - 8, statusY, LinkLook.Text(view.State), col, dot: true, Theme.MicroStrong, alignRight: true);
                var name = AppNameRect(r, view);

                g.Text(link.Title, name, Theme.BodyMedium, Theme.Text);
                string subtitle = hasDetail ? $"{LinkLook.Target(link)}  ·  {view.Message}" : $"{entry.ExeName}  ·  {LinkLook.Target(link)}";
                float subtitleWidth = hasDetail ? acts.Edit.X - 8 - tx : name.W;
                g.Text(subtitle, new RectF(tx, r.Bottom - 20, Math.Max(30, subtitleWidth), 16), Theme.Caption, Theme.TextSecondary);

                Draw.ButtonLook(g, acts.Edit, "Ändern", ButtonStyle.Gray, true, hot && _hotAppAction == 0, font: Theme.MicroStrong);
                Draw.ButtonLook(g, acts.Del, "Lösen", ButtonStyle.DestructiveTinted, true, hot && _hotAppAction == 1, font: Theme.MicroStrong);
            }
        }
        g.PopClip();
        _appScroll.Paint(g);

        PaintChildren(g);
    }
}
