using NxMix.Audio.Interop;

namespace NxMix.Audio.Apps;

/// <summary>Eine Gruppe zusammengehoeriger Prozesse derselben Exe (z. B. ein Browser mit all seinen Hilfsprozessen). Root = der Elternprozess,
/// der keinen Vorfahren derselben Exe hat. ProcessLoopback auf Root nimmt auch Kinder hinter Hilfsprozessen anderer Exe auf.</summary>
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
    /// Ein Prozess ist Wurzel, wenn kein lebender Vorfahr dieselbe Exe hat. Wiederverwendete Eltern-PIDs unterbrechen den Pfad.</summary>
    public static IReadOnlyList<ProcessGroup> FindGroups(IReadOnlyList<ProcInfo> processes, string exeName)
    {
        var same = new Dictionary<int, ProcInfo>();
        var all = new Dictionary<int, ProcInfo>();
        foreach (ProcInfo p in processes)
        {
            if (p.Pid <= 0) continue;
            all[p.Pid] = p;
            if (ExeMatches(p.ExeName, exeName)) same[p.Pid] = p;
        }
        if (same.Count == 0) return Array.Empty<ProcessGroup>();

        var rootOf = new Dictionary<int, int>();
        int RootFor(int pid)
        {
            if (rootOf.TryGetValue(pid, out int cached)) return cached;
            int cur = pid;
            int root = pid;
            var seen = new HashSet<int> { cur };
            while (true)
            {
                ProcInfo p = all[cur];
                if (!all.TryGetValue(p.ParentPid, out ProcInfo parent) || ParentIsNewer(parent, p) ||
                    !seen.Add(parent.Pid)) break;
                cur = parent.Pid;
                if (same.ContainsKey(cur)) root = cur;
            }
            rootOf[pid] = root;
            return root;
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
