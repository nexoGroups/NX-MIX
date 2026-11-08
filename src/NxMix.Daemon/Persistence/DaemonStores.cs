using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Win32;
using NxMix.Audio;
using NxMix.Engine;
using NxMix.Ipc;

[assembly: InternalsVisibleTo("NxMix.Tests")]
[assembly: InternalsVisibleTo("NX-MIX")]

namespace NxMix.Daemon.Persistence;

public sealed class DaemonPaths
{
    public string RootDir { get; }
    public string SystemConfigPath => Path.Combine(RootDir, "system-config.json");
    public string LiveStatePath => Path.Combine(RootDir, "live-state.json");
    public string SnapshotsDir => Path.Combine(RootDir, "snapshots");
    public string BackupsDir => Path.Combine(RootDir, "backups");
    public string UiDecksPath => Path.Combine(RootDir, "ui-decks.json");
    public string UiPrefsPath => Path.Combine(RootDir, "ui.json");
    public bool IsDefaultUserRoot { get; }

    public DaemonPaths(string? customRootDir = null)
    {
        if (!string.IsNullOrWhiteSpace(customRootDir))
        {
            RootDir = Path.GetFullPath(customRootDir);
            IsDefaultUserRoot = false;
        }
        else
        {
            RootDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NX-MIX");
            IsDefaultUserRoot = true;
        }
        Directory.CreateDirectory(RootDir);
        Directory.CreateDirectory(SnapshotsDir);
    }

    public static void AtomicWriteJson<T>(string path, T value)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, IpcJson.PrettyOptions);
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }

    public static T? ReadJson<T>(string path)
    {
        byte[] raw = File.ReadAllBytes(path);
        ReadOnlySpan<byte> span = raw;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
            span = span[3..];
        return JsonSerializer.Deserialize<T>(span, IpcJson.Options);
    }
}

/// <summary>Persistenz für Kategorie 1: Systemkonfiguration (Hardware, Sample-Rate, Puffer, Master-Clock, Dienst-Optionen).</summary>
public sealed class SystemConfigStore
{
    private const string RunRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "NX-MIX Engine";
    private readonly DaemonPaths _paths;

    public SystemConfigStore(DaemonPaths paths) => _paths = paths;

    public SystemConfig Load()
    {
        try
        {
            if (!File.Exists(_paths.SystemConfigPath))
                return new SystemConfig();
            var cfg = DaemonPaths.ReadJson<SystemConfig>(_paths.SystemConfigPath) ?? new SystemConfig();
            return Sanitize(cfg);
        }
        catch
        {
            ConfigBackupManager.PreserveCorruptFile(_paths.SystemConfigPath);
            return new SystemConfig();
        }
    }

    public SystemConfig Save(SystemConfig config, string? daemonExePath = null)
    {
        var clean = Sanitize(config);
        DaemonPaths.AtomicWriteJson(_paths.SystemConfigPath, clean);
        if (_paths.IsDefaultUserRoot)
            ApplyWindowsLogonAutostart(clean.LaunchDaemonAtWindowsLogon, daemonExePath);
        return clean;
    }

