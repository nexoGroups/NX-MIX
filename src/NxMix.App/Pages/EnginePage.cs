using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Audio;
using NxMix.Ipc;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Tab „Engine“ (Strg+8):
/// Steuerung des eigenständigen NX-MIX Hintergrunddienstes (NxMix.Daemon), Master-Clock &amp; Puffer-Konfiguration
/// (mit transparenter Kennzeichnung von kurzen Stream-Reopens), Echtzeit-Telemetrie, Companion-API, Darstellung
/// sowie „Updates &amp; Release Channel“ (Stable / Beta).</summary>
internal sealed class EnginePage : Page
{
    readonly AppModel _m;
    int _leftTab;  // 0 = Dienst & Takt, 1 = API & Ansicht, 2 = Updates & Release Channel
    int _rightTab; // 0 = Geräte-Streams, 1 = Diagnose-Log

    // Linke Karte — Kopfleiste & Umschalter
    readonly Button _audioBtn = new("Audio starten", ButtonStyle.Filled, Icon.Play) { Font = Theme.CaptionStrong };
    readonly Segmented _cfgSeg = new("Dienst & Takt", "API & Ansicht", "Updates") { Font = Theme.CaptionStrong };

    // Linke Karte — Tab 0: Dienst & Takt
    readonly Dropdown _clockDd = new() { Tooltip = "Master-Clock-Quelle wählen (kurzer Stream-Reopen ~50 ms)" };
    readonly Dropdown _rateDd = new() { Tooltip = "System-Sample-Rate wählen (kurzer Stream-Reopen ~50 ms)" };
    readonly Dropdown _blockDd = new() { Tooltip = "Ziel-Puffergröße in Frames wählen (kurzer Stream-Reopen ~50 ms)" };
    readonly Switch _logonSw = new() { Tooltip = "NX-MIX Hintergrund-Engine automatisch bei Windows-Anmeldung im Tray starten" };
    readonly Switch _bootAudioSw = new() { Tooltip = "Audio-Verarbeitung beim Start des Hintergrunddienstes sofort aktivieren" };
    readonly Switch _offlineAuxSw = new() { Tooltip = "Abgesteckte Zweit-/USB-Geräte beim Start als Offline tolerieren statt abzubrechen" };
    readonly Button _shutdownBtn = new("Hintergrund-Engine vollständig beenden …", ButtonStyle.DestructiveTinted, Icon.Stop)
    {
        Font = Theme.CaptionStrong,
        Tooltip = "Stoppt Audio, hebt App-Umleitungen auf und beendet NxMix.Daemon.exe sowie das Fenster",
    };

    // Linke Karte — Tab 1: API & Ansicht
    readonly Switch _apiSw = new() { Tooltip = "Lokalen JSON-RPC-WebSocket-Server für Bitfocus Companion / Stream Deck+ aktivieren" };
    readonly Switch _loopbackSw = new() { Tooltip = "WebSocket-Server strikt auf 127.0.0.1 beschränken (empfohlen)" };
    readonly Switch _exclSw = new() { Tooltip = "Standardmäßig WASAPI-Exclusive für physische Windows-Geräte verwenden" };
    readonly Dropdown _themeDd = new() { Tooltip = "Farbschema der Oberfläche" };
    readonly Segmented _densitySeg = new("Maus", "Touch") { Font = Theme.CaptionMedium };
    readonly Dropdown _scaleDd = new() { Tooltip = "Zusätzliche UI-Skalierung (75 % .. 150 %)" };
    readonly Button _updateBtn = new("Updates …", ButtonStyle.Gray, Icon.Refresh) { Font = Theme.CaptionMedium, Tooltip = "Abschnitt „Updates & Release Channel“ öffnen" };

    // Linke Karte — Tab 2: Updates & Release Channel
    readonly Segmented _channelSeg = new("Stable", "Beta")
    {
        Font = Theme.CaptionStrong,
        Tooltip = "Release-Kanal wählen: Stable (offizielle Live-Versionen) oder Beta (Vorabversionen & neue Funktionen)",
    };
    readonly Switch _autoUpdateSw = new() { Tooltip = "Beim Start automatisch im gewählten Release-Kanal nach Updates suchen (installiert niemals ungefragt)" };
    readonly Button _checkUpdatesBtn = new("Updates suchen", ButtonStyle.Gray, Icon.Refresh)
    {
        Font = Theme.CaptionStrong,
        Tooltip = "Jetzt manuell auf GitHub im gewählten Release-Kanal nach Updates suchen",
    };
    readonly Button _changelogBtn = new("Changelog", ButtonStyle.Gray)
    {
        Font = Theme.CaptionStrong,
        Tooltip = "Versionshinweise (Release Notes) anzeigen oder auf GitHub öffnen",
    };
    readonly Button _installUpdateBtn = new("Kein Update bereit", ButtonStyle.Gray, Icon.Import)
    {
        Font = Theme.CaptionStrong,
        Tooltip = "Gefundenes Update herunterladen bzw. kontrolliert installieren",
    };

    // Rechte Karte — Echtzeit-Telemetrie & Diagnose
    readonly Segmented _telSeg = new("Geräte-Streams", "Diagnose-Log") { Font = Theme.CaptionStrong };
    readonly VScroll _rightScroll = new();

    RectF _leftCard, _rightCard, _kpiRect, _clockInfoRect, _rightListRect;
    RectF _updVersionRect, _updOfferRect, _updStatusRect;
    readonly List<(string Label, float Y, float H, bool ReopenBadge)> _leftLabels = new();

    public override string Title => "Engine";
    public override Icon Icon => Icon.Engine;
    public override string? Subtitle =>
        $"{_m.ConnectionSummary}  ·  {_m.Settings.SampleRate / 1000.0:0.#} kHz / {_m.Config.BlockFrames} Frames  ·  Master: {_m.Stats.MasterClockName ?? "Automatisch"}";

