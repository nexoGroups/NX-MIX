using NxMix.Audio.Interop;
using NxMix.Engine;

namespace NxMix.Audio.Host;

/// <summary>Ein Durchlauf von Start() bis Stop(): Plan aus der Konfiguration, alle Streams/Threads, gemeinsame Zaehler.</summary>
internal sealed class HostRun
{
    private const int OpenTimeoutMs = 20000;

    private readonly List<StreamSpec> _specs = new();
    private readonly StreamSpec? _masterSpec;
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

        if (config.Bindings == null || config.Bindings.Count == 0)
            throw new ArgumentException("Keine Geraete-Anbindungen konfiguriert.");
        if (config.SampleRate < 8000 || config.SampleRate > 768000)
            throw new ArgumentException($"Ungueltige Samplerate {config.SampleRate}.");
        if (processor.SampleRate != config.SampleRate)
            throw new ArgumentException($"Die Samplerate des Prozessors ({processor.SampleRate} Hz) passt nicht zur Host-Konfiguration ({config.SampleRate} Hz).");

        EngineRate = config.SampleRate;
        Chunk = Math.Clamp((Math.Max(config.BlockFrames, 1) + 7) / 8 * 8, 32, 16384);

        var byKey = new Dictionary<string, StreamSpec>();
        foreach (DeviceBinding b in config.Bindings)
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
        _masterSpec = _specs.FirstOrDefault(s => !s.IsCapture && (s.SampleRate == null || s.SampleRate == EngineRate));
        Asios = config.Bindings.Where(b => AsioCatalog.IsAsio(b.DeviceId)).GroupBy(b => AsioCatalog.DriverName(b.DeviceId))
            .Select(g => new AsioStream(this, g.Key, g.ToArray())).ToArray();

        if (config.AppBindings is { Count: > 0 })
        {
            var taken = new HashSet<int>();
            var renderIds = new List<string?>();
            foreach (DeviceBinding b in config.Bindings)
            {
                if (b.Loopback || b.Flow == DeviceFlow.Capture)
                    for (int k = 0; k < b.Count; k++) taken.Add(b.EngineFirstChannel + k);
                else
                    renderIds.Add(b.DeviceId);
            }
            Apps = new AppLinkEngine(this, config.AppBindings, config.ParkingDeviceId, processor.InputChannels, taken,
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

        Master = _masterSpec != null ? new MasterDeviceStream(this, _masterSpec) : new MasterTimerStream(this);
        var captures = new List<CaptureStream>();
        var renders = new List<RenderStream>();
        foreach (StreamSpec spec in _specs)
        {
            if (ReferenceEquals(spec, _masterSpec)) continue;
            if (spec.IsCapture) captures.Add(new CaptureStream(this, spec));
            else renders.Add(new RenderStream(this, spec));
        }
        Captures = captures.ToArray();
        Renders = renders.ToArray();
        _all = new StreamBase[] { Master }.Concat(Captures).Concat(Renders).Concat(Asios).ToArray();
        Core = new MixCore(Processor, EngineRate, Chunk, Captures, Renders, Apps?.Slots, Asios);

        // Master zuerst: seine Periode bestimmt die Sollfuellstaende der uebrigen Geraete.
        Master.StartThread();
        WaitOpened(Master);
        foreach (StreamBase s in _all)
            if (!ReferenceEquals(s, Master)) s.StartThread();
        foreach (StreamBase s in _all)
            if (!ReferenceEquals(s, Master)) WaitOpened(s);

        Core.Prepare();
        if (_masterSpec == null && Asios.Length > 0) OutputLatencyMs = Asios.Max(a => a.OutputLatencyMs);
        Go.Set();
        Apps?.StartThread();
    }

    private static void WaitOpened(StreamBase s)
    {
        if (!s.Opened.Wait(OpenTimeoutMs))
            throw new TimeoutException($"Geraet '{s.Name}' antwortet beim Oeffnen nicht.");
        if (s.OpenError != null)
            throw new AudioException(0, $"Geraet '{s.Name}': {s.OpenError}");
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

        if (Master is MasterDeviceStream) devices.Add(Master.GetStat());
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
        foreach (var asio in Asios) { devices.Add(asio.GetStat()); underruns += asio.Underruns; overruns += asio.Overruns; }

        return new HostStats(state, lastError, Chunk, Volatile.Read(ref CpuLoad), underruns, overruns,
            Volatile.Read(ref OutputLatencyMs), devices);
    }
}