    public static SystemConfig Sanitize(SystemConfig c)
    {
        int sr = c.SampleRate is 44100 or 48000 or 88200 or 96000 or 176400 or 192000 ? c.SampleRate : 48000;
        int bf = Math.Clamp(c.BlockFrames <= 0 ? 256 : c.BlockFrames, 32, 4096);
        int port = Math.Clamp(c.CompanionApiPort <= 0 ? 19480 : c.CompanionApiPort, 1024, 65535);
        var vaios = c.Vaios ?? Array.Empty<VaioSlotConfig>();
        var legacyAppLinks = vaios.Where(v => v.Kind == VaioSlotKind.AppCapture).Select(v => new AppLinkEntry
        {
            Id = v.Id, ExeName = v.EndpointOrExe, DisplayName = v.Name,
            FirstChannel = v.EngineFirstChannel, Count = Math.Clamp(v.Channels, 1, 2), Enabled = v.Enabled,
        });
        var appLinks = (c.AppLinks ?? Array.Empty<AppLinkEntry>()).Concat(legacyAppLinks)
            .Select(link =>
            {
                string exe = Path.GetFileName((link.ExeName ?? "").Trim().Trim('"'));
                if (exe.Length > 0 && !Path.HasExtension(exe)) exe += ".exe";
                return link with { ExeName = exe };
            })
            .Where(link => link.ExeName.Length > 4 && link.ExeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            .GroupBy(link => link.ExeName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.FirstOrDefault(link => link.Enabled) ?? group.First()).ToArray();
        return c with
        {
            Version = 2,
            SampleRate = sr,
            BlockFrames = bf,
            CompanionApiPort = port,
            Devices = (c.Devices ?? Array.Empty<AudioDeviceNode>()).ToArray(),
            Vaios = vaios.Where(v => v.Kind != VaioSlotKind.AppCapture)
                .Select(v => v.Loopback ? v with { Flow = DeviceFlow.Capture } : v).ToArray(),
            AppLinks = appLinks,
        };
    }

    public static void RemoveWindowsLogonAutostart() => ApplyWindowsLogonAutostart(false, null);

    private static void ApplyWindowsLogonAutostart(bool enabled, string? daemonExePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryKey, writable: true);
            if (key == null) return;
            if (!enabled)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                return;
            }
            string exe = daemonExePath ?? Environment.ProcessPath ?? "";
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
                key.SetValue(RunValueName, $"\"{exe}\" --autostart");
        }
        catch
        {
            // Eingeschränkte Registry-Berechtigungen dürfen den Audio-Betrieb nicht stören.
        }
    }
}

/// <summary>Persistenz für Kategorie 2: Laufender Mischzustand mit Entprellung (Debounced Atomic Save).</summary>
public sealed class LiveStateStore : IDisposable
{
    private readonly DaemonPaths _paths;
    private readonly object _gate = new();
    private readonly Timer _debounceTimer;
    private LiveMixState? _pending;
    private bool _disposed;

    public LiveStateStore(DaemonPaths paths)
    {
        _paths = paths;
        _debounceTimer = new Timer(_ => FlushNow(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public LiveMixState? Load()
    {
        try
        {
            if (!File.Exists(_paths.LiveStatePath)) return null;
            return DaemonPaths.ReadJson<LiveMixState>(_paths.LiveStatePath);
        }
        catch
        {
            ConfigBackupManager.PreserveCorruptFile(_paths.LiveStatePath);
            return null;
        }
    }

    public void ScheduleSave(MixerSettings mixer, Guid? activeSnapshotId)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _pending = new LiveMixState
            {
                Version = 2,
                SavedAtUtc = DateTimeOffset.UtcNow,
                ActiveSnapshotId = activeSnapshotId,
                Mixer = mixer with { TransitionMs = null },
            };
            _debounceTimer.Change(300, Timeout.Infinite);
        }
    }

    public void FlushNow()
    {
        LiveMixState? toWrite;
        lock (_gate)
        {
            toWrite = _pending;
            _pending = null;
        }
        if (toWrite == null) return;
        try
        {
            DaemonPaths.AtomicWriteJson(_paths.LiveStatePath, toWrite);
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _debounceTimer.Dispose();
        FlushNow();
    }
}

/// <summary>Persistenz & Selektiv-Recall für Kategorie 3: Mischpult-Snapshots (inkl. RecallSafe & Diff-Vorschau).</summary>
public sealed class SnapshotManager
{
    private readonly DaemonPaths _paths;
    private readonly object _gate = new();

    public SnapshotManager(DaemonPaths paths) => _paths = paths;

    private string FileFor(Guid id) => Path.Combine(_paths.SnapshotsDir, $"{id:D}.json");

    public IReadOnlyList<MixSnapshotSummary> ListSummaries()
    {
        lock (_gate)
        {
            var list = new List<MixSnapshotSummary>();
            if (!Directory.Exists(_paths.SnapshotsDir)) return list;
            foreach (string file in Directory.EnumerateFiles(_paths.SnapshotsDir, "*.json"))
            {
                try
                {
                    var snap = DaemonPaths.ReadJson<MixSnapshot>(file);
                    if (snap != null)
                    {
                        list.Add(new MixSnapshotSummary(
                            snap.Id, snap.Name, snap.Notes, snap.ColorHex,
                            snap.UpdatedUtc, snap.DefaultFadeMs, snap.DefaultScope));
                    }
                }
                catch { }
            }
            return list.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(s => s.UpdatedUtc).ToArray();
        }
    }

    public MixSnapshot? Load(Guid id)
    {
        lock (_gate)
        {
            string f = FileFor(id);
            if (!File.Exists(f)) return null;
            try
            {
                return DaemonPaths.ReadJson<MixSnapshot>(f);
            }
            catch
            {
                return null;
            }
        }
    }

    public MixSnapshot Save(Guid? existingId, string name, string notes, string colorHex, double defaultFadeMs, SnapshotScopeFlags scope, MixerSettings currentMixer)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            MixSnapshot? prev = existingId.HasValue ? Load(existingId.Value) : null;
            var snap = new MixSnapshot
            {
                Id = prev?.Id ?? existingId ?? Guid.NewGuid(),
                Name = string.IsNullOrWhiteSpace(name) ? "Snapshot" : name.Trim(),
                Notes = notes?.Trim() ?? "",
                ColorHex = string.IsNullOrWhiteSpace(colorHex) ? "#3B82F6" : colorHex.Trim(),
                CreatedUtc = prev?.CreatedUtc ?? now,
                UpdatedUtc = now,
                DefaultFadeMs = Math.Clamp(defaultFadeMs, 0.0, 10000.0),
                DefaultScope = scope == SnapshotScopeFlags.None ? SnapshotScopeFlags.All : scope,
                CapturedState = currentMixer with { TransitionMs = null },
            };
            DaemonPaths.AtomicWriteJson(FileFor(snap.Id), snap);
            return snap;
        }
    }

