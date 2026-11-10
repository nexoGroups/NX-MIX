using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NxMix.Audio;
using Velopack.Locators;

namespace NxMix.App.Native;

internal static partial class VbCableSetup
{
    private const string PackageName = "VBCABLE_Driver_Pack46.zip";
    public const string ProductUrl = "https://vb-audio.com/Cable/";
    private static readonly string? PackageHash = typeof(VbCableSetup).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "NxMixVbCableSha256")?.Value;

    public static bool IsBundled => PackageHash != null;
    public static bool AutoSetupEnabled => IsBundled &&
        VelopackLocator.Current is { IsPortable: false, CurrentlyInstalledVersion: not null };

    public static bool InstallIfNeeded(bool automatic = true)
    {
        if (PackageHash is not { } expectedHash) return false;
        try
        {
            var state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NX-MIX");
            string attempted = Path.Combine(state, "vb-cable-setup-attempted.sha256");
            if (automatic && File.Exists(attempted) && File.ReadAllText(attempted) == expectedHash)
                return false;

            if (HasPresentDriverDevice() || new DeviceCatalog().ListWindows().Any(d => d.IsVbCable))
            {
                if (!automatic)
                    Show("VB-CABLE ist bereits installiert. Nach einer neuen Installation starte Windows neu, damit CABLE Output verfügbar wird.", 0x40);
                return false;
            }

            string packagePath = Path.Combine(AppContext.BaseDirectory, "drivers", PackageName);
            using var package = File.Open(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(package)), expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Das VB-CABLE-Paket stimmt nicht mit dem NX-MIX-Setup überein.");
            package.Position = 0;

            var temp = Directory.CreateTempSubdirectory("NxMix-VBCable-");
            try
            {
                using (var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true))
                    archive.ExtractToDirectory(temp.FullName);

                Directory.CreateDirectory(state);
                File.WriteAllText(attempted, expectedHash);
                var start = new ProcessStartInfo(Path.Combine(temp.FullName, "VBCABLE_Setup_x64.exe"))
                {
                    WorkingDirectory = temp.FullName,
                    UseShellExecute = true,
                    Verb = "runas",
                };
                using var installer = Process.Start(start) ?? throw new InvalidOperationException("VB-CABLE-Setup konnte nicht gestartet werden.");
                installer.WaitForExit();
                return true;
            }
            finally
            {
                try { temp.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Abbruch der Windows-Admin-Abfrage. Virtual I/O bietet das Setup erneut an.
        }
        catch (Exception ex)
        {
            Show($"VB-CABLE von VB-Audio konnte nicht gestartet werden. Du kannst das Setup unter Virtual I/O erneut öffnen.\n\n{ex.Message}", 0x30);
        }
        return false;
    }

    private static unsafe bool HasPresentDriverDevice()
    {
        const string service = "VBAudioVACMME";
        const uint filterService = 0x2, presentOnly = 0x100, doNotGenerate = 0x10000040;
        const uint flags = filterService | presentOnly | doNotGenerate;
        const uint bufferSmall = 0x1A;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDeviceListSize(out uint length, service, flags) != 0 || length < 2) return false;
            var ids = new char[length];
            fixed (char* buffer = ids)
            {
                uint result = GetDeviceList(service, buffer, length, flags);
                if (result == 0) return ids[0] != '\0';
                if (result != bufferSmall) return false;
            }
        }
        return false;
    }

    private static void Show(string text, uint icon) => MessageBox(0, text, "NX-MIX · VB-CABLE von VB-Audio", icon);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_List_SizeW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetDeviceListSize(out uint length, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", EntryPoint = "CM_Get_Device_ID_ListW", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial uint GetDeviceList(string filter, char* buffer, uint length, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint owner, string text, string caption, uint type);
}
