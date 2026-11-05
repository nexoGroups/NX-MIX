using NxMix.Audio;

namespace NxMix.App.Model;

/// <summary>
/// App-Links im Modell: Programm (Exe-Name) -> Eingangskanal(e), Parkplatz-Ausgang, Status aus dem Audio-Host.
/// Ein Eingang darf nie gleichzeitig an einer Geraete-Eingangs-Zuordnung (Capture/Loopback) und an einem App-Link haengen.
/// </summary>
internal sealed partial class AppModel
{
    List<AppLink> _links = new();
    IAppCatalog? _appCatalog;
    IAppRouteRestorer? _restorer;
    bool _appCatalogTried, _restorerTried;
    IReadOnlyList<AppLinkStatus>? _linkStatus;
    double _linkPoll;
    string _confirmedLinkSig = "";

    /// <summary>Fabriken (Reflection ueber NxMix.Audio); Screenshots/Selbsttest setzen Attrappen.</summary>
    public Func<IAppCatalog?> AppCatalogFactory { get; set; } = AudioFactory.CreateAppCatalog;
    public Func<IAppRouteRestorer?> RestorerFactory { get; set; } = AudioFactory.CreateRestorer;
    /// <summary>Nur fuer Screenshots/Selbsttest: liefert feste Link-Stati, auch wenn kein Host laeuft.</summary>
    public Func<IReadOnlyList<AppLinkStatus>?>? LinkStatusSource { get; set; }

    public IReadOnlyList<AppLink> AppLinks => _links;
    /// <summary>Hauptschalter: aus = die Links bleiben im Projekt, werden aber beim Start nicht an den Host gegeben.</summary>
    public bool AppLinksEnabled { get; private set; } = true;
    public string? ParkingDeviceId { get; private set; }
    public string? ParkingDeviceName { get; private set; }
    public bool HasActiveLinks => AppLinksEnabled && _links.Count > 0;

    public AppLinkSetup AppLinkSetup => new(_links.ToArray(), AppLinksEnabled, ParkingDeviceId, ParkingDeviceName);

    /// <summary>Startwerte aus der Projektdatei (ohne Aenderungsmeldung); die Eingaenge muessen schon feststehen.</summary>
    public void InitAppLinks(AppLinkSetup? setup)
    {
        _links = AppLinkRules.Sanitize(setup?.Links, InputCount);
        AppLinksEnabled = setup?.Enabled ?? true;
        ParkingDeviceId = string.IsNullOrWhiteSpace(setup?.ParkingDeviceId) ? null : setup!.ParkingDeviceId;
        ParkingDeviceName = ParkingDeviceId == null ? null : setup!.ParkingDeviceName;
    }

    void NotifyLinks()
    {
        ValuesChanged?.Invoke();
        BindingsChanged?.Invoke();
        HostChanged?.Invoke();
        Changed?.Invoke();
    }

    // ---- Abfragen ---------------------------------------------------------------------------
    public AppLink? LinkAt(int input) => _links.FirstOrDefault(l => l.Covers(input));
    public AppLink? LinkStartingAt(int input) => _links.FirstOrDefault(l => l.FirstChannel == input);

    bool NameEnds(int i, string suffix)
    {
        if (i < 0 || i >= InputCount) return false;
        var n = Settings.Inputs[i].Name;
        return n != null && n.Length > suffix.Length && n.EndsWith(suffix, StringComparison.Ordinal);
    }

    /// <summary>Gehoert der Eingang zu einem benannten Stereo-Paar ("... L" und direkt danach "... R")?</summary>
    public bool IsNamedPair(int input) => NamedPairStart(input) >= 0;

    /// <summary>Index des linken Kanals des benannten Paares, zu dem der Eingang gehoert; sonst -1.</summary>
    public int NamedPairStart(int input)
    {
        if (NameEnds(input, " L") && input + 1 < InputCount && Settings.Inputs[input + 1].Name == Settings.Inputs[input].Name[..^1] + "R") return input;
        if (NameEnds(input, " R") && input > 0 && Settings.Inputs[input - 1].Name == Settings.Inputs[input].Name[..^1] + "L") return input - 1;
        return -1;
    }

    /// <summary>Vorschlag fuer einen Link, der ueber diesen Eingangsstreifen angelegt wird: bestehender Link, benanntes Paar (2) oder mono (1).</summary>
    public (int First, int Count) SuggestSpan(int input)
    {
        var l = LinkAt(input);
        if (l != null) return (l.FirstChannel, l.Count);
        int p = NamedPairStart(input);
        return p >= 0 ? (p, 2) : (input, 1);
    }

