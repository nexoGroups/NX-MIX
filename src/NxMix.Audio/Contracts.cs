using NxMix.Engine;

namespace NxMix.Audio;

public enum DeviceFlow { Render, Capture }

public enum AudioBackend { Windows, Asio }

public sealed record AudioDeviceInfo(string Id, string Name, DeviceFlow Flow, int Channels, int SampleRate, bool IsDefault)
{
    public AudioBackend Backend { get; init; }
    public IReadOnlyList<int> SupportedRates { get; init; } = Array.Empty<int>();
    public IReadOnlyList<int> SupportedBuffers { get; init; } = Array.Empty<int>();
    public string? UnavailableReason { get; init; }
    /// <summary>True, wenn es sich um einen echten Windows-Virtual-Audio-Treiber-Endpunkt (z. B. VB-Audio, VAC, WaveRT-VAD) handelt.</summary>
    public bool IsVirtualEndpoint { get; init; }
    /// <summary>Erkannter Treiber-/Hardware-Anbieter oder Enumerator (z. B. "ASIO", "VB-Audio", "USB-Audio", "HDAUDIO").</summary>
    public string? DriverProvider { get; init; }
}

/// <summary>Eine Anbindung eines Geraets an Engine-Kanaele.
/// Capture: Geraetekanaele [DeviceFirstChannel, +Count) -> Engine-Eingang [EngineFirstChannel, +Count).
/// Render: Engine-Ausgang [EngineFirstChannel, +Count) -> Geraetekanaele [DeviceFirstChannel, +Count).
/// Loopback=true: nimmt das auf, was ein RENDER-Geraet abspielt (nur lesend, aendert nichts am Geraet).</summary>
public sealed record DeviceBinding(string DeviceId, DeviceFlow Flow, int DeviceFirstChannel, int EngineFirstChannel, int Count, bool Loopback = false)
{
    public int? SampleRate { get; init; }
    public int? BufferFrames { get; init; }
    public bool? Exclusive { get; init; }
}

public sealed record AudioHostConfig(
    int SampleRate,
    IReadOnlyList<DeviceBinding> Bindings,
    /// <summary>Gewuenschte Blockgroesse in Frames (z. B. 480 bei 96 kHz = 5 ms). Die Schicht darf aufrunden.</summary>
    int BlockFrames = 480,
    /// <summary>Standard fuer WASAPI-Geraete ohne explizites DeviceBinding.Exclusive.</summary>
    bool UseExclusiveMode = false,
    /// <summary>Programme, die direkt an Eingangskanaele gebunden werden (siehe AppBinding). Erfordert ParkingDeviceId.</summary>
    IReadOnlyList<AppBinding>? AppBindings = null,
    /// <summary>Render-Geraet, auf das Programme umgeleitet werden, damit ihr Ton nirgends hoerbar ist (stilles virtuelles Geraet; darf NICHT das Standardgeraet sein).</summary>
    string? ParkingDeviceId = null,
    /// <summary>Optional explizit gewaehltes Master-Clock-Geraet (MMDevice-ID oder "asio:DriverName/...").
    /// Null waehlt automatisch: 1. aktives ASIO-Ausgabegeraet -> 2. erstes WASAPI-Ausgabegeraet -> 3. Software-Takt.</summary>
    string? PreferredMasterDeviceId = null,
    /// <summary>Wenn true, duerfen abgesteckte Hilfsgeraete beim Start als Offline uebersprungen werden, solange mindestens ein Geraet oder App-Link laeuft.</summary>
    bool AllowOfflineAuxDevices = false);

public enum HostState { Stopped, Starting, Running, Faulted }

public sealed record HostStats(
    HostState State, string? LastError, int BlockFrames, double CpuLoad, long Underruns, long Overruns,
    double OutputLatencyMs, IReadOnlyList<DeviceStat> Devices)
{
    public string? MasterClockName { get; init; }
}

public sealed record DeviceStat(
    string DeviceId, string Name, double BufferFill, double ResampleRatioPpm, long Glitches,
    bool IsMasterClock = false, bool IsOffline = false, string? OfflineReason = null);

public interface IAudioHost : IDisposable
{
    HostState State { get; }
    /// <summary>Startet alle Geraete. Beginnt STETS mit Stille und blendet in ~50 ms ein (Schutz der Lautsprecher).</summary>
    void Start(AudioHostConfig config, IBlockProcessor processor);
    void Stop();
    HostStats GetStats();
    event Action<string>? Faulted;
}

public interface IDeviceCatalog
{
    IReadOnlyList<AudioDeviceInfo> List();
}

// ===========================================================================================
// App-Links: Ein Programm (z. B. spotify.exe, chrome.exe) wird direkt an Engine-Eingangskanaele gebunden.
// ===========================================================================================

public sealed record AppBinding(string ExeName, int EngineFirstChannel, int Count = 2);

public enum AppLinkState { WaitingForApp, Linked, Failed, Capturing, MixedWithoutRedirect }

public sealed record AppLinkStatus(string ExeName, AppLinkState State, int ProcessCount, string? Message, double BufferFill);

public interface IAppLinkMonitor
{
    IReadOnlyList<AppLinkStatus> GetAppLinks();
}

public sealed record AppSessionInfo(int ProcessId, string ExeName, string DisplayName, string? ExePath, bool IsPlaying);

public interface IAppCatalog
{
    IReadOnlyList<AppSessionInfo> List();
}

public interface IAppRouteRestorer
{
    bool IsSupported { get; }
    string? UnsupportedReason { get; }
    void RestoreAllKnown();
}
