using NxMix.Engine;

namespace NxMix.Ipc;

/// <summary>Gemeinsame Schnittstelle zwischen NX-MIX UI-Client und NX-MIX Hintergrund-Engine (über Named-Pipe/Shared-Memory oder In-Process für Headless-Selbsttests).</summary>
public interface INxMixBackend : IDisposable
{
    bool IsConnected { get; }
    string ConnectionSummary { get; }
    DaemonRuntimeSnapshot Snapshot { get; }

    event Action<DaemonRuntimeSnapshot>? SnapshotUpdated;
    event Action<bool>? ConnectionChanged;

    /// <summary>Liest aktuelle 60-Hz-Spitzenpegel und Spektralwerte allokationsfrei.</summary>
    bool TryReadTelemetry(
        Span<float> inPeaks,
        Span<float> outPeaks,
        Span<float> busPeaks,
        Span<float> devPeaks,
        Span<float> specPre,
        Span<float> specPost,
        out TelemetrySnapshotFrame meta,
        Span<float> busChannelPeaks = default);

    // ---- Echtzeit-Mischer & Geräte-Master (sofort per Rampe) ----
    Task<string?> ApplyMixerAsync(MixerSettings mixer);
    Task<string?> AdjustChannelGainAsync(Guid channelId, bool isInput, double deltaDb);
    Task<string?> AdjustDeviceMasterGainAsync(Guid deviceId, double deltaDb);
    Task SetPanicMuteAsync(bool muted);

    // ---- Systemkonfiguration & Hardware (getrennt von Snapshots) ----
    Task<string?> ApplySystemConfigAsync(SystemConfig config, bool reopenAudioIfNeeded = true);
    Task RefreshDevicesAsync();
    Task StartAudioAsync();
    Task StopAudioAsync();
    Task<string?> ShowAsioControlPanelAsync(string endpointId);
    Task<int> RestoreOrphanedAppRoutesAsync();

    // ---- Spektral-Tap (für Outputs / DSP) ----
    Task<string?> PlayOutputTestToneAsync(Guid channelId);
    Task SubscribeSpectrumAsync(int mode, int channelIndex, int windowSize = 0);

    // ---- Mischpult-Snapshots ----
    Task<MixSnapshot> SaveSnapshotAsync(Guid? existingId, string name, string notes, string colorHex, double defaultFadeMs, SnapshotScopeFlags defaultScope);
    Task<MixSnapshot?> GetSnapshotAsync(Guid snapshotId);
    Task<MixSnapshot> ImportSnapshotAsync(MixSnapshot snapshot);
    Task<MixSnapshot?> RenameSnapshotAsync(Guid snapshotId, string newName);
    Task DeleteSnapshotAsync(Guid snapshotId);
    Task<MixSnapshot?> DuplicateSnapshotAsync(Guid snapshotId);
    Task<SnapshotDiffPreview?> PreviewSnapshotAsync(Guid snapshotId, SnapshotScopeFlags scope);
    Task<string?> RecallSnapshotAsync(Guid snapshotId, SnapshotScopeFlags scope, double fadeMs);

    // ---- Daemon-Steuerung (nur bei explizitem Beenden / Update!) ----
    Task RequestDaemonShutdownAsync();
}