    public MixSnapshot? Rename(Guid id, string newName)
    {
        lock (_gate)
        {
            var src = Load(id);
            if (src == null) return null;
            string cleanName = string.IsNullOrWhiteSpace(newName) ? src.Name : newName.Trim();
            var updated = src with
            {
                Name = cleanName,
                UpdatedUtc = DateTimeOffset.UtcNow,
            };
            DaemonPaths.AtomicWriteJson(FileFor(updated.Id), updated);
            return updated;
        }
    }

    public MixSnapshot? Duplicate(Guid id)
    {
        lock (_gate)
        {
            var src = Load(id);
            if (src == null) return null;
            var now = DateTimeOffset.UtcNow;
            var copy = src with
            {
                Id = Guid.NewGuid(),
                Name = src.Name + " (Kopie)",
                CreatedUtc = now,
                UpdatedUtc = now,
            };
            DaemonPaths.AtomicWriteJson(FileFor(copy.Id), copy);
            return copy;
        }
    }

    public void Delete(Guid id)
    {
        lock (_gate)
        {
            string f = FileFor(id);
            try { if (File.Exists(f)) File.Delete(f); } catch { }
        }
    }

    public static SnapshotDiffPreview ComputeDiff(MixerSettings current, MixSnapshot snap, SnapshotScopeFlags scope)
    {
        var changes = new List<SnapshotDiffItem>();
        int skipped = 0;
        var target = snap.CapturedState;

        if (scope.HasFlag(SnapshotScopeFlags.DeviceMasters))
        {
            var tgtMap = target.DeviceMasters.ToDictionary(d => d.Id);
            foreach (var curDm in current.DeviceMasters)
            {
                if (!tgtMap.TryGetValue(curDm.Id, out var tgtDm)) continue;
                string label = string.IsNullOrWhiteSpace(curDm.Name) ? "Geräte-Master" : curDm.Name;
                if (Math.Abs(curDm.GainDb - tgtDm.GainDb) > 0.05)
                {
                    bool safe = curDm.RecallSafe.HasFlag(RecallSafeFlags.Fader);
                    if (safe) skipped++;
                    changes.Add(new SnapshotDiffItem("Geräte-Master", label, "Pegel", FormatDb(curDm.GainDb), FormatDb(tgtDm.GainDb), safe));
                }
                if (curDm.Mute != tgtDm.Mute)
                {
                    bool safe = curDm.RecallSafe.HasFlag(RecallSafeFlags.Mute);
                    if (safe) skipped++;
                    changes.Add(new SnapshotDiffItem("Geräte-Master", label, "Mute", curDm.Mute ? "Stumm" : "Aktiv", tgtDm.Mute ? "Stumm" : "Aktiv", safe));
                }
            }
        }

        CompareChannels(current.Inputs, target.Inputs, isInput: true, scope, changes, ref skipped);
        CompareChannels(current.Outputs, target.Outputs, isInput: false, scope, changes, ref skipped);

        if (scope.HasFlag(SnapshotScopeFlags.Buses))
        {
            var tgtBuses = target.Buses.ToDictionary(b => b.Id);
            foreach (var cb in current.Buses)
            {
                if (!tgtBuses.TryGetValue(cb.Id, out var tb)) continue;
                if (Math.Abs(cb.GainDb - tb.GainDb) > 0.05)
                {
                    bool safe = cb.RecallSafe.HasFlag(RecallSafeFlags.Fader);
                    if (safe) skipped++;
                    changes.Add(new SnapshotDiffItem("Mix-Bus", cb.Name, "Pegel", FormatDb(cb.GainDb), FormatDb(tb.GainDb), safe));
                }
                if (cb.Mute != tb.Mute)
                {
                    bool safe = cb.RecallSafe.HasFlag(RecallSafeFlags.Mute);
                    if (safe) skipped++;
                    changes.Add(new SnapshotDiffItem("Mix-Bus", cb.Name, "Mute", cb.Mute ? "Stumm" : "Aktiv", tb.Mute ? "Stumm" : "Aktiv", safe));
                }
            }
        }

        if (scope.HasFlag(SnapshotScopeFlags.MatrixRouting))
        {
            int ni = Math.Min(current.Inputs.Count, target.Inputs.Count);
            int no = Math.Min(current.Outputs.Count, target.Outputs.Count);
            for (int i = 0; i < ni; i++)
            {
                for (int o = 0; o < no; o++)
                {
                    double cv = GetMatrix(current.MatrixDb, i, o);
                    double tv = GetMatrix(target.MatrixDb, i, o);
                    if (SameDb(cv, tv)) continue;
                    bool safe = current.Inputs[i].RecallSafe.HasFlag(RecallSafeFlags.Routing)
                                || current.Outputs[o].RecallSafe.HasFlag(RecallSafeFlags.Routing);
                    if (safe) skipped++;
                    string inName = string.IsNullOrWhiteSpace(current.Inputs[i].Name) ? $"In {i + 1}" : current.Inputs[i].Name;
                    string outName = string.IsNullOrWhiteSpace(current.Outputs[o].Name) ? $"Out {o + 1}" : current.Outputs[o].Name;
                    changes.Add(new SnapshotDiffItem("Matrix", $"{inName} → {outName}", "Send", FormatDb(cv), FormatDb(tv), safe));
                }
            }
        }

        return new SnapshotDiffPreview(snap.Id, snap.Name, changes, skipped);
    }

