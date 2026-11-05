using NxMix.Audio.Dsp;
using NxMix.Audio.Interop;

namespace NxMix.Audio.Host;

/// <summary>Parameter, die eine Spur vom Host braucht (damit sie auch ohne HostRun testbar ist).</summary>
internal readonly record struct LaneContext(int EngineRate, int Chunk, int MasterPeriodFrames, Func<bool> ShouldAbort);

/// <summary>Eine Aufnahme-Spur: Prozess-Loopback EINES Prozessbaums (Wurzel-PID) -> Ringpuffer -> taktangepasster Leser (adaptives Resampling).
/// Der Geraete-Thread schreibt, der Master-Thread liest (MixCore -> AppLinkSlot). Format: float32 stereo; bei ringChannels = 1 wird auf mono gemischt.
/// Underruns (die App spielt gerade nichts) ergeben Stille. Im eingeschwungenen Zustand keine Allokationen im Thread.</summary>
internal sealed unsafe class AppLane
{
    public const int OpenTimeoutMs = 10000;

    private readonly LaneContext _ctx;
    private readonly nint _stopEvent;
    private readonly ManualResetEventSlim _opened = new(false);
    private Thread? _thread;
    private ProcessLoopbackEndpoint? _ep;
    private float[] _stage = Array.Empty<float>();
    private float[] _zeros = Array.Empty<float>();
    private volatile string? _error;
    private volatile bool _dead;

    private long _allocBytes;
    private int _probeCount;
    private long _probeBase;

    public int Pid { get; }
    public int RingChannels { get; }
    public int SampleRate { get; private set; }
    public SpscFloatRing? Ring { get; private set; }
    public ClockMatchedReader? Reader { get; private set; }
    public string? Error => _error;

    /// <summary>true, solange der Thread laeuft und kein Fehler auftrat.</summary>
    public bool IsAlive => !_dead && (_thread == null || _thread.IsAlive);

    /// <summary>Diagnose: auf dem GC-Heap allokierte Bytes des Threads nach dem Einschwingen. Soll 0 sein.</summary>
    public long SteadyStateAllocatedBytes => Volatile.Read(ref _allocBytes);

    private AppLane(int pid, int ringChannels, LaneContext ctx)
    {
        Pid = pid;
        RingChannels = ringChannels;
        _ctx = ctx;
        _stopEvent = Native.CreateEvent(0, 1, 0, null);
    }

    /// <summary>Oeffnet den Prozess-Loopback auf einem eigenen Thread und wartet, bis er offen ist. Wirft AudioException/TimeoutException bei Fehlern.</summary>
    public static AppLane Start(int pid, int ringChannels, LaneContext ctx)
    {
        var lane = new AppLane(pid, ringChannels, ctx);
        try
        {
            lane._thread = new Thread(lane.ThreadMain, 512 * 1024)
            {
                IsBackground = true,
                Priority = ThreadPriority.Highest,
                Name = "NxMix.AppLane." + pid,
            };
            lane._thread.SetApartmentState(ApartmentState.MTA);
            lane._thread.Start();
            if (!lane._opened.Wait(OpenTimeoutMs))
                throw new TimeoutException($"Prozess-Loopback fuer PID {pid} antwortet beim Oeffnen nicht.");
            if (lane._error != null)
                throw new AudioException(0, lane._error);
            return lane;
        }
        catch
        {
            lane.Stop();
            throw;
        }
    }

    /// <summary>Spur ohne Geraet und ohne Thread (nur Ringpuffer + Leser): fuer Tests der Mischlogik.</summary>
    internal static AppLane CreateDetached(int pid, int ringChannels, int sampleRate, LaneContext ctx)
    {
        var lane = new AppLane(pid, ringChannels, ctx);
        lane.BuildRing(sampleRate);
        return lane;
    }

    private void BuildRing(int sampleRate)
    {
        SampleRate = sampleRate;
        double scale = sampleRate / (double)_ctx.EngineRate;
        int producer = Math.Max(1, sampleRate / 100); // Paketgroesse des Prozess-Loopbacks ~10 ms
        int consumer = Math.Max(1, (int)Math.Ceiling(_ctx.MasterPeriodFrames * scale));
        int target = (producer + consumer) / 2 + consumer + sampleRate * 4 / 1000;
        Ring = new SpscFloatRing(RingChannels, target * 5);
        Reader = new ClockMatchedReader(Ring, scale, _ctx.EngineRate, target, _ctx.Chunk, countUnderruns: false);
    }

