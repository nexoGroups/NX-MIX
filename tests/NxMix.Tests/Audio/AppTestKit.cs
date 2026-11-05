using NxMix.Audio.Apps;
using NxMix.Audio.Interop;

namespace NxMix.Tests.Audio;

/// <summary>Attrappe fuer die Pro-App-Umleitung. Verhaelt sich wie Windows (gemessen auf Build 26300): der Wert gilt je PROGRAMM (Exe), nicht je Prozess;
/// eine PID, die Windows nicht kennt (Prozess nicht gestartet oder noch ohne Audio-Nutzung), liefert E_INVALIDARG.</summary>
internal sealed class FakeAppRouter : IAppRouter
{
    public const int EInvalidArg = unchecked((int)0x80070057);

    /// <summary>PID -> Exe fuer alle Prozesse, die Windows "kennt".</summary>
    public readonly Dictionary<int, string> Known = new();
    /// <summary>Exe (klein) -> drei Rollenwerte.</summary>
    public readonly Dictionary<string, string?[]> Values = new();
    public readonly List<string> Log = new();
    /// <summary>true: Set meldet Erfolg, aendert aber nichts (Windows uebernimmt die Umleitung nicht).</summary>
    public bool IgnoreSet;
    /// <summary>Wird vor jedem Set gerufen (z. B. um zu pruefen, dass das Journal schon geschrieben ist).</summary>
    public Action<int, string?[]>? BeforeSet;

    public void AddProcess(int pid, string exe) => Known[pid] = exe.ToLowerInvariant();

    public string?[] ValueOf(string exe) =>
        Values.TryGetValue(exe.ToLowerInvariant(), out string?[]? v) ? (string?[])v.Clone() : new string?[3];

    public void Preset(string exe, params string?[] roles)
    {
        var v = new string?[3];
        for (int i = 0; i < 3; i++) v[i] = i < roles.Length ? roles[i] : roles.Length > 0 ? roles[0] : null;
        Values[exe.ToLowerInvariant()] = v;
    }

    public int Get(int pid, string?[] roles)
    {
        if (!Known.TryGetValue(pid, out string? exe)) return EInvalidArg;
        string?[] v = ValueOf(exe);
        for (int i = 0; i < 3; i++) roles[i] = v[i];
        return 0;
    }

    public int Set(int pid, string?[] roles)
    {
        BeforeSet?.Invoke(pid, roles);
        Log.Add($"Set {pid} [{string.Join("|", roles.Select(r => r ?? "-"))}]");
        if (!Known.TryGetValue(pid, out string? exe)) return EInvalidArg;
        if (IgnoreSet) return 0;
        Values[exe] = roles.Select(r => string.IsNullOrEmpty(r) ? null : r).ToArray();
        return 0;
    }
}

internal sealed class FakeProcessLister : IProcessLister
{
    public List<ProcInfo> Processes = new();
    public IReadOnlyList<ProcInfo> Snapshot() => Processes.ToList();
}

internal static class AppTestKit
{
    public static string TempJournal() =>
        Path.Combine(Path.GetTempPath(), "nxmix-test-" + Guid.NewGuid().ToString("N") + ".json");

    public static void Delete(string path)
    {
        foreach (string p in new[] { path, path + ".tmp", path + ".bad" })
        {
            try { File.Delete(p); } catch { /* egal */ }
        }
    }

    /// <summary>Policy-Ids wie Windows sie liefert.</summary>
    public static string Pol(string guid) => NxMix.Audio.Interop.AudioPolicyConfig.PolicyDeviceId("{0.0.0.00000000}.{" + guid + "}");
}