    public int SelectedLeftSection => _leftTab;

    public void SelectLeftSection(int index)
    {
        _leftTab = Math.Clamp(index, 0, 2);
        _cfgSeg.SetSelected(_leftTab);
        Layout();
        Invalidate();
    }

    public EnginePage(AppModel m)
    {
        _m = m;
        Add(_audioBtn); Add(_cfgSeg);

        Add(_clockDd); Add(_rateDd); Add(_blockDd);
        Add(_logonSw); Add(_bootAudioSw); Add(_offlineAuxSw); Add(_shutdownBtn);

        Add(_apiSw); Add(_loopbackSw); Add(_exclSw);
        Add(_themeDd); Add(_densitySeg); Add(_scaleDd); Add(_updateBtn);

        Add(_channelSeg); Add(_autoUpdateSw);
        Add(_checkUpdatesBtn); Add(_changelogBtn); Add(_installUpdateBtn);

        Add(_telSeg);

        _audioBtn.Clicked += () =>
        {
            if (_m.HostRunning) _m.StopAudio();
            else _m.StartAudio();
        };

        _cfgSeg.Changed += idx => { _leftTab = idx; Layout(); Invalidate(); };
        _telSeg.Changed += idx => { _rightTab = idx; _rightScroll.Offset = 0; Layout(); Invalidate(); };

        WireDaemonAndClockControls();
        WireApiAndUiControls();
        WireUpdateControls();

        m.HostChanged += () => { Layout(); Invalidate(); };
        m.BindingsChanged += () => { Layout(); Invalidate(); };
        m.ValuesChanged += () => Invalidate();
    }

    public override void OnShow() => Layout();

    public override void Tick(float dt)
    {
        if (_m.HostRunning) Invalidate();
    }

    void WireDaemonAndClockControls()
    {
        _clockDd.Text = () =>
        {
            string? pref = _m.Config.PreferredMasterDeviceId;
            if (string.IsNullOrWhiteSpace(pref)) return "Automatisch (ASIO bevorzugt)";
            var node = _m.Config.Devices.FirstOrDefault(d => string.Equals(d.EndpointId, pref, StringComparison.OrdinalIgnoreCase));
            if (node != null) return node.DisplayTitle;
            return _m.FindDevice(pref)?.Name ?? pref;
        };
        _clockDd.Items = () =>
        {
            var items = new List<MenuItem>
            {
                MenuItem.Header("Master-Clock-Quelle (löst ~50 ms Stream-Reopen aus)"),
                new("Automatisch (ASIO-Hardware bevorzugt)", null,
                    () => _m.ApplySystemConfig(_m.Config with { PreferredMasterDeviceId = null }))
                {
                    Checked = string.IsNullOrWhiteSpace(_m.Config.PreferredMasterDeviceId),
                },
                MenuItem.Sep,
            };

            foreach (var d in _m.Config.Devices)
            {
                var dev = d;
                items.Add(new MenuItem(
                    dev.DisplayTitle,
                    dev.Backend == AudioBackend.Asio ? "ASIO Native" : "WASAPI",
                    () => _m.ApplySystemConfig(_m.Config with { PreferredMasterDeviceId = dev.EndpointId }))
                {
                    Checked = string.Equals(_m.Config.PreferredMasterDeviceId, dev.EndpointId, StringComparison.OrdinalIgnoreCase),
                });
            }
            return items;
        };

        _rateDd.Text = () => $"{_m.Config.SampleRate / 1000.0:0.#} kHz ({_m.Config.SampleRate} Hz)";
        _rateDd.Items = () => AppModel.SampleRates.Select(sr => new MenuItem(
            $"{sr / 1000.0:0.#} kHz", $"{sr} Hz",
            () => _m.ApplySystemConfig(_m.Config with { SampleRate = sr }))
        {
            Checked = _m.Config.SampleRate == sr,
        }).ToList();

        _blockDd.Text = () =>
        {
            double ms = _m.Config.BlockFrames * 1000.0 / Math.Max(1, _m.Config.SampleRate);
            return $"{_m.Config.BlockFrames} Frames (~{ms:0.1} ms)";
        };
        _blockDd.Items = () => AppModel.BlockSizes.Select(bf =>
        {
            double ms = bf * 1000.0 / Math.Max(1, _m.Config.SampleRate);
            return new MenuItem($"{bf} Frames", $"~{ms:0.1} ms",
                () => _m.ApplySystemConfig(_m.Config with { BlockFrames = bf }))
            {
                Checked = _m.Config.BlockFrames == bf,
            };
        }).ToList();

        _logonSw.Changed += on => _m.ApplySystemConfig(_m.Config with { LaunchDaemonAtWindowsLogon = on }, reopenAudioIfNeeded: false);
        _bootAudioSw.Changed += on => _m.ApplySystemConfig(_m.Config with { AutoStartAudioOnBoot = on }, reopenAudioIfNeeded: false);
        _offlineAuxSw.Changed += on => _m.ApplySystemConfig(_m.Config with { AllowOfflineAuxDevices = on });

        _shutdownBtn.Clicked += () =>
        {
            Root?.Confirm(
                "Hintergrund-Engine wirklich beenden?",
                "Normales Schließen des Fensters lässt Audio und App-Links im Hintergrund weiterlaufen.\n\n" +
                "„Engine beenden“ stoppt alle Audio-Streams, setzt Windows-App-Umleitungen zurück und beendet sowohl NxMix.Daemon.exe als auch dieses Fenster.",
                "Engine beenden",
                () =>
                {
                    var root = Root;
                    root?.SaveNow();
                    _m.ShutdownDaemonExplicitly();
                    root?.Shell.Close();
                },
                danger: true);
        };
    }

