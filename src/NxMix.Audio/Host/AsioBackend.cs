using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using NAudio.Wave;

namespace NxMix.Audio.Host;

internal delegate void AsioProcessHandler(in AsioFrameBlock block);
internal delegate void AsioCaptureHandler(in AsioCaptureBlock block);

/// <summary>Allokationsfreie Stack-Sicht auf Ein- und Ausgangspuffer eines ASIO-Callbacks (echter NAudio-Treiber oder Test-Simulator).</summary>
internal readonly ref struct AsioFrameBlock
{
    private readonly bool _hasNative;
    private readonly AsioProcessBuffers _native;
    private readonly float[][]? _simIn;
    private readonly float[][]? _simOut;
    public readonly int Frames;

    public AsioFrameBlock(in AsioProcessBuffers native)
    {
        _hasNative = true;
        _native = native;
        _simIn = null;
        _simOut = null;
        Frames = native.Frames;
    }

    public AsioFrameBlock(float[][] simIn, float[][] simOut, int frames)
    {
        _hasNative = false;
        _native = default;
        _simIn = simIn;
        _simOut = simOut;
        Frames = frames;
    }

    public ReadOnlySpan<float> GetInput(int channel) =>
        _hasNative ? _native.GetInput(channel) : _simIn![channel].AsSpan(0, Frames);

    public Span<float> GetOutput(int channel) =>
        _hasNative ? _native.GetOutput(channel) : _simOut![channel].AsSpan(0, Frames);
}

internal readonly ref struct AsioCaptureBlock
{
    private readonly bool _hasNative;
    private readonly AsioAudioCapturedEventArgs? _native;
    private readonly float[][]? _simIn;
    public readonly int Frames;

    public AsioCaptureBlock(AsioAudioCapturedEventArgs native)
    {
        _hasNative = true;
        _native = native;
        _simIn = null;
        Frames = native.Frames;
    }

    public AsioCaptureBlock(float[][] simIn, int frames)
    {
        _hasNative = false;
        _native = null;
        _simIn = simIn;
        Frames = frames;
    }

    public ReadOnlySpan<float> GetChannel(int channel) =>
        _hasNative ? _native!.GetChannel(channel) : _simIn![channel].AsSpan(0, Frames);
}

internal readonly record struct AsioCaps(
    int InputChannels,
    int OutputChannels,
    int BufferMinSize,
    int BufferMaxSize,
    int BufferPreferredSize,
    int BufferGranularity);

internal interface IAsioSession : IDisposable
{
    string DriverName { get; }
    AsioCaps Capabilities { get; }
    int CurrentSampleRate { get; }
    int FramesPerBuffer { get; }
    int InputLatencySamples { get; }
    int OutputLatencySamples { get; }
    bool IsSampleRateSupported(int sampleRate);
    void InitRecording(int[] inputChannels, int sampleRate, int? bufferSize, AsioCaptureHandler handler);
    void InitDuplex(int[] inputChannels, int[] outputChannels, int sampleRate, int? bufferSize, AsioProcessHandler handler);
    void Start();
    void Stop();
    void ShowControlPanel();
    event EventHandler? DriverResetRequest;
    event EventHandler<string?>? UnexpectedStop;
}

internal interface IAsioBackend
{
    IReadOnlyList<string> GetDriverNames();
    IAsioSession Open(string driverName);
}

internal static class AsioBackendRegistry
{
    private static IAsioBackend s_current = new NaudioStaAsioBackend();

    public static IAsioBackend Current
    {
        get => Volatile.Read(ref s_current);
        set => Volatile.Write(ref s_current, value ?? throw new ArgumentNullException(nameof(value)));
    }

    public static void ResetToDefault() => Current = new NaudioStaAsioBackend();
}

/// <summary>Dedizierter STA-Thread mit echter Win32-Message-Pump fuer COM-basierte ASIO-Treiber (CoCreateInstance, Init, Start/Stop, ControlPanel).</summary>
internal static unsafe class AsioStaDispatcher
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nuint wParam;
        public nint lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int PeekMessageW(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int TranslateMessage(in MSG lpMsg);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint DispatchMessageW(in MSG lpMsg);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint MsgWaitForMultipleObjects(uint nCount, nint* pHandles, int fWaitAll, uint dwMilliseconds, uint dwWakeMask);

    private const uint PmRemove = 0x0001;
    private const uint QsAllInput = 0x04FF;

    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<Action> Work = new();
    private static nint s_wakeEvent;
    private static Thread? s_thread;

    private static void EnsureStarted()
    {
        lock (Gate)
        {
            if (s_thread is { IsAlive: true }) return;
            s_wakeEvent = Interop.Native.CreateEvent(0, 0, 0, null);
            s_thread = new Thread(PumpLoop, 512 * 1024)
            {
                IsBackground = true,
                Name = "NxMix.Asio.STA",
            };
            s_thread.SetApartmentState(ApartmentState.STA);
            s_thread.Start();
        }
    }

    private static void PumpLoop()
    {
        nint wake = s_wakeEvent;
        nint* handles = stackalloc nint[1];
        handles[0] = wake;

        while (true)
        {
            while (Work.TryDequeue(out Action? item))
            {
                try { item(); } catch { /* Fehler wird im jeweiligen TaskCompletionSource uebertragen */ }
            }

            while (PeekMessageW(out MSG msg, 0, 0, 0, PmRemove) != 0)
            {
                TranslateMessage(in msg);
                DispatchMessageW(in msg);
            }

            if (!Work.IsEmpty) continue;
            MsgWaitForMultipleObjects(1, handles, 0, 250, QsAllInput);
        }
    }

    public static T Run<T>(Func<T> func, int timeoutMs = 20000)
    {
        if (Thread.CurrentThread == s_thread) return func();
        EnsureStarted();
        var done = new ManualResetEventSlim(false);
        T result = default!;
        Exception? error = null;

        Work.Enqueue(() =>
        {
            try { result = func(); }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        });
        Interop.Native.SetEvent(s_wakeEvent);

        if (!done.Wait(timeoutMs))
            throw new TimeoutException("ASIO-Treiber antwortet nicht auf dem STA-Host-Thread.");
        if (error != null)
            throw new InvalidOperationException(error.Message, error);
        return result;
    }

    public static void Run(Action action, int timeoutMs = 20000) =>
        Run(() => { action(); return true; }, timeoutMs);
}