    private static void CompareChannels(
        IReadOnlyList<ChannelDsp> curList,
        IReadOnlyList<ChannelDsp> tgtList,
        bool isInput,
        SnapshotScopeFlags scope,
        List<SnapshotDiffItem> changes,
        ref int skipped)
    {
        bool doFader = isInput ? scope.HasFlag(SnapshotScopeFlags.InputFaders) : scope.HasFlag(SnapshotScopeFlags.OutputFaders);
        bool doMute = isInput ? scope.HasFlag(SnapshotScopeFlags.InputMutes) : scope.HasFlag(SnapshotScopeFlags.OutputMutes);
        bool doPan = isInput && scope.HasFlag(SnapshotScopeFlags.InputPans);
        bool doDsp = !isInput && scope.HasFlag(SnapshotScopeFlags.OutputDsp);
        string cat = isInput ? "Eingang" : "Ausgang";

        for (int i = 0; i < curList.Count; i++)
        {
            var cur = curList[i];
            var tgt = MatchChannel(tgtList, cur, i);
            if (tgt == null) continue;
            string name = string.IsNullOrWhiteSpace(cur.Name) ? $"{(isInput ? "In" : "Out")} {i + 1}" : cur.Name;

            if (doFader && !SameDb(cur.GainDb, tgt.GainDb))
            {
                bool safe = cur.RecallSafe.HasFlag(RecallSafeFlags.Fader);
                if (safe) skipped++;
                changes.Add(new SnapshotDiffItem(cat, name, "Fader", FormatDb(cur.GainDb), FormatDb(tgt.GainDb), safe));
            }
            if (doMute && cur.Mute != tgt.Mute)
            {
                bool safe = cur.RecallSafe.HasFlag(RecallSafeFlags.Mute);
                if (safe) skipped++;
                changes.Add(new SnapshotDiffItem(cat, name, "Mute", cur.Mute ? "Stumm" : "Aktiv", tgt.Mute ? "Stumm" : "Aktiv", safe));
            }
            if (doPan && Math.Abs(cur.Pan - tgt.Pan) > 0.01)
            {
                bool safe = cur.RecallSafe.HasFlag(RecallSafeFlags.Pan);
                if (safe) skipped++;
                changes.Add(new SnapshotDiffItem(cat, name, "Pan", $"{cur.Pan:+0.00;-0.00;0.00}", $"{tgt.Pan:+0.00;-0.00;0.00}", safe));
            }
            if (doDsp && (Math.Abs(cur.DelayMs - tgt.DelayMs) > 0.01 || cur.Filters.Count != tgt.Filters.Count || cur.Correction != tgt.Correction))
            {
                bool safe = cur.RecallSafe.HasFlag(RecallSafeFlags.Dsp);
                if (safe) skipped++;
                changes.Add(new SnapshotDiffItem(cat, name, "Output-DSP", $"{cur.Filters.Count} Filter, {cur.DelayMs:0.#} ms", $"{tgt.Filters.Count} Filter, {tgt.DelayMs:0.#} ms", safe));
            }
        }
    }

