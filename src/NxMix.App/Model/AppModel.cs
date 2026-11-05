using NxMix.Audio;
using NxMix.Engine;
using NxMix.App.Ui;

namespace NxMix.App.Model;

/// <summary>
/// Zustand der Anwendung: unveraenderliches MixerSettings (per `with` geaendert), Geraete-Zuordnungen,
/// Engine und Audio-Host. Alles wird nur auf dem UI-Thread angefasst. Jede Aenderung geht an
/// IMixerEngine.TrySetSettings; Aenderungen der Kanalzahl/Samplerate erzeugen eine NEUE Engine
/// (der laufende Host wird dafuer kurz gestoppt und danach wieder gestartet).
/// </summary>
internal sealed partial class AppModel
{
    public static readonly int[] SampleRates = { 44100, 48000, 88200, 96000, 176400, 192000 };
    public static readonly int[] BlockSizes = { 128, 240, 256, 480, 512, 960, 1024 };
    public const int DefaultSampleRate = 96000;
    public const int DefaultBlockFrames = 480;

    readonly Func<MixerSettings, IMixerEngine> _engineFactory;
    readonly Func<IDeviceCatalog?> _catalogFactory;
    readonly Func<IAudioHost> _hostFactory;
    readonly bool _hostAvailable;
    readonly string? _hostUnavailableReason;
    IAudioHost? _host;
    double _pollTimer;

    public MixerSettings Settings { get; private set; }
    public IReadOnlyList<DeviceBinding> Bindings { get; private set; } = Array.Empty<DeviceBinding>();
    public int BlockFrames { get; private set; } = DefaultBlockFrames;
    public bool Exclusive { get; private set; }
    public IMixerEngine Engine { get; private set; }

    public IReadOnlyList<AudioDeviceInfo> Devices { get; private set; } = Array.Empty<AudioDeviceInfo>();
    public string? DeviceError { get; private set; }
    public bool DevicesLoading { get; private set; }
    public IDeviceCatalog? Catalog { get; private set; }

    public HostStats? Stats { get; private set; }
    public string? HostError { get; private set; }
    public bool HostBusy { get; private set; }
    /// <summary>Wurde in dieser Sitzung schon "Lautstaerke und Gain-Sperren geprueft?" bestaetigt?</summary>
    public bool SessionConfirmed { get; set; }

    /// <summary>Zum Ausfuehren von Code auf dem UI-Thread (vom Fenster gesetzt).</summary>
    public Action<Action> Post { get; set; } = a => a();

    public event Action? ValuesChanged;     // Pegel/Namen/Tasten: nur Anzeige angleichen
    public event Action? StructureChanged;  // Kanalzahl, Samplerate, Projekt geladen: Seiten neu aufbauen
    public event Action? BindingsChanged;
    public event Action? HostChanged;       // Status/Statistik/Geraeteliste
    public event Action? Changed;           // irgendetwas Speicherbares hat sich geaendert

    public AppModel(MixerSettings initial, IReadOnlyList<DeviceBinding>? bindings,
        Func<MixerSettings, IMixerEngine> engineFactory, Func<IDeviceCatalog?> catalogFactory,
        Func<IAudioHost> hostFactory, bool hostAvailable, string? hostUnavailableReason)
    {
        _engineFactory = engineFactory;
        _catalogFactory = catalogFactory;
        _hostFactory = hostFactory;
        _hostAvailable = hostAvailable;
        _hostUnavailableReason = hostUnavailableReason;
        Settings = Sanitize(initial);
        Bindings = bindings ?? Array.Empty<DeviceBinding>();
        Engine = engineFactory(Settings);
    }

    /// <summary>Startwerte aus der Projektdatei (ohne Aenderungsmeldung).</summary>
    public void InitOptions(int blockFrames, bool exclusive)
    {
        BlockFrames = blockFrames > 0 ? blockFrames : DefaultBlockFrames;
        Exclusive = exclusive;
    }

    /// <summary>Nur fuer Screenshots/Selbsttest: Geraeteliste und Host direkt setzen.</summary>
    public void SetDevicesNow(IReadOnlyList<AudioDeviceInfo> devices) { Devices = devices; DevicesLoading = false; }
    public void AttachHostForShot(IAudioHost host) { _host = host; Stats = host.GetStats(); }

