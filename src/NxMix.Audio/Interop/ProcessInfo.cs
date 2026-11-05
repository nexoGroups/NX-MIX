using System.Runtime.InteropServices;

namespace NxMix.Audio.Interop;

/// <summary>Ein laufender Prozess (Momentaufnahme). StartTimeUtc dient nur dazu, wiederverwendete Prozess-IDs zu erkennen (null = unbekannt).</summary>
internal readonly record struct ProcInfo(int Pid, int ParentPid, string ExeName, DateTime? StartTimeUtc = null);

/// <summary>Liefert die Prozessliste. Austauschbar fuer Tests.</summary>
internal interface IProcessLister
{
    IReadOnlyList<ProcInfo> Snapshot();
}

internal static unsafe partial class ProcessNative
{
    public const uint Th32csSnapProcess = 0x00000002;
    public const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public nuint th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        public fixed char szExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32First(nint snapshot, ProcessEntry32* entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool Process32Next(nint snapshot, ProcessEntry32* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(nint process, long* creation, long* exit, long* kernel, long* user);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(nint process, uint flags, char* buffer, uint* size);
}

/// <summary>Prozessliste ueber ToolHelp32 (Exe-Name + Eltern-PID in einem Rutsch, ohne Berechtigungsprobleme). Wirft nie.</summary>
internal sealed unsafe class SystemProcessLister : IProcessLister
{
    public IReadOnlyList<ProcInfo> Snapshot()
    {
        var list = new List<ProcInfo>(256);
        nint snap = ProcessNative.CreateToolhelp32Snapshot(ProcessNative.Th32csSnapProcess, 0);
        if (snap == -1 || snap == 0) return list;
        try
        {
            var e = new ProcessNative.ProcessEntry32 { dwSize = (uint)sizeof(ProcessNative.ProcessEntry32) };
            if (!ProcessNative.Process32First(snap, &e)) return list;
            do
            {
                string exe = new string(e.szExeFile);
                list.Add(new ProcInfo((int)e.th32ProcessID, (int)e.th32ParentProcessID, exe));
                e.dwSize = (uint)sizeof(ProcessNative.ProcessEntry32);
            }
            while (ProcessNative.Process32Next(snap, &e));
        }
        catch
        {
            // unvollstaendige Liste liefern
        }
        finally
        {
            Native.CloseHandle(snap);
        }
        return list;
    }

    /// <summary>Startzeit (UTC) eines Prozesses oder null (kein Zugriff / beendet).</summary>
    public static DateTime? StartTimeOf(int pid)
    {
        nint h = ProcessNative.OpenProcess(ProcessNative.ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == 0) return null;
        try
        {
            long c, x, k, u;
            if (!ProcessNative.GetProcessTimes(h, &c, &x, &k, &u) || c <= 0) return null;
            return DateTime.FromFileTimeUtc(c);
        }
        catch
        {
            return null;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    /// <summary>Voller Pfad der Exe oder null.</summary>
    public static string? ImagePathOf(int pid)
    {
        nint h = ProcessNative.OpenProcess(ProcessNative.ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == 0) return null;
        try
        {
            char* buf = stackalloc char[1024];
            uint size = 1024;
            return ProcessNative.QueryFullProcessImageName(h, 0, buf, &size) ? new string(buf, 0, (int)size) : null;
        }
        catch
        {
            return null;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    public static bool IsAlive(int pid, DateTime? expectedStartUtc = null)
    {
        DateTime? start = StartTimeOf(pid);
        if (start == null) return false;
        if (expectedStartUtc is { } exp && Math.Abs((start.Value - exp).TotalSeconds) > 1.0) return false; // PID wiederverwendet
        return true;
    }
}
