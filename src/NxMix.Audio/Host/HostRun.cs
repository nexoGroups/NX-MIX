using NxMix.Audio.Interop;
using NxMix.Engine;

namespace NxMix.Audio.Host;

/// <summary>Ein Durchlauf von Start() bis Stop(): Plan aus der Konfiguration, alle Streams/Threads, gemeinsame Zaehler.
/// Takt-Hierarchie (ASIO-First):
///   1. Explizit gewaehltes PreferredMasterDeviceId (ASIO oder WASAPI Render)
///   2. Aktives ASIO-Ausgabe-/Duplex-Geraet mit passender Samplerate (AsioStream mit IsMaster = true, 0 Ringpuffer)
///   3. Erstes WASAPI-Ausgabegeraet mit passender Samplerate (MasterDeviceStream)
///   4. Software-Takt (MasterTimerStream, z. B. bei reinen Capture-/App-Link-Konfigurationen oder wenn Ausgabegeraete offline sind).</summary>
internal sealed class HostRun
{
    private const int OpenTimeoutMs = 20000;

    private readonly List<StreamSpec> _specs = new();
    private readonly StreamSpec? _masterWasapiSpec;
    private readonly string? _masterAsioDriver;
    private readonly List<DeviceStat> _offlineDevices = new();
    private StreamBase[] _all = Array.Empty<StreamBase>();
    private int _faulted;
    private bool _shutdown;

    public readonly AudioHost Host;
    public readonly AudioHostConfig Config;
    public readonly IBlockProcessor Processor;
    public readonly int EngineRate;
    public readonly int Chunk;

    public nint StopEvent;
    public readonly ManualResetEventSlim Go = new(false);
    public volatile bool Aborting;
    public volatile bool StopRequested;

    // vom Master-Thread / von Streams geschrieben, von GetStats() gelesen
    public int MasterPeriodFrames;
    public double CpuLoad;
    public double OutputLatencyMs;
    public long MasterUnderruns;

    public MixCore Core = null!;
    public StreamBase Master = null!;
    public CaptureStream[] Captures = Array.Empty<CaptureStream>();
    public RenderStream[] Renders = Array.Empty<RenderStream>();
    public AsioStream[] Asios = Array.Empty<AsioStream>();

    /// <summary>App-Links (Prozess-Loopback + Pro-App-Umleitung); null, wenn keine AppBindings konfiguriert sind.</summary>
    public AppLinkEngine? Apps;

