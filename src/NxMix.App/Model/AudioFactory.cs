using NxMix.Audio;
using NxMix.Engine;

namespace NxMix.App.Model;

/// <summary>
/// Holt Audio-Host und Geraete-Katalog per Reflection (NxMix.Audio.AudioHost / NxMix.Audio.DeviceCatalog),
/// damit die App auch baut und laeuft, solange diese Klassen noch fehlen.
/// Der Host wird erst beim Klick auf "Start" erzeugt; es startet nie etwas von selbst.
/// </summary>
internal static class AudioFactory
{
    static System.Reflection.Assembly Asm => typeof(IAudioHost).Assembly;

    static Type? Find(string name, Type iface)
    {
        try
        {
            var t = Asm.GetType(name);
            return t != null && !t.IsAbstract && iface.IsAssignableFrom(t) ? t : null;
        }
        catch { return null; }
    }

    public static Type? HostType => Find("NxMix.Audio.AudioHost", typeof(IAudioHost));
    public static Type? CatalogType => Find("NxMix.Audio.DeviceCatalog", typeof(IDeviceCatalog));

    public static bool HostAvailable => HostType != null;
    public static string HostUnavailableReason => "Audio-Host nicht verfügbar (NxMix.Audio.AudioHost fehlt).";

    public static IAudioHost CreateHost()
    {
        var t = HostType ?? throw new InvalidOperationException(HostUnavailableReason);
        try { return (IAudioHost)Activator.CreateInstance(t)!; }
        catch (MissingMethodException)
        {
            var cat = CreateCatalog() ?? throw new InvalidOperationException("Audio-Host braucht einen Geräte-Katalog.");
            return (IAudioHost)Activator.CreateInstance(t, cat)!;
        }
        catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException != null)
        {
            throw new InvalidOperationException(ex.InnerException.Message, ex.InnerException);
        }
    }

    public static IDeviceCatalog? CreateCatalog()
    {
        var t = CatalogType;
        if (t == null) return null;
        try { return (IDeviceCatalog)Activator.CreateInstance(t)!; }
        catch { return null; }
    }

    // ---- App-Links (NxMix.Audio.AppCatalog / NxMix.Audio.AppRouteRestorer) -----------------------
    public static Type? AppCatalogType => Find("NxMix.Audio.AppCatalog", typeof(IAppCatalog));
    public static Type? RestorerType => Find("NxMix.Audio.AppRouteRestorer", typeof(IAppRouteRestorer));

    public static IAppCatalog? CreateAppCatalog()
    {
        var t = AppCatalogType;
        if (t == null) return null;
        try { return (IAppCatalog)Activator.CreateInstance(t)!; }
        catch { return null; }
    }

    public static IAppRouteRestorer? CreateRestorer()
    {
        var t = RestorerType;
        if (t == null) return null;
        try { return (IAppRouteRestorer)Activator.CreateInstance(t)!; }
        catch { return null; }
    }

    /// <summary>Setzt alle bekannten Programm-Umleitungen zurueck (Programmstart, Beenden). Schluckt jeden Fehler; wartet hoechstens maxMs.</summary>
    public static void TryRestoreAll(int maxMs = 6000)
    {
        try
        {
            var r = CreateRestorer();
            if (r == null) return;
            var task = Task.Run(() => { try { r.RestoreAllKnown(); } catch { /* wirft nie */ } });
            task.Wait(maxMs);
        }
        catch { /* nichts zu tun */ }
    }
}

/// <summary>Erfundene Geraete, nur fuer Screenshots (--demo) und den Selbsttest.</summary>
internal sealed class DemoCatalog : IDeviceCatalog
{
    public IReadOnlyList<AudioDeviceInfo> List() => new[]
    {
        new AudioDeviceInfo("demo-out-1", "Demo-Interface 18i (Ausgabe 1–10)", DeviceFlow.Render, 10, 96000, false),
        new AudioDeviceInfo("demo-in-1", "Demo-Interface 18i (Eingang 1–8)", DeviceFlow.Capture, 8, 96000, false),
        new AudioDeviceInfo("demo-out-2", "Virtuelles Kabel (Eingang)", DeviceFlow.Render, 16, 48000, false),
        new AudioDeviceInfo("demo-in-2", "Virtuelles Kabel (Ausgang)", DeviceFlow.Capture, 16, 48000, false),
        new AudioDeviceInfo("demo-out-3", "Lautsprecher (Standard)", DeviceFlow.Render, 2, 48000, true),
        new AudioDeviceInfo("demo-in-3", "Mikrofon (Standard)", DeviceFlow.Capture, 1, 48000, true),
        new AudioDeviceInfo(DemoProject.ParkingId, "Parkplatz (virtuelles Kabel, stumm)", DeviceFlow.Render, 2, 48000, false),
    };
}

/// <summary>Attrappe fuer Screenshots: gibt nichts aus.</summary>
internal sealed class DemoHost : IAudioHost, IAppLinkMonitor
{
    readonly bool _running;
    public DemoHost(bool running) { _running = running; }
    public IReadOnlyList<AppLinkStatus> GetAppLinks() => DemoProject.LinkStatuses();
    public HostState State => _running ? HostState.Running : HostState.Stopped;
    public event Action<string>? Faulted { add { } remove { } }
    public void Start(AudioHostConfig config, IBlockProcessor processor) { }
    public void Stop() { }
    public HostStats GetStats() => new(State, null, 480, 0.071, 0, 2, 11.3, Array.Empty<DeviceStat>());
    public void Dispose() { }
}
