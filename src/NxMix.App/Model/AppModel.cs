using System.Diagnostics;
using NxMix.App.Ui;
using NxMix.Audio;
using NxMix.Daemon;
using NxMix.Engine;
using NxMix.Ipc;

namespace NxMix.App.Model;

/// <summary>
/// Client-Modell der NX-MIX Live-Konsole.
/// Verbindet sich über <see cref="INxMixBackend"/> mit der eigenständigen Hintergrund-Engine (NxMix.Daemon.exe)
/// oder nutzt im Headless-Modus (--shot / --selftest) einen In-Process-Controller.
/// Schließen des Fensters trennt ausschließlich die Client-Verbindung und stoppt NIEMALS Audio oder App-Links!
/// </summary>
internal sealed class AppModel
{
    public static readonly int[] SampleRates = { 44100, 48000, 88200, 96000, 176400, 192000 };
    public static readonly int[] BlockSizes = { 64, 128, 240, 256, 480, 512, 960, 1024 };
    public const int DefaultSampleRate = 48000;
    public const int DefaultBlockFrames = 256;

    readonly INxMixBackend _backend;
    readonly float[] _inPeaks = new float[DaemonEngineController.PoolMaxInputs];
    readonly float[] _outPeaks = new float[DaemonEngineController.PoolMaxOutputs];
    readonly float[] _busPeaks = new float[DaemonEngineController.PoolMaxBuses];
    readonly float[] _busChannelPeaks = new float[DaemonEngineController.PoolMaxBuses];
    readonly float[] _devPeaks = new float[TelemetrySharedBlock.MaxDeviceMasters];
    readonly float[] _specPreDb = new float[SpectrumAnalyzer.Points];
    readonly float[] _specPostDb = new float[SpectrumAnalyzer.Points];
    TelemetrySnapshotFrame _telemetryMeta;
    long _telemetryFrameSeq;

    public INxMixBackend Backend => _backend;
    public DaemonRuntimeSnapshot Runtime { get; private set; }
    public SystemConfig Config { get; private set; }
    public MixerSettings Settings { get; private set; }
    public UiDeckCollection Decks { get; private set; }

    /// <summary>True im Headless-Selbsttest und Screenshot-Lauf: synchrone Ausführung ohne Hintergrund-Race.</summary>
    public bool RunInline { get; set; }
    public bool HostBusy { get; private set; }
    public string? HostError { get; private set; }

    /// <summary>Zum Ausführen von Rückrufen auf dem UI-Thread (von MainWindow gesetzt).</summary>
    public Action<Action> Post { get; set; } = a => a();
    public Action<string, bool>? Toast { get; set; }

    public event Action? ValuesChanged;
    public event Action? StructureChanged;
    public event Action? BindingsChanged;
    public event Action? HostChanged;
    public event Action? DecksChanged;
    public event Action? SnapshotsChanged;
    public event Action? Changed;

    public AppModel(INxMixBackend backend, UiDeckCollection? initialDecks = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        Array.Fill(_specPreDb, SpectrumAnalyzer.FloorDb);
        Array.Fill(_specPostDb, SpectrumAnalyzer.FloorDb);

        Runtime = _backend.Snapshot;
        Config = Runtime.Config;
        Settings = Sanitize(Runtime.Mixer);
        Decks = initialDecks ?? UiDeckStore.Load();
        HostError = Runtime.LastError;

        _backend.SnapshotUpdated += snap => Post(() => ApplyIncomingSnapshot(snap));
        _backend.ConnectionChanged += connected => Post(() =>
        {
            if (connected && _subSpecMode != 0) QueueSpectrumRequest(_subSpecMode, _subSpecChannel, _subSpecWindowSize);
            HostChanged?.Invoke();
        });
    }

    // ---- Echtzeit-Telemetrie (Spitzenpegel & Spektrum) ----------------------------------------
    public ReadOnlySpan<float> InputPeaks => _inPeaks;
    public ReadOnlySpan<float> OutputPeaks => _outPeaks;
    public ReadOnlySpan<float> BusPeaks => _busPeaks;
    public ReadOnlySpan<float> DeviceMasterPeaks => _devPeaks;
    public ReadOnlySpan<float> SpectrumPreDb => _specPreDb;
    public ReadOnlySpan<float> SpectrumPostDb => _specPostDb;
    public TelemetrySnapshotFrame Telemetry => _telemetryMeta;
    public long TelemetryFrameSeq => _telemetryFrameSeq;

    public float GetInputPeak(int idx) => (uint)idx < (uint)_inPeaks.Length ? _inPeaks[idx] : 0f;
    public float GetOutputPeak(int idx) => (uint)idx < (uint)_outPeaks.Length ? _outPeaks[idx] : 0f;
    public ReadOnlySpan<float> GetBusChannelPeaks(Guid id)
    {
        int offset = 0;
        foreach (var bus in Settings.Buses)
        {
            if (bus.Id == id)
                return offset + bus.Channels <= _busChannelPeaks.Length ? _busChannelPeaks.AsSpan(offset, bus.Channels) : default;
            offset += bus.Channels;
        }
        return default;
    }

    public float GetBusPeak(int idx) => (uint)idx < (uint)_busPeaks.Length ? _busPeaks[idx] : 0f;
    public float GetDeviceMasterPeak(Guid deviceId)
    {
        var dms = Settings.DeviceMasters;
        for (int i = 0; i < dms.Count && i < _devPeaks.Length; i++)
            if (dms[i].Id == deviceId) return _devPeaks[i];
        return 0f;
    }

    // ---- Abgeleitete Status-Eigenschaften -----------------------------------------------------
    public bool IsConnected => _backend.IsConnected;
    public string ConnectionSummary => _backend.ConnectionSummary;
    public int InputCount => Settings.Inputs.Count;
    public int OutputCount => Settings.Outputs.Count;
    public int BusCount => Settings.Buses.Count;
    public bool PanicMuted => Runtime.PanicMuted;
    public long LimiterHits => Math.Max(_telemetryMeta.LimiterHits, 0);
    public HostStats Stats => Runtime.HostStats;
    public HostState HostState => HostBusy && Runtime.HostStats.State == HostState.Stopped ? HostState.Starting : Runtime.HostStats.State;
    public bool HostRunning => HostState is HostState.Running or HostState.Starting;

    public IReadOnlyList<AudioDeviceInfo> DiscoveredDevices => Runtime.DiscoveredDevices;
    public IReadOnlyList<AudioDeviceInfo> PhysicalDevices => Runtime.DiscoveredDevices.Where(d => !d.IsVirtualEndpoint).ToList();
    public IReadOnlyList<AudioDeviceInfo> VirtualEndpointDevices => Runtime.DiscoveredDevices.Where(d => d.IsVirtualEndpoint).ToList();
    public IReadOnlyList<AppSessionInfo> ActiveAppSessions => Runtime.ActiveAppSessions;
    public IReadOnlyList<AppLinkStatus> AppLinkStatuses => Runtime.AppLinkStatuses;
    public VaioDriverAvailability VaioAvailability => Runtime.VaioAvailability;
    public IReadOnlyList<MixSnapshotSummary> Snapshots => Runtime.Snapshots;
    public Guid? ActiveSnapshotId => Runtime.ActiveSnapshotId;
    public IReadOnlyList<EngineLogEntry> RecentLogs => Runtime.RecentLogs;
    public IReadOnlyList<ParallelPathWarning> ParallelWarnings => BusRouting.FindParallelDirectAndBusPaths(Settings);

    public ChannelDsp Channel(bool input, int i) => input ? Settings.Inputs[i] : Settings.Outputs[i];
    public static bool IsProtected(ChannelDsp c) => c.MaxGainDb < Theme.ProtectBelowDb;

    public string DisplayName(bool input, int i)
    {
        if (i < 0 || i >= (input ? InputCount : OutputCount)) return DefaultName(input, i);
        var c = Channel(input, i);
        if (!string.IsNullOrWhiteSpace(c.Name)) return c.Name;
        if (input && LinkAt(i) is { } link)
            return link.Count == 2 ? $"{link.Title} {(i == link.FirstChannel ? "L" : "R")}" : link.Title;
        return DefaultName(input, i);
    }

    public static string DefaultName(bool input, int i) => (input ? "Eingang " : "Ausgang ") + (i + 1);

    public string ChannelOwnerTitle(ChannelDsp ch)
    {
        if (ch.DeviceId is { } did)
        {
            if (Config.Devices.FirstOrDefault(d => d.Id == did) is { } dn) return dn.DisplayTitle;
            if (Config.Vaios.FirstOrDefault(v => v.Id == did) is { } vn) return vn.Name;
            if (Settings.DeviceMasters.FirstOrDefault(m => m.Id == did) is { } dm && !string.IsNullOrWhiteSpace(dm.Name)) return dm.Name;
        }
        return "";
    }