internal sealed class NaudioStaAsioBackend : IAsioBackend
{
    public IReadOnlyList<string> GetDriverNames() =>
        AsioStaDispatcher.Run(() => (IReadOnlyList<string>)AsioDevice.GetDriverNames());

    public IAsioSession Open(string driverName) =>
        AsioStaDispatcher.Run(() => (IAsioSession)new NaudioStaAsioSession(driverName));

    private sealed class NaudioStaAsioSession : IAsioSession
    {
        private AsioDevice? _device;
        private AsioProcessHandler? _duplexHandler;
        private AsioCaptureHandler? _captureHandler;

        public string DriverName { get; }
        public AsioCaps Capabilities { get; }
        public int CurrentSampleRate => AsioStaDispatcher.Run(() => _device!.CurrentSampleRate);
        public int FramesPerBuffer { get; private set; }
        public int InputLatencySamples { get; private set; }
        public int OutputLatencySamples { get; private set; }

        public event EventHandler? DriverResetRequest;
        public event EventHandler<string?>? UnexpectedStop;

        public NaudioStaAsioSession(string driverName)
        {
            DriverName = driverName;
            _device = AsioDevice.Open(driverName);
            var c = _device.Capabilities;
            Capabilities = new AsioCaps(
                c.NbInputChannels, c.NbOutputChannels,
                c.BufferMinSize, c.BufferMaxSize, c.BufferPreferredSize, c.BufferGranularity);
            _device.DriverResetRequest += (_, e) => DriverResetRequest?.Invoke(this, e);
            _device.Stopped += (_, e) => UnexpectedStop?.Invoke(this, e.Exception?.Message);
        }

        public bool IsSampleRateSupported(int sampleRate) =>
            AsioStaDispatcher.Run(() => _device!.IsSampleRateSupported(sampleRate));

        public void InitRecording(int[] inputChannels, int sampleRate, int? bufferSize, AsioCaptureHandler handler)
        {
            _captureHandler = handler;
            AsioStaDispatcher.Run(() =>
            {
                _device!.InitRecording(new AsioRecordingOptions
                {
                    InputChannels = inputChannels,
                    SampleRate = sampleRate,
                    BufferSize = bufferSize,
                });
                _device.AudioCaptured += OnCaptured;
                FramesPerBuffer = _device.FramesPerBuffer;
                InputLatencySamples = _device.InputLatencySamples;
                OutputLatencySamples = _device.OutputLatencySamples;
            });
        }

        public void InitDuplex(int[] inputChannels, int[] outputChannels, int sampleRate, int? bufferSize, AsioProcessHandler handler)
        {
            _duplexHandler = handler;
            AsioStaDispatcher.Run(() =>
            {
                _device!.InitDuplex(new AsioDuplexOptions
                {
                    InputChannels = inputChannels,
                    OutputChannels = outputChannels,
                    SampleRate = sampleRate,
                    BufferSize = bufferSize,
                    Processor = OnDuplex,
                });
                FramesPerBuffer = _device.FramesPerBuffer;
                InputLatencySamples = _device.InputLatencySamples;
                OutputLatencySamples = _device.OutputLatencySamples;
            });
        }

        private void OnCaptured(object? sender, AsioAudioCapturedEventArgs e)
        {
            var h = _captureHandler;
            if (h == null) return;
            var blk = new AsioCaptureBlock(e);
            h(in blk);
        }

        private void OnDuplex(in AsioProcessBuffers buffer)
        {
            var h = _duplexHandler;
            if (h == null) return;
            var blk = new AsioFrameBlock(in buffer);
            h(in blk);
        }

        public void Start() => AsioStaDispatcher.Run(() => _device!.Start());

        public void Stop() => AsioStaDispatcher.Run(() =>
        {
            try { _device?.Stop(); } catch { }
        });

        public void ShowControlPanel() => AsioStaDispatcher.Run(() => _device!.ShowControlPanel(), 120000);

        public void Dispose()
        {
            AsioStaDispatcher.Run(() =>
            {
                var d = Interlocked.Exchange(ref _device, null);
                if (d == null) return;
                try { d.Stop(); } catch { }
                try { d.Dispose(); } catch { }
            });
        }
    }
}