    public HostRun(AudioHost host, AudioHostConfig config, IBlockProcessor processor)
    {
        Host = host;
        Config = config;
        Processor = processor;

        bool hasBindings = config.Bindings is { Count: > 0 };
        bool hasApps = config.AppBindings is { Count: > 0 };
        if (!hasBindings && !hasApps)
            throw new ArgumentException("Keine Geraete-Anbindungen konfiguriert.");
        if (config.SampleRate < 8000 || config.SampleRate > 768000)
            throw new ArgumentException($"Ungueltige Samplerate {config.SampleRate}.");
        if (processor.SampleRate != config.SampleRate)
            throw new ArgumentException($"Die Samplerate des Prozessors ({processor.SampleRate} Hz) passt nicht zur Host-Konfiguration ({config.SampleRate} Hz).");

        EngineRate = config.SampleRate;
        Chunk = Math.Clamp((Math.Max(config.BlockFrames, 1) + 7) / 8 * 8, 32, 16384);

        var bindings = config.Bindings ?? Array.Empty<DeviceBinding>();
        var byKey = new Dictionary<string, StreamSpec>();
        foreach (DeviceBinding b in bindings)
        {
            if (b.Count < 1) throw new ArgumentException($"Bindung auf '{b.DeviceId}': Count muss >= 1 sein.");
            if (b.DeviceFirstChannel < 0 || b.EngineFirstChannel < 0)
                throw new ArgumentException($"Bindung auf '{b.DeviceId}': negative Kanalnummer.");

            bool capture = b.Loopback || b.Flow == DeviceFlow.Capture;
            int engineChannels = capture ? processor.InputChannels : processor.OutputChannels;
            if (b.EngineFirstChannel + b.Count > engineChannels)
                throw new ArgumentException(
                    $"Bindung auf '{b.DeviceId}': Engine-{(capture ? "Eingang" : "Ausgang")} {b.EngineFirstChannel}..{b.EngineFirstChannel + b.Count - 1} " +
                    $"existiert nicht (Prozessor hat {engineChannels}).");

            if (AsioCatalog.IsAsio(b.DeviceId))
            {
                if (b.Loopback) throw new ArgumentException("ASIO verwendet direkte Eingänge, keinen Windows-Loopback.");
                continue;
            }
            var probe = new StreamSpec(b.DeviceId ?? "", capture, b.Loopback)
            { SampleRate = b.SampleRate, BufferFrames = b.BufferFrames, Exclusive = b.Exclusive };
            if (!byKey.TryGetValue(probe.Key, out StreamSpec? spec))
            {
                spec = probe;
                byKey[probe.Key] = spec;
                _specs.Add(spec);
            }
            if (spec.SampleRate != b.SampleRate || spec.BufferFrames != b.BufferFrames || spec.Exclusive != b.Exclusive)
                throw new ArgumentException("Alle Zuordnungen eines Geräts müssen dieselben Geräteeinstellungen verwenden.");
            spec.Add(b.DeviceFirstChannel, b.EngineFirstChannel, b.Count);
        }

        var asioGroups = bindings
            .Where(b => AsioCatalog.IsAsio(b.DeviceId))
            .GroupBy(b => AsioCatalog.DriverName(b.DeviceId))
            .Select(g => (Driver: g.Key, Bindings: g.ToArray()))
            .ToArray();

        // Master-Clock bestimmen (1. Preferred -> 2. ASIO Render -> 3. WASAPI Render -> 4. Software-Timer)
        string? pref = config.PreferredMasterDeviceId;
        if (!string.IsNullOrWhiteSpace(pref))
        {
            if (AsioCatalog.IsAsio(pref))
            {
                string prefDriver = AsioCatalog.DriverName(pref);
                var match = asioGroups.FirstOrDefault(g => string.Equals(g.Driver, prefDriver, StringComparison.OrdinalIgnoreCase));
                if (match.Bindings != null && AsioStream.CanActAsMaster(match.Bindings, EngineRate))
                    _masterAsioDriver = match.Driver;
            }
            else
            {
                _masterWasapiSpec = _specs.FirstOrDefault(s =>
                    !s.IsCapture &&
                    string.Equals(s.DeviceId, pref, StringComparison.OrdinalIgnoreCase) &&
                    (s.SampleRate == null || s.SampleRate == EngineRate));
            }
        }

        if (_masterAsioDriver == null && _masterWasapiSpec == null)
        {
            var firstAsioMaster = asioGroups.FirstOrDefault(g => AsioStream.CanActAsMaster(g.Bindings, EngineRate));
            if (firstAsioMaster.Bindings != null)
            {
                _masterAsioDriver = firstAsioMaster.Driver;
            }
            else
            {
                _masterWasapiSpec = _specs.FirstOrDefault(s => !s.IsCapture && (s.SampleRate == null || s.SampleRate == EngineRate));
            }
        }

        Asios = asioGroups
            .Select(g => new AsioStream(this, g.Driver, g.Bindings, isMaster: _masterAsioDriver != null && g.Driver == _masterAsioDriver))
            .ToArray();

        if (hasApps)
        {
            var taken = new HashSet<int>();
            var renderIds = new List<string?>();
            foreach (DeviceBinding b in bindings)
            {
                if (b.Loopback || b.Flow == DeviceFlow.Capture)
                    for (int k = 0; k < b.Count; k++) taken.Add(b.EngineFirstChannel + k);
                else
                    renderIds.Add(b.DeviceId);
            }
            Apps = new AppLinkEngine(this, config.AppBindings!, config.ParkingDeviceId, processor.InputChannels, taken,
                () => new LaneContext(EngineRate, Chunk, Math.Max(1, Volatile.Read(ref MasterPeriodFrames)), () => Aborting), renderIds);
        }
    }

    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;

    /// <summary>Meldet einen Laufzeitfehler (erster gewinnt). Darf von jedem Thread gerufen werden, nimmt keine Locks.</summary>
    public void Fault(string message)
    {
        if (Interlocked.CompareExchange(ref _faulted, 1, 0) != 0) return;
        Host.OnRunFault(this, message);
    }

