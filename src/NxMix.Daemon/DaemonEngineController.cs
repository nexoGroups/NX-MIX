using NxMix.Audio;
using NxMix.Daemon.Persistence;
using NxMix.Daemon.Vaio;
using NxMix.Engine;
using NxMix.Ipc;

namespace NxMix.Daemon;

public sealed class DaemonEngineOptions
{
    public string? CustomDataDir { get; init; }
    public string? CustomIpcKey { get; init; }
    public IDeviceCatalog? DeviceCatalog { get; init; }
    public IAppCatalog? AppCatalog { get; init; }
    public IAppRouteRestorer? AppRouteRestorer { get; init; }
    public Func<IAudioHost>? HostFactory { get; init; }
    public Func<MixerSettings, IMixerEngine>? EngineFactory { get; init; }
    public Action? ShutdownRequestedCallback { get; init; }
    public bool SkipInitialDeviceScan { get; init; }
    public SystemConfig? InitialSystemConfigOverride { get; init; }
    public MixerSettings? InitialMixerOverride { get; init; }
}

/// <summary>Herzstück des NX-MIX Hintergrunddienstes: besitzt die vorallokierte 128×64×128 MixerEngine, AudioHost, Persistenz, Snapshots und 60-Hz-Telemetrie.</summary>
public sealed class DaemonEngineController : INxMixBackend
{
    public const int PoolMaxInputs = 128;
    public const int PoolMaxBuses = 64;
    public const int PoolMaxOutputs = 128;

    private readonly object _gate = new();
    private readonly DaemonPaths _paths;
    private readonly SystemConfigStore _configStore;
    private readonly LiveStateStore _liveStore;
    private readonly SnapshotManager _snapshots;
    private readonly VaioManager _vaioManager = new();
    private readonly IDeviceCatalog _deviceCatalog;
    private readonly IAppCatalog _appCatalog;
    private readonly IAppRouteRestorer _routeRestorer;
    private readonly Func<IAudioHost> _hostFactory;
    private readonly Func<MixerSettings, IMixerEngine> _engineFactory;
    private readonly Action? _shutdownCallback;
    private readonly TelemetrySharedMemoryWriter? _shmWriter;

    private readonly List<EngineLogEntry> _logs = new();
    private long _logSeq;
    private long _stateRev;

    private IMixerEngine _engine;
    private IAudioHost _host;
    private SystemConfig _config;
    private MixerSettings _mixer;
    private Guid? _activeSnapshotId;
    private bool _panicMuted;
    private string? _lastError;

    private IReadOnlyList<AudioDeviceInfo> _discoveredDevices = Array.Empty<AudioDeviceInfo>();
    private IReadOnlyList<AppSessionInfo> _activeApps = Array.Empty<AppSessionInfo>();
    private IReadOnlyList<AppLinkStatus> _appLinkStatuses = Array.Empty<AppLinkStatus>();
    private VaioDriverAvailability _vaioAvail = new(false, "Prüfung ausstehend …", 0, true, null);
    private DaemonRuntimeSnapshot _currentSnapshot = new();

    // Telemetrie & Spektrum (außerhalb des Audiothreads auf 60-Hz-Worker)
    private readonly Timer _telemetryTimer;
    private readonly Timer _statusPollTimer;
    private bool _audioRequested;
    private long _nextAsioRetry;
    private long _nextAppRouteRetry;
    private long _nextNativeFormatPoll;
    private readonly float[] _inPeaks = new float[PoolMaxInputs];
    private readonly float[] _outPeaks = new float[PoolMaxOutputs];
    private readonly float[] _busPeaks = new float[PoolMaxBuses];
    private readonly float[] _busChannelPeaks = new float[PoolMaxBuses];
    private readonly float[] _devPeaks = new float[TelemetrySharedBlock.MaxDeviceMasters];
    private readonly float[] _specPreDb = new float[SpectrumAnalyzer.Points];
    private readonly float[] _specPostDb = new float[SpectrumAnalyzer.Points];
    private TelemetrySnapshotFrame _lastMeta;

    private readonly object _telemetryGate = new();
    private SpectrumCapture? _specCapture;
    private SpectrumAnalyzer? _specAnalyzer;
    private float[] _specWinBefore = Array.Empty<float>();
    private float[] _specWinAfter = Array.Empty<float>();
    private long _specLastFrame;
    private int _specMode; // 0 = Aus, 1 = Input, 2 = Output
    private int _specChannel = -1;
    private int _specWindowSize;

    private bool _disposed;

    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public DaemonPaths Paths => _paths;
    public IMixerEngine RawEngine => _engine;
    public IAudioHost RawHost => _host;

    public bool IsConnected => !_disposed;
    public string ConnectionSummary => $"NX-MIX Engine aktiv (PID {Environment.ProcessId})";
    public DaemonRuntimeSnapshot Snapshot => Volatile.Read(ref _currentSnapshot);

    public event Action<DaemonRuntimeSnapshot>? SnapshotUpdated;
    public event Action<bool>? ConnectionChanged;

    public DaemonEngineController(DaemonEngineOptions? options = null)
    {
        options ??= new DaemonEngineOptions();
        _paths = new DaemonPaths(options.CustomDataDir);
        _configStore = new SystemConfigStore(_paths);
        _liveStore = new LiveStateStore(_paths);
        _snapshots = new SnapshotManager(_paths);

        _deviceCatalog = options.DeviceCatalog ?? new DeviceCatalog();
        _appCatalog = options.AppCatalog ?? new AppCatalog();
        _routeRestorer = options.AppRouteRestorer ?? new AppRouteRestorer();
        _hostFactory = options.HostFactory ?? (() => new AudioHost());
        _engineFactory = options.EngineFactory ?? (s => MixerEngine.CreatePooled(s, PoolMaxInputs, PoolMaxBuses, PoolMaxOutputs));
        _shutdownCallback = options.ShutdownRequestedCallback;

        Array.Fill(_specPreDb, SpectrumAnalyzer.FloorDb);
        Array.Fill(_specPostDb, SpectrumAnalyzer.FloorDb);

        try
        {
            _shmWriter = new TelemetrySharedMemoryWriter(options.CustomIpcKey);
        }
        catch (Exception ex)
        {
            AppendLog("Warn", "IPC", $"Shared-Memory-Telemetrie konnte nicht geöffnet werden: {ex.Message}");
        }

        // Crash-Recovery verwaister Pro-App-Umleitungen beim Daemon-Start:
        try
        {
            _routeRestorer.RestoreAllKnown();
        }
        catch (Exception ex)
        {
            AppendLog("Warn", "App-Capture", $"App-Routen-Wiederherstellung: {ex.Message}");
        }

        _config = options.InitialSystemConfigOverride ?? _configStore.Load();
        var savedLive = _liveStore.Load();
        _activeSnapshotId = savedLive?.ActiveSnapshotId;

        var rawMixer = options.InitialMixerOverride
            ?? savedLive?.Mixer
            ?? MixerSettings.Create(_config.SampleRate, 0, 0);

        _mixer = ReconcileMixerWithSystemConfig(SanitizeMixer(rawMixer with { SampleRate = _config.SampleRate }), _config);
        _engine = _engineFactory(_mixer);
        _host = CreateAndWireHost();

        if (!options.SkipInitialDeviceScan)
        {
            RefreshCatalogsCore();
        }
        else
        {
            _vaioAvail = _vaioManager.Evaluate(_discoveredDevices, _routeRestorer);
        }

        if (_config.AutoStartAudioOnBoot && CanStartAudio(out _))
        {
            StartAudioInternal(reason: "Auto-Start beim Dienststart");
        }

        RebuildSnapshotAndNotify(saveLive: false);

        _telemetryTimer = new Timer(_ => PumpTelemetry(), null, 16, 16);
        _statusPollTimer = new Timer(_ => PollSlowStatus(), null, 1000, 1000);
    }

    private IAudioHost CreateAndWireHost()
    {
        var h = _hostFactory();
        h.Faulted += msg =>
        {
            lock (_gate)
            {
                if (_disposed || h.State != HostState.Faulted) return;
                _lastError = msg;
                AppendLog("Error", "AudioHost", $"Stream-Fehler: {msg}");
                RebuildSnapshotAndNotify(saveLive: false);
            }
        };
        return h;
    }

