using NxMix.Audio;
using NxMix.Engine;

namespace NxMix.App.Model;

/// <summary>Darstellungsmodell für eine App-Audio-Quelle (ProcessLoopback) in der Oberfläche.</summary>
internal sealed record AppLink(string ExeName, int FirstChannel, int Count = 2, string? DisplayName = null)
{
    public int End => FirstChannel + Count;
    public bool Covers(int input) => input >= FirstChannel && input < End;
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? AppLinkRules.PrettyName(ExeName) : DisplayName!;
}

internal enum LinkUi { Off, Idle, NeedsSetup, AudioBlocked, Waiting, Capturing, MixedWithoutRedirect, Linked, Failed, Conflict }

internal readonly record struct LinkView(LinkUi State, string? Message, int ProcessCount);

internal sealed record AppProgram(string ExeName, string DisplayName, bool IsPlaying, int Instances);

internal static class AppLinkRules
{
    public static string PrettyName(string? exe)
    {
        var n = Path.GetFileNameWithoutExtension(exe ?? "");
        if (n.Length == 0) return exe ?? "";
        return char.ToUpperInvariant(n[0]) + n[1..];
    }

    public static string? NormalizeExe(string? raw, out string? error)
    {
        error = null;
        var s = (raw ?? "").Trim().Trim('"').Trim();
        int cut = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/'));
        if (cut >= 0) s = s[(cut + 1)..].Trim();
        if (s.Length == 0) { error = "Bitte den Namen des Programms eingeben, z. B. spotify.exe."; return null; }
        if (s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { error = "Der Name enthält ungültige Zeichen."; return null; }
        if (s.Length > 80) { error = "Der Name ist zu lang."; return null; }
        if (!s.Contains('.')) s += ".exe";
        else if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { error = "Es werden nur Programme (.exe) unterstützt."; return null; }
        if (s.Length <= 4) { error = "Bitte den Namen des Programms eingeben, z. B. spotify.exe."; return null; }
        return s;
    }

    public static List<AppProgram> Group(IReadOnlyList<AppSessionInfo> sessions)
    {
        var map = new Dictionary<string, List<AppSessionInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sessions)
        {
            string? exe = NormalizeExe(s.ExeName, out _);
            if (exe == null) continue;
            if (!map.TryGetValue(exe, out var l)) map[exe] = l = new List<AppSessionInfo>();
            l.Add(s);
        }
        var res = new List<AppProgram>();
        foreach (var (exe, l) in map)
        {
            string name = l.OrderByDescending(x => x.IsPlaying).Select(x => x.DisplayName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? PrettyName(exe);
            res.Add(new AppProgram(exe, name, l.Any(x => x.IsPlaying), l.Count));
        }
        return res.OrderByDescending(p => p.IsPlaying).ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

// ================================================================================================
// Attrappen für Screenshots (--demo) und den Headless-Selbsttest (--selftest)
// ================================================================================================

internal sealed class DemoCatalog : IDeviceCatalog
{
    public IReadOnlyList<AudioDeviceInfo> List() => new[]
    {
        new AudioDeviceInfo("asio:RME Fireface USB/in", "RME Fireface USB (ASIO)", DeviceFlow.Capture, 8, 48000, false)
        {
            Backend = AudioBackend.Asio, DriverProvider = "ASIO · RME",
            SupportedRates = new[] { 44100, 48000, 96000, 192000 },
            SupportedBuffers = new[] { 64, 128, 256, 512 },
        },
        new AudioDeviceInfo("asio:RME Fireface USB/out", "RME Fireface USB (ASIO)", DeviceFlow.Render, 8, 48000, false)
        {
            Backend = AudioBackend.Asio, DriverProvider = "ASIO · RME",
            SupportedRates = new[] { 44100, 48000, 96000, 192000 },
            SupportedBuffers = new[] { 64, 128, 256, 512 },
        },
        new AudioDeviceInfo("demo-in-usb", "Focusrite Scarlett 4i4 USB", DeviceFlow.Capture, 4, 48000, true)
        {
            Backend = AudioBackend.Windows, DriverProvider = "Focusrite Audio",
        },
        new AudioDeviceInfo("demo-out-main", "Active Studio Monitor 8ch USB", DeviceFlow.Render, 8, 48000, true)
        {
            Backend = AudioBackend.Windows, DriverProvider = "USB-Audio",
        },
        new AudioDeviceInfo("demo-vcable-out", "CABLE Output (VB-Audio Virtual Cable)", DeviceFlow.Capture, 2, 48000, false)
        {
            Backend = AudioBackend.Windows, IsVirtualEndpoint = true, DriverProvider = "VB-Audio Software",
        },
        new AudioDeviceInfo(DemoProject.ParkingId, DemoProject.ParkingName, DeviceFlow.Render, 2, 48000, false)
        {
            Backend = AudioBackend.Windows, IsVirtualEndpoint = true, DriverProvider = "VB-Audio Software",
        },
    };
}

internal sealed class DemoHost : IAudioHost, IAppLinkMonitor
{
    private HostState _state;
    private AudioHostConfig? _cfg;
    private IBlockProcessor? _proc;
    private readonly IReadOnlyList<AppLinkStatus> _linkStatuses;

    public event Action<string>? Faulted { add { } remove { } }

    public DemoHost(bool initiallyRunning = false, IReadOnlyList<AppLinkStatus>? linkStatuses = null)
    {
        _state = initiallyRunning ? HostState.Running : HostState.Stopped;
        _linkStatuses = linkStatuses ?? DemoProject.LinkStatuses();
    }

    public HostState State => _state;

    public void Start(AudioHostConfig config, IBlockProcessor processor)
    {
        _cfg = config;
        _proc = processor;
        _state = HostState.Running;

        // Ein synthetischer Audio-Block pumpen, damit Pegel-Meter und Spektrum sofort realistische Werte zeigen
        int inCh = Math.Max(1, processor.InputChannels);
        int outCh = Math.Max(1, processor.OutputChannels);
        int frames = 256;
        var ins = new float[inCh][];
        var outs = new float[outCh][];
        for (int i = 0; i < inCh; i++)
        {
            ins[i] = new float[frames];
            float amp = 0.28f * (1f - (i % 5) * 0.14f);
            double freq = 110.0 * (1 + (i % 6));
            for (int n = 0; n < frames; n++)
                ins[i][n] = amp * MathF.Sin((float)(2.0 * Math.PI * freq * n / Math.Max(1, processor.SampleRate)));
        }
        for (int o = 0; o < outCh; o++) outs[o] = new float[frames];
        processor.Process(ins, outs, frames);
    }

    public void Stop() => _state = HostState.Stopped;

    public HostStats GetStats()
    {
        int bf = _cfg?.BlockFrames ?? 256;
        int sr = _cfg?.SampleRate ?? 48000;
        double lat = Math.Round(bf * 1000.0 / Math.Max(1, sr), 1);
        var devs = (_cfg?.Bindings ?? Array.Empty<DeviceBinding>())
            .GroupBy(b => b.DeviceId)
            .Select((g, idx) => new DeviceStat(
                g.Key,
                g.Key.StartsWith("asio:", StringComparison.OrdinalIgnoreCase) ? "RME Fireface USB (ASIO)" : g.Key,
                g.Key.StartsWith("asio:", StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.52,
                idx == 0 ? 0.0 : 4.2,
                0,
                IsMasterClock: idx == 0))
            .ToArray();
        return new HostStats(_state, null, bf, _state == HostState.Running ? 0.068 : 0.0, 0, 0, lat, devs)
        {
            MasterClockName = devs.FirstOrDefault(d => d.IsMasterClock)?.Name ?? "Software-Takt",
        };
    }

    public IReadOnlyList<AppLinkStatus> GetAppLinks() => _linkStatuses;

    public void Dispose() => _state = HostState.Stopped;
}

internal sealed class DemoAppCatalog : IAppCatalog
{
    public IReadOnlyList<AppSessionInfo> List() => new[]
    {
        new AppSessionInfo(4120, "spotify.exe", "Spotify", @"C:\Users\Demo\AppData\Roaming\Spotify\Spotify.exe", true),
        new AppSessionInfo(5532, "chrome.exe", "Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", true),
        new AppSessionInfo(2210, "Discord.exe", "Discord", @"C:\Users\Demo\AppData\Local\Discord\Discord.exe", false),
        new AppSessionInfo(7788, "vlc.exe", "VLC media player", @"C:\Program Files\VideoLAN\VLC\vlc.exe", false),
        new AppSessionInfo(9014, "firefox.exe", "Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe", true),
    };
}

internal sealed class DemoRestorer : IAppRouteRestorer
{
    readonly bool _supported;
    readonly string? _reason;
    public int Calls;
    public DemoRestorer(bool supported = true, string? reason = null) { _supported = supported; _reason = reason; }
    public bool IsSupported => _supported;
    public string? UnsupportedReason => _supported ? null : _reason ?? "Diese Windows-Version unterstützt kein ProcessLoopback.";
    public void RestoreAllKnown() => Calls++;
}