    /// <summary>Erzeugt Streams und Threads, oeffnet alle Geraete und gibt dann gemeinsam frei. Wirft bei Oeffnungsfehlern.</summary>
    public void Launch()
    {
        StopEvent = Native.CreateEvent(0, 1, 0, null);
        if (StopEvent == 0) throw new AudioException(0, "Stop-Event konnte nicht erzeugt werden.");

        AsioStream? asioMaster = Asios.FirstOrDefault(a => a.IsMaster);
        Master = asioMaster != null
            ? asioMaster
            : _masterWasapiSpec != null
                ? new MasterDeviceStream(this, _masterWasapiSpec)
                : new MasterTimerStream(this);

        var captures = new List<CaptureStream>();
        var renders = new List<RenderStream>();
        foreach (StreamSpec spec in _specs)
        {
            if (ReferenceEquals(spec, _masterWasapiSpec)) continue;
            if (spec.IsCapture) captures.Add(new CaptureStream(this, spec));
            else renders.Add(new RenderStream(this, spec));
        }

        // 1. Master-Stream oeffnen (falls Offline und AllowOfflineAuxDevices aktiv: sauber auf Software-Timer zurueckfallen!)
        Master.StartThread();
        if (!TryWaitOpened(Master, out string? masterErr))
        {
            if (Config.AllowOfflineAuxDevices && Master is not MasterTimerStream)
            {
                RecordOffline(Master, masterErr!);
                if (ReferenceEquals(Master, asioMaster))
                    Asios = Asios.Where(a => !ReferenceEquals(a, asioMaster)).ToArray();
                Master = new MasterTimerStream(this);
                Master.StartThread();
                WaitOpenedStrict(Master);
            }
            else
            {
                throw new AudioException(0, $"Geraet '{Master.Name}': {masterErr}");
            }
        }

        // 2. Hilfs-Streams parallel oeffnen
        var auxCandidates = captures.Cast<StreamBase>()
            .Concat(renders)
            .Concat(Asios.Where(a => !ReferenceEquals(a, Master)))
            .ToArray();

        foreach (StreamBase s in auxCandidates) s.StartThread();

        var activeCaptures = new List<CaptureStream>(captures.Count);
        var activeRenders = new List<RenderStream>(renders.Count);
        var activeAsios = new List<AsioStream>(Asios.Length);
        if (Master is AsioStream masterAsioActive) activeAsios.Add(masterAsioActive);

        foreach (StreamBase s in auxCandidates)
        {
            if (TryWaitOpened(s, out string? err))
            {
                if (s is CaptureStream cs) activeCaptures.Add(cs);
                else if (s is RenderStream rs) activeRenders.Add(rs);
                else if (s is AsioStream asx) activeAsios.Add(asx);
            }
            else if (Config.AllowOfflineAuxDevices)
            {
                RecordOffline(s, err!);
            }
            else
            {
                throw new AudioException(0, $"Geraet '{s.Name}': {err}");
            }
        }

        Captures = activeCaptures.ToArray();
        Renders = activeRenders.ToArray();
        Asios = activeAsios.ToArray();

        _all = new StreamBase[] { Master }
            .Concat(Captures)
            .Concat(Renders)
            .Concat(Asios.Where(a => !ReferenceEquals(a, Master)))
            .ToArray();

        Core = new MixCore(Processor, EngineRate, Chunk, Captures, Renders, Apps?.Slots, Asios);
        Core.Prepare();
        if (Master is MasterTimerStream && Asios.Length > 0)
            OutputLatencyMs = Asios.Max(a => a.OutputLatencyMs);

        Go.Set();
        Apps?.StartThread();
    }

    private void RecordOffline(StreamBase s, string error)
    {
        string devId = s.Spec?.DeviceId ?? (s is AsioStream asio ? asio.PrimaryDeviceId : s.Name);
        _offlineDevices.Add(new DeviceStat(devId, s.Name, 0, 0, 0, IsMasterClock: false, IsOffline: true, OfflineReason: error));
    }

    private static bool TryWaitOpened(StreamBase s, out string? error)
    {
        if (!s.Opened.Wait(OpenTimeoutMs))
        {
            error = "antwortet beim Oeffnen nicht (Timeout).";
            return false;
        }
        error = s.OpenError;
        return error == null;
    }

    private static void WaitOpenedStrict(StreamBase s)
    {
        if (!TryWaitOpened(s, out string? err))
            throw new AudioException(0, $"Geraet '{s.Name}': {err}");
    }

    /// <summary>Beendet alle Threads (zuerst sanft: Master blendet aus und schiebt Stille nach) und gibt alles frei. Mehrfach aufrufbar.</summary>
    public void Shutdown()
    {
        if (_shutdown) return;
        _shutdown = true;

        StopRequested = true;
        Aborting = true;
        Go.Set(); // Threads, die noch auf "Go" warten, entlassen

        Thread? masterThread = Master?.Thread;
        if (masterThread is { IsAlive: true } && !masterThread.Join(2500))
            Native.SetEvent(StopEvent);

        if (StopEvent != 0) Native.SetEvent(StopEvent); // alle uebrigen hart beenden

        bool allDone = true;
        foreach (StreamBase s in _all)
        {
            Thread? t = s.Thread;
            if (t is { IsAlive: true } && !t.Join(3000)) allDone = false;
        }

        // Erst nach dem Ausblenden: Spuren schliessen und alle Umleitungen zuruecksetzen
        try { Apps?.Shutdown(); } catch { /* Aufraeumen darf nie werfen */ }

        if (allDone && StopEvent != 0)
        {
            Native.CloseHandle(StopEvent);
            StopEvent = 0;
        }
    }

    public HostStats Snapshot(HostState state, string? lastError)
    {
        var devices = new List<DeviceStat>();
        long underruns = Interlocked.Read(ref MasterUnderruns);
        long overruns = 0;

        if (Master is MasterDeviceStream)
            devices.Add(Master.GetStat() with { IsMasterClock = true });
        foreach (CaptureStream c in Captures)
        {
            devices.Add(c.GetStat());
            if (c.Reader is { } r) { underruns += r.Underruns; overruns += r.Overruns; }
        }
        foreach (RenderStream rs in Renders)
        {
            devices.Add(rs.GetStat());
            if (rs.Reader is { } r) { underruns += r.Underruns; overruns += r.Overruns; }
        }
        foreach (var asio in Asios)
        {
            devices.Add(asio.GetStat());
            underruns += asio.Underruns;
            overruns += asio.Overruns;
        }
        if (_offlineDevices.Count > 0)
            devices.AddRange(_offlineDevices);

        return new HostStats(state, lastError, Chunk, Volatile.Read(ref CpuLoad), underruns, overruns,
            Volatile.Read(ref OutputLatencyMs), devices)
        {
            MasterClockName = Master?.Name,
        };
    }
}