    private void ThreadMain()
    {
        nint mmcss = Mmcss.Enter();
        try
        {
            try
            {
                _ep = ProcessLoopbackEndpoint.Open(Pid, _ctx.EngineRate);
                BuildRing(_ep.SampleRate);
                int frames = (int)Math.Max(_ep.BufferFrames, 64u);
                _stage = new float[frames * RingChannels];
                _zeros = new float[frames * 2];
                _ep.Start();
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                _dead = true;
                return;
            }
            finally
            {
                _opened.Set();
            }

            RunLoop();
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            _dead = true;
        }
        finally
        {
            _dead = true;
            try { _ep?.Dispose(); } catch { /* egal */ }
            Mmcss.Leave(mmcss);
        }
    }

    private void RunLoop()
    {
        ProcessLoopbackEndpoint ep = _ep!;
        IAudioCaptureClient cap = ep.CaptureClient!;
        nint* handles = stackalloc nint[2];
        handles[0] = ep.EventHandle;
        handles[1] = _stopEvent;
        int stageFrames = _stage.Length / RingChannels;
        int maxFrames = _zeros.Length / 2;
        SpscFloatRing ring = Ring!;
        int rc = RingChannels;

        while (!_ctx.ShouldAbort())
        {
            if (ep.EventDriven)
            {
                uint r = Native.WaitForMultipleObjects(2, handles, 0, 100);
                if (r == Native.WaitObject0 + 1) break;
                if (r == Native.WaitFailed) throw new AudioException(0, "Warten auf das Prozess-Loopback-Event ist fehlgeschlagen.");
            }
            else if (Native.WaitForSingleObject(_stopEvent, 3) == Native.WaitObject0)
            {
                break;
            }

            while (true)
            {
                Hr.Check(cap.GetNextPacketSize(out uint packet), "GetNextPacketSize (Prozess-Loopback)");
                if (packet == 0) break;
                int hr = cap.GetBuffer(out nint data, out uint frames, out uint flags, out _, out _);
                if (hr == Hr.AudclntSBufferEmpty) break;
                Hr.Check(hr, "GetBuffer (Prozess-Loopback)");

                int nf = (int)Math.Min(frames, (uint)Math.Min(stageFrames, maxFrames));
                bool silent = (flags & BufferFlags.Silent) != 0;
                if (rc == 2)
                {
                    ReadOnlySpan<float> src = silent
                        ? new ReadOnlySpan<float>(_zeros, 0, nf * 2)
                        : new ReadOnlySpan<float>((void*)data, nf * 2);
                    ring.Write(src, nf);
                }
                else
                {
                    fixed (float* st = _stage)
                    {
                        if (silent) new Span<float>(st, nf).Clear();
                        else
                        {
                            float* s = (float*)data;
                            for (int i = 0; i < nf; i++) st[i] = 0.5f * (s[2 * i] + s[2 * i + 1]);
                        }
                    }
                    ring.Write(_stage, nf);
                }
                Hr.Check(cap.ReleaseBuffer(frames), "ReleaseBuffer (Prozess-Loopback)");
            }
            ProbeAllocation();
        }
    }

    private void ProbeAllocation()
    {
        int n = ++_probeCount;
        if (n == 40) _probeBase = GC.GetAllocatedBytesForCurrentThread();
        else if (n > 40) Volatile.Write(ref _allocBytes, GC.GetAllocatedBytesForCurrentThread() - _probeBase);
    }

    /// <summary>Beendet den Thread (wartet bis zu 3 s) und gibt alles frei. Mehrfach aufrufbar.</summary>
    public void Stop()
    {
        if (_stopEvent != 0) Native.SetEvent(_stopEvent);
        Thread? t = _thread;
        bool joined = true;
        if (t is { IsAlive: true } && t != Thread.CurrentThread) joined = t.Join(3000);
        if (joined && _stopEvent != 0 && Interlocked.Exchange(ref _stopClosed, 1) == 0) Native.CloseHandle(_stopEvent);
        _dead = true;
    }

    private int _stopClosed;
}
