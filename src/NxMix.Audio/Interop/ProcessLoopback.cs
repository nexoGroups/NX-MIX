using System.Runtime.InteropServices;

namespace NxMix.Audio.Interop;

[ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceCompletionHandler
{
    [PreserveSig] int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
}

[ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IActivateAudioInterfaceAsyncOperation
{
    [PreserveSig] int GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object? activatedInterface);
}

/// <summary>Marker: das Objekt ist threadunabhaengig (Voraussetzung dafuer, dass Windows den Handler auf einem beliebigen Thread ruft).</summary>
[ComImport, Guid("94EA2B94-E9CC-49E0-C0FF-EE64CA8F5B90"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAgileObject
{
}

/// <summary>Completion-Handler fuer ActivateAudioInterfaceAsync. Agil (IAgileObject + freies .NET-CCW), setzt nur ein Ereignis.</summary>
[ClassInterface(ClassInterfaceType.None)]
internal sealed class ActivateCompletionHandler : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    public readonly ManualResetEventSlim Done = new(false);
    public int ActivateResult = unchecked((int)0x80004005);
    public object? Activated;

    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation)
    {
        try
        {
            int hr = activateOperation.GetActivateResult(out int result, out object? iface);
            ActivateResult = hr < 0 ? hr : result;
            Activated = iface;
        }
        catch (Exception ex)
        {
            ActivateResult = ex.HResult != 0 ? ex.HResult : unchecked((int)0x80004005);
        }
        finally
        {
            Done.Set();
        }
        return 0;
    }
}

internal static class MmDevApi
{
    [DllImport("mmdevapi.dll", ExactSpelling = true, PreserveSig = true)]
    public static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        in Guid riid,
        nint activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation? activationOperation);
}

/// <summary>Ein geoeffneter Prozess-Loopback-Capture (nur der Ton eines Prozessbaums, unabhaengig davon, auf welchem Geraet er abgespielt wird).
/// Das Format steht nicht per GetMixFormat zur Verfuegung: es wird vorgegeben (float32, 2 Kanaele) und von Windows konvertiert.
/// Wird auf dem Thread erzeugt und benutzt, der den Stream betreibt (MTA).</summary>
internal sealed unsafe class ProcessLoopbackEndpoint : IDisposable
{
    public const string VirtualDeviceId = "VAD\\Process_Loopback";
    private const uint ActivationTypeProcessLoopback = 1;
    private const uint ModeIncludeTargetProcessTree = 0;
    private const uint ModeExcludeTargetProcessTree = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct ActivationParams
    {
        public uint ActivationType;
        public uint TargetProcessId;
        public uint ProcessLoopbackMode;
    }

    private IAudioClient? _client;
    private bool _started;
    private bool _disposed;

    public IAudioClient Client => _client!;
    public IAudioCaptureClient? CaptureClient { get; private set; }
    public nint EventHandle { get; private set; }
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public uint BufferFrames { get; private set; }
    public int ProcessId { get; }

    private ProcessLoopbackEndpoint(int pid) => ProcessId = pid;

    /// <summary>Oeffnet den Prozess-Loopback. Probiert die Wunschrate, dann 48000 Hz; jeweils zuerst eventgesteuert, dann Polling.</summary>
    public static ProcessLoopbackEndpoint Open(int pid, int preferredRate, int timeoutMs = 8000)
    {
        var ep = new ProcessLoopbackEndpoint(pid);
        try
        {
            ep.OpenCore(preferredRate, timeoutMs);
            return ep;
        }
        catch
        {
            ep.Dispose();
            throw;
        }
    }

    /// <summary>Nur Pruefung: laesst sich der Prozess-Loopback ueberhaupt aktivieren (Windows-Version)? Aktiviert fuer den eigenen Prozess und gibt sofort wieder frei.</summary>
    public static bool ProbeSupported(out string? reason)
    {
        reason = null;
        try
        {
            using var ep = Open(Environment.ProcessId, 48000, 5000);
            return true;
        }
        catch (Exception ex)
        {
            reason = "Prozess-Loopback (ActivateAudioInterfaceAsync) nicht verfuegbar: " + ex.Message;
            return false;
        }
    }

