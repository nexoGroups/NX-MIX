using System.Runtime.InteropServices;
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
        statusMessage = "Windows ProcessLoopback + Parkplatz-Umleitung betriebsbereit.";
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

/// <summary>Prüft ehrlich, ob ein signierter nativer NX-MIX WaveRT-Kernel-Treiber im System vorhanden ist (keine Fake-Simulation).</summary>
public sealed partial class NxMixKernelDriverStubProvider : IVaioBackendProvider
{
    private const string KernelSymbolicLink = @"\\.\NxMixWaveRtControl";
    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ_WRITE = 0x00000003;
    private const uint OPEN_EXISTING = 3;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode, nint lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, nint hTemplateFile);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    public VaioSlotKind Kind => VaioSlotKind.NxMixKernelDriver;

    public bool IsAvailable(IReadOnlyList<AudioDeviceInfo> catalog, IAppRouteRestorer? restorer, out string statusMessage)
    {
        nint h = CreateFileW(KernelSymbolicLink, GENERIC_READ, FILE_SHARE_READ_WRITE, 0, OPEN_EXISTING, 0, 0);
        if (h != 0 && h != -1)
        {
            CloseHandle(h);
            statusMessage = "Nativer NX-MIX WaveRT-Kernel-Treiber aktiv.";
            return true;
        }
        statusMessage = "Kein nativer NX-MIX Kernel-Treiber installiert. Für echte Windows-System-Endpunkte bitte vorhandene Virtual-Cables (z. B. VB-Cable) oder für Einzelprogramme App-Capture verwenden.";
        return false;
    }
}

public sealed class VaioManager
{
    private readonly AppLinkVaioProvider _appProvider = new();
    private readonly ExternalDriverVaioProvider _extProvider = new();
    private readonly NxMixKernelDriverStubProvider _kernelProvider = new();

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
