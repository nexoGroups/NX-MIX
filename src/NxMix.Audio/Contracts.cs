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
    /// <summary>Takt-Master ist die ERSTE Render-Anbindung; alle anderen Geraete laufen ueber Ringpuffer + adaptives Resampling.</summary>
    bool UseExclusiveMode = false,
    /// <summary>Programme, die direkt an Eingangskanaele gebunden werden (siehe AppBinding). Erfordert ParkingDeviceId.</summary>
    IReadOnlyList<AppBinding>? AppBindings = null,
    /// <summary>Render-Geraet, auf das Programme umgeleitet werden, damit ihr Ton nirgends hoerbar ist (stilles virtuelles Geraet; darf NICHT das Standardgeraet sein).</summary>
    string? ParkingDeviceId = null);

public enum HostState { Stopped, Starting, Running, Faulted }

public sealed record HostStats(
    HostState State, string? LastError, int BlockFrames, double CpuLoad, long Underruns, long Overruns,
    double OutputLatencyMs, IReadOnlyList<DeviceStat> Devices);

public sealed record DeviceStat(string DeviceId, string Name, double BufferFill, double ResampleRatioPpm, long Glitches);

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
// Technik: (1) Die Windows-Ausgabe des Programms wird per Pro-App-Umleitung auf ein stilles "Parkplatz"-Rendergerät
// gelegt, damit sie nirgends hoerbar ankommt; (2) der Ton wird per Prozess-Loopback (nur dieser Prozessbaum) abgegriffen
// und als Eingang in die Engine gespeist. So geht der Ton NUR zum Fader.
// ===========================================================================================

/// <summary>Bindung eines Programms an Engine-Eingaenge (Count = 2: Stereo). Erkennung ueber den Exe-Namen (ohne Pfad, z. B. "spotify.exe"), nicht ueber die PID, damit es auch nach einem Neustart des Programms wieder greift.</summary>
public sealed record AppBinding(string ExeName, int EngineFirstChannel, int Count = 2);

public enum AppLinkState { WaitingForApp, Linked, Failed }

/// <summary>Status eines App-Links. Message erklaert Fehler (z. B. "Parkplatz-Geraet fehlt").</summary>
public sealed record AppLinkStatus(string ExeName, AppLinkState State, int ProcessCount, string? Message, double BufferFill);

/// <summary>Wird von AudioHost zusaetzlich implementiert (UI prueft per "is"). Nicht Teil von IAudioHost, damit Attrappen nicht brechen.</summary>
public interface IAppLinkMonitor
{
    IReadOnlyList<AppLinkStatus> GetAppLinks();
}

public sealed record AppSessionInfo(int ProcessId, string ExeName, string DisplayName, string? ExePath, bool IsPlaying);

/// <summary>Liste der Programme, die gerade Audio-Sitzungen auf irgendeinem Render-Geraet haben (ohne NX-MIX selbst). Reflection-Name: NxMix.Audio.AppCatalog.</summary>
public interface IAppCatalog
{
    IReadOnlyList<AppSessionInfo> List();
}

/// <summary>Reflection-Name: NxMix.Audio.AppRouteRestorer. Stellt Umleitungen wieder her. Wird beim Start von NX-MIX und beim Beenden aufgerufen
/// (auch nach einem Absturz: Umleitungen stehen in einem Journal %APPDATA%\NX-MIX\app-routes.json).</summary>
public interface IAppRouteRestorer
{
    /// <summary>False, wenn diese Windows-Version die Pro-App-Umleitung oder den Prozess-Loopback nicht kann.</summary>
    bool IsSupported { get; }
    string? UnsupportedReason { get; }
    /// <summary>Setzt ALLE vom Journal bekannten Umleitungen zurueck auf "System-Standard". Mehrfach aufrufbar, wirft nie.</summary>
    void RestoreAllKnown();
}
