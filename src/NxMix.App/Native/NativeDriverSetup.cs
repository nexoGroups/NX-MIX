using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using NxMix.Audio;
using NxMix.Daemon.Vaio;

namespace NxMix.App.Native;

internal static partial class NativeDriverSetup
{
    internal static void InstallIfNeeded()
    {
        string? expectedHash = typeof(NativeDriverSetup).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "NxMixNativeDriverMsiSha256")?.Value;
        if (expectedHash is null) return;

        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "drivers", "NxMixWaveRt-x64.msi");
            string stateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NX-MIX");
            string installedHash = Path.Combine(stateDir, "wave-rt-msi.sha256");
            if (File.Exists(installedHash) && File.ReadAllText(installedHash) == expectedHash &&
                new NxMixWaveRtProvider().IsAvailable(new DeviceCatalog().List(), null, out _))
                return;
            // Andere Prozesse dürfen das Paket während der MSI-Installation lesen.
            using var package = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            string actualHash = Convert.ToHexString(SHA256.HashData(package));
            if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Das Treiberpaket stimmt nicht mit dem NX-MIX-Setup überein.");

            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"))
            {
                UseShellExecute = false,
            };
            start.ArgumentList.Add("/i");
            start.ArgumentList.Add(path);
            start.ArgumentList.Add("/passive");
            start.ArgumentList.Add("/norestart");

            using var installer = Process.Start(start) ?? throw new InvalidOperationException("Windows Installer konnte nicht gestartet werden.");
            installer.WaitForExit();
            switch (installer.ExitCode)
            {
                case 0:
                case 3010:
                    Directory.CreateDirectory(stateDir);
                    File.WriteAllText(installedHash, expectedHash);
                    if (installer.ExitCode == 3010)
                        ShowMessage("Der NX-MIX-Audiotreiber wurde installiert. Starte Windows neu, um ihn zu aktivieren.", 0x40);
                    break;
                case 1602:
                    ShowMessage("Die Treiberinstallation wurde abgebrochen. Du kannst das NX-MIX-Setup erneut starten.", 0x30);
                    break;
                default:
                    throw new InvalidOperationException($"Windows Installer meldet Fehler {installer.ExitCode}.");
            }
        }
        catch (Exception ex)
        {
            ShowMessage($"Der NX-MIX-Audiotreiber konnte nicht installiert werden.\n\n{ex.Message}", 0x30);
        }
    }

    private static void ShowMessage(string text, uint icon) => MessageBox(0, text, "NX-MIX Audiotreiber", icon);

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint owner, string text, string caption, uint type);
}