    /// <summary>Wendet einen Snapshot selektiv unter strikter Beachtung von Scope und RecallSafe an und setzt die Sample-genaue Fade-Zeit.</summary>
    public static MixerSettings ApplySelectiveRecall(MixerSettings current, MixSnapshot snap, SnapshotScopeFlags scope, double fadeMs)
    {
        var target = snap.CapturedState;

        // 1. DeviceMasters
        var nextDeviceMasters = current.DeviceMasters.ToArray();
        if (scope.HasFlag(SnapshotScopeFlags.DeviceMasters))
        {
            var tgtDmMap = target.DeviceMasters.ToDictionary(d => d.Id);
            for (int i = 0; i < nextDeviceMasters.Length; i++)
            {
                var cur = nextDeviceMasters[i];
                if (!tgtDmMap.TryGetValue(cur.Id, out var tgt)) continue;
                nextDeviceMasters[i] = cur with
                {
                    GainDb = cur.RecallSafe.HasFlag(RecallSafeFlags.Fader) ? cur.GainDb : tgt.GainDb,
                    Mute = cur.RecallSafe.HasFlag(RecallSafeFlags.Mute) ? cur.Mute : tgt.Mute,
                    Dim = cur.RecallSafe.HasFlag(RecallSafeFlags.Mute) ? cur.Dim : tgt.Dim,
                    DimDb = cur.RecallSafe.HasFlag(RecallSafeFlags.Fader) ? cur.DimDb : tgt.DimDb,
                };
            }
        }

        // 2. Inputs & Outputs (RecallSafe bleibt stets beim Kanal erhalten)
        var nextInputs = MergeChannels(current.Inputs, target.Inputs, isInput: true, scope);
        var nextOutputs = MergeChannels(current.Outputs, target.Outputs, isInput: false, scope);

        // 3. Buses
        var nextBuses = current.Buses.ToArray();
        var nextBusRoutes = current.BusRoutes;
        if (scope.HasFlag(SnapshotScopeFlags.Buses))
        {
            var tgtBusMap = target.Buses.ToDictionary(b => b.Id);
            for (int i = 0; i < nextBuses.Length; i++)
            {
                var cb = nextBuses[i];
                if (!tgtBusMap.TryGetValue(cb.Id, out var tb)) continue;
                nextBuses[i] = cb with
                {
                    GainDb = cb.RecallSafe.HasFlag(RecallSafeFlags.Fader) ? cb.GainDb : tb.GainDb,
                    Mute = cb.RecallSafe.HasFlag(RecallSafeFlags.Mute) ? cb.Mute : tb.Mute,
                    Solo = cb.RecallSafe.HasFlag(RecallSafeFlags.Mute) ? cb.Solo : tb.Solo,
                };
            }
        }

        // 4. MatrixRouting
        var nextMatrix = current.MatrixDb.Select(r => (double[])r.Clone()).ToArray();
        if (scope.HasFlag(SnapshotScopeFlags.MatrixRouting))
        {
            for (int i = 0; i < nextInputs.Length && i < nextMatrix.Length; i++)
            {
                if (nextInputs[i].RecallSafe.HasFlag(RecallSafeFlags.Routing)) continue;
                var tgtIn = MatchChannel(target.Inputs, nextInputs[i], i);
                int tInIdx = tgtIn != null ? IndexOfId(target.Inputs, tgtIn.Id) : i;
                for (int o = 0; o < nextOutputs.Length && o < nextMatrix[i].Length; o++)
                {
                    if (nextOutputs[o].RecallSafe.HasFlag(RecallSafeFlags.Routing)) continue;
                    var tgtOut = MatchChannel(target.Outputs, nextOutputs[o], o);
                    int tOutIdx = tgtOut != null ? IndexOfId(target.Outputs, tgtOut.Id) : o;
                    if (tInIdx >= 0 && tOutIdx >= 0 && tInIdx < target.MatrixDb.Length && tOutIdx < target.MatrixDb[tInIdx].Length)
                        nextMatrix[i][o] = target.MatrixDb[tInIdx][tOutIdx];
                }
            }

            // BusRoutes übernehmen, sofern alle referenzierten IDs im aktuellen System existieren:
            var validInIds = nextInputs.Where(c => !c.RecallSafe.HasFlag(RecallSafeFlags.Routing)).Select(c => c.Id).ToHashSet();
            var validOutIds = nextOutputs.Where(c => !c.RecallSafe.HasFlag(RecallSafeFlags.Routing)).Select(c => c.Id).ToHashSet();
            var allInIds = nextInputs.Select(c => c.Id).ToHashSet();
            var allOutIds = nextOutputs.Select(c => c.Id).ToHashSet();
            var allBusIds = nextBuses.Select(b => b.Id).ToHashSet();

            bool PortExists(RoutePort p) => p.Kind switch
            {
                RouteKind.Input => allInIds.Contains(p.Id),
                RouteKind.Output => allOutIds.Contains(p.Id),
                RouteKind.Bus => allBusIds.Contains(p.Id),
                _ => false,
            };
            bool RouteProtected(BusRoute r) =>
                (r.Source.Kind == RouteKind.Input && !validInIds.Contains(r.Source.Id)) ||
                (r.Target.Kind == RouteKind.Output && !validOutIds.Contains(r.Target.Id));

            var keptProtected = current.BusRoutes.Where(RouteProtected);
            var recalledRoutes = target.BusRoutes.Where(r => PortExists(r.Source) && PortExists(r.Target) && !RouteProtected(r));
            var candidateRoutes = keptProtected.Concat(recalledRoutes).Distinct().ToArray();
            var candidate = current with { Buses = nextBuses, BusRoutes = candidateRoutes };
            try
            {
                BusRouting.Compile(candidate);
                nextBusRoutes = candidateRoutes;
            }
            catch
            {
                nextBusRoutes = current.BusRoutes;
            }
        }

        return current with
        {
            DeviceMasters = nextDeviceMasters,
            Inputs = nextInputs,
            Outputs = nextOutputs,
            Buses = nextBuses,
            BusRoutes = nextBusRoutes,
            MatrixDb = nextMatrix,
            TransitionMs = Math.Clamp(fadeMs, 0.0, 10000.0),
        };
    }