    void WireApiAndUiControls()
    {
        _apiSw.Changed += on => _m.ApplySystemConfig(_m.Config with { CompanionApiEnabled = on }, reopenAudioIfNeeded: false);
        _loopbackSw.Changed += on => _m.ApplySystemConfig(_m.Config with { CompanionApiBindLoopbackOnly = on }, reopenAudioIfNeeded: false);
        _exclSw.Changed += on => _m.ApplySystemConfig(_m.Config with { DefaultWasapiExclusive = on });

        _themeDd.Text = () => (Root?.Prefs.Theme ?? ThemeMode.System) switch
        {
            ThemeMode.Dark => "Dunkel",
            ThemeMode.Light => "Hell",
            _ => "Systemstandard",
        };
        _themeDd.Items = () =>
        {
            var cur = Root?.Prefs.Theme ?? ThemeMode.System;
            return new List<MenuItem>
            {
                new("Systemstandard", null, () => Root?.SetThemeMode(ThemeMode.System)) { Checked = cur == ThemeMode.System },
                new("Dunkel (Live-Konsole)", null, () => Root?.SetThemeMode(ThemeMode.Dark)) { Checked = cur == ThemeMode.Dark },
                new("Hell", null, () => Root?.SetThemeMode(ThemeMode.Light)) { Checked = cur == ThemeMode.Light },
            };
        };

        _densitySeg.Changed += idx => Root?.SetDensity(idx == 1 ? Density.Touch : Density.Mouse);

        _scaleDd.Text = () => $"{Root?.Prefs.UiScalePercent ?? 100} %";
        _scaleDd.Items = () =>
        {
            int cur = Root?.Prefs.UiScalePercent ?? 100;
            int[] scales = { 75, 85, 100, 110, 125, 150 };
            return scales.Select(s => new MenuItem($"{s} %", s == 100 ? "Standard" : null, () => Root?.SetUiScale(s))
            {
                Checked = cur == s,
            }).ToList();
        };

        _updateBtn.Clicked += () => SelectLeftSection(2);
    }

    void WireUpdateControls()
    {
        _channelSeg.Changed += idx =>
        {
            Root?.SetUpdateChannel(idx == 1 ? ReleaseChannel.Beta : ReleaseChannel.Stable);
            Layout();
            Invalidate();
        };

        _autoUpdateSw.Changed += on =>
        {
            Root?.SetAutoUpdateCheck(on);
            Invalidate();
        };

        _checkUpdatesBtn.Clicked += () =>
        {
            Root?.Updater.Check();
            Layout();
            Invalidate();
        };

        _changelogBtn.Clicked += () => Root?.OpenChangelog();

        _installUpdateBtn.Clicked += () =>
        {
            var u = Root?.Updater;
            if (u == null) return;
            if (u.Stage == UpdateStage.Available)
                u.Download();
            else if (u.Stage == UpdateStage.Ready)
                Root?.ConfirmAndInstallUpdate();
        };
    }

    static float StreamRowH => Theme.Touch ? 46f : 40f;
    static float LogRowH => Theme.Touch ? 34f : 28f;

