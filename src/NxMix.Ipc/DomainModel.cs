using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using NxMix.Audio;
using NxMix.Engine;

[assembly: InternalsVisibleTo("NxMix.Tests")]

namespace NxMix.Ipc;

/// <summary>Offizielle NX-MIX Update- und Release-Kanäle.</summary>
public enum ReleaseChannel
{
    /// <summary>Offizielle, getestete Produktionsversion für den dauerhaften Live-Betrieb (Velopack-Kanal "win").</summary>
    Stable = 0,
    /// <summary>Vorabversion mit neuen UI-/Engine-Funktionen und frühen Bugfixes (Velopack-Kanal "win-beta").</summary>
    Beta = 1,
}

/// <summary>Klassifiziert für den Nutzer transparent, welche Auswirkung eine Konfigurationsänderung auf den laufenden Ton hat.</summary>
public enum ConfigImpactLevel
{
    /// <summary>Wird Sample-genau per Rampe im laufenden Betrieb ohne Audio-Unterbrechung übernommen.</summary>
    LiveRamp = 0,
    /// <summary>Erfordert kurzes Schließen und Neuöffnen der Hardware-Streams (~50 ms sanftes Aus-/Einblenden).</summary>
    StreamReopen = 1,
    /// <summary>Betrifft nur Hintergrunddienst, Netzwerk-API oder Autostart ohne Eingriff in den Audiostrom.</summary>
    DaemonSetting = 2,
}

public enum VaioSlotKind
{
    /// <summary>Prozess-spezifisches App-Audio über Windows ProcessLoopback + Parkplatz-Umleitung.</summary>
    AppCapture = 0,
    /// <summary>Echter installierter Windows-Virtual-Audio-Treiber-Endpunkt (z. B. VB-CABLE, Virtual Audio Cable, WaveRT-VAD).</summary>
    ExternalDriverEndpoint = 1,
    /// <summary>Nativer signierter NX-MIX WaveRT-Kernel-Treiber (nur aktiv, wenn tatsächlich im System installiert).</summary>
    NxMixKernelDriver = 2,
}

/// <summary>Registriertes physisches oder virtuelles Audio-Gerät in der Systemkonfiguration.
/// <see cref="Id"/> korrespondiert 1:1 mit <see cref="DeviceMasterDsp.Id"/> und <see cref="ChannelDsp.DeviceId"/>.</summary>
public sealed record AudioDeviceNode
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string EndpointId { get; init; } = "";
    public string FriendlyName { get; init; } = "";
    public string CustomTitle { get; init; } = "";
    public DeviceFlow Flow { get; init; }
    public AudioBackend Backend { get; init; }
    public bool Loopback { get; init; }
    public int DeviceFirstChannel { get; init; }
    public int EngineFirstChannel { get; init; }
    public int ChannelCount { get; init; } = 2;
    public int? SampleRateOverride { get; init; }
    public int? BufferFramesOverride { get; init; }
    public bool? ExclusiveOverride { get; init; }
    public bool Enabled { get; init; } = true;
    public bool IsVirtualCable { get; init; }

    public string DisplayTitle =>
        !string.IsNullOrWhiteSpace(CustomTitle) ? CustomTitle :
        !string.IsNullOrWhiteSpace(FriendlyName) ? FriendlyName : EndpointId;
}

/// <summary>Dedizierter virtueller Ein-/Ausgangs-Slot auf der Seite „Virtual I/O“.</summary>
public sealed record VaioSlotConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "VAIO 1";
    public VaioSlotKind Kind { get; init; } = VaioSlotKind.ExternalDriverEndpoint;
    public DeviceFlow Flow { get; init; } = DeviceFlow.Capture;
    /// <summary>Ein Windows-Wiedergabegerät als Mixer-Eingang lesen.</summary>
    public bool Loopback { get; init; }
    public int Channels { get; init; } = 2;
    public int EngineFirstChannel { get; init; }
    /// <summary>Bei ExternalDriverEndpoint: MMDevice-ID; bei AppCapture: normalisierter Programmname (z. B. "spotify.exe").</summary>
    public string EndpointOrExe { get; init; } = "";
    public bool Enabled { get; init; } = true;
}