    public static MixerSettings EmptySettings(int sampleRate = DefaultSampleRate) => MixerSettings.Create(sampleRate, 0, 0);

    // ---- Abfragen ---------------------------------------------------------------------------
    public int InputCount => Settings.Inputs.Count;
    public int OutputCount => Settings.Outputs.Count;
    public bool EngineIsDemo => Engine is DemoEngine;
    public string EngineName => EngineFactory.Describe(Engine);
    public bool HostAvailable => _hostAvailable;
    public string? HostUnavailableReason => _hostUnavailableReason;
    /// <summary>True waehrend ein Stopp laeuft (Host-Zustand wird dann noch als laufend gemeldet).</summary>
    public bool HostStopping { get; private set; }
    public HostState HostState
    {
        get
        {
            var s = _host?.State ?? HostState.Stopped;
            return HostBusy && !HostStopping && s == HostState.Stopped ? HostState.Starting : s;
        }
    }
    public bool HostRunning => HostState is HostState.Running or HostState.Starting;

    public ChannelDsp Channel(bool input, int i) => input ? Settings.Inputs[i] : Settings.Outputs[i];
    public static bool IsProtected(ChannelDsp c) => c.MaxGainDb < Theme.ProtectBelowDb;

    /// <summary>Anzeigename: eigener Name oder "Eingang 3" / "Ausgang 5".</summary>
    public string DisplayName(bool input, int i)
    {
        var n = Channel(input, i).Name;
        return string.IsNullOrWhiteSpace(n) ? DefaultName(input, i) : n;
    }
    public static string DefaultName(bool input, int i) => (input ? "Eingang " : "Ausgang ") + (i + 1);

    // ---- Bereinigung ------------------------------------------------------------------------
    public static ChannelDsp SanitizeChannel(ChannelDsp c)
    {
        double max = double.IsNaN(c.MaxGainDb) ? Theme.FaderMaxDb : Math.Clamp(c.MaxGainDb, -60.0, Theme.FaderMaxDb);
        double gain = double.IsNaN(c.GainDb) ? 0.0 : c.GainDb;
        if (gain > max) gain = max;
        if (gain < -200 && !double.IsNegativeInfinity(gain)) gain = double.NegativeInfinity;
        double ceil = double.IsNaN(c.CeilingDbfs) ? -1.0 : Math.Clamp(c.CeilingDbfs, -60.0, 0.0);
        double delay = double.IsNaN(c.DelayMs) || double.IsInfinity(c.DelayMs) ? 0.0 : Math.Clamp(c.DelayMs, 0.0, 1000.0);
        return c with { Name = c.Name ?? "", MaxGainDb = max, GainDb = gain, CeilingDbfs = ceil, DelayMs = delay, Filters = c.Filters ?? Array.Empty<FilterSpec>() };
    }

    public static MixerSettings Sanitize(MixerSettings s)
    {
        var ins = s.Inputs.Select(SanitizeChannel).ToArray();
        var outs = s.Outputs.Select(SanitizeChannel).ToArray();
        var m = new double[ins.Length][];
        for (int i = 0; i < ins.Length; i++)
        {
            m[i] = new double[outs.Length];
            for (int j = 0; j < outs.Length; j++)
            {
                double v = i < s.MatrixDb.Length && j < s.MatrixDb[i].Length ? s.MatrixDb[i][j] : double.NegativeInfinity;
                if (double.IsNaN(v) || v <= -200) v = double.NegativeInfinity;
                else
                {
                    v = Math.Clamp(v, Theme.MatrixMinDb, Theme.MatrixMaxDb);
                    if (IsProtected(outs[j])) v = Math.Min(v, Theme.ProtectedMatrixMaxDb);
                }
                m[i][j] = v;
            }
        }
        double master = double.IsNaN(s.MasterGainDb) ? 0 : Math.Clamp(s.MasterGainDb, -120.0, Theme.MasterMaxDb);
        int sr = s.SampleRate > 0 ? s.SampleRate : DefaultSampleRate;
        return s with { SampleRate = sr, Inputs = ins, Outputs = outs, MatrixDb = m, MasterGainDb = master };
    }