    public override void Layout()
    {
        var area = Bounds.Deflate(Theme.Pad, 8, Theme.Pad, Theme.Pad);
        float gap = Theme.Gap;
        float leftW = MathF.Floor((area.W - gap) * 0.47f);
        float rightW = Math.Max(220f, area.W - leftW - gap);

        _leftCard = new RectF(area.X, area.Y, leftW, area.H);
        _rightCard = new RectF(_leftCard.Right + gap, area.Y, rightW, area.H);

        float pad = 12f;
        float ch = Theme.ControlHSmall;

        // ---- Linke Karte ----
        float ly = _leftCard.Y + 10;
        bool running = _m.HostRunning;
        bool tab0 = _leftTab == 0;
        bool tab1 = _leftTab == 1;
        bool tab2 = _leftTab == 2;

        _audioBtn.Visible = !tab2;
        _audioBtn.Text = running ? "Audio stoppen" : "Audio starten";
        _audioBtn.Icon = running ? Icon.Stop : Icon.Play;
        _audioBtn.Style = running ? ButtonStyle.DestructiveTinted : ButtonStyle.Filled;
        _audioBtn.Bounds = new RectF(_leftCard.Right - pad - 126, ly, 126, ch);

        ly += ch + 6;
        _cfgSeg.SetSelected(_leftTab);
        _cfgSeg.Bounds = new RectF(_leftCard.X + pad, ly, _leftCard.W - 2 * pad, ch);
        ly += ch + 8;

        _clockDd.Visible = _rateDd.Visible = _blockDd.Visible = _logonSw.Visible = _bootAudioSw.Visible = _offlineAuxSw.Visible = _shutdownBtn.Visible = tab0;
        _apiSw.Visible = _loopbackSw.Visible = _exclSw.Visible = _themeDd.Visible = _densitySeg.Visible = _scaleDd.Visible = _updateBtn.Visible = tab1;
        _channelSeg.Visible = _autoUpdateSw.Visible = _checkUpdatesBtn.Visible = _changelogBtn.Visible = _installUpdateBtn.Visible = tab2;

        _leftLabels.Clear();
        float innerW = _leftCard.W - 2 * pad;
        float availRowsH = Math.Max(160f, _leftCard.Bottom - pad - ly);
        float rowStep = Math.Min(Theme.Touch ? 38f : 34f, MathF.Floor((availRowsH - (tab0 ? (ch + 4) : 0)) / 6f));
        float ctrlH = Math.Min(ch, rowStep - 3f);
        float ddW = Math.Clamp(_leftCard.W * 0.50f, 134f, 220f);
        float rx = _leftCard.Right - pad;

        if (tab0)
        {
            _logonSw.SetOn(_m.Config.LaunchDaemonAtWindowsLogon);
            _bootAudioSw.SetOn(_m.Config.AutoStartAudioOnBoot);
            _offlineAuxSw.SetOn(_m.Config.AllowOfflineAuxDevices);

            _leftLabels.Add(("Master-Clock", ly, ctrlH, true));
            _clockDd.Bounds = new RectF(rx - ddW, ly, ddW, ctrlH);
            ly += rowStep;

            _leftLabels.Add(("Sample-Rate", ly, ctrlH, true));
            _rateDd.Bounds = new RectF(rx - ddW, ly, ddW, ctrlH);
            ly += rowStep;

            _leftLabels.Add(("Puffergröße", ly, ctrlH, true));
            _blockDd.Bounds = new RectF(rx - ddW, ly, ddW, ctrlH);
            ly += rowStep;

            _leftLabels.Add(("Mit Windows starten (Logon)", ly, ctrlH, false));
            _logonSw.Bounds = new RectF(rx - Theme.SwitchW, ly, Theme.SwitchW, ctrlH);
            ly += rowStep;

            _leftLabels.Add(("Audio beim Dienststart aktiv", ly, ctrlH, false));
            _bootAudioSw.Bounds = new RectF(rx - Theme.SwitchW, ly, Theme.SwitchW, ctrlH);
            ly += rowStep;

            _leftLabels.Add(("Offline-Hilfsgeräte tolerieren", ly, ctrlH, false));
            _offlineAuxSw.Bounds = new RectF(rx - Theme.SwitchW, ly, Theme.SwitchW, ctrlH);
            ly += rowStep;

            float shutY = Math.Max(ly + 2, _leftCard.Bottom - pad - ctrlH);
            _shutdownBtn.Bounds = new RectF(_leftCard.X + pad, shutY, innerW, ctrlH);
        }
        else if (tab1)
        {
            _apiSw.SetOn(_m.Config.CompanionApiEnabled);
            _loopbackSw.SetOn(_m.Config.CompanionApiBindLoopbackOnly);
            _exclSw.SetOn(_m.Config.DefaultWasapiExclusive);
            _densitySeg.SetSelected((Root?.Prefs.Density ?? Density.Mouse) == Density.Touch ? 1 : 0);

            _leftLabels.Add(($"Companion API (Port {_m.Config.CompanionApiPort})", ly, ctrlH, false));
            _apiSw.Bounds = new RectF(rx - Theme.SwitchW, ly, Theme.SwitchW, ctrlH);
            ly += rowStep;

            _leftLabels.Add(("API nur Loopback (127.0.0.1)", ly, ctrlH, false));
            _loopbackSw.Bounds = new RectF(rx - Theme.SwitchW, ly, Theme.SwitchW, ctrlH);
            ly += rowStep;

            _leftLabels.Add(("Standard WASAPI-Exclusive", ly, ctrlH, true));
            _exclSw.Bounds = new RectF(rx - Theme.SwitchW, ly, Theme.SwitchW, ctrlH);
            ly += rowStep;

            _leftLabels.Add(("Farbschema", ly, ctrlH, false));
            _themeDd.Bounds = new RectF(rx - ddW, ly, ddW, ctrlH);
            ly += rowStep;

            _leftLabels.Add(("Bedienmodus", ly, ctrlH, false));
            _densitySeg.Bounds = new RectF(rx - ddW, ly, ddW, ctrlH);
            ly += rowStep;

            _leftLabels.Add(("UI-Skalierung", ly, ctrlH, false));
            float updW = 84f;
            _updateBtn.Bounds = new RectF(rx - updW, ly, updW, ctrlH);
            float scW = Math.Max(68f, ddW - updW - 6f);
            _scaleDd.Bounds = new RectF(_updateBtn.Bounds.X - 6 - scW, ly, scW, ctrlH);
        }
        else
        {
            var u = Root?.Updater;
            var selChan = u?.SelectedChannel ?? ReleaseChannel.Stable;
            _channelSeg.SetSelected(selChan == ReleaseChannel.Beta ? 1 : 0);
            _autoUpdateSw.SetOn(Root?.Prefs.AutoUpdateCheck ?? true);

            bool compact = availRowsH < 340f;
            float verH = compact ? 38f : 44f;
            float offerH = compact ? 34f : 40f;
            float uCtrlH = Math.Min(ch, compact ? 27f : 30f);
            float uGap = compact ? 4f : 6f;
            float chanSegW = Math.Clamp(_leftCard.W * 0.48f, 124f, 196f);

            _updVersionRect = new RectF(_leftCard.X + pad, ly, innerW, verH);
            ly += verH + uGap;

            _leftLabels.Add(("Release-Kanal", ly, uCtrlH, false));
            _channelSeg.Bounds = new RectF(rx - chanSegW, ly, chanSegW, uCtrlH);
            ly += uCtrlH + uGap;

            _leftLabels.Add(("Automatisch prüfen", ly, uCtrlH, false));
            _autoUpdateSw.Bounds = new RectF(rx - Theme.SwitchW, ly, Theme.SwitchW, uCtrlH);
            ly += uCtrlH + uGap;

            _updOfferRect = new RectF(_leftCard.X + pad, ly, innerW, offerH);
            ly += offerH + uGap;

            bool busy = u?.Stage is UpdateStage.Checking or UpdateStage.Downloading;
            _checkUpdatesBtn.Enabled = !busy;
            _checkUpdatesBtn.Text = u?.Stage == UpdateStage.Checking ? "Prüfe …" : "Updates suchen";

            float btnGap = 6f;
            float checkW = MathF.Floor((innerW - btnGap) * 0.56f);
            float logW = Math.Max(70f, innerW - btnGap - checkW);
            _checkUpdatesBtn.Bounds = new RectF(_leftCard.X + pad, ly, checkW, uCtrlH);
            _changelogBtn.Bounds = new RectF(_checkUpdatesBtn.Bounds.Right + btnGap, ly, logW, uCtrlH);
            ly += uCtrlH + uGap;

            ConfigureInstallButton(u);
            _installUpdateBtn.Bounds = new RectF(_leftCard.X + pad, ly, innerW, uCtrlH);
            ly += uCtrlH + uGap;

            float statusH = Math.Max(46f, _leftCard.Bottom - pad - ly);
            _updStatusRect = new RectF(_leftCard.X + pad, ly, innerW, statusH);
        }

        // ---- Rechte Karte ----
        float ry = _rightCard.Y + 10;
        float telW = Math.Clamp(_rightCard.W * 0.48f, 176f, 236f);
        _telSeg.SetSelected(_rightTab);
        _telSeg.Bounds = new RectF(_rightCard.Right - pad - telW, ry, telW, ch);

        _kpiRect = new RectF(_rightCard.X + pad, ry + ch + 8, _rightCard.W - 2 * pad, 50f);
        _clockInfoRect = new RectF(_rightCard.X + pad, _kpiRect.Bottom + 6, _rightCard.W - 2 * pad, 20f);
        _rightListRect = RectF.FromLTRB(_rightCard.X + pad, _clockInfoRect.Bottom + 6, _rightCard.Right - pad, _rightCard.Bottom - pad);

        int count = _rightTab == 0
            ? (_m.Stats.Devices.Count > 0 ? _m.Stats.Devices.Count : _m.Config.Devices.Count)
            : _m.RecentLogs.Count;
        float rh = _rightTab == 0 ? (StreamRowH + 5) : (LogRowH + 3);
        _rightScroll.Update(_rightListRect, count * rh);
    }