/// <summary>Konfigurierte App-Audio-Quelle (ProcessLoopback).</summary>
public sealed record AppLinkEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string ExeName { get; init; } = "";
    public string? DisplayName { get; init; }
    public int FirstChannel { get; init; }
    public int Count { get; init; } = 2;
    public bool Enabled { get; init; } = true;

    public int EndChannel => FirstChannel + Count;
    public string Title => !string.IsNullOrWhiteSpace(DisplayName) ? DisplayName! : PrettyExeName(ExeName);

    public static string PrettyExeName(string? exe)
    {
        var n = Path.GetFileNameWithoutExtension(exe ?? "");
        if (n.Length == 0) return exe ?? "";
        return char.ToUpperInvariant(n[0]) + n[1..];
    }
}

/// <summary>Kategorie 1: Persistente Hardware-, Takt-, Dienst- und API-Systemkonfiguration (%APPDATA%\NX-MIX\system-config.json).
/// Wird NIEMALS durch Mischpult-Snapshots überschrieben.</summary>
public sealed record SystemConfig
{
    public int Version { get; init; } = 2;
    public int SampleRate { get; init; } = 48000;
    public int BlockFrames { get; init; } = 256;
    public string? PreferredMasterDeviceId { get; init; }
    public bool DefaultWasapiExclusive { get; init; }
    public bool AutoStartAudioOnBoot { get; init; } = true;
    public bool LaunchDaemonAtWindowsLogon { get; init; }
    public bool AllowOfflineAuxDevices { get; init; } = true;
    public bool AppLinksEnabled { get; init; } = true;
    public string? ParkingDeviceId { get; init; }
    public string? ParkingDeviceName { get; init; }
    public bool CompanionApiEnabled { get; init; } = true;
    public int CompanionApiPort { get; init; } = 19480;
    public bool CompanionApiBindLoopbackOnly { get; init; } = true;
    public string? CompanionApiAuthToken { get; init; }
    public IReadOnlyList<AudioDeviceNode> Devices { get; init; } = Array.Empty<AudioDeviceNode>();
    public IReadOnlyList<VaioSlotConfig> Vaios { get; init; } = Array.Empty<VaioSlotConfig>();
    public IReadOnlyList<AppLinkEntry> AppLinks { get; init; } = Array.Empty<AppLinkEntry>();