    // ---- Wertaenderungen (ohne neue Engine) ------------------------------------------------
    void Commit(MixerSettings s)
    {
        Settings = Sanitize(s);
        Engine.TrySetSettings(Settings);
        ValuesChanged?.Invoke();
        Changed?.Invoke();
    }

    public void UpdateChannel(bool input, int i, Func<ChannelDsp, ChannelDsp> f)
    {
        var list = (input ? Settings.Inputs : Settings.Outputs).ToArray();
        if (i < 0 || i >= list.Length) return;
        list[i] = f(list[i]);
        Commit(input ? Settings with { Inputs = list } : Settings with { Outputs = list });
    }

    public void SetMaster(double db) => Commit(Settings with { MasterGainDb = db });

    public void SetMatrix(int i, int o, double db)
    {
        if (i < 0 || i >= InputCount || o < 0 || o >= OutputCount) return;
        var m = (double[][])Settings.MatrixDb.Clone();
        m[i] = (double[])m[i].Clone();
        m[i][o] = db;
        Commit(Settings with { MatrixDb = m });
    }

    // ---- Kanaele: anlegen / loeschen / verschieben (Kanalzahl aendert sich -> neue Engine) -----
    public void AddChannels(bool input, int count, string[]? names = null)
    {
        if (count <= 0) return;
        var ins = Settings.Inputs.ToList();
        var outs = Settings.Outputs.ToList();
        var target = input ? ins : outs;
        for (int k = 0; k < count; k++) target.Add(new ChannelDsp { Name = names != null && k < names.Length ? names[k] : "" });
        ApplyStructural(Settings with { Inputs = ins, Outputs = outs, MatrixDb = ResizeMatrix(Settings.MatrixDb, ins.Count, outs.Count) }, Bindings);
    }

    /// <summary>Name fuer ein neues Stereo-Paar ("Stereo 1 L"/"Stereo 1 R" usw.).</summary>
    public string[] NextPairNames(bool input)
    {
        var list = input ? Settings.Inputs : Settings.Outputs;
        int n = 1;
        while (list.Any(c => c.Name == $"Stereo {n} L" || c.Name == $"Stereo {n} R")) n++;
        return new[] { $"Stereo {n} L", $"Stereo {n} R" };
    }

    public void RemoveChannel(bool input, int idx)
    {
        var ins = Settings.Inputs.ToList();
        var outs = Settings.Outputs.ToList();
        var target = input ? ins : outs;
        if (idx < 0 || idx >= target.Count) return;
        target.RemoveAt(idx);
        var m = Settings.MatrixDb.Select(r => (double[])r.Clone()).ToList();
        if (input) m.RemoveAt(idx);
        else for (int i = 0; i < m.Count; i++) m[i] = m[i].Where((_, j) => j != idx).ToArray();
        if (input) _links = LinksAfterRemove(idx);
        var flow = input ? DeviceFlow.Capture : DeviceFlow.Render;
        var nb = new List<DeviceBinding>();
        foreach (var b in Bindings)
        {
            if (b.Flow != flow) { nb.Add(b); continue; }
            int first = b.EngineFirstChannel, end = first + b.Count; // [first, end)
            if (idx < first) { first--; end--; }
            else if (idx < end) end--;
            if (end - first <= 0) continue;
            nb.Add(b with { EngineFirstChannel = first, Count = end - first });
        }
        ApplyStructural(Settings with { Inputs = ins, Outputs = outs, MatrixDb = m.ToArray() }, nb);
    }

    public void MoveChannel(bool input, int idx, int dir)
    {
        int to = idx + dir;
        var list = (input ? Settings.Inputs : Settings.Outputs).ToArray();
        if (idx < 0 || idx >= list.Length || to < 0 || to >= list.Length) return;
        if (input && !CanMoveChannel(true, idx, dir)) return;
        if (input) SwapLinks(idx, to);
        (list[idx], list[to]) = (list[to], list[idx]);
        var m = Settings.MatrixDb.Select(r => (double[])r.Clone()).ToArray();
        if (input) (m[idx], m[to]) = (m[to], m[idx]);
        else foreach (var r in m) (r[idx], r[to]) = (r[to], r[idx]);
        Commit(input ? Settings with { Inputs = list, MatrixDb = m } : Settings with { Outputs = list, MatrixDb = m });
        StructureChanged?.Invoke(); // Streifen-Reihenfolge neu zeichnen
    }

