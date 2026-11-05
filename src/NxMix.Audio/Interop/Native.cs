using System.Runtime.InteropServices;

namespace NxMix.Audio.Interop;

/// <summary>Win32-Funktionen (kernel32, avrt, ole32, winmm), per LibraryImport (Source Generator, kein Reflection-Marshalling).</summary>
internal static unsafe partial class Native
{
    public const uint WaitObject0 = 0;
    public const uint WaitTimeout = 0x00000102;
    public const uint WaitFailed = 0xFFFFFFFF;
    public const uint Infinite = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateEvent(nint attributes, int manualReset, int initialState, string? name);

    [LibraryImport("kernel32.dll")]
    public static partial int SetEvent(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial int ResetEvent(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial int CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForMultipleObjects(uint count, nint* handles, int waitAll, uint milliseconds);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint desiredAccess);

    [LibraryImport("kernel32.dll")]
    public static partial int SetWaitableTimer(nint timer, long* dueTime, int period, nint completionRoutine, nint argToCompletionRoutine, int resume);

    [LibraryImport("winmm.dll")]
    public static partial uint timeBeginPeriod(uint milliseconds);

    [LibraryImport("winmm.dll")]
    public static partial uint timeEndPeriod(uint milliseconds);

    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint AvSetMmThreadCharacteristics(string taskName, ref uint taskIndex);

    [LibraryImport("avrt.dll")]
    public static partial int AvRevertMmThreadCharacteristics(nint handle);

    [LibraryImport("avrt.dll")]
    public static partial int AvSetMmThreadPriority(nint handle, int priority);

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(PropVariant* value);

    [LibraryImport("ole32.dll")]
    public static partial void CoTaskMemFree(nint pointer);
}

/// <summary>MMCSS ("Pro Audio") fuer den aufrufenden Thread.</summary>
internal static class Mmcss
{
    public static nint Enter()
    {
        try
        {
            uint index = 0;
            nint handle = Native.AvSetMmThreadCharacteristics("Pro Audio", ref index);
            if (handle == 0)
            {
                index = 0;
                handle = Native.AvSetMmThreadCharacteristics("Audio", ref index);
            }
            return handle;
        }
        catch
        {
            return 0;
        }
    }

    public static void Leave(nint handle)
    {
        if (handle == 0) return;
        try { Native.AvRevertMmThreadCharacteristics(handle); } catch { /* egal */ }
    }
}