    private void AppendLog(string level, string category, string message)
    {
        var entry = new EngineLogEntry(Interlocked.Increment(ref _logSeq), DateTimeOffset.UtcNow, level, category, message);
        lock (_logs)
        {
            _logs.Add(entry);
            if (_logs.Count > 250) _logs.RemoveRange(0, _logs.Count - 250);
        }
    }

    private IReadOnlyList<EngineLogEntry> GetLogsCopy()
    {
        lock (_logs) return _logs.ToArray();
    }

    public void SetInjectedCatalogs(IReadOnlyList<AudioDeviceInfo> devices, IReadOnlyList<AppSessionInfo>? apps = null)
    {
        lock (_gate)
        {
            _discoveredDevices = devices;
            if (apps != null) _activeApps = apps;
            _vaioAvail = _vaioManager.Evaluate(_discoveredDevices, _routeRestorer);
            RebuildSnapshotAndNotify(saveLive: false);
        }
    }

    private void RefreshCatalogsCore()
    {
        try { _discoveredDevices = _deviceCatalog.List(); }
        catch (Exception ex) { AppendLog("Warn", "Hardware", $"Gerätesuche fehlgeschlagen: {ex.Message}"); }

        try { _activeApps = _appCatalog.List(); }
        catch { }

        _vaioAvail = _vaioManager.Evaluate(_discoveredDevices, _routeRestorer);
    }

    private MixerSettings EffectiveEngineSettings(MixerSettings logical)
    {
        var active = _config.Vaios.Where(v => v.Kind == VaioSlotKind.NxMixKernelDriver)
            .ToDictionary(v => v.Id, v => v.Enabled ? NativeEndpoint(v)?.NxMixActiveChannels ?? 0 : 0);
        if (active.Count > 0)
        {
            ChannelDsp EffectiveChannel(ChannelDsp c) => c.DeviceId is Guid id &&
                active.TryGetValue(id, out int count) && c.DeviceChannelIndex >= count
                ? c with { Mute = true, Solo = false }
                : c;
            logical = logical with
            {
                Inputs = logical.Inputs.Select(EffectiveChannel).ToArray(),
                Outputs = logical.Outputs.Select(EffectiveChannel).ToArray(),
            };
        }
        return _panicMuted ? logical with { MasterGainDb = double.NegativeInfinity, TransitionMs = 10.0 } : logical;
    }

    private AudioDeviceInfo? NativeEndpoint(VaioSlotConfig slot) =>
        _discoveredDevices.FirstOrDefault(d => d.IsNxMixEndpoint &&
            string.Equals(d.Id, slot.EndpointOrExe, StringComparison.OrdinalIgnoreCase));

    private static MixerSettings SanitizeMixer(MixerSettings s)
    {
        int ni = Math.Min(PoolMaxInputs, s.Inputs?.Count ?? 0);
        int no = Math.Min(PoolMaxOutputs, s.Outputs?.Count ?? 0);
        var ins = new ChannelDsp[ni];
        for (int i = 0; i < ni; i++) ins[i] = SanitizeChannel(s.Inputs![i]);
        var outs = new ChannelDsp[no];
        for (int o = 0; o < no; o++) outs[o] = SanitizeChannel(s.Outputs![o]);

        var m = new double[ni][];
        for (int i = 0; i < ni; i++)
        {
            m[i] = new double[no];
            for (int o = 0; o < no; o++)
            {
                double v = s.MatrixDb != null && i < s.MatrixDb.Length && s.MatrixDb[i] != null && o < s.MatrixDb[i].Length
                    ? s.MatrixDb[i][o]
                    : double.NegativeInfinity;
                if (double.IsNaN(v) || v <= -150.0) v = double.NegativeInfinity;
                else v = Math.Clamp(v, -80.0, 12.0);
                m[i][o] = v;
            }
        }

        return (s with
        {
            Inputs = ins,
            Outputs = outs,
            MatrixDb = m,
            DeviceMasters = (s.DeviceMasters ?? Array.Empty<DeviceMasterDsp>()).ToArray(),
            Buses = (s.Buses ?? Array.Empty<MixBus>()).ToArray(),
            BusRoutes = (s.BusRoutes ?? Array.Empty<BusRoute>()).ToArray(),
        }).FoldLegacyMasterIntoOutputs();
    }

    private static ChannelDsp SanitizeChannel(ChannelDsp c)
    {
        double max = double.IsNaN(c.MaxGainDb) ? 12.0 : Math.Clamp(c.MaxGainDb, -60.0, 12.0);
        double gain = double.IsNaN(c.GainDb) ? 0.0 : Math.Min(c.GainDb, max);
        if (gain <= -150.0 && !double.IsNegativeInfinity(gain)) gain = double.NegativeInfinity;
        double trim = double.IsNaN(c.TrimDb) ? 0.0 : Math.Clamp(c.TrimDb, -36.0, 36.0);
        double pan = double.IsNaN(c.Pan) ? 0.0 : Math.Clamp(c.Pan, -1.0, 1.0);
        double ceil = double.IsNaN(c.CeilingDbfs) ? -1.0 : Math.Clamp(c.CeilingDbfs, -60.0, 0.0);
        double delay = double.IsNaN(c.DelayMs) || double.IsInfinity(c.DelayMs) ? 0.0 : Math.Clamp(c.DelayMs, 0.0, 1000.0);
        return c with
        {
            Name = c.Name ?? "",
            MaxGainDb = max,
            GainDb = gain,
            TrimDb = trim,
            Pan = pan,
            CeilingDbfs = ceil,
            DelayMs = delay,
            Filters = c.Filters ?? Array.Empty<FilterSpec>(),
        };
    }