    /// <summary>Text, wenn die Eingaenge [first, first+count) an einer Geraete-Eingangs-Zuordnung haengen; sonst null.</summary>
    public string? DeviceConflict(int first, int count)
    {
        foreach (var b in Bindings)
        {
            if (b.Flow != DeviceFlow.Capture) continue;
            if (b.EngineFirstChannel < first + count && first < b.EngineFirstChannel + b.Count)
            {
                int n = Math.Max(first, b.EngineFirstChannel) + 1;
                string dn = FindDevice(b.DeviceId)?.Name ?? (b.Loopback ? "Loopback" : "Aufnahmegerät");
                return $"Eingang {n:00} hängt schon an der Geräte-Zuordnung „{dn}“ (Seite Geräte). Ein Eingang kann nicht gleichzeitig Gerät und Programm sein – entferne zuerst die Zuordnung.";
            }
        }
        return null;
    }

    /// <summary>Ein neuer/geaenderter Geraete-Eingang darf keinen App-Link-Eingang belegen. null = in Ordnung.</summary>
    public string? BindingLinkConflict(DeviceBinding b)
    {
        if (b.Flow != DeviceFlow.Capture) return null;
        foreach (var l in _links)
        {
            if (b.EngineFirstChannel < l.End && l.FirstChannel < b.EngineFirstChannel + b.Count)
            {
                int n = Math.Max(b.EngineFirstChannel, l.FirstChannel) + 1;
                return $"Eingang {n:00} ist an das Programm {l.Title} ({l.ExeName}) gebunden (App-Link). Löse zuerst die Verknüpfung am Eingangsstreifen.";
            }
        }
        return null;
    }

    /// <summary>Pruefung fuer einen Link auf [first, first+count). ignoreFirst = ein Link, der ersetzt wird. null = in Ordnung.</summary>
    public string? LinkConflict(int first, int count, string? exe, int ignoreFirst = -1)
    {
        if (first < 0 || count < 1 || count > 2) return "Ungültiger Kanalbereich.";
        var dc = DeviceConflict(first, count);
        if (dc != null) return dc;
        foreach (var l in _links)
        {
            if (l.FirstChannel == ignoreFirst) continue;
            if (l.FirstChannel < first + count && first < l.End)
                return $"Eingang {Math.Max(first, l.FirstChannel) + 1:00} ist schon an {l.Title} ({l.ExeName}) gebunden.";
            if (exe != null && string.Equals(l.ExeName, exe, StringComparison.OrdinalIgnoreCase))
                return $"{l.ExeName} ist schon an Eingang {l.FirstChannel + 1:00} gebunden – ein Programm kann nur an einen Fader.";
        }
        return null;
    }

    // ---- Aendern ----------------------------------------------------------------------------
    /// <summary>Bindet ein Programm an [first, first+count). Fehlende Eingaenge werden angelegt. Gibt einen Hinweis zurueck, wenn es abgelehnt wurde.</summary>
    public string? SetLink(int first, int count, string exe, string? displayName)
    {
        if (HostRunning) return "Zum Ändern von App-Links zuerst Audio stoppen.";
        var norm = AppLinkRules.NormalizeExe(exe, out var err);
        if (norm == null) return err;
        count = Math.Clamp(count, 1, 2);
        var why = LinkConflict(first, count, norm, first);
        if (why != null) return why;
        var list = _links.Where(l => l.FirstChannel != first).ToList();
        list.Add(new AppLink(norm, first, count, string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim()));
        list.Sort((a, b) => a.FirstChannel.CompareTo(b.FirstChannel));
        _links = list;
        if (first + count > InputCount) AddChannels(true, first + count - InputCount);
        NotifyLinks();
        return null;
    }

    public bool RemoveLink(int first)
    {
        if (HostRunning) return false;
        int n = _links.RemoveAll(l => l.FirstChannel == first);
        if (n == 0) return false;
        NotifyLinks();
        return true;
    }

    public void SetAppLinksEnabled(bool on)
    {
        if (HostRunning || on == AppLinksEnabled) return;
        AppLinksEnabled = on;
        NotifyLinks();
    }

    List<AppLink> LinksAfterRemove(int idx)
    {
        var r = new List<AppLink>();
        foreach (var l in _links)
        {
            int first = l.FirstChannel, end = l.End; // [first, end)
            if (idx < first) { first--; end--; }
            else if (idx < end) end--;
            if (end - first <= 0) continue;
            r.Add(l with { FirstChannel = first, Count = end - first });
        }
        return r;
    }

    /// <summary>Verschieben ist bei Eingaengen, die zu einem Stereo-App-Link gehoeren, nicht moeglich (das Paar wuerde zerrissen).</summary>
    public bool CanMoveChannel(bool input, int idx, int dir)
    {
        if (!input) return true;
        int to = idx + dir;
        if (to < 0 || to >= InputCount) return false;
        return (LinkAt(idx)?.Count ?? 1) < 2 && (LinkAt(to)?.Count ?? 1) < 2;
    }