    private static ChannelDsp[] MergeChannels(IReadOnlyList<ChannelDsp> curList, IReadOnlyList<ChannelDsp> tgtList, bool isInput, SnapshotScopeFlags scope)
    {
        bool doFader = isInput ? scope.HasFlag(SnapshotScopeFlags.InputFaders) : scope.HasFlag(SnapshotScopeFlags.OutputFaders);
        bool doMute = isInput ? scope.HasFlag(SnapshotScopeFlags.InputMutes) : scope.HasFlag(SnapshotScopeFlags.OutputMutes);
        bool doPan = isInput && scope.HasFlag(SnapshotScopeFlags.InputPans);
        bool doDsp = !isInput && scope.HasFlag(SnapshotScopeFlags.OutputDsp);

        var res = curList.ToArray();
        for (int i = 0; i < res.Length; i++)
        {
            var cur = res[i];
            var tgt = MatchChannel(tgtList, cur, i);
            if (tgt == null) continue;

            res[i] = cur with
            {
                GainDb = doFader && !cur.RecallSafe.HasFlag(RecallSafeFlags.Fader) ? Math.Min(tgt.GainDb, cur.MaxGainDb) : cur.GainDb,
                TrimDb = doFader && !cur.RecallSafe.HasFlag(RecallSafeFlags.Fader) ? tgt.TrimDb : cur.TrimDb,
                Mute = doMute && !cur.RecallSafe.HasFlag(RecallSafeFlags.Mute) ? tgt.Mute : cur.Mute,
                Solo = doMute && !cur.RecallSafe.HasFlag(RecallSafeFlags.Mute) ? tgt.Solo : cur.Solo,
                Pan = doPan && !cur.RecallSafe.HasFlag(RecallSafeFlags.Pan) ? tgt.Pan : cur.Pan,
                InvertPhase = doDsp && !cur.RecallSafe.HasFlag(RecallSafeFlags.Dsp) ? tgt.InvertPhase : cur.InvertPhase,
                DelayMs = doDsp && !cur.RecallSafe.HasFlag(RecallSafeFlags.Dsp) ? tgt.DelayMs : cur.DelayMs,
                Filters = doDsp && !cur.RecallSafe.HasFlag(RecallSafeFlags.Dsp) ? tgt.Filters : cur.Filters,
                Correction = doDsp && !cur.RecallSafe.HasFlag(RecallSafeFlags.Dsp) ? tgt.Correction : cur.Correction,
                CeilingDbfs = doDsp && !cur.RecallSafe.HasFlag(RecallSafeFlags.Dsp) ? tgt.CeilingDbfs : cur.CeilingDbfs,
            };
        }
        return res;
    }