    public AudioDeviceInfo? FindDevice(string endpointId) =>
        Runtime.DiscoveredDevices.FirstOrDefault(d => string.Equals(d.Id, endpointId, StringComparison.OrdinalIgnoreCase));

    public int ActiveVaioChannels(VaioSlotConfig slot) => slot.Kind == VaioSlotKind.NxMixKernelDriver
        ? Math.Min(slot.Channels, FindDevice(slot.EndpointOrExe)?.NxMixActiveChannels ?? 2)
        : slot.Channels;

    public bool IsChannelVisible(ChannelDsp channel) => !channel.IsVacant &&
        !Config.Vaios.Any(v => v.Id == channel.DeviceId && channel.DeviceChannelIndex >= ActiveVaioChannels(v));

    // ---- Synchronisation vom Backend ----------------------------------------------------------
    readonly object _mixerCommitGate = new();
    bool _mixerInFlight;
    MixerSettings? _queuedMixer;

    static bool DevicesStructureEqual(IReadOnlyList<AudioDeviceNode> a, IReadOnlyList<AudioDeviceNode> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Id != b[i].Id || a[i].ChannelCount != b[i].ChannelCount || a[i].EngineFirstChannel != b[i].EngineFirstChannel || a[i].Enabled != b[i].Enabled)
                return false;
        return true;
    }

    static bool BusesStructureEqual(IReadOnlyList<MixBus> a, IReadOnlyList<MixBus> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i].Id != b[i].Id || a[i].Channels != b[i].Channels)
                return false;
        return true;
    }

    void ApplyIncomingSnapshot(DaemonRuntimeSnapshot snap)
    {
        var prevMix = Settings;
        var prevCfg = Config;
        var prevDevices = Runtime.DiscoveredDevices;
        bool snapListChanged = Runtime.Snapshots.Count != snap.Snapshots.Count
            || Runtime.ActiveSnapshotId != snap.ActiveSnapshotId
            || !Runtime.Snapshots.SequenceEqual(snap.Snapshots);

        bool holdLocalMixer;
        lock (_mixerCommitGate)
            holdLocalMixer = !RunInline && (_mixerInFlight || _queuedMixer != null);

        Runtime = snap;
        Config = snap.Config;
        if (!holdLocalMixer)
            Settings = Sanitize(snap.Mixer);
        if (!SameRouting(prevMix, Settings)) _patchUndo.Clear();
        HostError = snap.LastError;
        HostBusy = false;

        bool structure = !prevMix.Inputs.Select(c => (c.Id, c.DeviceId)).SequenceEqual(Settings.Inputs.Select(c => (c.Id, c.DeviceId)))
            || !prevMix.Outputs.Select(c => (c.Id, c.DeviceId)).SequenceEqual(Settings.Outputs.Select(c => (c.Id, c.DeviceId)))
            || !BusesStructureEqual(prevMix.Buses, Settings.Buses)
            || prevMix.DeviceMasters.Count != Settings.DeviceMasters.Count
            || prevMix.SampleRate != Settings.SampleRate
            || !DevicesStructureEqual(prevCfg.Devices, Config.Devices)
            || prevCfg.Vaios.Count != Config.Vaios.Count
            || prevCfg.AppLinks.Count != Config.AppLinks.Count
            || !prevDevices.Where(d => d.IsNxMixEndpoint).Select(d => (d.Id, d.NxMixActiveChannels))
                .SequenceEqual(snap.DiscoveredDevices.Where(d => d.IsNxMixEndpoint).Select(d => (d.Id, d.NxMixActiveChannels)));

        if (structure) StructureChanged?.Invoke();
        ValuesChanged?.Invoke();
        BindingsChanged?.Invoke();
        HostChanged?.Invoke();
        if (snapListChanged) SnapshotsChanged?.Invoke();
        Changed?.Invoke();
    }

    void RunBackend(Func<Task<string?>> call)
    {
        if (RunInline)
        {
            string? err = call().GetAwaiter().GetResult();
            ApplyIncomingSnapshot(_backend.Snapshot);
            if (!string.IsNullOrWhiteSpace(err)) { HostError = err; Toast?.Invoke(err, true); }
        }
        else
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    string? err = await call().ConfigureAwait(false);
                    if (!string.IsNullOrWhiteSpace(err))
                        Post(() => { HostError = err; Toast?.Invoke(err, true); HostChanged?.Invoke(); });
                }
                catch (Exception ex)
                {
                    Post(() => { HostError = ex.Message; Toast?.Invoke(ex.Message, true); HostChanged?.Invoke(); });
                }
            });
        }
    }

    void RunBackendVoid(Func<Task> call)
    {
        if (RunInline)
        {
            call().GetAwaiter().GetResult();
            ApplyIncomingSnapshot(_backend.Snapshot);
        }
        else
        {
            _ = Task.Run(async () =>
            {
                try { await call().ConfigureAwait(false); }
                catch (Exception ex) { Post(() => { HostError = ex.Message; Toast?.Invoke(ex.Message, true); HostChanged?.Invoke(); }); }
            });
        }
    }

    // ---- Bereinigung --------------------------------------------------------------------------
    public static ChannelDsp SanitizeChannel(ChannelDsp c)
    {
        double max = double.IsNaN(c.MaxGainDb) ? Theme.FaderMaxDb : Math.Clamp(c.MaxGainDb, -60.0, Theme.FaderMaxDb);
        double gain = double.IsNaN(c.GainDb) ? 0.0 : c.GainDb;
        if (gain > max) gain = max;
        if (gain < -200 && !double.IsNegativeInfinity(gain)) gain = double.NegativeInfinity;
        double trim = double.IsNaN(c.TrimDb) || double.IsInfinity(c.TrimDb) ? 0.0 : Math.Clamp(c.TrimDb, -24.0, 24.0);
        double pan = double.IsNaN(c.Pan) || double.IsInfinity(c.Pan) ? 0.0 : Math.Clamp(c.Pan, -1.0, 1.0);
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

    public static MixerSettings Sanitize(MixerSettings s)
    {
        var ins = s.Inputs.Select(SanitizeChannel).ToArray();
        var outs = s.Outputs.Select(SanitizeChannel).ToArray();
        var dms = (s.DeviceMasters ?? Array.Empty<DeviceMasterDsp>())
            .Select(d => d with
            {
                Name = d.Name ?? "",
                GainDb = double.IsNaN(d.GainDb) ? 0.0 : Math.Clamp(d.GainDb, -120.0, Theme.FaderMaxDb),
                DimDb = double.IsNaN(d.DimDb) ? -20.0 : Math.Clamp(d.DimDb, -60.0, 0.0),
            })
            .ToArray();
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
        int sr = s.SampleRate > 0 ? s.SampleRate : DefaultSampleRate;
        return (s with { SampleRate = sr, DeviceMasters = dms, Inputs = ins, Outputs = outs, MatrixDb = m }).FoldLegacyMasterIntoOutputs();
    }

    void CommitMixer(MixerSettings next, bool structural = false)
    {
        if (structural) _patchUndo.Clear();
        Settings = Sanitize(next);
        if (structural) StructureChanged?.Invoke();
        ValuesChanged?.Invoke();
        Changed?.Invoke();
        var toSend = Settings;

        if (RunInline)
        {
            RunBackend(() => _backend.ApplyMixerAsync(toSend));
            return;
        }

        lock (_mixerCommitGate)
        {
            _queuedMixer = toSend;
            if (_mixerInFlight) return;
            _mixerInFlight = true;
        }
        _ = Task.Run(DrainMixerCommitsAsync);
    }

    async Task DrainMixerCommitsAsync()
    {
        while (true)
        {
            MixerSettings? next;
            lock (_mixerCommitGate)
            {
                next = _queuedMixer;
                _queuedMixer = null;
                if (next == null)
                {
                    _mixerInFlight = false;
                    break;
                }
            }

            try
            {
                string? err = await _backend.ApplyMixerAsync(next).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(err))
                    Post(() => { HostError = err; Toast?.Invoke(err, true); HostChanged?.Invoke(); });
            }
            catch (Exception ex)
            {
                Post(() => { HostError = ex.Message; Toast?.Invoke(ex.Message, true); HostChanged?.Invoke(); });
            }
        }
    }

    // ---- Kanäle & Stereo-Kopplung (Relative Pegelabstände bleiben gewahrt!) --------------------
    public void UpdateChannel(bool input, int i, Func<ChannelDsp, ChannelDsp> f)
    {
        var list = (input ? Settings.Inputs : Settings.Outputs).ToArray();
        if (i < 0 || i >= list.Length) return;
        var before = list[i];
        var after = SanitizeChannel(f(before));
        list[i] = after;

        // Stereo-/Gruppen-Kopplung: relativer dB-Abstand bleibt erhalten, Mute/Solo folgen
        if (after.LinkGroupId is { } groupId && before.LinkGroupId == groupId)
        {
            bool gainChanged = !before.GainDb.Equals(after.GainDb);
            bool muteChanged = before.Mute != after.Mute;
            bool soloChanged = before.Solo != after.Solo;
            double oldFloor = Db.IsOff(before.GainDb) ? -72.0 : before.GainDb;
            double newFloor = Db.IsOff(after.GainDb) ? -72.0 : after.GainDb;
            double deltaDb = newFloor - oldFloor;

            if (gainChanged || muteChanged || soloChanged)
            {
                for (int k = 0; k < list.Length; k++)
                {
                    if (k == i || list[k].LinkGroupId != groupId) continue;
                    var peer = list[k];
                    if (gainChanged)
                    {
                        double peerDb = Db.IsOff(after.GainDb)
                            ? double.NegativeInfinity
                            : Math.Clamp((Db.IsOff(peer.GainDb) ? -72.0 : peer.GainDb) + deltaDb, -72.0, peer.MaxGainDb);
                        if (peerDb <= -71.5 && Db.IsOff(after.GainDb)) peerDb = double.NegativeInfinity;
                        peer = peer with { GainDb = peerDb };
                    }
                    if (muteChanged) peer = peer with { Mute = after.Mute };
                    if (soloChanged) peer = peer with { Solo = after.Solo };
                    list[k] = SanitizeChannel(peer);
                }
            }
        }

        CommitMixer(input ? Settings with { Inputs = list } : Settings with { Outputs = list });
    }

    public bool CopyChannelSettings(bool input, Guid sourceId, Guid targetId)
    {
        if (sourceId == targetId) return false;
        var list = (input ? Settings.Inputs : Settings.Outputs).ToArray();
        int sourceIndex = Array.FindIndex(list, channel => channel.Id == sourceId);
        int targetIndex = Array.FindIndex(list, channel => channel.Id == targetId);
        if (sourceIndex < 0 || targetIndex < 0) return false;

        var source = list[sourceIndex];
        var target = list[targetIndex];
        list[targetIndex] = target with
        {
            TrimDb = source.TrimDb,
            GainDb = Math.Min(source.GainDb, target.MaxGainDb),
            Pan = source.Pan,
            InvertPhase = source.InvertPhase,
            DelayMs = source.DelayMs,
            Filters = source.Filters.ToArray(),
            Correction = source.Correction,
        };
        CommitMixer(input ? Settings with { Inputs = list } : Settings with { Outputs = list });
        return true;
    }

    public void UpdateChannelById(bool input, Guid channelId, Func<ChannelDsp, ChannelDsp> f)
    {
        var list = input ? Settings.Inputs : Settings.Outputs;
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Id == channelId) { UpdateChannel(input, i, f); return; }
        }
    }

    public void TogglePairLink(bool input, int firstIndex)
    {
        var list = (input ? Settings.Inputs : Settings.Outputs).ToArray();
        if (firstIndex < 0 || firstIndex + 1 >= list.Length) return;
        bool currentlyLinked = list[firstIndex].LinkGroupId.HasValue && list[firstIndex].LinkGroupId == list[firstIndex + 1].LinkGroupId;
        Guid? nextGroup = currentlyLinked ? null : Guid.NewGuid();
        list[firstIndex] = list[firstIndex] with { LinkGroupId = nextGroup };
        list[firstIndex + 1] = list[firstIndex + 1] with { LinkGroupId = nextGroup };
        CommitMixer(input ? Settings with { Inputs = list } : Settings with { Outputs = list });
    }

    // ---- Geräte-Master (unabhängige Summen-Gain-Stufe — ändert KEINEN Einzelkanal-GainDb!) ----
    public DeviceMasterDsp? GetDeviceMaster(Guid deviceId) =>
        Settings.DeviceMasters.FirstOrDefault(d => d.Id == deviceId);

    public void RenameDeviceFader(Guid id, string name)
    {
        name = name.Trim();
        if (Config.Devices.Any(d => d.Id == id))
            ApplySystemConfig(Config with { Devices = Config.Devices.Select(d => d.Id == id ? d with { CustomTitle = name } : d).ToArray() }, reopenAudioIfNeeded: false);
        else if (Config.Vaios.Any(v => v.Id == id))
            ApplySystemConfig(Config with { Vaios = Config.Vaios.Select(v => v.Id == id ? v with { Name = name } : v).ToArray() }, reopenAudioIfNeeded: false);
        else if (Config.AppLinks.Any(a => a.Id == id))
            ApplySystemConfig(Config with { AppLinks = Config.AppLinks.Select(a => a.Id == id ? a with { DisplayName = name } : a).ToArray() }, reopenAudioIfNeeded: false);
        else
            UpdateDeviceMaster(id, d => d with { Name = name });
    }

    public void UpdateDeviceMaster(Guid deviceId, Func<DeviceMasterDsp, DeviceMasterDsp> f, string fallbackName = "", bool isInput = false)
    {
        var dms = Settings.DeviceMasters.ToList();
        int idx = dms.FindIndex(d => d.Id == deviceId);
        if (idx >= 0) dms[idx] = f(dms[idx]);
        else dms.Add(f(new DeviceMasterDsp { Id = deviceId, Name = fallbackName, IsInput = isInput }));
        CommitMixer(Settings with { DeviceMasters = dms });
    }

    public void PlayOutputTestTone(Guid channelId) => RunBackend(() => _backend.PlayOutputTestToneAsync(channelId));

    public void SetPanicMute(bool muted) => RunBackendVoid(() => _backend.SetPanicMuteAsync(muted));

    // ---- Kanäle manuell hinzufügen / löschen / verschieben ------------------------------------
    public void AddChannels(bool input, int count, string[]? names = null, Guid? deviceId = null)
    {
        if (count <= 0) return;
        var ins = Settings.Inputs.ToList();
        var outs = Settings.Outputs.ToList();
        var target = input ? ins : outs;
        for (int k = 0; k < count; k++)
        {
            target.Add(new ChannelDsp
            {
                DeviceId = deviceId,
                DeviceChannelIndex = k,
                Name = names != null && k < names.Length ? names[k] : "",
            });
        }
        CommitMixer(Settings with
        {
            Inputs = ins,
            Outputs = outs,
            MatrixDb = ResizeMatrix(Settings.MatrixDb, ins.Count, outs.Count),
        }, structural: true);
    }

    public string[] NextPairNames(bool input)
    {
        var list = input ? Settings.Inputs : Settings.Outputs;
        int n = 1;
        while (list.Any(c => c.Name == $"Stereo {n} L" || c.Name == $"Stereo {n} R")) n++;
        return new[] { $"Stereo {n} L", $"Stereo {n} R" };
    }

    public bool CanMoveChannel(bool input, int idx, int dir)
    {
        var channels = input ? Settings.Inputs : Settings.Outputs;
        int to = idx + dir;
        if (idx < 0 || idx >= channels.Count || to < 0 || to >= channels.Count) return false;
        return channels[idx].DeviceId == null && channels[to].DeviceId == null &&
            !channels[idx].IsVacant && !channels[to].IsVacant;
    }

    public void MoveChannel(bool input, int idx, int dir)
    {
        if (!CanMoveChannel(input, idx, dir)) return;
        int to = idx + dir;
        var list = (input ? Settings.Inputs : Settings.Outputs).ToArray();
        (list[idx], list[to]) = (list[to], list[idx]);
        var m = Settings.MatrixDb.Select(r => (double[])r.Clone()).ToArray();
        if (input) (m[idx], m[to]) = (m[to], m[idx]);
        else foreach (var r in m) (r[idx], r[to]) = (r[to], r[idx]);
        CommitMixer(input ? Settings with { Inputs = list, MatrixDb = m } : Settings with { Outputs = list, MatrixDb = m }, structural: true);
    }

    public void RemoveChannel(bool input, int idx)
    {
        var ins = Settings.Inputs.ToList();
        var outs = Settings.Outputs.ToList();
        var target = input ? ins : outs;
        if (idx < 0 || idx >= target.Count) return;
        var channel = target[idx];
        if (channel.DeviceId is Guid id)
        {
            if (Config.AppLinks.FirstOrDefault(a => a.Id == id) is { } app) { RemoveLink(app.FirstChannel); return; }
            if (Config.Devices.Any(d => d.Id == id)) { RemoveHardwareDevice(id); return; }
            if (Config.Vaios.Any(v => v.Id == id)) { RemoveVaioSlot(id); return; }
        }

        bool keepSlot = target.Skip(idx + 1).Any(c => c.DeviceId != null);
        if (keepSlot) target[idx] = new ChannelDsp { DeviceChannelIndex = idx, AutoCreatedForDevice = true };
        else target.RemoveAt(idx);
        var m = Settings.MatrixDb.Select(r => (double[])r.Clone()).ToList();
        if (input && idx < m.Count)
        {
            if (keepSlot) Array.Fill(m[idx], double.NegativeInfinity);
            else m.RemoveAt(idx);
        }
        else if (!input)
        {
            for (int i = 0; i < m.Count; i++)
            {
                if (keepSlot) m[i][idx] = double.NegativeInfinity;
                else m[i] = m[i].Where((_, j) => j != idx).ToArray();
            }
        }

        CommitMixer(Settings with
        {
            Inputs = ins,
            Outputs = outs,
            MatrixDb = m.ToArray(),
            BusRoutes = Settings.BusRoutes.Where(r => r.Source.Id != channel.Id && r.Target.Id != channel.Id).ToArray(),
        }, structural: true);
    }

    static double[][] ResizeMatrix(double[][] old, int ni, int no)
    {
        var m = new double[ni][];
        for (int i = 0; i < ni; i++)
        {
            m[i] = new double[no];
            for (int j = 0; j < no; j++)
                m[i][j] = i < old.Length && j < old[i].Length ? old[i][j] : double.NegativeInfinity;
        }
        return m;
    }

    // ---- Matrix & Mix-Busse (OHNE destruktives Löschen direkter Matrix-Routen!) ---------------
    readonly List<MixerSettings> _patchUndo = new();

    static bool SameRouting(MixerSettings a, MixerSettings b) =>
        a.Inputs.Select(c => c.Id).SequenceEqual(b.Inputs.Select(c => c.Id)) &&
        a.Outputs.Select(c => c.Id).SequenceEqual(b.Outputs.Select(c => c.Id)) &&
        BusesStructureEqual(a.Buses, b.Buses) &&
        a.MatrixDb.Length == b.MatrixDb.Length &&
        a.MatrixDb.Zip(b.MatrixDb).All(rows => rows.First.SequenceEqual(rows.Second)) &&
        a.BusRoutes.SequenceEqual(b.BusRoutes);

    void CommitPatch(MixerSettings next, bool recordUndo = true)
    {
        next = Sanitize(next);
        if (SameRouting(Settings, next)) return;
        if (recordUndo)
        {
            if (_patchUndo.Count == 32) _patchUndo.RemoveAt(0);
            _patchUndo.Add(Settings);
        }
        CommitMixer(next);
    }

    public bool UndoPatch()
    {
        if (_patchUndo.Count == 0) return false;
        var previous = _patchUndo[^1];
        _patchUndo.RemoveAt(_patchUndo.Count - 1);
        CommitMixer(Settings with { MatrixDb = previous.MatrixDb, BusRoutes = previous.BusRoutes });
        return true;
    }

    public void SetMatrix(int i, int o, double db, bool recordUndo = true)
    {
        if (i < 0 || i >= InputCount || o < 0 || o >= OutputCount) return;
        var m = Settings.MatrixDb.Select(r => (double[])r.Clone()).ToArray();
        m[i][o] = db;
        CommitPatch(Settings with { MatrixDb = m }, recordUndo);
    }

    public void PatchDiagonal1To1(int startIn = 0, int startOut = 0, int? count = null, double gainDb = 0.0)
    {
        int n = count ?? Math.Min(Math.Max(0, InputCount - startIn), Math.Max(0, OutputCount - startOut));
        if (n <= 0) return;
        var m = Settings.MatrixDb.Select(r => (double[])r.Clone()).ToArray();
        for (int k = 0; k < n; k++)
        {
            int i = startIn + k, o = startOut + k;
            if (i >= 0 && i < InputCount && o >= 0 && o < OutputCount)
                m[i][o] = gainDb;
        }
        CommitPatch(Settings with { MatrixDb = m });
    }

    public void PatchStereoPairs(double gainDb = 0.0)
    {
        if (InputCount == 0 || OutputCount < 2) return;
        var m = Settings.MatrixDb.Select(r => (double[])r.Clone()).ToArray();
        for (int i = 0; i < InputCount; i++)
        {
            int targetOut = i % 2;
            if (targetOut < OutputCount)
                m[i][targetOut] = gainDb;
        }
        CommitPatch(Settings with { MatrixDb = m });
    }

    public void ClearAllDirectMatrix()
    {
        var m = new double[InputCount][];
        for (int i = 0; i < InputCount; i++)
        {
            m[i] = new double[OutputCount];
            Array.Fill(m[i], double.NegativeInfinity);
        }
        CommitPatch(Settings with { MatrixDb = m });
    }

    public Guid AddBus(int channels = 2, string? name = null)
    {
        int ch = Math.Clamp(channels, 1, 8);
        int nr = Settings.Buses.Count + 1;
        while (Settings.Buses.Any(b => b.Name.Equals($"Bus {nr}", StringComparison.OrdinalIgnoreCase))) nr++;
        var bus = new MixBus
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? $"Bus {nr}" : name.Trim(),
            Channels = ch,
            GainDb = 0.0,
        };
        CommitMixer(Settings with { Buses = Settings.Buses.Append(bus).ToArray() }, structural: true);
        return bus.Id;
    }

    public void UpdateBus(Guid busId, Func<MixBus, MixBus> f)
    {
        var list = Settings.Buses.ToArray();
        int idx = Array.FindIndex(list, b => b.Id == busId);
        if (idx < 0) return;
        list[idx] = f(list[idx]);
        CommitMixer(Settings with { Buses = list });
    }

    public void RemoveBus(Guid busId)
    {
        var buses = Settings.Buses.Where(b => b.Id != busId).ToArray();
        var routes = Settings.BusRoutes.Where(r => r.Source.Id != busId && r.Target.Id != busId).ToArray();
        CommitMixer(Settings with { Buses = buses, BusRoutes = routes }, structural: true);
    }

    public BusRoute? FindBusRoute(RoutePort source, RoutePort target) =>
        Settings.BusRoutes.FirstOrDefault(r => r.Source == source && r.Target == target);

    /// <summary>Fügt eine Bus-Route hinzu oder aktualisiert ihren Pegel — OHNE bestehende direkte Matrix-Kreuzpunkte zu löschen!</summary>
    public string? SetOrAddBusRoute(RoutePort source, RoutePort target, double gainDb = 0.0, bool recordUndo = true)
    {
        var old = FindBusRoute(source, target);
        if (Db.IsOff(gainDb) ? old == null : old?.GainDb == Math.Clamp(gainDb, Theme.MatrixMinDb, Theme.MatrixMaxDb)) return null;
        var list = Settings.BusRoutes.Where(r => !(r.Source == source && r.Target == target)).ToList();
        if (!Db.IsOff(gainDb))
            list.Add(new BusRoute(source, target, Math.Clamp(gainDb, Theme.MatrixMinDb, Theme.MatrixMaxDb)));

        var candidate = Settings with { BusRoutes = list };
        try
        {
            if (candidate.Buses.Sum(b => b.Channels) > DaemonEngineController.PoolMaxBuses)
                return $"Maximale Bus-Kanal-Kapazität ({DaemonEngineController.PoolMaxBuses}) erreicht.";
            _ = BusRouting.Compile(candidate);
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
        CommitPatch(candidate, recordUndo);
        return null;
    }

    public string? AddBusRoute(BusRoute route) => SetOrAddBusRoute(route.Source, route.Target, route.GainDb);

    public void RemoveBusRoute(BusRoute route)
    {
        var routes = Settings.BusRoutes.Where(r => !(r.Source == route.Source && r.Target == route.Target)).ToArray();
        CommitPatch(Settings with { BusRoutes = routes });
    }

    // ---- UI-Decks (%APPDATA%\NX-MIX\ui-decks.json — 100 % referenzbasiert, keine Audio-Kopie!) -
    void SaveDecks(UiDeckCollection next)
    {
        Decks = next;
        UiDeckStore.Save(next);
        DecksChanged?.Invoke();
    }

    public UiDeckDefinition? ActiveCustomDeck =>
        Decks.ActiveDeckId is { } id ? Decks.Decks.FirstOrDefault(d => d.Id == id) : null;

    public void SelectDeck(Guid? deckId) =>
        SaveDecks(Decks with { ActiveDeckId = deckId });

    public bool IsMainDeviceExpanded(Guid deviceId) =>
        Decks.ExpandedMainDeviceIds.Contains(deviceId);

    public void ToggleMainDeviceExpanded(Guid deviceId)
    {
        var set = Decks.ExpandedMainDeviceIds.ToList();
        if (!set.Remove(deviceId)) set.Add(deviceId);
        SaveDecks(Decks with { ExpandedMainDeviceIds = set });
    }

    public UiDeckDefinition CreateDeck(string? name = null)
    {
        int nr = Decks.Decks.Count + 1;
        var deck = new UiDeckDefinition
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? $"Deck {nr}" : name.Trim(),
            ColorHex = (nr % 4) switch { 1 => "#3B82F6", 2 => "#10B981", 3 => "#F59E0B", _ => "#A855F7" },
            VisibleInTopBar = true,
            Density = DeckDensity.Detailed,
            Items = Array.Empty<DeckSlotItem>(),
        };
        SaveDecks(Decks with { Decks = Decks.Decks.Append(deck).ToArray(), ActiveDeckId = deck.Id });
        return deck;
    }

    public UiDeckDefinition? DuplicateDeck(Guid deckId)
    {
        var src = Decks.Decks.FirstOrDefault(d => d.Id == deckId);
        if (src == null) return null;
        var copy = src with
        {
            Id = Guid.NewGuid(),
            Name = src.Name + " (Kopie)",
            Items = src.Items.Select(it => it with { ItemId = Guid.NewGuid() }).ToArray(),
        };
        SaveDecks(Decks with { Decks = Decks.Decks.Append(copy).ToArray(), ActiveDeckId = copy.Id });
        return copy;
    }

    public void UpdateDeck(Guid deckId, Func<UiDeckDefinition, UiDeckDefinition> f)
    {
        var list = Decks.Decks.ToArray();
        int idx = Array.FindIndex(list, d => d.Id == deckId);
        if (idx < 0) return;
        list[idx] = f(list[idx]);
        SaveDecks(Decks with { Decks = list });
    }

    public void DeleteDeck(Guid deckId)
    {
        var list = Decks.Decks.Where(d => d.Id != deckId).ToArray();
        Guid? active = Decks.ActiveDeckId == deckId ? null : Decks.ActiveDeckId;
        SaveDecks(Decks with { Decks = list, ActiveDeckId = active });
    }

    public void AddDeckSlot(Guid deckId, DeckItemKind kind, Guid targetId, string? customLabel = null)
    {
        UpdateDeck(deckId, d => d with
        {
            Items = d.Items.Append(new DeckSlotItem
            {
                ItemId = Guid.NewGuid(),
                Kind = kind,
                TargetId = targetId,
                CustomLabel = customLabel,
            }).ToArray(),
        });
    }

    public void MoveDeck(Guid deckId, int insertion)
    {
        var decks = Decks.Decks.ToList();
        int index = decks.FindIndex(d => d.Id == deckId);
        if (index < 0) return;
        int target = Math.Clamp(insertion - (insertion > index ? 1 : 0), 0, decks.Count - 1);
        if (target == index) return;
        var deck = decks[index];
        decks.RemoveAt(index);
        decks.Insert(target, deck);
        SaveDecks(Decks with { Decks = decks });
    }

    public void RemoveDeckSlot(Guid deckId, Guid itemId)
    {
        UpdateDeck(deckId, d =>
        {
            var removed = d.Items.FirstOrDefault(i => i.ItemId == itemId);
            return d with
            {
                Items = d.Items.Where(i => i.ItemId != itemId)
                    .Select(i => removed?.FaderPairId is { } pair && i.FaderPairId == pair ? i with { FaderPairId = null } : i)
                    .ToArray(),
            };
        });
    }

    public void MoveDeckSlot(Guid deckId, Guid itemId, int insertion)
    {
        UpdateDeck(deckId, d =>
        {
            int index = d.Items.ToList().FindIndex(i => i.ItemId == itemId);
            if (index < 0) return d;
            int first = index, last = index;
            if (d.Items[index].FaderPairId is { } pair)
            {
                if (first > 0 && d.Items[first - 1].FaderPairId == pair) first--;
                if (last + 1 < d.Items.Count && d.Items[last + 1].FaderPairId == pair) last++;
            }

            insertion = Math.Clamp(insertion, 0, d.Items.Count);
            if (insertion >= first && insertion <= last + 1) return d;
            if (insertion > 0 && insertion < d.Items.Count &&
                d.Items[insertion].FaderPairId is { } other && d.Items[insertion - 1].FaderPairId == other)
                insertion += insertion > last ? 1 : -1;

            var items = d.Items.ToList();
            var moving = items.GetRange(first, last - first + 1);
            items.RemoveRange(first, moving.Count);
            items.InsertRange(insertion > last ? insertion - moving.Count : insertion, moving);
            return d with { Items = items };
        });
    }

    public void ToggleDeckFaderPair(Guid deckId, Guid itemId)
    {
        UpdateDeck(deckId, d =>
        {
            var items = d.Items.ToArray();
            int index = Array.FindIndex(items, i => i.ItemId == itemId);
            if (index < 0) return d;
            var first = items[index];
            if (first.FaderPairId is { } existing)
            {
                for (int i = 0; i < items.Length; i++)
                    if (items[i].FaderPairId == existing) items[i] = items[i] with { FaderPairId = null };
            }
            else
            {
                if (index + 1 >= items.Length) return d;
                var second = items[index + 1];
                if (first.Kind == DeckItemKind.Spacer || second.Kind == DeckItemKind.Spacer ||
                    first.TargetId == second.TargetId || second.FaderPairId.HasValue) return d;
                var pair = Guid.NewGuid();
                items[index] = first with { FaderPairId = pair };
                items[index + 1] = second with { FaderPairId = pair };
            }
            return d with { Items = items };
        });
    }

    public bool SetDeckFaderPairGain(DeckSlotItem source, DeckSlotItem peer, double requestedGain)
    {
        var settings = Settings;
        var sourceGain = FindGain(source);
        var peerGain = FindGain(peer);
        if (sourceGain == null || peerGain == null) return false;

        double next = ClampGain(requestedGain, sourceGain.Value.Max);
        double delta = Floor(next) - Floor(sourceGain.Value.Gain);
        double linked = Db.IsOff(next) ? next : ClampGain(Floor(peerGain.Value.Gain) + delta, peerGain.Value.Max);
        ApplyGain(source, next);
        ApplyGain(peer, linked);
        CommitMixer(settings);
        return true;

        static double Floor(double db) => Db.IsOff(db) ? -72 : db;
        static double ClampGain(double db, double max) => Db.IsOff(db) ? double.NegativeInfinity : Math.Clamp(db, -120, max);

        (double Gain, double Max)? FindGain(DeckSlotItem slot) => slot.Kind switch
        {
            DeckItemKind.InputChannel => ChannelGain(settings.Inputs, slot.TargetId),
            DeckItemKind.OutputChannel => ChannelGain(settings.Outputs, slot.TargetId),
            DeckItemKind.DeviceMaster or DeckItemKind.VaioSlot =>
                settings.DeviceMasters.FirstOrDefault(d => d.Id == slot.TargetId) is { } device ? (device.GainDb, Theme.FaderMaxDb) : null,
            DeckItemKind.MixBus => settings.Buses.FirstOrDefault(b => b.Id == slot.TargetId) is { } bus ? (bus.GainDb, Theme.FaderMaxDb) : null,
            _ => null,
        };

        static (double Gain, double Max)? ChannelGain(IReadOnlyList<ChannelDsp> channels, Guid id) =>
            channels.FirstOrDefault(c => c.Id == id) is { } channel ? (channel.GainDb, channel.MaxGainDb) : null;

        void ApplyGain(DeckSlotItem slot, double db)
        {
            switch (slot.Kind)
            {
                case DeckItemKind.InputChannel:
                    settings = settings with { Inputs = SetChannelGain(settings.Inputs, slot.TargetId, db) };
                    break;
                case DeckItemKind.OutputChannel:
                    settings = settings with { Outputs = SetChannelGain(settings.Outputs, slot.TargetId, db) };
                    break;
                case DeckItemKind.DeviceMaster:
                case DeckItemKind.VaioSlot:
                    settings = settings with { DeviceMasters = settings.DeviceMasters.Select(d => d.Id == slot.TargetId ? d with { GainDb = db } : d).ToArray() };
                    break;
                case DeckItemKind.MixBus:
                    settings = settings with { Buses = settings.Buses.Select(b => b.Id == slot.TargetId ? b with { GainDb = db } : b).ToArray() };
                    break;
            }
        }

        static ChannelDsp[] SetChannelGain(IReadOnlyList<ChannelDsp> channels, Guid id, double db) =>
            channels.Select(c => c.Id == id ? c with { GainDb = db } : c).ToArray();
    }

    public void RenameDeckSlot(Guid deckId, Guid itemId, string name) =>
        UpdateDeck(deckId, d => d with
        {
            Items = d.Items.Select(i => i.ItemId == itemId
                ? i with { CustomLabel = string.IsNullOrWhiteSpace(name) ? null : name.Trim() }
                : i).ToArray(),
        });

    public void ToggleDeckSlotExpanded(Guid deckId, Guid itemId)
    {
        UpdateDeck(deckId, d => d with
        {
            Items = d.Items.Select(i => i.ItemId == itemId ? i with { ExpandedInline = !i.ExpandedInline } : i).ToArray(),
        });
    }

    // ---- SystemConfig (Physische Geräte, VAIOs, App-Links, Takt, API) -------------------------
    public static ConfigImpactLevel ClassifyChange(SystemConfig current, SystemConfig next)
    {
        if (current.SampleRate != next.SampleRate ||
            current.BlockFrames != next.BlockFrames ||
            current.PreferredMasterDeviceId != next.PreferredMasterDeviceId ||
            current.DefaultWasapiExclusive != next.DefaultWasapiExclusive ||
            current.AllowOfflineAuxDevices != next.AllowOfflineAuxDevices ||
            current.AppLinksEnabled != next.AppLinksEnabled ||
            current.ParkingDeviceId != next.ParkingDeviceId ||
            !SequenceEqual(current.Devices, next.Devices) ||
            !SequenceEqual(current.Vaios, next.Vaios) ||
            !SequenceEqual(current.AppLinks, next.AppLinks))
        {
            return ConfigImpactLevel.StreamReopen;
        }
        return ConfigImpactLevel.DaemonSetting;
    }

    static bool SequenceEqual<T>(IReadOnlyList<T> a, IReadOnlyList<T> b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (!EqualityComparer<T>.Default.Equals(a[i], b[i])) return false;
        return true;
    }

    public void ApplySystemConfig(SystemConfig next, bool reopenAudioIfNeeded = true)
    {
        Config = next;
        BindingsChanged?.Invoke();
        StructureChanged?.Invoke();
        Changed?.Invoke();
        RunBackend(() => _backend.ApplySystemConfigAsync(next, reopenAudioIfNeeded));
    }

    public int NextFreeEngineChannel(DeviceFlow flow, int count = 2)
    {
        var channels = flow == DeviceFlow.Capture ? Settings.Inputs : Settings.Outputs;
        var occupied = Config.Devices.Where(d => d.Flow == flow).Select(d => (First: d.EngineFirstChannel, Count: d.ChannelCount))
            .Concat(Config.Vaios.Where(v => v.Flow == flow).Select(v => (First: v.EngineFirstChannel, Count: v.Channels)));
        if (flow == DeviceFlow.Capture)
            occupied = occupied.Concat(Config.AppLinks.Select(a => (First: a.FirstChannel, Count: a.Count)));
        var ranges = occupied.ToArray();
        int first = 0;
        int free = 0;
        for (int i = 0; free < count; i++)
        {
            if ((i < channels.Count && !channels[i].IsVacant) || ranges.Any(r => i >= r.First && i - r.First < r.Count))
            {
                first = i + 1;
                free = 0;
            }
            else free++;
        }
        return first;
    }

    public AudioDeviceNode AddHardwareDevice(AudioDeviceInfo info, bool loopback = false, int? channels = null)
    {
        var flow = loopback ? DeviceFlow.Capture : info.Flow;
        int count = Math.Clamp(channels ?? Math.Max(1, info.Channels), 1, 32);
        if (Config.Devices.FirstOrDefault(d => d.Flow == flow && d.Backend == info.Backend &&
            d.Loopback == loopback && d.DeviceFirstChannel == 0 &&
            string.Equals(d.EndpointId, info.Id, StringComparison.OrdinalIgnoreCase)) is { } existing)
        {
            if (!existing.Enabled) UpdateHardwareDevice(existing.Id, d => d with { Enabled = true });
            return existing with { Enabled = true };
        }
        int first = NextFreeEngineChannel(flow, count);
        var node = new AudioDeviceNode
        {
            Id = Guid.NewGuid(),
            EndpointId = info.Id,
            FriendlyName = info.Name,
            CustomTitle = info.Name,
            Flow = flow,
            Backend = info.Backend,
            Loopback = loopback,
            DeviceFirstChannel = 0,
            EngineFirstChannel = first,
            ChannelCount = count,
            Enabled = true,
            IsVirtualCable = info.IsVirtualEndpoint,
        };
        ApplySystemConfig(Config with { Devices = Config.Devices.Append(node).ToArray() });
        return node;
    }

    public void UpdateHardwareDevice(Guid nodeId, Func<AudioDeviceNode, AudioDeviceNode> f)
    {
        var list = Config.Devices.ToArray();
        int idx = Array.FindIndex(list, d => d.Id == nodeId);
        if (idx < 0) return;
        list[idx] = f(list[idx]);
        ApplySystemConfig(Config with { Devices = list });
    }

    public void RemoveHardwareDevice(Guid nodeId) =>
        ApplySystemConfig(Config with { Devices = Config.Devices.Where(d => d.Id != nodeId).ToArray() });

    public void ConfigureVbCableInput()
    {
        if (Config.VbCableInputConfigured) return;
        var captures = DiscoveredDevices.Where(d => d.IsVbCable && d.Flow == DeviceFlow.Capture && d.Channels > 0).Take(2).ToArray();
        if (captures.Length != 1) return;
        var capture = captures[0];
        var next = Config with { VbCableInputConfigured = true };
        bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        if (Config.Devices.Any(d => SameId(d.EndpointId, capture.Id)) ||
            Config.Vaios.Any(v => SameId(v.EndpointOrExe, capture.Id)))
        {
            ApplySystemConfig(next, reopenAudioIfNeeded: false);
            return;
        }

        var playbackIds = DiscoveredDevices.Where(d => d.IsVbCable && d.Flow == DeviceFlow.Render)
            .Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var loopbackSlots = Config.Vaios.Where(v => v.Loopback && playbackIds.Contains(v.EndpointOrExe)).ToArray();
        var loopbackDevices = Config.Devices.Where(d => d.Loopback && playbackIds.Contains(d.EndpointId)).ToArray();
        // Bei mehreren oder größeren Altzuordnungen bleibt die Kanalbelegung erhalten.
        if (loopbackSlots.Length + loopbackDevices.Length > 1) return;
        if (loopbackSlots is [var slot])
        {
            if (slot.Channels > capture.Channels) return;
            next = next with { Vaios = Config.Vaios.Select(v => v.Id == slot.Id
                ? v with { EndpointOrExe = capture.Id, Flow = DeviceFlow.Capture, Loopback = false }
                : v).ToArray() };
        }
        else if (loopbackDevices is [var device])
        {
            if (device.DeviceFirstChannel + device.ChannelCount > capture.Channels) return;
            next = next with { Devices = Config.Devices.Select(d => d.Id == device.Id
                ? d with { EndpointId = capture.Id, FriendlyName = capture.Name, Flow = DeviceFlow.Capture, Loopback = false, ExclusiveOverride = false }
                : d).ToArray() };
        }
        else
        {
            var input = CreateVirtualCableSlot(capture, "VB-CABLE", loopback: false);
            if (input.EngineFirstChannel + input.Channels > DaemonEngineController.PoolMaxInputs) return;
            next = next with { Vaios = Config.Vaios.Append(input).ToArray() };
        }
        ApplySystemConfig(next);
    }

    public VaioSlotConfig AddVirtualCableSlot(AudioDeviceInfo endpoint, string? customName = null, bool loopback = false)
    {
        var slot = CreateVirtualCableSlot(endpoint, customName, loopback);
        ApplySystemConfig(Config with { Vaios = Config.Vaios.Append(slot).ToArray() });
        return slot;
    }

    private VaioSlotConfig CreateVirtualCableSlot(AudioDeviceInfo endpoint, string? customName, bool loopback)
    {
        bool native = endpoint.IsNxMixEndpoint && (loopback || endpoint.NativeOutputPort > 0);
        var flow = loopback ? DeviceFlow.Capture : native ? DeviceFlow.Render : endpoint.Flow;
        int count = native ? AudioDeviceInfo.NxMixMaxChannels :
            loopback ? Math.Clamp(endpoint.Channels, 1, 2) : Math.Clamp(Math.Max(endpoint.IsVbCable ? 1 : 2, endpoint.Channels), 1, 16);
        int first = NextFreeEngineChannel(flow, count);
        if (loopback && !native && string.IsNullOrWhiteSpace(customName))
        {
            int number = 1;
            while (Config.Vaios.Any(v => string.Equals(v.Name, $"NX-MIX In {number}", StringComparison.OrdinalIgnoreCase)))
                number++;
            customName = $"NX-MIX In {number}";
        }
        return new VaioSlotConfig
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(customName) ? endpoint.Name : customName.Trim(),
            Kind = native ? VaioSlotKind.NxMixKernelDriver : VaioSlotKind.ExternalDriverEndpoint,
            Flow = flow,
            Loopback = loopback,
            Channels = count,
            EngineFirstChannel = first,
            EndpointOrExe = endpoint.Id,
            Enabled = true,
        };
    }

    public void UpdateVaioSlot(Guid slotId, Func<VaioSlotConfig, VaioSlotConfig> f)
    {
        var list = Config.Vaios.ToArray();
        int idx = Array.FindIndex(list, v => v.Id == slotId);
        if (idx < 0) return;
        list[idx] = f(list[idx]);
        ApplySystemConfig(Config with { Vaios = list });
    }

    public void RemoveVaioSlot(Guid slotId) =>
        ApplySystemConfig(Config with { Vaios = Config.Vaios.Where(v => v.Id != slotId).ToArray() });

    public void ShowAsioControlPanel(string endpointId) =>
        RunBackend(() => _backend.ShowAsioControlPanelAsync(endpointId));

    public void RefreshDevices(bool configureVbCable = false) =>
        RunBackendVoid(async () =>
        {
            await _backend.RefreshDevicesAsync().ConfigureAwait(false);
            if (configureVbCable)
                Post(() =>
                {
                    ApplyIncomingSnapshot(_backend.Snapshot);
                    ConfigureVbCableInput();
                });
        });

    public void StartAudio()
    {
        HostBusy = true;
        HostError = null;
        HostChanged?.Invoke();
        RunBackendVoid(() => _backend.StartAudioAsync());
    }

    public void StopAudio()
    {
        HostBusy = true;
        HostChanged?.Invoke();
        RunBackendVoid(() => _backend.StopAudioAsync());
    }

    public void RestoreOrphanedAppRoutes(Action<int>? onDone = null)
    {
        if (RunInline)
        {
            int n = _backend.RestoreOrphanedAppRoutesAsync().GetAwaiter().GetResult();
            onDone?.Invoke(n);
            return;
        }
        _ = Task.Run(async () =>
        {
            int n = await _backend.RestoreOrphanedAppRoutesAsync().ConfigureAwait(false);
            Post(() => onDone?.Invoke(n));
        });
    }

    // ---- App-Links (für Virtual I/O & AppLinkPicker) ------------------------------------------
    public IReadOnlyList<AppLink> AppLinks =>
        Config.AppLinks.Where(a => a.Enabled).Select(a => new AppLink(a.ExeName, a.FirstChannel, a.Count, a.DisplayName)).ToList();

    public bool AppLinksEnabled => Config.AppLinksEnabled;
    public string? ParkingDeviceId => Config.ParkingDeviceId;
    public string? ParkingDeviceName => Config.ParkingDeviceName;

    public AppLink? LinkAt(int input) => AppLinks.FirstOrDefault(l => l.Covers(input));
    public AppLink? LinkStartingAt(int first) => AppLinks.FirstOrDefault(l => l.FirstChannel == first);

    public string? AppLinksUnsupportedReason() =>
        VaioAvailability.AppLoopbackSupported ? null : (VaioAvailability.AppLoopbackMessage ?? "ProcessLoopback wird von Windows nicht unterstützt.");

    public void SetAppLinksEnabled(bool enabled) =>
        ApplySystemConfig(Config with { AppLinksEnabled = enabled });

    public void SetParking(string? endpointId, string? friendlyName = null)
    {
        var name = friendlyName ?? (endpointId != null ? FindDevice(endpointId)?.Name : null);
        ApplySystemConfig(Config with { ParkingDeviceId = endpointId, ParkingDeviceName = name });
    }

    public (int First, int Count) SuggestSpan(int clickedInput)
    {
        if (LinkAt(clickedInput) is { } existing) return (existing.FirstChannel, existing.Count);
        if (IsNamedPair(clickedInput))
        {
            int first = clickedInput > 0 && Settings.Inputs[clickedInput].Name.EndsWith(" R", StringComparison.Ordinal) ? clickedInput - 1 : clickedInput;
            return (first, 2);
        }
        int count = clickedInput + 1 < InputCount && LinkAt(clickedInput + 1) == null ? 2 : 1;
        return (clickedInput, Math.Max(1, count));
    }

    public bool IsNamedPair(int input)
    {
        if (input < 0 || input >= InputCount) return false;
        string n = Settings.Inputs[input].Name;
        if (n.EndsWith(" L", StringComparison.Ordinal) && input + 1 < InputCount && Settings.Inputs[input + 1].Name == n[..^2] + " R") return true;
        if (n.EndsWith(" R", StringComparison.Ordinal) && input - 1 >= 0 && Settings.Inputs[input - 1].Name == n[..^2] + " L") return true;
        return false;
    }

    public string? LinkConflict(int first, int count, string? exeName, int? ignoreFirst = null)
    {
        if (first < 0 || count < 1) return "Ungültiger Eingangsbereich.";
        foreach (var d in Config.Devices.Where(d => d.Enabled && d.Flow == DeviceFlow.Capture))
        {
            if (first < d.EngineFirstChannel + d.ChannelCount && d.EngineFirstChannel < first + count)
                return $"Eingang {first + 1} ist bereits dem Hardware-Gerät „{d.DisplayTitle}“ zugeordnet.";
        }
        foreach (var v in Config.Vaios.Where(v => v.Enabled && v.Flow == DeviceFlow.Capture))
        {
            if (first < v.EngineFirstChannel + v.Channels && v.EngineFirstChannel < first + count)
                return $"Eingang {first + 1} ist bereits dem Virtual-I/O-Slot „{v.Name}“ zugeordnet.";
        }
        foreach (var l in Config.AppLinks)
        {
            if (ignoreFirst.HasValue && l.FirstChannel == ignoreFirst.Value) continue;
            if (first < l.EndChannel && l.FirstChannel < first + count)
                return $"Eingang {first + 1} wird bereits von „{l.Title}“ belegt.";
            if (exeName != null && string.Equals(l.ExeName, exeName, StringComparison.OrdinalIgnoreCase))
                return $"„{l.Title}“ ist bereits an Eingang {l.FirstChannel + 1} gebunden.";
        }
        return null;
    }

    public string? SetLink(int firstChannel, int count, string rawExe, string? displayName = null) =>
        SetLink(firstChannel, rawExe, count, displayName);

    public string? SetLink(int firstChannel, string rawExe, int count = 2, string? displayName = null)
    {
        var exe = AppLinkRules.NormalizeExe(rawExe, out var err);
        if (exe == null) return err;
        int chCount = Math.Clamp(count, 1, 2);
        var conflict = LinkConflict(firstChannel, chCount, exe, firstChannel);
        if (conflict != null) return conflict;

        var existing = Config.AppLinks.FirstOrDefault(l => l.FirstChannel == firstChannel &&
            string.Equals(l.ExeName, exe, StringComparison.OrdinalIgnoreCase));
        var list = Config.AppLinks.Where(l => l.FirstChannel != firstChannel && !string.Equals(l.ExeName, exe, StringComparison.OrdinalIgnoreCase)).ToList();
        list.Add(new AppLinkEntry
        {
            Id = existing?.Id ?? Guid.NewGuid(),
            ExeName = exe,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim(),
            FirstChannel = firstChannel,
            Count = chCount,
            Enabled = true,
        });
        ApplySystemConfig(Config with { AppLinks = list.OrderBy(a => a.FirstChannel).ToArray() });
        return null;
    }

    public string? BindNewAppQuick(string rawExe, int count = 2, string? displayName = null)
    {
        int first = NextFreeEngineChannel(DeviceFlow.Capture, Math.Clamp(count, 1, 2));
        return SetLink(first, rawExe, count, displayName);
    }

    public bool RemoveLink(int firstChannel)
    {
        var next = Config.AppLinks.Where(a => a.FirstChannel != firstChannel).ToArray();
        if (next.Length == Config.AppLinks.Count) return false;
        ApplySystemConfig(Config with { AppLinks = next });
        return true;
    }

    public void LoadAppPrograms(Action<List<AppProgram>, string?> done)
    {
        if (RunInline)
        {
            _backend.RefreshDevicesAsync().GetAwaiter().GetResult();
            ApplyIncomingSnapshot(_backend.Snapshot);
            done(AppLinkRules.Group(ActiveAppSessions), null);
            return;
        }
        _ = Task.Run(async () =>
        {
            string? err = null;
            try { await _backend.RefreshDevicesAsync().ConfigureAwait(false); }
            catch (Exception ex) { err = ex.Message; }
            Post(() => done(AppLinkRules.Group(_backend.Snapshot.ActiveAppSessions), err));
        });
    }

    public LinkView ViewOf(AppLink link)
    {
        if (!Config.AppLinksEnabled) return new LinkView(LinkUi.Off, null, 0);
        if (AppLinksUnsupportedReason() is { } un) return new LinkView(LinkUi.Failed, un, 0);
        if (!HostRunning && (Runtime.AudioStartBlockedReason ?? (HostState == HostState.Faulted ? HostError : null)) is { } reason)
            return new LinkView(LinkUi.AudioBlocked, reason, 0);
        if (!HostRunning) return new LinkView(LinkUi.Idle, null, 0);
        var st = AppLinkStatuses.FirstOrDefault(s => string.Equals(s.ExeName, link.ExeName, StringComparison.OrdinalIgnoreCase));
        if (st == null) return new LinkView(LinkUi.Waiting, null, 0);
        return st.State switch
        {
            AppLinkState.Linked => new LinkView(LinkUi.Linked, st.Message, st.ProcessCount),
            AppLinkState.Capturing => new LinkView(LinkUi.Capturing, st.Message, st.ProcessCount),
            AppLinkState.MixedWithoutRedirect => new LinkView(LinkUi.MixedWithoutRedirect, st.Message, st.ProcessCount),
            AppLinkState.Failed => new LinkView(LinkUi.Failed, st.Message, st.ProcessCount),
            _ => new LinkView(LinkUi.Waiting, st.Message, st.ProcessCount),
        };
    }

    // ---- Spektral-Tap (für Outputs / DSP & Kanal-Spektrum) ------------------------------------
    int _subSpecMode;
    int _subSpecChannel = -1;
    int _subSpecWindowSize;
    Task _spectrumRequest = Task.CompletedTask;

    public void SubscribeSpectrum(int mode, int channelIndex, int windowSize = 0)
    {
        if (_subSpecMode == mode && _subSpecChannel == channelIndex && _subSpecWindowSize == windowSize) return;
        _subSpecMode = mode;
        _subSpecChannel = channelIndex;
        _subSpecWindowSize = windowSize;
        QueueSpectrumRequest(mode, channelIndex, windowSize);
    }

    void QueueSpectrumRequest(int mode, int channelIndex, int windowSize)
    {
        if (RunInline)
        {
            _backend.SubscribeSpectrumAsync(mode, channelIndex, windowSize).GetAwaiter().GetResult();
            return;
        }
        var previous = _spectrumRequest;
        _spectrumRequest = Task.Run(async () =>
        {
            await previous.ConfigureAwait(false);
            try { await _backend.SubscribeSpectrumAsync(mode, channelIndex, windowSize).ConfigureAwait(false); }
            catch (Exception ex) { Post(() => Toast?.Invoke(ex.Message, true)); }
        });
    }

    // ---- Mischpult-Snapshots (Kategorie 3 — berühren NIEMALS SystemConfig!) -------------------
    public void SaveSnapshot(Guid? existingId, string name, string notes, string colorHex, double fadeMs, SnapshotScopeFlags scope, Action<MixSnapshot>? onSaved = null)
    {
        if (RunInline)
        {
            var snap = _backend.SaveSnapshotAsync(existingId, name, notes, colorHex, fadeMs, scope).GetAwaiter().GetResult();
            ApplyIncomingSnapshot(_backend.Snapshot);
            SnapshotsChanged?.Invoke();
            onSaved?.Invoke(snap);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var snap = await _backend.SaveSnapshotAsync(existingId, name, notes, colorHex, fadeMs, scope).ConfigureAwait(false);
                Post(() => { SnapshotsChanged?.Invoke(); onSaved?.Invoke(snap); });
            }
            catch (Exception ex) { Post(() => Toast?.Invoke("Snapshot speichern fehlgeschlagen: " + ex.Message, true)); }
        });
    }

    public void RenameSnapshot(Guid snapshotId, string newName, Action<MixSnapshot?>? onRenamed = null)
    {
        if (RunInline)
        {
            var renamed = _backend.RenameSnapshotAsync(snapshotId, newName).GetAwaiter().GetResult();
            ApplyIncomingSnapshot(_backend.Snapshot);
            SnapshotsChanged?.Invoke();
            onRenamed?.Invoke(renamed);
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var renamed = await _backend.RenameSnapshotAsync(snapshotId, newName).ConfigureAwait(false);
                Post(() => { SnapshotsChanged?.Invoke(); onRenamed?.Invoke(renamed); });
            }
            catch (Exception ex) { Post(() => Toast?.Invoke("Snapshot umbenennen fehlgeschlagen: " + ex.Message, true)); }
        });
    }

    public void DuplicateSnapshot(Guid snapshotId) =>
        RunBackendVoid(async () =>
        {
            await _backend.DuplicateSnapshotAsync(snapshotId).ConfigureAwait(false);
            Post(() => SnapshotsChanged?.Invoke());
        });

    public void DeleteSnapshot(Guid snapshotId) =>
        RunBackendVoid(async () =>
        {
            await _backend.DeleteSnapshotAsync(snapshotId).ConfigureAwait(false);
            Post(() => SnapshotsChanged?.Invoke());
        });

    public void PreviewSnapshot(Guid snapshotId, SnapshotScopeFlags scope, Action<SnapshotDiffPreview?> onPreview)
    {
        if (RunInline)
        {
            onPreview(_backend.PreviewSnapshotAsync(snapshotId, scope).GetAwaiter().GetResult());
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var diff = await _backend.PreviewSnapshotAsync(snapshotId, scope).ConfigureAwait(false);
                Post(() => onPreview(diff));
            }
            catch { Post(() => onPreview(null)); }
        });
    }

    public void RecallSnapshot(Guid snapshotId, SnapshotScopeFlags scope, double fadeMs) =>
        RunBackend(() => _backend.RecallSnapshotAsync(snapshotId, scope, fadeMs));

    // ---- Takt & Lifecycle ---------------------------------------------------------------------
    public bool Tick(float dt)
    {
        if (RunInline && _backend is DaemonEngineController inProc)
            inProc.PumpTelemetryNow();

        if (_backend.TryReadTelemetry(_inPeaks, _outPeaks, _busPeaks, _devPeaks, _specPreDb, _specPostDb, out var meta, _busChannelPeaks))
        {
            _telemetryMeta = meta;
            _telemetryFrameSeq++;
            return HostRunning || _subSpecMode != 0;
        }
        return false;
    }

    /// <summary>Normales Schließen des UI-Fensters: trennt NUR die Pipe-Verbindung. Audio und App-Links im Daemon laufen ununterbrochen weiter!</summary>
    public void DisconnectClientOnly()
    {
        try { _backend.Dispose(); } catch { }
    }

    /// <summary>NUR bei ausdrücklichem Benutzerwunsch oder vor einem Velopack-Update:
    /// fordert geordnetes Herunterfahren an und wartet synchron auf das Prozessende von NxMix.Daemon.exe,
    /// damit alle Audio-Handles, App-Umleitungen und Datei-Locks vor einem Update vollständig freigegeben sind.</summary>
    public bool ShutdownDaemonExplicitly(int waitForProcessExitMs = 5000)
    {
        int daemonPid = Runtime.DaemonPid;
        Process? daemonProc = null;
        if (daemonPid > 0 && daemonPid != Environment.ProcessId)
        {
            try { daemonProc = Process.GetProcessById(daemonPid); } catch { daemonProc = null; }
        }

        try
        {
            _backend.RequestDaemonShutdownAsync().Wait(1500);
        }
        catch { }

        try { _backend.Dispose(); } catch { }

        if (daemonProc != null)
        {
            try
            {
                bool exited = daemonProc.WaitForExit(Math.Max(500, waitForProcessExitMs));
                daemonProc.Dispose();
                return exited;
            }
            catch
            {
                daemonProc.Dispose();
            }
        }
        return true;
    }
}
