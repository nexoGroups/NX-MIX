using NxMix.Audio;
using NxMix.Ipc;

namespace NxMix.Daemon.Vaio;

public interface IVaioBackendProvider
{
    VaioSlotKind Kind { get; }
    bool IsAvailable(IReadOnlyList<AudioDeviceInfo> catalog, IAppRouteRestorer? restorer, out string statusMessage);
}

public sealed class AppLinkVaioProvider : IVaioBackendProvider
{
    public VaioSlotKind Kind => VaioSlotKind.AppCapture;

    public bool IsAvailable(IReadOnlyList<AudioDeviceInfo> catalog, IAppRouteRestorer? restorer, out string statusMessage)
    {
        if (restorer is { IsSupported: false })
        {
            statusMessage = restorer.UnsupportedReason ?? "Windows Pro-App-Umleitung wird auf dieser Windows-Version nicht unterstützt.";
            return false;
        }
        statusMessage = "Windows ProcessLoopback verfügbar.";
        return true;
    }
}

public sealed class ExternalDriverVaioProvider : IVaioBackendProvider
{
    public VaioSlotKind Kind => VaioSlotKind.ExternalDriverEndpoint;

    public bool IsAvailable(IReadOnlyList<AudioDeviceInfo> catalog, IAppRouteRestorer? restorer, out string statusMessage)
    {
        int count = catalog.Count(d => d.IsVirtualEndpoint);
        if (count == 0)
        {
            statusMessage = "Kein externer Windows-Virtual-Audio-Treiber (z. B. VB-Cable / VAC) erkannt.";
            return false;
        }
        statusMessage = $"{count} virtuelle Windows-Audio-Endpunkte erkannt.";
        return true;
    }

    public static IReadOnlyList<AudioDeviceInfo> FilterVirtualEndpoints(IReadOnlyList<AudioDeviceInfo> catalog) =>
        catalog.Where(d => d.IsVirtualEndpoint).ToArray();
}

/// <summary>Erkennt native Wiedergabeendpunkte über den Windows-Gerätekatalog.</summary>
public sealed class NxMixWaveRtProvider : IVaioBackendProvider
{
    public VaioSlotKind Kind => VaioSlotKind.NxMixKernelDriver;

    public bool IsAvailable(IReadOnlyList<AudioDeviceInfo> catalog, IAppRouteRestorer? restorer, out string statusMessage)
    {
        int endpoints = catalog.Count(d => d.Flow == DeviceFlow.Render && d.Backend == AudioBackend.Windows &&
            d.DriverProvider == "NX-MIX WaveRT");
        statusMessage = endpoints > 0
            ? $"{endpoints} native NX-MIX-Wiedergabegeräte in Windows verfügbar."
            : "Eigene NX-MIX-Wiedergabegeräte erscheinen in Windows, sobald der native WaveRT-Treiber installiert ist. App-Capture kann bereits ohne Treiber den Eingangspegel anzeigen.";
        return endpoints > 0;
    }
}

public sealed class VaioManager
{
    private readonly AppLinkVaioProvider _appProvider = new();
    private readonly ExternalDriverVaioProvider _extProvider = new();
    private readonly NxMixWaveRtProvider _kernelProvider = new();

    public VaioDriverAvailability Evaluate(IReadOnlyList<AudioDeviceInfo> catalog, IAppRouteRestorer? restorer)
    {
        bool kernelOk = _kernelProvider.IsAvailable(catalog, restorer, out string kernelMsg);
        _ = _extProvider.IsAvailable(catalog, restorer, out _);
        int extCount = catalog.Count(d => d.IsVirtualEndpoint);
        bool appOk = _appProvider.IsAvailable(catalog, restorer, out string appMsg);

        return new VaioDriverAvailability(
            NativeKernelDriverInstalled: kernelOk,
            NativeDriverMessage: kernelMsg,
            ExternalVirtualEndpointCount: extCount,
            AppLoopbackSupported: appOk,
            AppLoopbackMessage: appMsg);
    }
}