    void ConfigureInstallButton(AppUpdater? u)
    {
        if (u == null)
        {
            _installUpdateBtn.Text = "Kein Update bereit";
            _installUpdateBtn.Style = ButtonStyle.Gray;
            _installUpdateBtn.Enabled = false;
            return;
        }

        switch (u.Stage)
        {
            case UpdateStage.Available when u.Offer != null:
                _installUpdateBtn.Text = u.Offer.IsDowngrade
                    ? $"Stable v{u.Offer.Version} herunterladen"
                    : $"Update v{u.Offer.Version} herunterladen";
                _installUpdateBtn.Style = ButtonStyle.Filled;
                _installUpdateBtn.Enabled = true;
                break;

            case UpdateStage.Downloading:
                _installUpdateBtn.Text = $"Wird geladen … {u.Progress} %";
                _installUpdateBtn.Style = ButtonStyle.Gray;
                _installUpdateBtn.Enabled = false;
                break;

            case UpdateStage.Ready when u.Offer != null:
                if (u.InstallOnExitPending)
                {
                    _installUpdateBtn.Text = $"Beim Beenden installieren (v{u.Offer.Version})";
                    _installUpdateBtn.Style = ButtonStyle.Tinted;
                    _installUpdateBtn.Enabled = false;
                }
                else
                {
                    _installUpdateBtn.Text = u.Offer.IsDowngrade
                        ? $"Auf Stable v{u.Offer.Version} wechseln …"
                        : $"Update v{u.Offer.Version} installieren …";
                    _installUpdateBtn.Style = ButtonStyle.Filled;
                    _installUpdateBtn.Enabled = true;
                }
                break;

            default:
                _installUpdateBtn.Text = "Kein Update zur Installation bereit";
                _installUpdateBtn.Style = ButtonStyle.Gray;
                _installUpdateBtn.Enabled = false;
                break;
        }
    }

    public override bool AcceptsMouse => true;