    private static ChannelDsp? MatchChannel(IReadOnlyList<ChannelDsp> list, ChannelDsp probe, int fallbackIdx)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].Id == probe.Id) return list[i];
        if (probe.DeviceId.HasValue)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].DeviceId == probe.DeviceId && list[i].DeviceChannelIndex == probe.DeviceChannelIndex)
                    return list[i];
        }
        return fallbackIdx >= 0 && fallbackIdx < list.Count ? list[fallbackIdx] : null;
    }

    private static int IndexOfId(IReadOnlyList<ChannelDsp> list, Guid id)
    {
        for (int i = 0; i < list.Count; i++)
            if (list[i].Id == id) return i;
        return -1;
    }

    private static double GetMatrix(double[][] m, int i, int o) =>
        i >= 0 && i < m.Length && o >= 0 && o < m[i].Length ? m[i][o] : double.NegativeInfinity;

    private static bool SameDb(double a, double b)
    {
        bool aInf = double.IsNegativeInfinity(a) || double.IsNaN(a) || a <= -150.0;
        bool bInf = double.IsNegativeInfinity(b) || double.IsNaN(b) || b <= -150.0;
        if (aInf && bInf) return true;
        if (aInf != bInf) return false;
        return Math.Abs(a - b) <= 0.05;
    }

    private static string FormatDb(double db) =>
        double.IsNegativeInfinity(db) || double.IsNaN(db) || db <= -150.0 ? "-∞ dB" : $"{db:+0.0;-0.0;0.0} dB";
}

public sealed record ConfigBackupManifest(
    DateTimeOffset CreatedUtc,
    string Reason,
    IReadOnlyList<string> CopiedFiles);

/// <summary>Automatische Konfigurations- und Snapshot-Sicherung vor Updates, Kanalwechseln (insb. Beta → Stable Downgrade)
/// sowie Schutz defekter JSON-Dateien vor stillschweigendem Überschreiben.</summary>
public static class ConfigBackupManager
{
    private static readonly string[] s_topLevelFiles =
    {
        "system-config.json",
        "live-state.json",
        "ui.json",
        "ui-decks.json",
    };