    static double[][] ResizeMatrix(double[][] old, int ni, int no)
    {
        var m = new double[ni][];
        for (int i = 0; i < ni; i++)
        {
            m[i] = new double[no];
            for (int j = 0; j < no; j++) m[i][j] = i < old.Length && j < old[i].Length ? old[i][j] : double.NegativeInfinity;
        }
        return m;
    }

    public void SetSampleRate(int sr)
    {
        if (sr == Settings.SampleRate || HostRunning) return;
        ApplyStructural(Settings with { SampleRate = sr }, Bindings);
    }

    /// <summary>Neue Engine fuer neue Kanalzahl/Samplerate. Ein laufender Host wird kurz gestoppt und neu gestartet.</summary>
    void ApplyStructural(MixerSettings s, IReadOnlyList<DeviceBinding> bindings)
    {
        bool wasRunning = HostRunning;
        if (wasRunning) StopHostNow();
        Settings = Sanitize(s);
        Bindings = bindings;
        var old = Engine;
        Engine = _engineFactory(Settings);
        (old as IDisposable)?.Dispose();
        StructureChanged?.Invoke();
        BindingsChanged?.Invoke();
        Changed?.Invoke();
        if (wasRunning && CanStart(out _)) StartHost(true);
    }

    // ---- Projekt -----------------------------------------------------------------------------
    public void NewProject()
    {
        if (HostRunning) StopHostNow();
        BlockFrames = DefaultBlockFrames;
        Exclusive = false;
        _links = new List<AppLink>();
        AppLinksEnabled = true;
        ApplyStructural(EmptySettings(), Array.Empty<DeviceBinding>());
        HostError = null;
    }

    public void LoadProject(MixerSettings settings, IReadOnlyList<DeviceBinding> bindings, int blockFrames, bool exclusive, AppLinkSetup? appLinks = null)
    {
        if (HostRunning) StopHostNow();
        BlockFrames = blockFrames > 0 ? blockFrames : DefaultBlockFrames;
        Exclusive = exclusive;
        _links = AppLinkRules.Sanitize(appLinks?.Links, settings.Inputs.Count);
        AppLinksEnabled = appLinks?.Enabled ?? true;
        if (appLinks?.ParkingDeviceId != null) { ParkingDeviceId = appLinks.ParkingDeviceId; ParkingDeviceName = appLinks.ParkingDeviceName; }
        ApplyStructural(settings, bindings);
        HostError = null;
    }

    // ---- Zuordnungen -------------------------------------------------------------------------
    public void SetBlockFrames(int frames)
    {
        if (HostRunning || frames <= 0) return;
        BlockFrames = frames;
        BindingsChanged?.Invoke();
        Changed?.Invoke();
    }

    public void SetExclusive(bool on)
    {
        if (HostRunning) return;
        Exclusive = on;
        BindingsChanged?.Invoke();
        Changed?.Invoke();
    }