    /// <summary>Beim Verschieben eines Eingangsstreifens wandert ein Mono-App-Link mit.</summary>
    void SwapLinks(int a, int b)
    {
        _links = _links.Select(l => l.Count == 1 && l.FirstChannel == a ? l with { FirstChannel = b }
            : l.Count == 1 && l.FirstChannel == b ? l with { FirstChannel = a } : l)
            .OrderBy(l => l.FirstChannel).ToList();
    }

    // ---- Parkplatz --------------------------------------------------------------------------
    /// <summary>Als Parkplatz taugt nur ein Render-Geraet, das NICHT das Standardgeraet ist.</summary>
    public static bool IsValidParking(AudioDeviceInfo d) => d.Flow == DeviceFlow.Render && !d.IsDefault;
    public IEnumerable<AudioDeviceInfo> ParkingCandidates => Devices.Where(IsValidParking);
    public AudioDeviceInfo? ParkingDevice => string.IsNullOrEmpty(ParkingDeviceId) ? null : FindDevice(ParkingDeviceId);

    public void SetParking(string? id)
    {
        if (HostRunning) return;
        if (string.IsNullOrEmpty(id)) { ParkingDeviceId = null; ParkingDeviceName = null; NotifyLinks(); return; }
        var d = FindDevice(id);
        if (d == null || !IsValidParking(d)) return;
        ParkingDeviceId = d.Id;
        ParkingDeviceName = d.Name;
        NotifyLinks();
    }

    // ---- Katalog und Umleitungs-Wiederherstellung -------------------------------------------
    public IAppCatalog? AppCatalog
    {
        get
        {
            if (!_appCatalogTried) { _appCatalogTried = true; try { _appCatalog = AppCatalogFactory(); } catch { _appCatalog = null; } }
            return _appCatalog;
        }
    }

    public IAppRouteRestorer? Restorer
    {
        get
        {
            if (!_restorerTried) { _restorerTried = true; try { _restorer = RestorerFactory(); } catch { _restorer = null; } }
            return _restorer;
        }
    }

    /// <summary>Null = App-Links gehen in dieser Windows-/Programmversion; sonst der Grund.</summary>
    public string? AppLinksUnsupportedReason()
    {
        var r = Restorer;
        if (r == null) return "App-Links sind in dieser Programmversion nicht verfügbar (die Audio-Schicht hat keine Umleitung).";
        try { return r.IsSupported ? null : "Diese Windows-Version unterstützt App-Links nicht: " + (r.UnsupportedReason ?? "unbekannter Grund"); }
        catch (Exception ex) { return "Umleitungs-Unterstützung nicht prüfbar: " + ex.Message; }
    }

    /// <summary>Liest die Programmliste im Hintergrund. done laeuft auf dem UI-Thread: (Liste, Fehlertext).</summary>
    public void LoadAppPrograms(Action<List<AppProgram>, string?> done)
    {
        var cat = AppCatalog;
        if (cat == null) { done(new List<AppProgram>(), "Programmliste nicht verfügbar (NxMix.Audio.AppCatalog fehlt)."); return; }
        (List<AppProgram>, string?) Read()
        {
            try { return (AppLinkRules.Group(cat.List()), null); }
            catch (Exception ex) { return (new List<AppProgram>(), "Programmliste: " + ex.Message); }
        }
        if (RunInline) { var (l, e) = Read(); done(l, e); return; }
        Task.Run(() => { var (l, e) = Read(); Post(() => done(l, e)); });
    }

    /// <summary>Nur Screenshots/Selbsttest: Katalog-Abfragen laufen sofort statt im Hintergrund (reproduzierbar).</summary>
    public bool RunInline { get; set; }

    /// <summary>"Alle Umleitungen zuruecksetzen": ruft RestoreAllKnown im Hintergrund. done(Fehlertext oder null) laeuft auf dem UI-Thread.</summary>
    public void ResetAllRoutes(Action<string?> done)
    {
        var r = Restorer;
        if (r == null) { done("Nicht verfügbar (NxMix.Audio.AppRouteRestorer fehlt)."); return; }
        string? Run() { try { r.RestoreAllKnown(); return null; } catch (Exception ex) { return ex.Message; } }
        if (RunInline) { done(Run()); return; }
        Task.Run(() => { var err = Run(); Post(() => done(err)); });
    }