    private void OpenCore(int preferredRate, int timeoutMs)
    {
        object? activated = Activate(ProcessId, timeoutMs);
        if (activated is not IAudioClient client)
            throw new AudioException(0, "Der aktivierte Audio-Client hat die erwartete Schnittstelle nicht.");
        // Der Client gehoert uns; die Initialisierung kann scheitern -> bei Fehler freigeben und neu aktivieren
        var errors = new System.Text.StringBuilder();
        int[] rates = preferredRate == 48000 ? new[] { 48000 } : new[] { preferredRate, 48000 };
        bool first = true;
        foreach (int rate in rates)
        {
            foreach (bool eventMode in new[] { true, false })
            {
                if (!first)
                {
                    WasapiEndpoint.ReleaseCom(client);
                    client = (IAudioClient)Activate(ProcessId, timeoutMs)!;
                }
                first = false;

                WaveFormatExtensible fmt = WasapiEndpoint.BuildFormat(SampleKind.Float32, 2, rate, 3 /* FL|FR */, 32);
                uint flags = StreamFlags.Loopback | StreamFlags.AutoConvertPcm | StreamFlags.SrcDefaultQuality | (eventMode ? StreamFlags.EventCallback : 0u);
                long duration = eventMode ? 400_000 : 1_000_000; // 40 ms bzw. 100 ms
                int hr = client.Initialize(0, flags, duration, 0, (nint)(&fmt), 0);
                if (hr < 0)
                {
                    errors.Append($"[{rate} Hz{(eventMode ? "" : " Polling")}: {Hr.Describe(hr)}] ");
                    continue;
                }

                _client = client;
                SampleRate = rate;
                Channels = 2;
                EventDriven = eventMode;
                Hr.Check(client.GetBufferSize(out uint frames), "GetBufferSize (Prozess-Loopback)");
                BufferFrames = frames;
                if (eventMode)
                {
                    EventHandle = Native.CreateEvent(0, 0, 0, null);
                    if (EventHandle == 0) throw new AudioException(Marshal.GetHRForLastWin32Error(), "Event konnte nicht erzeugt werden");
                    Hr.Check(client.SetEventHandle(EventHandle), "SetEventHandle (Prozess-Loopback)");
                }
                Guid iid = Iids.AudioCaptureClient;
                Hr.Check(client.GetService(ref iid, out object service), "GetService (Prozess-Loopback)");
                CaptureClient = (IAudioCaptureClient)service;
                return;
            }
        }
        WasapiEndpoint.ReleaseCom(client);
        throw new AudioException(0, $"Prozess-Loopback fuer PID {ProcessId} konnte nicht initialisiert werden. {errors}");
    }

    public bool EventDriven { get; private set; }

    private static object? Activate(int pid, int timeoutMs)
    {
        var p = new ActivationParams
        {
            ActivationType = ActivationTypeProcessLoopback,
            TargetProcessId = (uint)pid,
            ProcessLoopbackMode = ModeIncludeTargetProcessTree,
        };
        var pv = new PropVariant { vt = 65 /* VT_BLOB */, u32At8 = (uint)sizeof(ActivationParams), ptr16 = (nint)(&p) };
        var handler = new ActivateCompletionHandler();
        Guid iid = Iids.AudioClient;
        int hr = MmDevApi.ActivateAudioInterfaceAsync(VirtualDeviceId, in iid, (nint)(&pv), handler, out IActivateAudioInterfaceAsyncOperation? op);
        try
        {
            Hr.Check(hr, "ActivateAudioInterfaceAsync");
            if (!handler.Done.Wait(timeoutMs))
                throw new TimeoutException("ActivateAudioInterfaceAsync antwortet nicht (Prozess-Loopback).");
            Hr.Check(handler.ActivateResult, "Prozess-Loopback aktivieren");
            return handler.Activated;
        }
        finally
        {
            WasapiEndpoint.ReleaseCom(op);
            GC.KeepAlive(handler);
        }
    }

    public void Start()
    {
        Hr.Check(Client.Start(), "Prozess-Loopback starten");
        _started = true;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_started)
        {
            try { _client?.Stop(); } catch { /* egal */ }
            _started = false;
        }
        WasapiEndpoint.ReleaseCom(CaptureClient);
        WasapiEndpoint.ReleaseCom(_client);
        CaptureClient = null;
        _client = null;
        if (EventHandle != 0)
        {
            Native.CloseHandle(EventHandle);
            EventHandle = 0;
        }
    }
}