    public static string? CreateBackup(string rootDir, string reasonTag, int maxRetainedBackups = 10)
    {
        if (string.IsNullOrWhiteSpace(rootDir) || !Directory.Exists(rootDir))
            return null;

        try
        {
            string safeTag = SanitizeToken(reasonTag);
            string stamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
            string backupsRoot = Path.Combine(rootDir, "backups");
            string destDir = Path.Combine(backupsRoot, $"{stamp}_{safeTag}");
            Directory.CreateDirectory(destDir);

            var copied = new List<string>();
            foreach (string name in s_topLevelFiles)
            {
                string src = Path.Combine(rootDir, name);
                if (!File.Exists(src)) continue;
                File.Copy(src, Path.Combine(destDir, name), overwrite: true);
                copied.Add(name);
            }

            string snapsSrc = Path.Combine(rootDir, "snapshots");
            if (Directory.Exists(snapsSrc))
            {
                string snapsDst = Path.Combine(destDir, "snapshots");
                bool anySnap = false;
                foreach (string snapFile in Directory.EnumerateFiles(snapsSrc, "*.json"))
                {
                    if (!anySnap)
                    {
                        Directory.CreateDirectory(snapsDst);
                        anySnap = true;
                    }
                    string fn = Path.GetFileName(snapFile);
                    File.Copy(snapFile, Path.Combine(snapsDst, fn), overwrite: true);
                    copied.Add("snapshots/" + fn);
                }
            }

            DaemonPaths.AtomicWriteJson(
                Path.Combine(destDir, "backup-manifest.json"),
                new ConfigBackupManifest(DateTimeOffset.UtcNow, reasonTag ?? "update", copied));

            PruneOldBackups(backupsRoot, Math.Max(1, maxRetainedBackups));
            return destDir;
        }
        catch
        {
            return null;
        }
    }

    public static bool RestoreBackup(string backupDir, string targetRootDir)
    {
        if (string.IsNullOrWhiteSpace(backupDir) || !Directory.Exists(backupDir) || string.IsNullOrWhiteSpace(targetRootDir))
            return false;

        try
        {
            Directory.CreateDirectory(targetRootDir);
            foreach (string name in s_topLevelFiles)
            {
                string src = Path.Combine(backupDir, name);
                if (File.Exists(src))
                    AtomicCopyFile(src, Path.Combine(targetRootDir, name));
            }

            string snapsSrc = Path.Combine(backupDir, "snapshots");
            if (Directory.Exists(snapsSrc))
            {
                string snapsDst = Path.Combine(targetRootDir, "snapshots");
                Directory.CreateDirectory(snapsDst);
                foreach (string snapFile in Directory.EnumerateFiles(snapsSrc, "*.json"))
                    AtomicCopyFile(snapFile, Path.Combine(snapsDst, Path.GetFileName(snapFile)));
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static IReadOnlyList<string> ListBackups(string rootDir)
    {
        try
        {
            string backupsRoot = Path.Combine(rootDir, "backups");
            if (!Directory.Exists(backupsRoot)) return Array.Empty<string>();
            return Directory.GetDirectories(backupsRoot)
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static void PreserveCorruptFile(string filePath)
    {
        try
        {
            if (!File.Exists(filePath)) return;
            string bak = $"{filePath}.corrupt-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.bak";
            File.Copy(filePath, bak, overwrite: true);
        }
        catch
        {
        }
    }

    private static void AtomicCopyFile(string src, string dst)
    {
        string tmp = dst + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.Copy(src, tmp, overwrite: true);
        File.Move(tmp, dst, overwrite: true);
    }

    private static void PruneOldBackups(string backupsRoot, int keepCount)
    {
        try
        {
            var dirs = Directory.GetDirectories(backupsRoot)
                .OrderByDescending(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            for (int i = keepCount; i < dirs.Length; i++)
            {
                try { Directory.Delete(dirs[i], recursive: true); } catch { }
            }
        }
        catch
        {
        }
    }

    private static string SanitizeToken(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "backup";
        Span<char> buf = stackalloc char[Math.Min(raw.Length, 48)];
        int n = 0;
        foreach (char c in raw)
        {
            if (n >= buf.Length) break;
            buf[n++] = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-';
        }
        return n == 0 ? "backup" : new string(buf[..n]).Trim('-');
    }
}
