using System.Diagnostics;
using NxMix.Audio.Interop;

namespace NxMix.Audio;

/// <summary>Programme mit Audio-Sitzungen auf irgendeinem aktiven Render-Geraet (ohne den eigenen Prozess und ohne Systemtoene), ein Eintrag je Exe.
/// ProcessId = ein Prozess der Exe (bevorzugt einer mit aktiver Sitzung). IsPlaying = mindestens eine Sitzung im Zustand "aktiv".
/// Wirft nie. Reflection-Name: NxMix.Audio.AppCatalog.</summary>
public sealed class AppCatalog : IAppCatalog
{
    public IReadOnlyList<AppSessionInfo> List()
    {
        try
        {
            return ComThread.Run(Build, 20000);
        }
        catch
        {
            return Array.Empty<AppSessionInfo>();
        }
    }

    private static IReadOnlyList<AppSessionInfo> Build()
    {
        int self = Environment.ProcessId;
        var byExe = new Dictionary<string, (int Pid, bool Playing, string? Path, string Exe)>(StringComparer.OrdinalIgnoreCase);
        var pathCache = new Dictionary<int, string?>();

        foreach (SessionRef s in SessionEnumeration.Enumerate())
        {
            if (s.ProcessId == self || s.ProcessId <= 4) continue;
            if (!pathCache.TryGetValue(s.ProcessId, out string? path))
                pathCache[s.ProcessId] = path = SystemProcessLister.ImagePathOf(s.ProcessId);
            string exe = path != null ? System.IO.Path.GetFileName(path) : NameFromProcess(s.ProcessId);
            if (string.IsNullOrEmpty(exe)) continue;

            bool playing = s.State == 1;
            if (byExe.TryGetValue(exe, out var cur))
            {
                bool better = (playing && !cur.Playing) || (playing == cur.Playing && s.ProcessId < cur.Pid);
                byExe[exe] = better ? (s.ProcessId, playing || cur.Playing, path ?? cur.Path, cur.Exe) : (cur.Pid, cur.Playing || playing, cur.Path ?? path, cur.Exe);
            }
            else
            {
                byExe[exe] = (s.ProcessId, playing, path, exe);
            }
        }

        var result = new List<AppSessionInfo>(byExe.Count);
        foreach (var e in byExe.Values.OrderBy(v => v.Exe, StringComparer.OrdinalIgnoreCase))
            result.Add(new AppSessionInfo(e.Pid, e.Exe, DisplayNameOf(e.Pid, e.Path, e.Exe), e.Path, e.Playing));
        return result;
    }

    private static string NameFromProcess(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.ProcessName + ".exe";
        }
        catch
        {
            return "";
        }
    }

    private static string DisplayNameOf(int pid, string? path, string exe)
    {
        string fallback = exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? exe[..^4] : exe;
        try
        {
            if (path != null)
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                if (!string.IsNullOrWhiteSpace(vi.ProductName)) return vi.ProductName.Trim();
                if (!string.IsNullOrWhiteSpace(vi.FileDescription)) return vi.FileDescription.Trim();
            }
        }
        catch
        {
            // kein Lesezugriff auf die Datei (z. B. WindowsApps): naechste Quelle
        }
        try
        {
            using Process p = Process.GetProcessById(pid);
            if (!string.IsNullOrWhiteSpace(p.MainWindowTitle)) return p.MainWindowTitle.Trim();
            string? desc = p.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) return desc.Trim();
        }
        catch
        {
            // Zugriff verweigert / beendet
        }
        return fallback;
    }
}