    /// <summary>Synchronisiert DeviceMasterDsp-Einträge und Kanal-Zuordnungen aus SystemConfig, ohne bestehende Kanalpegel oder Filter zu verlieren.</summary>
    public static MixerSettings ReconcileMixerWithSystemConfig(MixerSettings mixer, SystemConfig cfg,
        SystemConfig? previousConfig = null)
    {
        int needIn = 0;
        int needOut = 0;

        foreach (var d in cfg.Devices)
        {
            int end = Math.Clamp(d.EngineFirstChannel + Math.Max(1, d.ChannelCount), 0, d.Flow == DeviceFlow.Capture ? PoolMaxInputs : PoolMaxOutputs);
            if (d.Flow == DeviceFlow.Capture) needIn = Math.Max(needIn, end);
            else needOut = Math.Max(needOut, end);
        }
        foreach (var v in cfg.Vaios)
        {
            int end = Math.Clamp(v.EngineFirstChannel + Math.Max(1, v.Channels), 0, v.Flow == DeviceFlow.Capture ? PoolMaxInputs : PoolMaxOutputs);
            if (v.Flow == DeviceFlow.Capture) needIn = Math.Max(needIn, end);
            else needOut = Math.Max(needOut, end);
        }
        foreach (var a in cfg.AppLinks)
        {
            int end = Math.Clamp(a.FirstChannel + Math.Max(1, a.Count), 0, PoolMaxInputs);
            needIn = Math.Max(needIn, end);
        }

        var ins = mixer.Inputs.ToList();
        while (ins.Count < needIn) ins.Add(new ChannelDsp { DeviceChannelIndex = ins.Count, AutoCreatedForDevice = true });
        var outs = mixer.Outputs.ToList();
        while (outs.Count < needOut) outs.Add(new ChannelDsp { DeviceChannelIndex = outs.Count, AutoCreatedForDevice = true });

        // DeviceMasters abgleichen (jedes AudioDeviceNode und jedes VaioSlotConfig hat genau einen DeviceMasterDsp)
        var existingMasters = mixer.DeviceMasters.ToDictionary(m => m.Id);
        var nextMasters = new List<DeviceMasterDsp>();

        var configuredIds = cfg.Devices.Select(d => d.Id).Concat(cfg.Vaios.Select(v => v.Id))
            .Concat(cfg.AppLinks.Select(a => a.Id)).ToHashSet();
        var activeTitles = cfg.Devices.Select(d => (d.Flow, d.DisplayTitle.ToUpperInvariant()))
            .Concat(cfg.Vaios.Select(v => (v.Flow, v.Name.ToUpperInvariant())))
            .Concat(cfg.AppLinks.Select(a => (DeviceFlow.Capture, a.Title.ToUpperInvariant()))).ToHashSet();
        var previousApps = (previousConfig?.AppLinks ?? Array.Empty<AppLinkEntry>()).GroupBy(a => a.Id)
            .ToDictionary(group => group.Key, group => group.First().Title);
        var previousDevices = previousConfig?.Devices ?? Array.Empty<AudioDeviceNode>();
        var previousVaios = previousConfig?.Vaios ?? Array.Empty<VaioSlotConfig>();
        var detachedIds = new HashSet<Guid>();
        var removedChannels = new HashSet<Guid>();
        var restoredColumns = new Dictionary<int, int>();
        var restoredOutputIds = new Dictionary<Guid, Guid>();
        var routedOrphans = previousConfig == null
            ? mixer.Outputs.Select((c, i) => (c, i))
                .Where(x => x.c.DeviceId is Guid id && !configuredIds.Contains(id) && HasOutputRoute(x.i, x.c.Id))
                .Select(x => x.c.DeviceId!.Value).ToHashSet()
            : new HashSet<Guid>();
        RestoreOutputBindings();
        var resetInputs = DetachChannels(ins, DeviceFlow.Capture);
        var resetOutputs = DetachChannels(outs, DeviceFlow.Render);
        resetOutputs.UnionWith(restoredColumns.Values.Except(restoredColumns.Keys));

        static bool IsAutoName(ChannelDsp channel, string title)
        {
            int k = channel.DeviceChannelIndex;
            return channel.Name == title || channel.Name == $"{title} {(k == 0 ? "L" : "R")}" || channel.Name == $"{title} {k + 1}";
        }

        void RestoreOutputBindings()
        {
            var orphaned = mixer.Outputs.Select((channel, index) => (Channel: channel, Index: index))
                .Where(x => x.Channel.DeviceId is Guid id && !configuredIds.Contains(id))
                .GroupBy(x => x.Channel.DeviceId!.Value)
                .ToArray();
            foreach (var device in cfg.Devices.Where(d => d.Enabled && d.Flow == DeviceFlow.Render && !d.Loopback))
            {
                if (device.ChannelCount <= 0 || device.EngineFirstChannel < 0 ||
                    device.EngineFirstChannel > outs.Count - device.ChannelCount) continue;
                bool Overlaps(int first, int count) =>
                    first < device.EngineFirstChannel + device.ChannelCount && device.EngineFirstChannel < (long)first + count;
                if (cfg.Devices.Any(d => d.Id != device.Id && d.Flow == DeviceFlow.Render && Overlaps(d.EngineFirstChannel, d.ChannelCount)) ||
                    cfg.Vaios.Any(v => v.Flow == DeviceFlow.Render && Overlaps(v.EngineFirstChannel, v.Channels)))
                    continue;

                bool SameDevice(Guid id)
                {
                    if (previousDevices.FirstOrDefault(d => d.Id == id) is { } old)
                        return old.Flow == device.Flow && old.Backend == device.Backend && !old.Loopback &&
                            old.DeviceFirstChannel == device.DeviceFirstChannel &&
                            !string.IsNullOrWhiteSpace(device.EndpointId) &&
                            string.Equals(old.EndpointId, device.EndpointId, StringComparison.OrdinalIgnoreCase);

                    // Alte Mixerstände speichern nur den Anzeigenamen. ASIO-Treibernamen identifizieren den Treiber.
                    return previousConfig == null && device.Backend == AudioBackend.Asio && device.DeviceFirstChannel == 0 &&
                        !string.IsNullOrWhiteSpace(device.FriendlyName) &&
                        string.Equals(device.DisplayTitle, device.FriendlyName, StringComparison.OrdinalIgnoreCase) &&
                        cfg.Devices.Count(d => d.Flow == DeviceFlow.Render && d.Backend == AudioBackend.Asio &&
                            string.Equals(d.DisplayTitle, device.DisplayTitle, StringComparison.OrdinalIgnoreCase)) == 1 &&
                        existingMasters.GetValueOrDefault(id) is { IsInput: false } master &&
                        string.Equals(master.Name, device.DisplayTitle, StringComparison.OrdinalIgnoreCase);
                }

                var candidates = orphaned.Where(group => SameDevice(group.Key)).ToArray();
                if (candidates.Length != 1 || detachedIds.Contains(candidates[0].Key)) continue;
                var source = candidates[0].OrderBy(x => x.Channel.DeviceChannelIndex).ToArray();
                if (source.Length != device.ChannelCount ||
                    source.Where((x, k) => x.Channel.DeviceChannelIndex != k || x.Channel.AutoCreatedForDevice == false).Any() ||
                    !source.Any(x => HasOutputRoute(x.Index, x.Channel.Id))) continue;

                var targets = outs.GetRange(device.EngineFirstChannel, device.ChannelCount);
                if (targets.Where((channel, k) => !CanRestore(channel, k)).Any()) continue;

                var oldMaster = existingMasters.GetValueOrDefault(candidates[0].Key);
                var currentMaster = existingMasters.GetValueOrDefault(device.Id);
                if (oldMaster != null && currentMaster != null &&
                    currentMaster != new DeviceMasterDsp { Id = device.Id, Name = currentMaster.Name } &&
                    currentMaster != (oldMaster with { Id = device.Id, Name = currentMaster.Name }))
                    continue;

                bool CanRestore(ChannelDsp channel, int k)
                {
                    int index = device.EngineFirstChannel + k;
                    if (index == source[k].Index) return true;
                    if (channel.IsVacant) return true;
                    return channel.DeviceId == device.Id && channel.AutoCreatedForDevice != false &&
                        !HasOutputRoute(index, channel.Id) && IsAutoName(channel, device.DisplayTitle) &&
                        channel.Filters.Count == 0 &&
                        (channel with { Id = Guid.Empty, DeviceId = null, AutoCreatedForDevice = null,
                            DeviceChannelIndex = 0, Name = "", Filters = Array.Empty<FilterSpec>() }) ==
                        new ChannelDsp { Id = Guid.Empty };
                }

                foreach (var old in source)
                    outs[old.Index] = new ChannelDsp { DeviceChannelIndex = old.Index, AutoCreatedForDevice = true };

                for (int k = 0; k < source.Length; k++)
                {
                    var old = source[k];
                    int index = device.EngineFirstChannel + k;
                    var target = targets[k];
                    outs[index] = old.Channel with { Id = target.Id, DeviceId = device.Id, AutoCreatedForDevice = true };
                    restoredColumns[index] = old.Index;
                    restoredOutputIds[old.Channel.Id] = target.Id;
                }
                detachedIds.Add(candidates[0].Key);
                if (oldMaster != null)
                    existingMasters[device.Id] = oldMaster with { Id = device.Id, Name = device.DisplayTitle };
            }
        }

        bool HasOutputRoute(int index, Guid id) =>
            mixer.MatrixDb.Any(row => index < row.Length && !double.IsNegativeInfinity(row[index])) ||
            mixer.BusRoutes.Any(r => r.Target.Kind == RouteKind.Output && r.Target.Id == id);

        HashSet<int> DetachChannels(List<ChannelDsp> channels, DeviceFlow flow)
        {
            var reset = new HashSet<int>();
            for (int i = 0; i < channels.Count; i++)
            {
                var channel = channels[i];
                if (channel.DeviceId is not Guid id) continue;
                if (flow == DeviceFlow.Render && routedOrphans.Contains(id)) continue;
                if (cfg.Devices.Any(d => d.Id == id && d.Flow == flow && i >= d.EngineFirstChannel && i - d.EngineFirstChannel < d.ChannelCount) ||
                    cfg.Vaios.Any(v => v.Id == id && v.Flow == flow && i >= v.EngineFirstChannel && i - v.EngineFirstChannel < v.Channels) ||
                    (flow == DeviceFlow.Capture && cfg.AppLinks.Any(a => a.Id == id && i >= a.FirstChannel && i < a.EndChannel)))
                    continue;

                var oldDevice = previousDevices.FirstOrDefault(d => d.Id == id);
                var oldVaio = previousVaios.FirstOrDefault(v => v.Id == id);
                string? title = oldDevice?.DisplayTitle ?? oldVaio?.Name ?? previousApps.GetValueOrDefault(id);
                string? masterName = existingMasters.GetValueOrDefault(id)?.Name;
                bool duplicate = masterName != null && !configuredIds.Contains(id) &&
                    activeTitles.Contains((flow, masterName.ToUpperInvariant()));
                bool generatedOrphan = masterName != null && channel.AutoCreatedForDevice == null && !configuredIds.Contains(id) &&
                    channels.Where(c => c.DeviceId == id).All(c => IsAutoName(c, masterName));
                if (title == null && channel.AutoCreatedForDevice != true && !configuredIds.Contains(id) && !duplicate && !generatedOrphan)
                    continue;
                title ??= masterName;
                bool autoNamed = title != null && IsAutoName(channel, title);
                bool copiedName = duplicate && channels.Any(c => c.DeviceId is Guid other && configuredIds.Contains(other) &&
                    string.Equals(existingMasters.GetValueOrDefault(other)?.Name, masterName, StringComparison.OrdinalIgnoreCase) &&
                    c.DeviceChannelIndex == channel.DeviceChannelIndex && c.Name == channel.Name);
                if (!configuredIds.Contains(id)) detachedIds.Add(id);
                if (channel.AutoCreatedForDevice ?? (autoNamed || copiedName))
                {
                    removedChannels.Add(channel.Id);
                    reset.Add(i);
                    channels[i] = new ChannelDsp { DeviceChannelIndex = i, AutoCreatedForDevice = true };
                }
                else
                {
                    channels[i] = channel with { DeviceId = null, DeviceChannelIndex = i, Name = autoNamed ? "" : channel.Name };
                }
            }
            return reset;
        }

        foreach (var d in cfg.Devices)
        {
            bool isIn = d.Flow == DeviceFlow.Capture;
            if (existingMasters.TryGetValue(d.Id, out var prev))
                nextMasters.Add(prev with { Name = d.DisplayTitle, IsInput = isIn });
            else
                nextMasters.Add(new DeviceMasterDsp { Id = d.Id, Name = d.DisplayTitle, IsInput = isIn });

            var targetList = isIn ? ins : outs;
            for (int k = 0; k < d.ChannelCount; k++)
            {
                int chIdx = d.EngineFirstChannel + k;
                if ((uint)chIdx >= (uint)targetList.Count) continue;
                var cur = targetList[chIdx];
                string autoName = d.ChannelCount == 2 ? $"{d.DisplayTitle} {(k == 0 ? "L" : "R")}" : $"{d.DisplayTitle} {k + 1}";
                targetList[chIdx] = cur with
                {
                    DeviceId = d.Id,
                    AutoCreatedForDevice = cur.AutoCreatedForDevice ?? (cur.DeviceId == null ? false : null),
                    DeviceChannelIndex = k,
                    Name = string.IsNullOrWhiteSpace(cur.Name) ? autoName : cur.Name,
                };
            }
        }

        foreach (var v in cfg.Vaios)
        {
            bool isIn = v.Flow == DeviceFlow.Capture;
            if (existingMasters.TryGetValue(v.Id, out var prev))
                nextMasters.Add(prev with { Name = v.Name, IsInput = isIn });
            else
                nextMasters.Add(new DeviceMasterDsp { Id = v.Id, Name = v.Name, IsInput = isIn });

            var targetList = isIn ? ins : outs;
            for (int k = 0; k < v.Channels; k++)
            {
                int chIdx = v.EngineFirstChannel + k;
                if ((uint)chIdx >= (uint)targetList.Count) continue;
                var cur = targetList[chIdx];
                string autoName = v.Channels == 2 ? $"{v.Name} {(k == 0 ? "L" : "R")}" : $"{v.Name} {k + 1}";
                targetList[chIdx] = cur with
                {
                    DeviceId = v.Id,
                    AutoCreatedForDevice = cur.AutoCreatedForDevice ?? (cur.DeviceId == null ? false : null),
                    DeviceChannelIndex = k,
                    Name = string.IsNullOrWhiteSpace(cur.Name) ? autoName : cur.Name,
                };
            }
        }

        foreach (var a in cfg.AppLinks)
        {
            if (existingMasters.TryGetValue(a.Id, out var prev))
                nextMasters.Add(prev with { Name = a.Title, IsInput = true });
            else
                nextMasters.Add(new DeviceMasterDsp { Id = a.Id, Name = a.Title, IsInput = true });

            for (int k = 0; k < a.Count; k++)
            {
                int chIdx = a.FirstChannel + k;
                if ((uint)chIdx >= (uint)ins.Count) continue;
                var cur = ins[chIdx];
                string autoName = a.Count == 2 ? $"{a.Title} {(k == 0 ? "L" : "R")}" : a.Title;
                ins[chIdx] = cur with
                {
                    DeviceId = a.Id,
                    AutoCreatedForDevice = cur.AutoCreatedForDevice ?? (cur.DeviceId == null ? false : null),
                    DeviceChannelIndex = k,
                    Name = string.IsNullOrWhiteSpace(cur.Name) ? autoName : cur.Name,
                };
            }
        }

        // Auch manuell angelegte DeviceMasters behalten, falls keine Kollision:
        foreach (var m in mixer.DeviceMasters)
        {
            if (!detachedIds.Contains(m.Id) && nextMasters.All(x => x.Id != m.Id) &&
                !(activeTitles.Contains((m.IsInput ? DeviceFlow.Capture : DeviceFlow.Render, m.Name.ToUpperInvariant())) &&
                    ins.All(c => c.DeviceId != m.Id) && outs.All(c => c.DeviceId != m.Id)))
                nextMasters.Add(m);
        }

        while (ins.Count > needIn && ins[^1].IsVacant) ins.RemoveAt(ins.Count - 1);
        while (outs.Count > needOut && outs[^1].IsVacant) outs.RemoveAt(outs.Count - 1);
        var resizedMatrix = new double[ins.Count][];
        for (int i = 0; i < ins.Count; i++)
        {
            resizedMatrix[i] = new double[outs.Count];
            for (int o = 0; o < outs.Count; o++)
            {
                int source = restoredColumns.GetValueOrDefault(o, o);
                resizedMatrix[i][o] = !resetInputs.Contains(i) && !resetOutputs.Contains(o) &&
                    !ins[i].IsVacant && !outs[o].IsVacant && i < mixer.MatrixDb.Length && source < mixer.MatrixDb[i].Length
                    ? mixer.MatrixDb[i][source]
                    : double.NegativeInfinity;
            }
        }

        if (previousConfig != null && FirstPlaybackOutput(cfg) is { } output &&
            !restoredColumns.Keys.Any(o => o >= output.First && o - output.First < output.Count))
        {
            bool firstOutput = FirstPlaybackOutput(previousConfig) == null;
            var appInputs = cfg.AppLinks.Where(a => cfg.AppLinksEnabled && a.Enabled &&
                (firstOutput || !previousConfig.AppLinksEnabled || !previousConfig.AppLinks.Any(old =>
                    old.Id == a.Id && old.Enabled && old.FirstChannel == a.FirstChannel && old.Count == a.Count)))
                .Select(a => (First: a.FirstChannel, Count: a.Count));
            var virtualInputs = cfg.Vaios.Where(v => v.Loopback && v.Enabled &&
                (firstOutput || !previousConfig.Vaios.Any(old => old.Id == v.Id && old.Enabled)))
                .Select(v => (First: v.EngineFirstChannel, Count: v.Kind == VaioSlotKind.NxMixKernelDriver ? Math.Min(v.Channels, 2) : v.Channels));
            foreach (var source in appInputs.Concat(virtualInputs))
            {
                for (int channel = 0; channel < source.Count; channel++)
                {
                    int input = source.First + channel;
                    int target = output.First + Math.Min(channel, output.Count - 1);
                    if ((uint)input >= (uint)ins.Count || (uint)target >= (uint)outs.Count) continue;
                    if (resizedMatrix[input].All(double.IsNegativeInfinity)) resizedMatrix[input][target] = 0.0;
                }
            }
        }

        return mixer with
        {
            SampleRate = cfg.SampleRate,
            DeviceMasters = nextMasters,
            Inputs = ins,
            Outputs = outs,
            MatrixDb = resizedMatrix,
            BusRoutes = mixer.BusRoutes.Where(r => !removedChannels.Contains(r.Source.Id) && !removedChannels.Contains(r.Target.Id))
                .Select(r => r.Target.Kind == RouteKind.Output && restoredOutputIds.TryGetValue(r.Target.Id, out Guid id)
                    ? r with { Target = r.Target with { Id = id } }
                    : r).ToArray(),
        };
    }