    void SetBindings(IReadOnlyList<DeviceBinding> b)
    {
        Bindings = b;
        BindingsChanged?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Legt eine Zuordnung an. Fehlende Engine-Kanaele werden automatisch (leer benannt) angelegt.</summary>
    public string? AddBinding(DeviceBinding b)
    {
        if (HostRunning) return "Zum Ändern zuerst Audio stoppen.";
        var refusal = BindingLinkConflict(b);
        if (refusal != null) return refusal;
        bool input = b.Flow == DeviceFlow.Capture;
        int have = input ? InputCount : OutputCount;
        int need = b.EngineFirstChannel + b.Count;
        var list = Bindings.Append(b).ToList();
        if (need > have)
        {
            var ins = Settings.Inputs.ToList();
            var outs = Settings.Outputs.ToList();
            var target = input ? ins : outs;
            while (target.Count < need) target.Add(new ChannelDsp());
            ApplyStructural(Settings with { Inputs = ins, Outputs = outs, MatrixDb = ResizeMatrix(Settings.MatrixDb, ins.Count, outs.Count) }, list);
        }
        else SetBindings(list);
        return null;
    }

    /// <summary>Aendert eine Zuordnung. Gibt einen Hinweis zurueck, wenn die Aenderung abgelehnt wurde (z. B. Konflikt mit einem App-Link).</summary>
    public string? UpdateBinding(int i, Func<DeviceBinding, DeviceBinding> f)
    {
        if (HostRunning || i < 0 || i >= Bindings.Count) return null;
        var list = Bindings.ToList();
        var nb = f(list[i]);
        // nur NEUE Konflikte ablehnen (ein schon bestehender darf behoben werden)
        var refusal = BindingLinkConflict(nb);
        if (refusal != null && BindingLinkConflict(list[i]) == null) return refusal;
        list[i] = nb;
        SetBindings(list);
        return null;
    }

    public void RemoveBinding(int i)
    {
        if (HostRunning || i < 0 || i >= Bindings.Count) return;
        var list = Bindings.ToList();
        list.RemoveAt(i);
        SetBindings(list);
    }

    /// <summary>Erste freie Engine-Kanalnummer fuer eine neue Zuordnung der Richtung.</summary>
    public int NextFreeEngineChannel(DeviceFlow flow)
    {
        int n = Bindings.Where(b => b.Flow == flow).Select(b => b.EngineFirstChannel + b.Count).DefaultIfEmpty(0).Max();
        if (flow == DeviceFlow.Capture) n = Math.Max(n, _links.Select(l => l.End).DefaultIfEmpty(0).Max()); // App-Links belegen Eingaenge
        return n;
    }

    public AudioDeviceInfo? FindDevice(string id) => Devices.FirstOrDefault(d => d.Id == id);

    /// <summary>Prueft eine Zuordnung. null = in Ordnung, sonst kurzer Hinweis.</summary>
    public string? ValidateBinding(DeviceBinding b) => ValidateBinding(b, HasActiveLinks);

    string? ValidateBinding(DeviceBinding b, bool linksActive)
    {
        int engineCount = b.Flow == DeviceFlow.Capture ? InputCount : OutputCount;
        if (b.Count < 1) return "Anzahl muss mindestens 1 sein";
        if (b.EngineFirstChannel < 0 || b.EngineFirstChannel + b.Count > engineCount)
            return b.Flow == DeviceFlow.Capture ? "Engine-Eingänge reichen nicht" : "Engine-Ausgänge reichen nicht";
        var d = FindDevice(b.DeviceId);
        if (d == null) return Devices.Count == 0 ? "Geräteliste leer" : "Gerät nicht gefunden";
        if (d.Flow != (b.Loopback ? DeviceFlow.Render : b.Flow)) return "Geräterichtung passt nicht";
        if (b.DeviceFirstChannel < 0 || b.DeviceFirstChannel + b.Count > Math.Max(d.Channels, 1))
            return $"Gerät hat nur {d.Channels} Kanäle";
        if (linksActive && BindingLinkConflict(b) is { } lc) return lc;
        return null;
    }

    // ---- Geraete -----------------------------------------------------------------------------
    public void RefreshDevices()
    {
        if (DevicesLoading) return;
        DevicesLoading = true;
        DeviceError = null;
        HostChanged?.Invoke();
        Task.Run(() =>
        {
            IReadOnlyList<AudioDeviceInfo> list = Array.Empty<AudioDeviceInfo>();
            string? err = null;
            IDeviceCatalog? cat = null;
            try
            {
                cat = Catalog ?? _catalogFactory();
                if (cat == null) err = "Geräte-Katalog nicht verfügbar (NxMix.Audio.DeviceCatalog fehlt).";
                else list = cat.List();
            }
            catch (Exception ex) { err = "Geräteliste: " + ex.Message; }
            Post(() =>
            {
                Catalog = cat;
                Devices = list;
                DeviceError = err;
                DevicesLoading = false;
                HostChanged?.Invoke();
                BindingsChanged?.Invoke();
            });
        });
    }

    // ---- Audio-Host (startet NIE von selbst) ------------------------------------------------
    public bool CanStart(out string reason) => CanStartCore(true, out reason);

    /// <summary>Wie CanStart; mit withLinks=false wird geprueft, ob der Start OHNE App-Links moeglich waere.</summary>
    public bool CanStartCore(bool withLinks, out string reason)
    {
        bool linksActive = withLinks && HasActiveLinks;
        reason = "";
        if (!_hostAvailable) { reason = _hostUnavailableReason ?? "Audio-Host nicht verfügbar."; return false; }
        if (EngineIsDemo) { reason = "Echte Engine (NxMix.Engine.MixerEngine) nicht verfügbar."; return false; }
        if (HostBusy) { reason = "Bitte warten …"; return false; }
        if (Bindings.Count == 0) { reason = "Noch keine Geräte-Zuordnung."; return false; }
        if (!Bindings.Any(b => b.Flow == DeviceFlow.Render)) { reason = "Mindestens ein Ausgabegerät zuordnen."; return false; }
        foreach (var b in Bindings)
        {
            var v = ValidateBinding(b, linksActive);
            if (v != null) { reason = "Zuordnung ungültig: " + v; return false; }
        }
        if (linksActive)
        {
            var p = AppLinkProblem();
            if (p != null) { reason = p; return false; }
        }
        return true;
    }

    public void StartHost(bool silentRestart = false)
    {
        string why = "";
        if (HostBusy || !CanStart(out why)) { if (!HostBusy) { HostError = why; HostChanged?.Invoke(); } return; }
        HostError = null;
        HostBusy = true;
        var cfg = BuildHostConfig();
        var engine = Engine;
        HostChanged?.Invoke();
        Task.Run(() =>
        {
            string? err = null;
            try
            {
                if (_host == null)
                {
                    _host = _hostFactory();
                    _host.Faulted += msg => Post(() => { HostError = msg; HostChanged?.Invoke(); });
                }
                _host.Start(cfg, engine);
            }
            catch (Exception ex) { err = ex.Message; }
            Post(() =>
            {
                HostBusy = false;
                if (err != null) HostError = err;
                Stats = _host?.GetStats();
                HostChanged?.Invoke();
            });
        });
    }

    public void StopHost()
    {
        if (_host == null || HostBusy) return;
        HostBusy = true;
        HostStopping = true;
        HostChanged?.Invoke();
        var host = _host;
        Task.Run(() =>
        {
            string? err = null;
            try { host.Stop(); } catch (Exception ex) { err = ex.Message; }
            Post(() =>
            {
                HostBusy = false;
                HostStopping = false;
                if (err != null) HostError = err;
                Stats = host.GetStats();
                HostChanged?.Invoke();
            });
        });
    }

    /// <summary>Synchrones Stoppen (vor Neuaufbau der Engine, beim Beenden).</summary>
    public void StopHostNow()
    {
        try { _host?.Stop(); } catch (Exception ex) { HostError = ex.Message; }
        HostBusy = false;
        Stats = null;
        HostChanged?.Invoke();
    }

    public void Shutdown()
    {
        try { _host?.Stop(); } catch { }
        try { _host?.Dispose(); } catch { }
        _host = null;
    }

    /// <summary>Wird vom UI-Takt gerufen: Statistik des Hosts etwa 4x pro Sekunde auffrischen.</summary>
    public bool Tick(float dt)
    {
        bool linkChanged = false;
        _linkPoll += dt;
        if (_linkPoll >= 0.25) { _linkPoll = 0; linkChanged = PollLinkStatus(); }
        if (_host == null) return linkChanged;
        _pollTimer += dt;
        if (_pollTimer < 0.25) return linkChanged;
        _pollTimer = 0;
        try
        {
            var st = _host.GetStats();
            bool changed = Stats == null || st.State != Stats.State || st.Underruns != Stats.Underruns || st.Overruns != Stats.Overruns
                || Math.Abs(st.CpuLoad - Stats.CpuLoad) > 0.004 || Math.Abs(st.OutputLatencyMs - Stats.OutputLatencyMs) > 0.05
                || st.LastError != Stats.LastError;
            Stats = st;
            return changed || linkChanged;
        }
        catch { return linkChanged; }
    }
}