    // ---- Problem / Start --------------------------------------------------------------------
    /// <summary>Was verhindert den Start MIT App-Links? null = nichts. App-Links werden nie still ignoriert.</summary>
    public string? AppLinkProblem()
    {
        if (!HasActiveLinks) return null;
        var unsupported = AppLinksUnsupportedReason();
        if (unsupported != null) return unsupported;
        foreach (var l in _links)
        {
            var dc = DeviceConflict(l.FirstChannel, l.Count);
            if (dc != null) return dc;
        }
        if (string.IsNullOrEmpty(ParkingDeviceId))
            return "App-Links bleiben aus, bis ein Parkplatz-Ausgang gewählt ist (Einstellungen › App-Links).";
        var d = FindDevice(ParkingDeviceId);
        if (d == null)
            return Devices.Count == 0 ? "Der Parkplatz-Ausgang lässt sich noch nicht prüfen (Geräteliste leer)."
                : $"Der Parkplatz-Ausgang „{ParkingDeviceName ?? ParkingDeviceId}“ ist nicht verfügbar (Gerät nicht gefunden).";
        if (!IsValidParking(d)) return "Der Parkplatz darf nicht das Standardgerät sein.";
        if (Bindings.Any(b => b.Flow == DeviceFlow.Render && b.DeviceId == ParkingDeviceId))
            return $"Der Parkplatz „{d.Name}“ ist zugleich ein Ausgang deiner Geräte-Zuordnung – dort würde der Ton hörbar. Wähle ein anderes Gerät.";
        return null;
    }

    /// <summary>Die Konfiguration fuer den Audio-Host inkl. App-Bindings (EngineFirstChannel = Index des Eingangs) und Parkplatz.</summary>
    public AudioHostConfig BuildHostConfig()
    {
        IReadOnlyList<AppBinding>? ab = null;
        string? park = null;
        if (HasActiveLinks)
        {
            ab = _links.Select(l => new AppBinding(l.ExeName, l.FirstChannel, l.Count)).ToArray();
            park = ParkingDeviceId;
        }
        return new AudioHostConfig(Settings.SampleRate, Bindings.ToArray(), BlockFrames, Exclusive, ab, park);
    }

    /// <summary>Kennung der Links + Parkplatz; wenn sie sich seit der letzten Bestaetigung aendert, fragt der Startdialog erneut.</summary>
    public string LinkSignature => HasActiveLinks
        ? ParkingDeviceId + "|" + string.Join(";", _links.Select(l => $"{l.ExeName.ToLowerInvariant()}@{l.FirstChannel}x{l.Count}"))
        : "";

    public bool LinksConfirmed => LinkSignature == "" || LinkSignature == _confirmedLinkSig;
    public void ConfirmLinks() => _confirmedLinkSig = LinkSignature;

    // ---- Status aus dem Host ----------------------------------------------------------------
    static bool SameStatus(IReadOnlyList<AppLinkStatus>? a, IReadOnlyList<AppLinkStatus>? b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].ExeName != b[i].ExeName || a[i].State != b[i].State || a[i].ProcessCount != b[i].ProcessCount || a[i].Message != b[i].Message) return false;
        return true;
    }

    bool PollLinkStatus()
    {
        IReadOnlyList<AppLinkStatus>? now = null;
        try
        {
            if (LinkStatusSource != null) now = LinkStatusSource();
            else if (_host != null && HostRunning && _host is IAppLinkMonitor mon) now = mon.GetAppLinks();
        }
        catch { now = null; }
        if (SameStatus(_linkStatus, now)) return false;
        _linkStatus = now;
        return true;
    }

    /// <summary>Zustand eines Links fuer die Anzeige (Streifen, Tooltip).</summary>
    public LinkView ViewOf(AppLink l)
    {
        if (!AppLinksEnabled) return new(LinkUi.Off, "App-Links sind in den Einstellungen ausgeschaltet – das Programm läuft normal.", 0);
        var dc = DeviceConflict(l.FirstChannel, l.Count);
        if (dc != null) return new(LinkUi.Conflict, dc, 0);
        var st = _linkStatus?.FirstOrDefault(s => string.Equals(s.ExeName, l.ExeName, StringComparison.OrdinalIgnoreCase));
        if (st != null)
        {
            return st.State switch
            {
                AppLinkState.Linked => new(LinkUi.Linked, st.Message, st.ProcessCount),
                AppLinkState.Failed => new(LinkUi.Failed, string.IsNullOrWhiteSpace(st.Message) ? "Unbekannter Fehler" : st.Message, st.ProcessCount),
                _ => new(LinkUi.Waiting, st.Message, st.ProcessCount),
            };
        }
        if (HostRunning) return new(LinkUi.Idle, "Der Audio-Host meldet noch keinen Status.", 0);
        var problem = AppLinkProblem();
        if (problem != null) return new(LinkUi.NeedsSetup, problem, 0);
        return new(LinkUi.Idle, null, 0);
    }
}