    private static (int First, int Count)? FirstPlaybackOutput(SystemConfig cfg)
    {
        var device = cfg.Devices.FirstOrDefault(d => d.Enabled && d.Flow == DeviceFlow.Render && !d.Loopback &&
            d.ChannelCount > 0 && !string.IsNullOrWhiteSpace(d.EndpointId));
        if (device != null) return (device.EngineFirstChannel, device.ChannelCount);
        var vaio = cfg.Vaios.FirstOrDefault(v => v.Enabled && v.Flow == DeviceFlow.Render && !v.Loopback &&
            v.Kind is VaioSlotKind.ExternalDriverEndpoint or VaioSlotKind.NxMixKernelDriver &&
            v.Channels > 0 && !string.IsNullOrWhiteSpace(v.EndpointOrExe));
        return vaio != null ? (vaio.EngineFirstChannel, vaio.Channels) : null;
    }

    public AudioHostConfig BuildAudioHostConfig(SystemConfig cfg)
    {
        var bindings = new List<DeviceBinding>();
        foreach (var d in cfg.Devices.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.EndpointId) && x.ChannelCount > 0))
        {
            bindings.Add(new DeviceBinding(d.EndpointId, d.Flow, d.DeviceFirstChannel, d.EngineFirstChannel, d.ChannelCount, d.Loopback)
            {
                SampleRate = d.SampleRateOverride,
                BufferFrames = d.BufferFramesOverride,
                Exclusive = d.ExclusiveOverride,
            });
        }

        var appBindings = new List<AppBinding>();
        if (cfg.AppLinksEnabled)
        {
            foreach (var a in cfg.AppLinks.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.ExeName) && x.Count > 0))
                appBindings.Add(new AppBinding(a.ExeName, a.FirstChannel, a.Count));
        }

        foreach (var v in cfg.Vaios.Where(x => x.Enabled && !string.IsNullOrWhiteSpace(x.EndpointOrExe) && x.Channels > 0))
        {
            if (v.Kind == VaioSlotKind.NxMixKernelDriver)
            {
                var endpoint = NativeEndpoint(v);
                if (endpoint == null || (v.Loopback ? endpoint.Flow != DeviceFlow.Render : endpoint.NativeOutputPort == 0))
                    continue;
                bindings.Add(new DeviceBinding(v.EndpointOrExe, v.Loopback ? DeviceFlow.Capture : DeviceFlow.Render, 0,
                    v.EngineFirstChannel, Math.Min(v.Channels, endpoint.NxMixActiveChannels), v.Loopback)
                {
                    Exclusive = false,
                    NativeOutputPort = v.Loopback ? 0 : endpoint.NativeOutputPort,
                });
            }
            else if (v.Kind == VaioSlotKind.ExternalDriverEndpoint)
            {
                var flow = v.Loopback ? DeviceFlow.Capture : v.Flow;
                bindings.Add(new DeviceBinding(v.EndpointOrExe, flow, 0, v.EngineFirstChannel, v.Channels, v.Loopback));
            }
            else if (v.Kind == VaioSlotKind.AppCapture && cfg.AppLinksEnabled)
            {
                appBindings.Add(new AppBinding(v.EndpointOrExe, v.EngineFirstChannel, Math.Clamp(v.Channels, 1, 2)));
            }
        }

        return new AudioHostConfig(
            SampleRate: cfg.SampleRate,
            Bindings: bindings,
            BlockFrames: cfg.BlockFrames,
            UseExclusiveMode: cfg.DefaultWasapiExclusive,
            AppBindings: appBindings.Count > 0 ? appBindings : null,
            ParkingDeviceId: cfg.ParkingDeviceId,
            PreferredMasterDeviceId: cfg.PreferredMasterDeviceId,
            AllowOfflineAuxDevices: cfg.AllowOfflineAuxDevices);
    }

    public bool CanStartAudio(out string reason)
    {
        var hc = BuildAudioHostConfig(_config);
        if (hc.Bindings.Count == 0 && (hc.AppBindings == null || hc.AppBindings.Count == 0))
        {
            reason = "Noch kein Ein-/Ausgabegerät oder Virtuelles I/O eingerichtet.";
            return false;
        }
        if (!hc.Bindings.Any(b => b.Flow == DeviceFlow.Render && !b.Loopback))
        {
            reason = "Mindestens ein aktives Ausgabegerät (oder virtueller Ausgang) erforderlich.";
            return false;
        }
        reason = "";
        return true;
    }

    private void StartAudioInternal(string reason)
    {
        _audioRequested = true;
        _nextAsioRetry = Environment.TickCount64 + 5000;
        (_engine as MixerEngine)?.CancelOutputTestTone();
        if (!CanStartAudio(out string why))
        {
            _lastError = why;
            AppendLog("Warn", "AudioHost", $"Audio-Start übersprungen: {why}");
            return;
        }
        try
        {
            _lastError = null;
            var hc = BuildAudioHostConfig(_config);
            _engine.TrySetSettings(EffectiveEngineSettings(_mixer));
            _host.Start(hc, _engine);
            var st = _host.GetStats();
            if (st.State == HostState.Faulted)
            {
                _lastError = st.LastError;
                AppendLog("Error", "AudioHost", $"Audio-Start fehlgeschlagen: {st.LastError}");
            }
            else
            {
                AppendLog("Info", "AudioHost", $"Audio gestartet ({reason}) – Taktquelle: {st.MasterClockName ?? "Automatisch"}, Puffer: {st.BlockFrames} Frames.");
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            AppendLog("Error", "AudioHost", $"Ausnahme beim Audio-Start: {ex.Message}");
        }
    }

    private void RebuildSnapshotAndNotify(bool saveLive)
    {
        if (saveLive)
            _liveStore.ScheduleSave(_mixer, _activeSnapshotId);

        HostStats stats;
        try { stats = _host.GetStats(); }
        catch { stats = new HostStats(HostState.Stopped, _lastError, _config.BlockFrames, 0, 0, 0, 0, Array.Empty<DeviceStat>()); }

        if (_host is IAppLinkMonitor mon)
        {
            try { _appLinkStatuses = mon.GetAppLinks(); } catch { }
        }

        var snap = new DaemonRuntimeSnapshot
        {
            DaemonPid = Environment.ProcessId,
            StartedUtc = StartedUtc,
            StateRevision = Interlocked.Increment(ref _stateRev),
            Config = _config,
            Mixer = _mixer,
            DiscoveredDevices = _discoveredDevices,
            ActiveAppSessions = _activeApps,
            AppLinkStatuses = _appLinkStatuses,
            VaioAvailability = _vaioAvail,
            HostStats = stats,
            LastError = _lastError ?? stats.LastError,
            AudioStartBlockedReason = stats.State is not (HostState.Running or HostState.Starting) &&
                !CanStartAudio(out string reason) ? reason : null,
            PanicMuted = _panicMuted,
            ActiveSnapshotId = _activeSnapshotId,
            Snapshots = _snapshots.ListSummaries(),
            RecentLogs = GetLogsCopy(),
        };
        Volatile.Write(ref _currentSnapshot, snap);
        SnapshotUpdated?.Invoke(snap);
    }

    public void PumpTelemetryNow() => PumpTelemetry();

    private void PumpTelemetry()
    {
        if (_disposed || !Monitor.TryEnter(_telemetryGate)) return;
        try
        {
            var mix = _mixer;
            int ni = Math.Min(mix.Inputs.Count, PoolMaxInputs);
            int no = Math.Min(mix.Outputs.Count, PoolMaxOutputs);
            int nb = Math.Min(mix.Buses.Count, PoolMaxBuses);
            int nd = Math.Min(mix.DeviceMasters.Count, TelemetrySharedBlock.MaxDeviceMasters);

            _engine.ReadInputPeaks(_inPeaks.AsSpan(0, ni));
            if (ni < _inPeaks.Length) _inPeaks.AsSpan(ni).Clear();

            _engine.ReadOutputPeaks(_outPeaks.AsSpan(0, no));
            if (no < _outPeaks.Length) _outPeaks.AsSpan(no).Clear();

            _engine.ReadBusPeaks(_busPeaks.AsSpan(0, nb), _busChannelPeaks);
            if (nb < _busPeaks.Length) _busPeaks.AsSpan(nb).Clear();

            _engine.ReadDeviceMasterPeaks(_devPeaks.AsSpan(0, nd));
            if (nd < _devPeaks.Length) _devPeaks.AsSpan(nd).Clear();

            var cap = Volatile.Read(ref _specCapture);
            if (cap != null && _specAnalyzer != null && _specWinBefore.Length >= cap.WindowSize)
            {
                if (cap.TryRead(_specWinBefore, _specWinAfter, ref _specLastFrame))
                {
                    _specAnalyzer.Analyze(_specWinBefore.AsSpan(0, cap.WindowSize), _specPreDb);
                    _specAnalyzer.Analyze(_specWinAfter.AsSpan(0, cap.WindowSize), _specPostDb);
                }
            }

            var st = _host.GetStats();
            var meta = new TelemetrySnapshotFrame(
                Environment.ProcessId,
                st.State,
                st.BlockFrames,
                mix.SampleRate,
                _panicMuted,
                st.CpuLoad,
                st.OutputLatencyMs,
                st.Underruns,
                st.Overruns,
                _engine.LimiterHits,
                _specMode,
                _specChannel);

            _lastMeta = meta;
            _shmWriter?.Publish(_inPeaks, _outPeaks, _busPeaks, _devPeaks, in meta, _specPreDb, _specPostDb, _busChannelPeaks);
        }
        catch
        {
        }
        finally { Monitor.Exit(_telemetryGate); }
    }

    private void PollSlowStatus()
    {
        if (_disposed) return;
        lock (_gate)
        {
            if (_disposed) return;
            if (Environment.TickCount64 >= _nextNativeFormatPoll && _deviceCatalog is DeviceCatalog catalog &&
                _config.Vaios.Any(v => v.Enabled && v.Kind == VaioSlotKind.NxMixKernelDriver))
            {
                _nextNativeFormatPoll = Environment.TickCount64 + 3000;
                var before = BuildAudioHostConfig(_config).Bindings;
                try
                {
                    var windows = catalog.ListWindows();
                    if (windows.Count > 0)
                        _discoveredDevices = windows.Concat(_discoveredDevices.Where(d => d.Backend == AudioBackend.Asio)).ToArray();
                    if (ApplyNativeFormatChanges(before))
                    {
                        _vaioAvail = _vaioManager.Evaluate(_discoveredDevices, _routeRestorer);
                        RebuildSnapshotAndNotify(saveLive: false);
                        return;
                    }
                }
                catch (Exception ex) { AppendLog("Warn", "WaveRT", $"Windows-Kanalformat: {ex.Message}"); }
            }
            var prevSnap = _currentSnapshot;
            HostStats st;
            try { st = _host.GetStats(); } catch { return; }
            if (TryRecoverAsio(st))
            {
                RebuildSnapshotAndNotify(saveLive: false);
                return;
            }

            IReadOnlyList<AppLinkStatus> links = _appLinkStatuses;
            if (_host is IAppLinkMonitor mon)
            {
                try { links = mon.GetAppLinks(); } catch { }
            }

            if (Environment.TickCount64 >= _nextAppRouteRetry &&
                (st.State is HostState.Stopped or HostState.Faulted || (st.State == HostState.Running && links.Count == 0)))
            {
                _nextAppRouteRetry = Environment.TickCount64 + 5000;
                _routeRestorer.RestoreAllKnown();
            }

            bool changed = st.State != prevSnap.HostStats.State
                || st.Underruns != prevSnap.HostStats.Underruns
                || st.Overruns != prevSnap.HostStats.Overruns
                || Math.Abs(st.CpuLoad - prevSnap.HostStats.CpuLoad) > 0.01
                || !st.Devices.Select(d => (d.DeviceId, d.IsOffline)).SequenceEqual(
                    prevSnap.HostStats.Devices.Select(d => (d.DeviceId, d.IsOffline)))
                || links.Count != prevSnap.AppLinkStatuses.Count;

            if (!changed)
            {
                for (int i = 0; i < links.Count; i++)
                {
                    var previous = prevSnap.AppLinkStatuses[i];
                    if (links[i].State != previous.State || links[i].ProcessCount != previous.ProcessCount ||
                        links[i].Message != previous.Message)
                    {
                        changed = true;
                        break;
                    }
                }
            }

            if (changed)
            {
                _appLinkStatuses = links;
                RebuildSnapshotAndNotify(saveLive: false);
            }
        }
    }

    private bool TryRecoverAsio(HostStats stats)
    {
        if (!_audioRequested || stats.State == HostState.Starting || Environment.TickCount64 < _nextAsioRetry)
            return false;

        _nextAsioRetry = Environment.TickCount64 + 5000;
        var bindings = BuildAudioHostConfig(_config).Bindings.Where(b => AsioCatalog.IsAsio(b.DeviceId));
        var drivers = bindings.Select(b => AsioCatalog.DriverName(b.DeviceId)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (drivers.Count == 0 || !CanStartAudio(out _)) return false;

        if (stats.State == HostState.Running)
        {
            var offline = stats.Devices.Where(d => d.IsOffline && AsioCatalog.IsAsio(d.DeviceId))
                .Select(d => AsioCatalog.DriverName(d.DeviceId)).Distinct(StringComparer.OrdinalIgnoreCase);
            if (!offline.Any(driver => drivers.Contains(driver) && AsioCatalog.CanOpen(driver))) return false;
        }
        StartAudioInternal("automatische ASIO-Wiederverbindung");
        if (stats.State == HostState.Running)
            _nextAsioRetry = Environment.TickCount64 + 30000;
        return true;
    }

    public bool TryReadTelemetry(
        Span<float> inPeaks,
        Span<float> outPeaks,
        Span<float> busPeaks,
        Span<float> devPeaks,
        Span<float> specPre,
        Span<float> specPost,
        out TelemetrySnapshotFrame meta,
        Span<float> busChannelPeaks = default)
    {
        meta = _lastMeta;
        CopySlice(_inPeaks, inPeaks);
        CopySlice(_outPeaks, outPeaks);
        CopySlice(_busPeaks, busPeaks);
        CopySlice(_busChannelPeaks, busChannelPeaks);
        CopySlice(_devPeaks, devPeaks);
        CopySlice(_specPreDb, specPre);
        CopySlice(_specPostDb, specPost);
        return true;
    }

    private static void CopySlice(float[] src, Span<float> dst)
    {
        int n = Math.Min(src.Length, dst.Length);
        src.AsSpan(0, n).CopyTo(dst);
        if (n < dst.Length) dst[n..].Clear();
    }

    // ============================================================================================
    // INxMixBackend Befehle
    // ============================================================================================

    public Task<string?> ApplyMixerAsync(MixerSettings mixer)
    {
        lock (_gate)
        {
            var clean = SanitizeMixer(mixer with { SampleRate = _config.SampleRate });
            try
            {
                if (clean.Buses.Count > 0 || clean.BusRoutes.Count > 0)
                    BusRouting.Compile(clean);
            }
            catch (ArgumentException ex)
            {
                return Task.FromResult<string?>(ex.Message);
            }

            if (!_mixer.Outputs.Select(c => c.Id).SequenceEqual(clean.Outputs.Select(c => c.Id)))
                (_engine as MixerEngine)?.CancelOutputTestTone();
            if (!_engine.TrySetSettings(EffectiveEngineSettings(clean)))
                return Task.FromResult<string?>("Einstellungen überschreiten die Pool-Kapazität (128×64×128) oder sind ungültig.");

            _mixer = clean with { TransitionMs = null };
            RebuildSnapshotAndNotify(saveLive: true);
            return Task.FromResult<string?>(null);
        }
    }

    public Task<string?> AdjustChannelGainAsync(Guid channelId, bool isInput, double deltaDb)
    {
        lock (_gate)
        {
            var list = (isInput ? _mixer.Inputs : _mixer.Outputs).ToArray();
            int idx = Array.FindIndex(list, c => c.Id == channelId);
            if (idx < 0) return Task.FromResult<string?>("Kanal nicht gefunden.");

            var ch = list[idx];
            double cur = double.IsNegativeInfinity(ch.GainDb) || ch.GainDb < -80.0 ? -80.0 : ch.GainDb;
            double next = Math.Clamp(cur + deltaDb, -80.0, ch.MaxGainDb);
            if (next <= -79.9 && deltaDb < 0) next = double.NegativeInfinity;

            // Falls der Kanal Teil einer Link-Gruppe ist, alle Mitglieder relativ mitbewegen:
            if (ch.LinkGroupId is { } groupId)
            {
                double appliedDelta = (double.IsNegativeInfinity(next) ? -80.0 : next) - cur;
                for (int k = 0; k < list.Length; k++)
                {
                    if (list[k].LinkGroupId != groupId) continue;
                    if (k == idx) { list[k] = ch with { GainDb = next }; continue; }
                    double mCur = double.IsNegativeInfinity(list[k].GainDb) || list[k].GainDb < -80.0 ? -80.0 : list[k].GainDb;
                    double mNext = Math.Clamp(mCur + appliedDelta, -80.0, list[k].MaxGainDb);
                    if (mNext <= -79.9 && deltaDb < 0) mNext = double.NegativeInfinity;
                    list[k] = list[k] with { GainDb = mNext };
                }
            }
            else
            {
                list[idx] = ch with { GainDb = next };
            }

            var updated = isInput ? _mixer with { Inputs = list } : _mixer with { Outputs = list };
            _engine.TrySetSettings(EffectiveEngineSettings(updated));
            _mixer = updated;
            RebuildSnapshotAndNotify(saveLive: true);
            return Task.FromResult<string?>(null);
        }
    }

    public Task<string?> AdjustDeviceMasterGainAsync(Guid deviceId, double deltaDb)
    {
        lock (_gate)
        {
            var dms = _mixer.DeviceMasters.ToArray();
            int idx = Array.FindIndex(dms, d => d.Id == deviceId);
            if (idx < 0) return Task.FromResult<string?>("Geräte-Master nicht gefunden.");

            var dm = dms[idx];
            double cur = double.IsNegativeInfinity(dm.GainDb) || dm.GainDb < -80.0 ? -80.0 : dm.GainDb;
            double next = Math.Clamp(cur + deltaDb, -80.0, 12.0);
            if (next <= -79.9 && deltaDb < 0) next = double.NegativeInfinity;
            dms[idx] = dm with { GainDb = next };

            var updated = _mixer with { DeviceMasters = dms };
            _engine.TrySetSettings(EffectiveEngineSettings(updated));
            _mixer = updated;
            RebuildSnapshotAndNotify(saveLive: true);
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetPanicMuteAsync(bool muted)
    {
        lock (_gate)
        {
            _panicMuted = muted;
            if (muted) (_engine as MixerEngine)?.CancelOutputTestTone();
            _engine.TrySetSettings(EffectiveEngineSettings(_mixer));
            AppendLog(muted ? "Warn" : "Info", "Engine", muted ? "PANIC-MUTE aktiviert: Alle Ausgänge stummgeschaltet." : "Panic-Mute aufgehoben.");
            RebuildSnapshotAndNotify(saveLive: false);
            return Task.CompletedTask;
        }
    }

    public Task<string?> ApplySystemConfigAsync(SystemConfig config, bool reopenAudioIfNeeded = true)
    {
        lock (_gate)
        {
            var prev = _config;
            bool couldStartBefore = CanStartAudio(out _);
            var clean = _configStore.Save(config);
            bool rateChanged = clean.SampleRate != prev.SampleRate;
            bool streamImpact = rateChanged
                || clean.BlockFrames != prev.BlockFrames
                || clean.PreferredMasterDeviceId != prev.PreferredMasterDeviceId
                || clean.DefaultWasapiExclusive != prev.DefaultWasapiExclusive
                || clean.AllowOfflineAuxDevices != prev.AllowOfflineAuxDevices
                || clean.AppLinksEnabled != prev.AppLinksEnabled
                || clean.ParkingDeviceId != prev.ParkingDeviceId
                || ! prev.Devices.SequenceEqual(clean.Devices)
                || ! prev.Vaios.SequenceEqual(clean.Vaios)
                || ! prev.AppLinks.SequenceEqual(clean.AppLinks);

            if (streamImpact) (_engine as MixerEngine)?.CancelOutputTestTone();
            _config = clean;
            _mixer = ReconcileMixerWithSystemConfig(_mixer, _config, prev);

            bool wasRunning = _host.State is HostState.Running or HostState.Starting;
            bool startWhenReady = !wasRunning && clean.AutoStartAudioOnBoot && (!couldStartBefore || !prev.AutoStartAudioOnBoot);

            if (rateChanged)
            {
                if (wasRunning) _host.Stop();
                var oldEngine = _engine;
                _engine = _engineFactory(EffectiveEngineSettings(_mixer));
                (oldEngine as IDisposable)?.Dispose();
                if (_specMode != 0 && _specChannel >= 0)
                    AttachSpectrumLocked(_specMode, _specChannel, _specWindowSize);
                AppendLog("Info", "Engine", $"Sample-Rate auf {_config.SampleRate} Hz umgestellt.");
                if ((wasRunning || startWhenReady) && reopenAudioIfNeeded && CanStartAudio(out _))
                    StartAudioInternal("Sample-Rate-Wechsel");
            }
            else
            {
                _engine.TrySetSettings(EffectiveEngineSettings(_mixer));
                if (wasRunning && streamImpact && reopenAudioIfNeeded)
                {
                    _host.Stop();
                    if (CanStartAudio(out _))
                        StartAudioInternal("Hardware-/Stream-Konfiguration aktualisiert");
                }
                else if (startWhenReady && reopenAudioIfNeeded && CanStartAudio(out _))
                    StartAudioInternal("Erster Wiedergabeausgang verfügbar");
            }

            RebuildSnapshotAndNotify(saveLive: true);
            return Task.FromResult<string?>(null);
        }
    }

    public Task RefreshDevicesAsync() => Task.Run(() =>
    {
        lock (_gate)
        {
            var before = BuildAudioHostConfig(_config).Bindings;
            RefreshCatalogsCore();
            ApplyNativeFormatChanges(before);
            RebuildSnapshotAndNotify(saveLive: false);
        }
    });

    private bool ApplyNativeFormatChanges(IReadOnlyList<DeviceBinding> before)
    {
        if (before.SequenceEqual(BuildAudioHostConfig(_config).Bindings)) return false;
        bool running = _host.State is HostState.Running or HostState.Starting;
        if (running) _host.Stop();
        if ((running || _audioRequested) && CanStartAudio(out _))
            StartAudioInternal("Windows-Kanalformat geändert");
        return true;
    }

    public Task StartAudioAsync() => Task.Run(() =>
    {
        lock (_gate)
        {
            if (_host.State is HostState.Running or HostState.Starting) return;
            StartAudioInternal("Benutzeranforderung");
            RebuildSnapshotAndNotify(saveLive: false);
        }
    });

    public Task StopAudioAsync() => Task.Run(() =>
    {
        lock (_gate)
        {
            _audioRequested = false;
            (_engine as MixerEngine)?.CancelOutputTestTone();
            try { _host.Stop(); } catch (Exception ex) { _lastError = ex.Message; }
            AppendLog("Info", "AudioHost", "Audio-Streaming gestoppt.");
            RebuildSnapshotAndNotify(saveLive: false);
        }
    });

    public Task<string?> ShowAsioControlPanelAsync(string endpointId) => Task.Run(() =>
    {
        try
        {
            AsioCatalog.ShowControlPanel(endpointId);
            return (string?)null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    });

    public Task<int> RestoreOrphanedAppRoutesAsync() => Task.Run(() =>
    {
        _routeRestorer.RestoreAllKnown();
        AppendLog("Info", "App-Capture", "Bekannte App-Audio-Umleitungen auf Ursprungszustand geprüft/zurückgesetzt.");
        return 1;
    });

    public Task<string?> PlayOutputTestToneAsync(Guid channelId)
    {
        lock (_gate)
        {
            string? error = PlayOutputTestToneLocked(channelId);
            return Task.FromResult(error);
        }
    }

    private string? PlayOutputTestToneLocked(Guid channelId)
    {
        if (_panicMuted || _host.State != HostState.Running || _engine is not MixerEngine mixer)
            return "Testton braucht eine laufende Audio-Engine ohne Panik-Stumm.";
        int index = _mixer.Outputs.ToList().FindIndex(c => c.Id == channelId);
        if (index < 0) return "Ausgang nicht gefunden.";

        var stats = _host.GetStats();
        var bindings = BuildAudioHostConfig(_config).Bindings.Where(b => b.Flow == DeviceFlow.Render &&
            index >= b.EngineFirstChannel && index < b.EngineFirstChannel + b.Count).ToArray();
        if (bindings.Length != 1 || !stats.Devices.Any(d => !d.IsOffline && SameOutputDevice(d.DeviceId, bindings[0].DeviceId)))
            return "Für den Testton muss genau ein aktives Ausgabegerät am Kanal hängen.";
        if (_mixer.Outputs[index].Mute || double.IsNegativeInfinity(_mixer.Outputs[index].GainDb))
            return "Der Ausgang ist stummgeschaltet.";
        return mixer.TryStartOutputTestTone(index) ? null : "Ein Testton läuft bereits.";
    }

    private static bool SameOutputDevice(string a, string b) => a == b ||
        (AsioCatalog.IsAsio(a) && AsioCatalog.IsAsio(b) &&
            string.Equals(AsioCatalog.DriverName(a), AsioCatalog.DriverName(b), StringComparison.OrdinalIgnoreCase));

    public Task SubscribeSpectrumAsync(int mode, int channelIndex, int windowSize = 0)
    {
        lock (_gate)
        {
            AttachSpectrumLocked(mode, channelIndex, windowSize);
            return Task.CompletedTask;
        }
    }

    private void AttachSpectrumLocked(int mode, int channelIndex, int windowSize)
    {
        lock (_telemetryGate)
        {
            if (_engine is not MixerEngine me) return;
            if (_specCapture != null)
            {
                me.StopSpectrum(_specCapture);
                _specCapture = null;
            }
            _specMode = mode;
            _specChannel = channelIndex;
            _specWindowSize = windowSize;
            Array.Fill(_specPreDb, SpectrumAnalyzer.FloorDb);
            Array.Fill(_specPostDb, SpectrumAnalyzer.FloorDb);
            if (mode is 1 or 2 && channelIndex >= 0)
            {
                bool isInput = mode == 1;
                int max = isInput ? PoolMaxInputs : PoolMaxOutputs;
                if (channelIndex < max)
                {
                    var cap = me.StartSpectrum(isInput, channelIndex, windowSize);
                    _specWinBefore = new float[cap.WindowSize];
                    _specWinAfter = new float[cap.WindowSize];
                    _specAnalyzer = new SpectrumAnalyzer(cap.WindowSize, cap.SampleRate);
                    _specLastFrame = 0;
                    _specCapture = cap;
                }
            }
        }
    }

    public Task<MixSnapshot> SaveSnapshotAsync(Guid? existingId, string name, string notes, string colorHex, double defaultFadeMs, SnapshotScopeFlags defaultScope)
    {
        lock (_gate)
        {
            var saved = _snapshots.Save(existingId, name, notes, colorHex, defaultFadeMs, defaultScope, _mixer);
            _activeSnapshotId = saved.Id;
            AppendLog("Info", "Snapshots", $"Snapshot '{saved.Name}' gespeichert.");
            RebuildSnapshotAndNotify(saveLive: true);
            return Task.FromResult(saved);
        }
    }

    public Task<MixSnapshot?> GetSnapshotAsync(Guid snapshotId) =>
        Task.FromResult(_snapshots.Load(snapshotId));

    public Task<MixSnapshot> ImportSnapshotAsync(MixSnapshot snapshot)
    {
        lock (_gate)
        {
            var imported = _snapshots.Import(snapshot);
            RebuildSnapshotAndNotify(saveLive: false);
            return Task.FromResult(imported);
        }
    }

    public Task<MixSnapshot?> RenameSnapshotAsync(Guid snapshotId, string newName)
    {
        lock (_gate)
        {
            var renamed = _snapshots.Rename(snapshotId, newName);
            if (renamed != null)
            {
                AppendLog("Info", "Snapshots", $"Snapshot umbenannt in '{renamed.Name}'.");
                RebuildSnapshotAndNotify(saveLive: false);
            }
            return Task.FromResult(renamed);
        }
    }

    public Task DeleteSnapshotAsync(Guid snapshotId)
    {
        lock (_gate)
        {
            _snapshots.Delete(snapshotId);
            if (_activeSnapshotId == snapshotId) _activeSnapshotId = null;
            RebuildSnapshotAndNotify(saveLive: true);
            return Task.CompletedTask;
        }
    }

    public Task<MixSnapshot?> DuplicateSnapshotAsync(Guid snapshotId)
    {
        lock (_gate)
        {
            var dup = _snapshots.Duplicate(snapshotId);
            if (dup != null) AppendLog("Info", "Snapshots", $"Snapshot '{dup.Name}' dupliziert.");
            RebuildSnapshotAndNotify(saveLive: false);
            return Task.FromResult(dup);
        }
    }

    public Task<SnapshotDiffPreview?> PreviewSnapshotAsync(Guid snapshotId, SnapshotScopeFlags scope)
    {
        lock (_gate)
        {
            var snap = _snapshots.Load(snapshotId);
            if (snap == null) return Task.FromResult<SnapshotDiffPreview?>(null);
            return Task.FromResult<SnapshotDiffPreview?>(SnapshotManager.ComputeDiff(_mixer, snap, scope));
        }
    }

    public Task<string?> RecallSnapshotAsync(Guid snapshotId, SnapshotScopeFlags scope, double fadeMs)
    {
        lock (_gate)
        {
            var snap = _snapshots.Load(snapshotId);
            if (snap == null) return Task.FromResult<string?>("Snapshot nicht gefunden.");

            var recalled = SnapshotManager.ApplySelectiveRecall(_mixer, snap, scope, fadeMs);
            var clean = SanitizeMixer(recalled);
            if (!_engine.TrySetSettings(EffectiveEngineSettings(clean)))
                return Task.FromResult<string?>("Snapshot konnte nicht auf die Engine angewendet werden.");

            _mixer = clean with { TransitionMs = null };
            _activeSnapshotId = snap.Id;
            AppendLog("Info", "Snapshots", $"Snapshot '{snap.Name}' geladen (Fade: {fadeMs:0} ms).");
            RebuildSnapshotAndNotify(saveLive: true);
            return Task.FromResult<string?>(null);
        }
    }

    public Task RequestDaemonShutdownAsync()
    {
        AppendLog("Info", "Daemon", "Geordnetes Herunterfahren der Hintergrund-Engine angefordert.");
        _shutdownCallback?.Invoke();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ConnectionChanged?.Invoke(false);
        _telemetryTimer.Dispose();
        _statusPollTimer.Dispose();
        _liveStore.Dispose();
        try { _host.Stop(); } catch { }
        try { _host.Dispose(); } catch { }
        try { (_engine as IDisposable)?.Dispose(); } catch { }
        try { _routeRestorer.RestoreAllKnown(); } catch { }
        _shmWriter?.Dispose();
    }
}