    public override void OnMouseMove(in MouseEvent e)
    {
        if (_rightScroll.Dragging) { _rightScroll.Drag(e.Y); Invalidate(); }
    }

    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button == 0 && _rightScroll.Down(e.X, e.Y)) Invalidate();
    }

    public override void OnMouseUp(in MouseEvent e) => _rightScroll.Up();

    public override bool OnMouseWheel(in MouseEvent e)
    {
        if (_rightListRect.Contains(e.X, e.Y) && _rightScroll.Wheel(e.Wheel)) { Invalidate(); return true; }
        return false;
    }

    public override void Paint(Renderer g)
    {
        float pad = 12f;

        // ---- Linke Karte ----
        Draw.Card(g, _leftCard);
        float leftTitleW = _leftTab == 2
            ? Math.Max(80f, _leftCard.W - 2 * pad)
            : Math.Max(60f, _audioBtn.Bounds.X - (_leftCard.X + pad) - 8);
        Draw.CardTitle(g, new RectF(_leftCard.X + pad, _leftCard.Y + 10, leftTitleW, Theme.ControlHSmall),
            _leftTab == 2 ? "Updates & Release Channel" : "Engine & Takt");

        foreach (var row in _leftLabels)
        {
            float maxW = _leftCard.W * 0.46f;
            g.Text(row.Label, new RectF(_leftCard.X + pad, row.Y, maxW, row.H), Theme.Body, Theme.Text);
            if (row.ReopenBadge && maxW > 130f)
            {
                float tw = Renderer.MeasureWidth(row.Label, Theme.Body);
                if (tw + 52 < maxW)
                    Draw.Chip(g, _leftCard.X + pad + tw + 6, row.Y + row.H / 2f, "~50ms", Theme.Orange, dot: false, Theme.MicroStrong);
            }
        }

        if (_leftTab == 2)
            PaintUpdatesSection(g);

        // ---- Rechte Karte: Echtzeit-Telemetrie ----
        Draw.Card(g, _rightCard);
        Draw.CardTitle(g, new RectF(_rightCard.X + pad, _rightCard.Y + 10, Math.Max(60, _telSeg.Bounds.X - (_rightCard.X + pad) - 8), Theme.ControlHSmall),
            "Telemetrie");

        PaintKpiStrip(g, _kpiRect);

        var st = _m.Stats;
        string stateTxt = st.State switch
        {
            HostState.Running => "AKTIV",
            HostState.Starting => "STARTET",
            HostState.Faulted => "FEHLER",
            _ => "BEREIT",
        };
        var stateCol = st.State switch
        {
            HostState.Running => Theme.Green,
            HostState.Faulted => Theme.Red,
            HostState.Starting => Theme.Orange,
            _ => Theme.TextSecondary,
        };
        float sw = Draw.Chip(g, _clockInfoRect.X, _clockInfoRect.CY, stateTxt, stateCol, dot: true, Theme.MicroStrong);
        g.Text($"Master: {st.MasterClockName ?? "Automatisch"}  ·  ws://127.0.0.1:{_m.Config.CompanionApiPort}/v1/ws ({(_m.Config.CompanionApiEnabled ? "aktiv" : "aus")})",
            new RectF(_clockInfoRect.X + sw + 8, _clockInfoRect.Y, Math.Max(40, _clockInfoRect.W - sw - 8), _clockInfoRect.H),
            Theme.Caption, Theme.TextSecondary);

        g.PushClip(_rightListRect);
        if (_rightTab == 0)
            PaintStreamRows(g);
        else
            PaintLogRows(g);
        g.PopClip();
        _rightScroll.Paint(g);

        PaintChildren(g);
    }

    void PaintKpiStrip(Renderer g, RectF area)
    {
        var st = _m.Stats;
        (string Title, string Value, Color4? Tint)[] kpis =
        {
            ("CPU-LAST", $"{st.CpuLoad * 100.0:0.1} %", st.CpuLoad > 0.75 ? Theme.Red : st.CpuLoad > 0.45 ? Theme.Orange : Theme.Green),
            ("PUFFER", $"{st.BlockFrames} f", null),
            ("LATENZ", $"{st.OutputLatencyMs:0.1} ms", null),
            ("XRUNS (U/O)", $"{st.Underruns}/{st.Overruns}", (st.Underruns + st.Overruns) > 0 ? Theme.Orange : null),
            ("LIMITER", _m.LimiterHits.ToString(), _m.LimiterHits > 0 ? Theme.Orange : null),
        };

        float gap = 6f;
        float w = (area.W - gap * (kpis.Length - 1)) / kpis.Length;
        for (int i = 0; i < kpis.Length; i++)
        {
            var r = new RectF(area.X + i * (w + gap), area.Y, w, area.H);
            g.FillRound(r, 8, Theme.Well);
            g.StrokeRound(r, 8, Theme.Border);
            g.Text(kpis[i].Title, new RectF(r.X + 6, r.Y + 5, r.W - 12, 14), Theme.MicroStrong, Theme.TextTertiary, HAlign.Center);
            g.Text(kpis[i].Value, new RectF(r.X + 4, r.Y + 21, r.W - 8, 22), Theme.Value, kpis[i].Tint ?? Theme.Text, HAlign.Center);
        }
    }

    void PaintStreamRows(Renderer g)
    {
        var live = _m.Stats.Devices;
        float w = _rightListRect.W - (_rightScroll.Needed ? 12 : 0);

        if (live.Count > 0)
        {
            for (int i = 0; i < live.Count; i++)
            {
                var r = new RectF(_rightListRect.X, _rightListRect.Y + i * (StreamRowH + 5) - _rightScroll.Offset, w, StreamRowH);
                if (r.Bottom < _rightListRect.Y || r.Y > _rightListRect.Bottom) continue;
                var d = live[i];

                g.FillRound(r, 8, Theme.Well);
                g.StrokeRound(r, 8, d.IsMasterClock ? Theme.Purple : Theme.Border);

                float x = r.X + 8;
                if (d.IsMasterClock)
                    x += Draw.Chip(g, x, r.CY, "MASTER", Theme.Purple, dot: false, Theme.MicroStrong) + 6;

                float statusW = Draw.Chip(g, r.Right - 8, r.CY,
                    d.IsOffline ? "Offline" : "Streaming",
                    d.IsOffline ? Theme.Orange : Theme.Green, dot: true, Theme.MicroStrong, alignRight: true);

                float textW = Math.Max(30, (r.Right - 14 - statusW) - x);
                g.Text(d.Name, new RectF(x, r.Y + 4, textW, 18), Theme.BodyMedium, Theme.Text);
                string metrics = d.IsOffline
                    ? (d.OfflineReason ?? "Gerät nicht verbunden")
                    : $"Füllstand {d.BufferFill * 100:0}%  ·  Drift {d.ResampleRatioPpm:+0.0;-0.0;0.0} ppm  ·  Glitches {d.Glitches}";
                g.Text(metrics, new RectF(x, r.Bottom - 18, textW, 15), Theme.Caption, Theme.TextSecondary);
            }
            return;
        }

        if (_m.Config.Devices.Count == 0)
        {
            Draw.Empty(g, _rightListRect, _rightListRect.Y + 10, Icon.Engine,
                "Keine Hardware-Streams konfiguriert",
                "Binde unter „Geräte“ ASIO- oder WASAPI-Interfaces ein und starte die Audio-Engine.");
            return;
        }

        for (int i = 0; i < _m.Config.Devices.Count; i++)
        {
            var r = new RectF(_rightListRect.X, _rightListRect.Y + i * (StreamRowH + 5) - _rightScroll.Offset, w, StreamRowH);
            if (r.Bottom < _rightListRect.Y || r.Y > _rightListRect.Bottom) continue;
            var n = _m.Config.Devices[i];
            g.FillRound(r, 8, Theme.Well);
            g.StrokeRound(r, 8, Theme.Border);
            float bw = Draw.Chip(g, r.X + 8, r.CY, n.Backend == AudioBackend.Asio ? "ASIO" : "WASAPI",
                n.Backend == AudioBackend.Asio ? Theme.Purple : Theme.TextSecondary, dot: false, Theme.MicroStrong);
            float sw = Draw.Chip(g, r.Right - 8, r.CY, "Gestoppt", Theme.TextSecondary, dot: true, Theme.MicroStrong, alignRight: true);
            g.Text($"{n.DisplayTitle} ({n.ChannelCount}ch)",
                new RectF(r.X + 14 + bw, r.Y, Math.Max(30, r.Right - 22 - sw - (r.X + 14 + bw)), r.H), Theme.BodyMedium, Theme.Text);
        }
    }

    void PaintLogRows(Renderer g)
    {
        var logs = _m.RecentLogs;
        if (logs.Count == 0)
        {
            Draw.Empty(g, _rightListRect, _rightListRect.Y + 10, Icon.Check,
                "Keine Diagnose-Meldungen",
                "Ereignisse des Hintergrunddienstes (Gerätewechsel, Snapshots, API) erscheinen hier in Echtzeit.");
            return;
        }

        float w = _rightListRect.W - (_rightScroll.Needed ? 12 : 0);
        for (int i = 0; i < logs.Count; i++)
        {
            var entry = logs[logs.Count - 1 - i];
            var r = new RectF(_rightListRect.X, _rightListRect.Y + i * (LogRowH + 3) - _rightScroll.Offset, w, LogRowH);
            if (r.Bottom < _rightListRect.Y || r.Y > _rightListRect.Bottom) continue;

            var col = entry.Level.Equals("Error", StringComparison.OrdinalIgnoreCase) ? Theme.Red
                : entry.Level.Equals("Warn", StringComparison.OrdinalIgnoreCase) ? Theme.Orange
                : Theme.AccentText;

            g.FillRound(r, 6, Theme.Well);
            string ts = entry.TimestampUtc.ToLocalTime().ToString("HH:mm:ss");
            g.Text(ts, new RectF(r.X + 8, r.Y, 58, r.H), Theme.Micro, Theme.TextTertiary);
            float cw = Draw.Chip(g, r.X + 68, r.CY, entry.Category, col, dot: false, Theme.MicroStrong);
            g.Text(entry.Message, new RectF(r.X + 74 + cw, r.Y, Math.Max(30, r.W - 82 - cw), r.H), Theme.Caption, Theme.Text);
        }
    }

    void PaintUpdatesSection(Renderer g)
    {
        var u = Root?.Updater;
        string curVer = u?.CurrentVersion ?? AppVersion.Build;
        bool isBetaInstalled = u?.IsCurrentBetaBuild ?? AppVersion.IsBetaBuild;
        var selChannel = u?.SelectedChannel ?? ReleaseChannel.Stable;
        bool selBeta = selChannel == ReleaseChannel.Beta;

        // 1. Installierte Version & aktiver Release-Kanal
        g.FillRound(_updVersionRect, 8, Theme.Well);
        g.StrokeRound(_updVersionRect, 8, isBetaInstalled ? Theme.Orange.With(0.6f) : Theme.Border);

        float chipW = Draw.Chip(
            g,
            _updVersionRect.Right - 8,
            _updVersionRect.CY,
            selBeta ? "KANAL: BETA" : "KANAL: STABLE",
            selBeta ? Theme.Orange : Theme.Green,
            dot: true,
            Theme.MicroStrong,
            alignRight: true);

        float verLeftMax = Math.Max(60f, _updVersionRect.W - chipW - 22f);
        g.Text("Installierte Version", new RectF(_updVersionRect.X + 10, _updVersionRect.Y + 3, verLeftMax, 14),
            Theme.MicroStrong, Theme.TextTertiary);

        string verLabel = $"v{curVer}";
        float verY = _updVersionRect.Bottom - 20;
        g.Text(verLabel, new RectF(_updVersionRect.X + 10, verY, verLeftMax, 18), Theme.BodyStrong, Theme.Text);
        float vw = Renderer.MeasureWidth(verLabel, Theme.BodyStrong);
        if (isBetaInstalled && vw + 48 < verLeftMax)
        {
            Draw.Chip(g, _updVersionRect.X + 14 + vw, verY + 9f, "BETA", Theme.Orange, dot: false, Theme.MicroStrong);
        }

        // 2. Verfügbare Version
        g.FillRound(_updOfferRect, 8, Theme.Well);
        g.StrokeRound(_updOfferRect, 8, Theme.Border);

        var offer = u?.Offer;
        float offerRightChipW = 0f;
        if (offer != null)
        {
            string tagTxt = offer.IsDowngrade ? "DOWNGRADE" : offer.IsPrerelease ? "BETA" : "STABLE";
            var tagCol = (offer.IsDowngrade || offer.IsPrerelease) ? Theme.Orange : Theme.Green;
            offerRightChipW = Draw.Chip(g, _updOfferRect.Right - 8, _updOfferRect.CY, tagTxt, tagCol, dot: false, Theme.MicroStrong, alignRight: true);
        }

        string availText = (u?.Stage ?? UpdateStage.Idle) switch
        {
            UpdateStage.Checking => "Suche läuft …",
            UpdateStage.UpToDate => $"Aktuell (v{curVer})",
            UpdateStage.Available or UpdateStage.Downloading or UpdateStage.Ready when offer != null => $"v{offer.Version}",
            UpdateStage.NotInstalled => "Nur mit Setup-Installation",
            UpdateStage.Failed => "Prüfung fehlgeschlagen",
            _ => "Noch nicht geprüft",
        };
        var availColor = offer != null ? Theme.AccentText
            : u?.Stage == UpdateStage.UpToDate ? Theme.Green
            : u?.Stage == UpdateStage.Failed ? Theme.Red
            : Theme.TextSecondary;

        float offerLabelW = Math.Min(116f, _updOfferRect.W * 0.44f);
        g.Text("Verfügbar", new RectF(_updOfferRect.X + 10, _updOfferRect.Y, offerLabelW, _updOfferRect.H), Theme.CaptionStrong, Theme.TextSecondary);
        float offerValRight = _updOfferRect.Right - 10 - (offerRightChipW > 0 ? offerRightChipW + 6 : 0);
        float offerValX = _updOfferRect.X + 10 + offerLabelW;
        g.Text(availText, new RectF(offerValX, _updOfferRect.Y, Math.Max(30f, offerValRight - offerValX), _updOfferRect.H),
            offer != null ? Theme.BodyStrong : Theme.CaptionMedium, availColor, HAlign.Right);

        // 3. Status-, Live-Audio-Schutz- & Fehleranzeige
        var stage = u?.Stage ?? UpdateStage.Idle;
        var (statusBadge, statusCol, statusDetail) = BuildStatusSummary(u, stage, offer);

        g.FillRound(_updStatusRect, 8, Theme.Well);
        g.StrokeRound(_updStatusRect, 8, stage == UpdateStage.Failed ? Theme.Red.With(0.75f) : Theme.Border);

        g.PushClip(_updStatusRect.Deflate(2, 2));
        float sy = _updStatusRect.Y + 6;
        float scw = Draw.Chip(g, _updStatusRect.X + 8, sy + 8f, statusBadge, statusCol, dot: true, Theme.MicroStrong);
        if (_m.HostRunning && _updStatusRect.W - scw > 110f)
        {
            g.Text("Live-Schutz aktiv", new RectF(_updStatusRect.X + 14 + scw, sy, _updStatusRect.W - 22 - scw, 16),
                Theme.MicroStrong, Theme.Orange, HAlign.Right);
        }

        var detailRect = RectF.FromLTRB(_updStatusRect.X + 9, sy + 19, _updStatusRect.Right - 9, _updStatusRect.Bottom - 5);
        if (detailRect.H >= 12f)
        {
            g.Text(
                statusDetail,
                detailRect,
                Theme.Caption,
                stage == UpdateStage.Failed ? Theme.Red : Theme.TextSecondary,
                HAlign.Left,
                VAlign.Top,
                ellipsis: true,
                wrap: true);
        }
        g.PopClip();
    }

    (string Badge, Color4 Color, string Detail) BuildStatusSummary(AppUpdater? u, UpdateStage stage, UpdateOffer? offer)
    {
        if (u == null)
            return ("BEREIT", Theme.TextSecondary, "Update-Steuerung bereit.");

        return stage switch
        {
            UpdateStage.Failed => (
                "UPDATE-FEHLER",
                Theme.Red,
                u.Error ?? "Unbekannter Fehler beim Abrufen oder Prüfen des Update-Pakets."),

            UpdateStage.NotInstalled => (
                "PORTABLE / DEV",
                Theme.TextSecondary,
                "Für automatische Kanal-Updates bitte über NX-MIX-Setup.exe (Stable) oder NX-MIX-Beta-Setup.exe (Beta) installieren."),

            UpdateStage.Checking => (
                "PRÜFE FEED",
                Theme.AccentText,
                $"GitHub-Feed für Kanal „{ReleaseChannelInfo.DisplayName(u.SelectedChannel)}“ wird abgefragt …"),

            UpdateStage.Downloading => (
                $"DOWNLOAD {u.Progress} %",
                Theme.AccentText,
                $"Paket v{offer?.Version} wird geladen und anschließend per SHA256 geprüft. Aktiver Betrieb läuft ungestört weiter."),

            UpdateStage.Available when offer != null => (
                offer.IsDowngrade ? "KANALWECHSEL" : "UPDATE BEREIT ZUM LADEN",
                offer.IsDowngrade ? Theme.Orange : Theme.AccentText,
                offer.IsDowngrade
                    ? $"Stable v{offer.Version} verfügbar. Vor dem Wechsel von Beta legt NX-MIX automatisch ein vollständiges Konfigurations-Backup an."
                    : $"Neue {ReleaseChannelInfo.DisplayName(offer.SourceChannel)}-Version v{offer.Version} verfügbar."),

            UpdateStage.Ready when offer != null => (
                u.InstallOnExitPending ? "BEIM BEENDEN" : "GEPRÜFT & BEREIT",
                Theme.Green,
                u.InstallOnExitPending
                    ? $"v{offer.Version} wird nach dem Schließen kontrolliert installiert (Backup gesichert)."
                    : _m.HostRunning
                        ? $"SHA256 geprüft. Audio läuft aktiv – kein automatischer Neustart! Empfehlung: „Beim Beenden installieren“."
                        : $"SHA256 geprüft. Klicke auf „Installieren“, um Engine & Konsole kontrolliert auf v{offer.Version} zu aktualisieren."),

            UpdateStage.UpToDate => (
                "AKTUELL",
                Theme.Green,
                $"Keine neuere Version im {ReleaseChannelInfo.DisplayName(u.SelectedChannel)}-Kanal gefunden. Einstellungen & Snapshots sind geschützt."),

            _ => (
                u.SelectedChannel == ReleaseChannel.Beta ? "BETA-KANAL AKTIV" : "STABLE-KANAL AKTIV",
                u.SelectedChannel == ReleaseChannel.Beta ? Theme.Orange : Theme.TextSecondary,
                u.SelectedChannel == ReleaseChannel.Beta
                    ? "Beta-Kanal bezieht GitHub-Prereleases (Vorabfunktionen & frühe Fixes). Keine unbeaufsichtigten Live-Neustarts."
                    : "Stable-Kanal bezieht ausschließlich offiziell freigegebene GitHub-Releases für den dauerhaften Live-Betrieb."),
        };
    }
}
