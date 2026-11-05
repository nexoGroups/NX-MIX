using NxMix.Audio.Interop;

namespace NxMix.Audio.Apps;

/// <summary>Eine Gruppe zusammengehoeriger Prozesse derselben Exe (z. B. ein Browser mit all seinen Hilfsprozessen). Root = der Elternprozess,
/// dessen Eltern NICHT dieselbe Exe ist; ein Prozess-Loopback mit INCLUDE_TARGET_PROCESS_TREE auf Root nimmt die ganze Gruppe auf.</summary>
internal sealed record ProcessGroup(int RootPid, IReadOnlyList<int> Members);

internal static class ProcessSelector
{
    /// <summary>Normalisiert einen Exe-Namen fuer Vergleiche (klein, ohne Pfad).</summary>
    public static string NormalizeExe(string? exeName)
    {
        string s = (exeName ?? "").Trim();
        int i = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/'));
        if (i >= 0) s = s[(i + 1)..];
        return s.ToLowerInvariant();
    }

    public static bool ExeMatches(string processExe, string wantedExe)
    {
        string a = NormalizeExe(processExe), b = NormalizeExe(wantedExe);
        if (b.Length == 0) return false;
        if (a == b) return true;
        // "spotify" ohne Endung soll auch "spotify.exe" treffen
        return !b.EndsWith(".exe", StringComparison.Ordinal) && a == b + ".exe";
    }

    /// <summary>Alle Prozesse mit dem Exe-Namen, gruppiert nach Elternprozess. Eine Gruppe pro Wurzel; Wurzeln aufsteigend nach PID.
    /// Ein Prozess ist Wurzel, wenn sein Elternprozess nicht (mehr) zur selben Exe gehoert - oder wenn die Eltern-PID inzwischen
    /// von einem juengeren Prozess wiederverwendet wurde (Startzeit des "Elternprozesses" nach der des Kindes).</summary>
    public static IReadOnlyList<ProcessGroup> FindGroups(IReadOnlyList<ProcInfo> processes, string exeName)
    {
        var same = new Dictionary<int, ProcInfo>();
        foreach (ProcInfo p in processes)
            if (p.Pid > 0 && ExeMatches(p.ExeName, exeName)) same[p.Pid] = p;
        if (same.Count == 0) return Array.Empty<ProcessGroup>();

        var rootOf = new Dictionary<int, int>();
        int RootFor(int pid)
        {
            if (rootOf.TryGetValue(pid, out int cached)) return cached;
            int cur = pid;
            var seen = new HashSet<int> { cur };
            while (true)
            {
                ProcInfo p = same[cur];
                bool parentSame = p.ParentPid != cur && same.TryGetValue(p.ParentPid, out ProcInfo parent) && !ParentIsNewer(parent, p);
                if (!parentSame) break;
                cur = p.ParentPid;
                if (!seen.Add(cur)) break; // Schleife (sollte nie vorkommen)
            }
            rootOf[pid] = cur;
            return cur;
        }

        var groups = new SortedDictionary<int, List<int>>();
        foreach (int pid in same.Keys.OrderBy(x => x))
        {
            int root = RootFor(pid);
            if (!groups.TryGetValue(root, out List<int>? list)) groups[root] = list = new List<int>();
            list.Add(pid);
        }
        return groups.Select(g => new ProcessGroup(g.Key, g.Value)).ToList();
    }

    private static bool ParentIsNewer(ProcInfo parent, ProcInfo child) =>
        parent.StartTimeUtc is { } ps && child.StartTimeUtc is { } cs && ps > cs;
}