    /// <summary>Bewahrt unbekannte Felder neuerer Versionen (z. B. nach Wechsel von Beta auf Stable) verlustfrei auf.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Kategorie 2: Laufender Mischzustand (%APPDATA%\NX-MIX\live-state.json).</summary>
public sealed record LiveMixState
{
    public int Version { get; init; } = 2;
    public DateTimeOffset SavedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public Guid? ActiveSnapshotId { get; init; }
    public MixerSettings Mixer { get; init; } = MixerSettings.Create(48000, 0, 0);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

[Flags]
public enum SnapshotScopeFlags
{
    None = 0,
    InputFaders = 1 << 0,
    InputMutes = 1 << 1,
    InputPans = 1 << 2,
    OutputFaders = 1 << 3,
    OutputMutes = 1 << 4,
    DeviceMasters = 1 << 5,
    Buses = 1 << 6,
    MatrixRouting = 1 << 7,
    OutputDsp = 1 << 8,
    All = InputFaders | InputMutes | InputPans | OutputFaders | OutputMutes | DeviceMasters | Buses | MatrixRouting | OutputDsp,
}

/// <summary>Kategorie 3: Mischpult-Snapshot (%APPDATA%\NX-MIX\snapshots\*.json).</summary>
public sealed record MixSnapshot
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Snapshot";
    public string Notes { get; init; } = "";
    public string ColorHex { get; init; } = "#3B82F6";
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public double DefaultFadeMs { get; init; } = 100.0;
    public SnapshotScopeFlags DefaultScope { get; init; } = SnapshotScopeFlags.All;
    public MixerSettings CapturedState { get; init; } = MixerSettings.Create(48000, 0, 0);

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed record MixSnapshotSummary(
    Guid Id,
    string Name,
    string Notes,
    string ColorHex,
    DateTimeOffset UpdatedUtc,
    double DefaultFadeMs,
    SnapshotScopeFlags DefaultScope);

public sealed record SnapshotDiffItem(
    string Category,
    string ObjectName,
    string Parameter,
    string CurrentValue,
    string TargetValue,
    bool BlockedByRecallSafe);

public sealed record SnapshotDiffPreview(
    Guid SnapshotId,
    string SnapshotName,
    IReadOnlyList<SnapshotDiffItem> Changes,
    int SafeSkippedCount);

/// <summary>Kategorie 4: Reine Darstellungs-Objekte für benutzerdefinierte Fader-Decks (%APPDATA%\NX-MIX\ui-decks.json).
/// Erzeugen niemals eigene Audio-Pfade oder Signal-Duplikate.</summary>
public enum DeckItemKind
{
    DeviceMaster = 0,
    InputChannel = 1,
    OutputChannel = 2,
    MixBus = 3,
    VaioSlot = 4,
    Spacer = 5,
}

public enum DeckDensity
{
    Detailed = 0,
    Compact = 1,
}

public sealed record DeckSlotItem
{
    public Guid ItemId { get; init; } = Guid.NewGuid();
    public DeckItemKind Kind { get; init; }
    public Guid TargetId { get; init; }
    public string? CustomLabel { get; init; }
    public bool ExpandedInline { get; init; }
}

public sealed record UiDeckDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Live Deck";
    public string ColorHex { get; init; } = "#10B981";
    public bool VisibleInTopBar { get; init; } = true;
    public DeckDensity Density { get; init; } = DeckDensity.Detailed;
    public IReadOnlyList<DeckSlotItem> Items { get; init; } = Array.Empty<DeckSlotItem>();
}

public sealed record UiDeckCollection
{
    public int Version { get; init; } = 2;
    /// <summary>Null = eingebautes geräteorientiertes Hauptpult ("Main").</summary>
    public Guid? ActiveDeckId { get; init; }
    public IReadOnlyList<Guid> ExpandedMainDeviceIds { get; init; } = Array.Empty<Guid>();
    public IReadOnlyList<UiDeckDefinition> Decks { get; init; } = Array.Empty<UiDeckDefinition>();

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed record VaioDriverAvailability(
    bool NativeKernelDriverInstalled,
    string NativeDriverMessage,
    int ExternalVirtualEndpointCount,
    bool AppLoopbackSupported,
    string? AppLoopbackMessage);

public sealed record EngineLogEntry(
    long Seq,
    DateTimeOffset TimestampUtc,
    string Level,
    string Category,
    string Message);

/// <summary>Vollständiger Laufzeit-Schnappschuss des Daemons für UI-Verbindungsaufbau und Status-Synchronisation.</summary>
public sealed record DaemonRuntimeSnapshot
{
    public int DaemonPid { get; init; }
    public DateTimeOffset StartedUtc { get; init; } = DateTimeOffset.UtcNow;
    public long StateRevision { get; init; }
    public SystemConfig Config { get; init; } = new();
    public MixerSettings Mixer { get; init; } = MixerSettings.Create(48000, 0, 0);
    public IReadOnlyList<AudioDeviceInfo> DiscoveredDevices { get; init; } = Array.Empty<AudioDeviceInfo>();
    public IReadOnlyList<AppSessionInfo> ActiveAppSessions { get; init; } = Array.Empty<AppSessionInfo>();
    public IReadOnlyList<AppLinkStatus> AppLinkStatuses { get; init; } = Array.Empty<AppLinkStatus>();
    public VaioDriverAvailability VaioAvailability { get; init; } = new(false, "Kein nativer NX-MIX Kernel-Treiber installiert.", 0, true, null);
    public HostStats HostStats { get; init; } = new(HostState.Stopped, null, 256, 0, 0, 0, 0, Array.Empty<DeviceStat>());
    public string? LastError { get; init; }
    public string? AudioStartBlockedReason { get; init; }
    public bool PanicMuted { get; init; }
    public Guid? ActiveSnapshotId { get; init; }
    public IReadOnlyList<MixSnapshotSummary> Snapshots { get; init; } = Array.Empty<MixSnapshotSummary>();
    public IReadOnlyList<EngineLogEntry> RecentLogs { get; init; } = Array.Empty<EngineLogEntry>();
}
