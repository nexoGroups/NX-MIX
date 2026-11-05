using System.Diagnostics;
using NxMix.Audio.Dsp;
using NxMix.Audio.Interop;

namespace NxMix.Audio.Host;

/// <summary>Ein Betriebs-Thread (hohe Prioritaet + MMCSS) fuer ein Geraet bzw. den Software-Takt.
/// Ablauf: oeffnen (auf dem Thread, MTA) -> melden -> auf "Go" warten -> starten -> Schleife -> aufraeumen.</summary>
internal abstract class StreamBase
{
    protected readonly HostRun Run;
    public readonly StreamSpec? Spec;
    public readonly ManualResetEventSlim Opened = new(false);
    public volatile string? OpenError;
    public volatile string Name;
    public Thread? Thread;

    private long _allocBytes;
    private int _probeCount;
    private long _probeBase;

    protected StreamBase(HostRun run, StreamSpec? spec, string name)
    {
        Run = run;
        Spec = spec;
        Name = name;
    }

    public abstract DeviceStat GetStat();

    /// <summary>Nur Diagnose: der geoeffnete Geraete-Endpoint (Format, Periode, Modus).</summary>
    public virtual WasapiEndpoint? Endpoint => null;

    /// <summary>Diagnose: Bytes, die dieser Thread nach dem Einschwingen (40 Schleifendurchlaeufe) auf dem GC-Heap allokiert hat. Soll 0 sein.</summary>
    public long SteadyStateAllocatedBytes => Volatile.Read(ref _allocBytes);

    /// <summary>Einmal pro Schleifendurchlauf des Audio-Threads aufrufen (kostet wenige Nanosekunden, allokiert nicht).</summary>
    protected void ProbeAllocation()
    {
        int n = ++_probeCount;
        if (n == 40) _probeBase = GC.GetAllocatedBytesForCurrentThread();
        else if (n > 40) Volatile.Write(ref _allocBytes, GC.GetAllocatedBytesForCurrentThread() - _probeBase);
    }

    protected abstract void OpenDevice();
    protected abstract void StartDevice();
    protected abstract void RunLoop();
    protected abstract void CloseDevice();
    protected virtual void OnExit() { }

    public void StartThread()
    {
        Thread = new Thread(ThreadMain, 512 * 1024)
        {
            IsBackground = true,
            Priority = ThreadPriority.Highest,
            Name = "NxMix.Audio." + Name,
        };
        Thread.SetApartmentState(ApartmentState.MTA);
        Thread.Start();
    }

    private void ThreadMain()
    {
        nint mmcss = Mmcss.Enter();
        try
        {
            try
            {
                OpenDevice();
            }
            catch (Exception ex)
            {
                OpenError = ex.Message;
                Opened.Set();
                return;
            }
            Opened.Set();

            Run.Go.Wait();
            if (Run.Aborting) return;

            StartDevice();
            RunLoop();
        }
        catch (Exception ex)
        {
            if (!Opened.IsSet)
            {
                OpenError = ex.Message;
                Opened.Set();
            }
            else
            {
                Run.Fault($"{Name}: {ex.Message}");
            }
        }
        finally
        {
            try { CloseDevice(); } catch { /* Geraet evtl. schon weg */ }
            Mmcss.Leave(mmcss);
            try { OnExit(); } catch { /* egal */ }
        }
    }

    protected void ValidateMaps(WasapiEndpoint ep)
    {
        foreach (Mapping m in Spec!.Maps)
        {
            if (m.DeviceFirstChannel + m.Count > ep.DeviceChannels)
                throw new AudioException(0, $"Geraet '{ep.Name}' hat nur {ep.DeviceChannels} Kanaele, die Bindung braucht Kanal {m.DeviceFirstChannel + m.Count}.");
        }
    }

    protected static unsafe void PrimeSilence(WasapiEndpoint ep)
    {
        IAudioRenderClient rc = ep.RenderClient!;
        Hr.Check(rc.GetBuffer(ep.BufferFrames, out nint data), "GetBuffer (Vorfuellen)");
        new Span<byte>((void*)data, (int)ep.BufferFrames * ep.BlockAlign).Clear();
        Hr.Check(rc.ReleaseBuffer(ep.BufferFrames, BufferFlags.Silent), "ReleaseBuffer (Vorfuellen)");
    }
}

// =============================================================================================================
// Master: ein Render-Geraet. Sein Event treibt den Block (Quellen ziehen -> Prozessor -> Ausgang schreiben).
// =============================================================================================================
internal sealed unsafe class MasterDeviceStream : StreamBase
{
    private WasapiEndpoint _ep = null!;

    public override WasapiEndpoint? Endpoint => _ep;
    private double _fillSample;

    public MasterDeviceStream(HostRun run, StreamSpec spec) : base(run, spec, spec.DeviceId) { }

    public override DeviceStat GetStat() =>
        new(Spec!.DeviceId, Name, Volatile.Read(ref _fillSample), 0.0, Interlocked.Read(ref Run.MasterUnderruns));

    protected override void OpenDevice()
    {
        _ep = WasapiEndpoint.Open(Spec!.DeviceId, capture: false, loopback: false, Run.EngineRate, Run.Chunk, Run.Config.UseExclusiveMode);
        Name = _ep.Name;
        if (_ep.SampleRate != Run.EngineRate)
            throw new AudioException(0, $"Master-Geraet '{_ep.Name}' laeuft nicht mit {Run.EngineRate} Hz (nur {_ep.SampleRate} Hz moeglich).");
        ValidateMaps(_ep);
        Run.MasterPeriodFrames = Math.Max(1, _ep.PeriodFrames);
        Run.OutputLatencyMs = _ep.BufferFrames * 1000.0 / _ep.SampleRate + _ep.StreamLatencyMs;
        PrimeSilence(_ep);
    }

    protected override void StartDevice() => _ep.Start();

    protected override void CloseDevice() => _ep?.Dispose();

    protected override void OnExit() => Native.SetEvent(Run.StopEvent); // Master weg -> alle anderen beenden

    protected override void RunLoop()
    {
        WasapiEndpoint ep = _ep;
        IAudioClient client = ep.Client;
        IAudioRenderClient render = ep.RenderClient!;
        MixCore core = Run.Core;
        nint* handles = stackalloc nint[2];
        handles[0] = ep.EventHandle;
        handles[1] = Run.StopEvent;

        int rate = Run.EngineRate;
        int chunk = Run.Chunk;
        int blockAlign = ep.BlockAlign;
        SampleKind kind = ep.Kind;
        uint bufFrames = ep.BufferFrames;
        bool exclusive = ep.Exclusive;
        Mapping[] maps = Spec!.Maps;
        long freq = Stopwatch.Frequency;

        int timeouts = 0, events = 0;
        bool fading = false, draining = false;
        int drainLeft = 0;
        double load = 0;

        while (true)
        {
            uint r = Native.WaitForMultipleObjects(2, handles, 0, 1000);
            if (r == Native.WaitObject0 + 1) break; // hartes Stop
            if (r == Native.WaitTimeout)
            {
                if (++timeouts >= 3) throw new AudioException(0, "Das Geraet liefert keinen Takt mehr (Event-Timeout).");
                if (!exclusive) Hr.Check(client.GetCurrentPadding(out _), "GetCurrentPadding");
                continue;
            }
            if (r != Native.WaitObject0) throw new AudioException(0, "Warten auf das Geraete-Event ist fehlgeschlagen.");
            timeouts = 0;

            long t0 = Stopwatch.GetTimestamp();
            int avail;
            if (exclusive)
            {
                avail = (int)bufFrames;
            }
            else
            {
                Hr.Check(client.GetCurrentPadding(out uint pad), "GetCurrentPadding");
                if (pad == 0 && events > 4) Interlocked.Increment(ref Run.MasterUnderruns); // Geraet war leergelaufen
                avail = (int)(bufFrames - pad);
                Volatile.Write(ref _fillSample, pad / (double)bufFrames);
            }
            events++;

            if (!fading && (Run.StopRequested || Run.IsFaulted))
            {
                fading = true;
                core.BeginFadeOut();
            }

            int total = 0;
            while (avail > 0)
            {
                int n = Math.Min(avail, chunk);
                if (draining)
                {
                    Hr.Check(render.GetBuffer((uint)n, out nint zeros), "GetBuffer");
                    new Span<byte>((void*)zeros, n * blockAlign).Clear();
                    Hr.Check(render.ReleaseBuffer((uint)n, BufferFlags.Silent), "ReleaseBuffer");
                    drainLeft -= n;
                }
                else
                {
                    core.ProcessChunk(n);
                    Hr.Check(render.GetBuffer((uint)n, out nint data), "GetBuffer");
                    byte* p = (byte*)data;
                    new Span<byte>(p, n * blockAlign).Clear();
                    foreach (Mapping m in maps)
                    {
                        for (int k = 0; k < m.Count; k++)
                        {
                            fixed (float* src = core.Outputs[m.EngineFirstChannel + k])
                                PcmConvert.WriteChannel(src, n, p, blockAlign, m.DeviceFirstChannel + k, kind);
                        }
                    }
                    Hr.Check(render.ReleaseBuffer((uint)n, 0), "ReleaseBuffer");
                    if (fading && core.FadeOutDone)
                    {
                        // Ausgeblendet: noch einen kompletten Geraetepuffer Stille nachschieben, damit nichts mitten im Signal abgeschnitten wird.
                        draining = true;
                        drainLeft = (int)bufFrames + chunk;
                    }
                }
                avail -= n;
                total += n;
            }

            if (total > 0)
            {
                double budget = (double)total / rate * freq;
                double inst = (Stopwatch.GetTimestamp() - t0) / budget;
                load += (inst - load) * 0.1;
                Volatile.Write(ref Run.CpuLoad, load);
            }

            ProbeAllocation();
            if (draining && drainLeft <= 0) break;
        }
    }
}

// =============================================================================================================
// Master ohne Render-Geraet: Software-Takt (hochaufloesender Waitable Timer). Ausgaenge werden verworfen.
// =============================================================================================================
internal sealed unsafe class MasterTimerStream : StreamBase
{
    private nint _timer;
    private bool _periodRaised;

    public MasterTimerStream(HostRun run) : base(run, null, "Software-Takt") { }

    public override DeviceStat GetStat() => new("(Software-Takt)", Name, 0.0, 0.0, 0);

    protected override void OpenDevice()
    {
        Run.MasterPeriodFrames = Run.Chunk;
        _timer = Native.CreateWaitableTimerEx(0, null, 0x00000002 /* CREATE_WAITABLE_TIMER_HIGH_RESOLUTION */, 0x1F0003);
        if (_timer == 0)
        {
            _timer = Native.CreateWaitableTimerEx(0, null, 0, 0x1F0003);
            if (_timer == 0) throw new AudioException(0, "Software-Takt (Waitable Timer) konnte nicht erzeugt werden.");
            Native.timeBeginPeriod(1);
            _periodRaised = true;
        }
        Run.OutputLatencyMs = 0;
    }

    protected override void StartDevice() { }

    protected override void CloseDevice()
    {
        if (_timer != 0) { Native.CloseHandle(_timer); _timer = 0; }
        if (_periodRaised) { Native.timeEndPeriod(1); _periodRaised = false; }
    }

    protected override void OnExit() => Native.SetEvent(Run.StopEvent);

    protected override void RunLoop()
    {
        MixCore core = Run.Core;
        nint* handles = stackalloc nint[2];
        handles[0] = _timer;
        handles[1] = Run.StopEvent;
        int chunk = Run.Chunk;
        long freq = Stopwatch.Frequency;
        double ticksPerChunk = (double)chunk / Run.EngineRate * freq;
        double next = Stopwatch.GetTimestamp();
        double load = 0;

        while (!Run.StopRequested && !Run.IsFaulted)
        {
            next += ticksPerChunk;
            long now = Stopwatch.GetTimestamp();
            long remain = (long)next - now;
            if (remain > 0)
            {
                long due = -(long)(remain * 1e7 / freq); // relativ, in 100 ns
                Native.SetWaitableTimer(_timer, &due, 0, 0, 0, 0);
                uint r = Native.WaitForMultipleObjects(2, handles, 0, 1000);
                if (r == Native.WaitObject0 + 1) break;
                if (r == Native.WaitFailed) throw new AudioException(0, "Warten auf den Software-Takt ist fehlgeschlagen.");
            }
            else if (-remain > freq / 5)
            {
                next = now; // weit im Rueckstand: neu aufsetzen
            }

            long t0 = Stopwatch.GetTimestamp();
            core.ProcessChunk(chunk);
            double inst = (Stopwatch.GetTimestamp() - t0) / ticksPerChunk;
            load += (inst - load) * 0.1;
            Volatile.Write(ref Run.CpuLoad, load);
            ProbeAllocation();
        }
    }
}

// =============================================================================================================
// Weitere Capture-/Loopback-Geraete: Geraete-Thread schreibt in den Ringpuffer, der Master liest taktangepasst.
// =============================================================================================================
internal sealed unsafe class CaptureStream : StreamBase
{
    private WasapiEndpoint _ep = null!;

    public override WasapiEndpoint? Endpoint => _ep;
    private float[] _stage = Array.Empty<float>();
    private long _discontinuities;

    public CaptureStream(HostRun run, StreamSpec spec) : base(run, spec, spec.DeviceId) { }

    public SpscFloatRing? Ring { get; private set; }
    public ClockMatchedReader? Reader { get; private set; }

    /// <summary>Planare Zielpuffer je Ring-Slot (verweisen direkt auf die Engine-Eingaenge). Vom MixCore gesetzt.</summary>
    public float[][]? Dst { get; set; }

    public override DeviceStat GetStat()
    {
        ClockMatchedReader? r = Reader;
        if (r == null) return new DeviceStat(Spec!.DeviceId, Name, 0, 0, 0);
        return new DeviceStat(Spec!.DeviceId, Name, r.FillFraction, r.CorrectionPpm,
            r.Underruns + r.Overruns + Interlocked.Read(ref _discontinuities));
    }

    protected override void OpenDevice()
    {
        _ep = WasapiEndpoint.Open(Spec!.DeviceId, capture: true, Spec.Loopback, Run.EngineRate, Run.Chunk, Run.Config.UseExclusiveMode);
        Name = _ep.Name;
        ValidateMaps(_ep);

        int rate = _ep.SampleRate;
        double scale = rate / (double)Run.EngineRate;
        // Sollwert des MITTLEREN Fuellstands: halbe Summe der Paketgroessen (Saegezahnmitte) + ein Verbrauchsburst + Jitter-Reserve (4 ms)
        int producer = _ep.PeriodFrames;
        int consumer = (int)Math.Ceiling(Run.MasterPeriodFrames * scale);
        int target = (producer + consumer) / 2 + consumer + rate * 4 / 1000;
        Ring = new SpscFloatRing(Spec.Slots, target * 5);
        Reader = new ClockMatchedReader(Ring, scale, Run.EngineRate, target, Run.Chunk, countUnderruns: !Spec.Loopback);
        _stage = new float[Math.Max(1, (int)_ep.BufferFrames) * Spec.Slots];
    }

    protected override void StartDevice() => _ep.Start();

    protected override void CloseDevice() => _ep?.Dispose();

    protected override void RunLoop()
    {
        WasapiEndpoint ep = _ep;
        IAudioCaptureClient cap = ep.CaptureClient!;
        nint* handles = stackalloc nint[2];
        handles[0] = ep.EventHandle;
        handles[1] = Run.StopEvent;
        int w = Spec!.Slots;
        int blockAlign = ep.BlockAlign;
        SampleKind kind = ep.Kind;
        Mapping[] maps = Spec.Maps;
        int stageFrames = _stage.Length / w;
        bool first = true;

        while (true)
        {
            if (ep.EventDriven)
            {
                uint r = Native.WaitForMultipleObjects(2, handles, 0, 100);
                if (r == Native.WaitObject0 + 1) break;
                if (r == Native.WaitFailed) throw new AudioException(0, "Warten auf das Geraete-Event ist fehlgeschlagen.");
            }
            else
            {
                if (Native.WaitForSingleObject(Run.StopEvent, 3) == Native.WaitObject0) break; // Polling
            }

            while (true)
            {
                Hr.Check(cap.GetNextPacketSize(out uint packet), "GetNextPacketSize");
                if (packet == 0) break;
                int hr = cap.GetBuffer(out nint data, out uint frames, out uint flags, out _, out _);
                if (hr == Hr.AudclntSBufferEmpty) break;
                Hr.Check(hr, "GetBuffer");

                int nf = (int)Math.Min(frames, (uint)stageFrames);
                fixed (float* st = _stage)
                {
                    if ((flags & BufferFlags.Silent) != 0)
                    {
                        new Span<float>(st, nf * w).Clear();
                    }
                    else
                    {
                        foreach (Mapping m in maps)
                            for (int k = 0; k < m.Count; k++)
                                PcmConvert.ReadChannel((byte*)data, nf, blockAlign, m.DeviceFirstChannel + k, kind, st + m.SlotOffset + k, w);
                    }
                }
                if ((flags & BufferFlags.DataDiscontinuity) != 0 && !first && !Spec.Loopback)
                    Interlocked.Increment(ref _discontinuities);
                first = false;

                Ring!.Write(_stage, nf);
                Hr.Check(cap.ReleaseBuffer(frames), "ReleaseBuffer");
            }
            ProbeAllocation();
        }
    }
}

// =============================================================================================================
// Weitere Render-Geraete: der Master schreibt in den Ringpuffer, der Geraete-Thread liest taktangepasst.
// =============================================================================================================
internal sealed unsafe class RenderStream : StreamBase
{
    private WasapiEndpoint _ep = null!;

    public override WasapiEndpoint? Endpoint => _ep;
    private float[][] _slots = Array.Empty<float[]>();

    public RenderStream(HostRun run, StreamSpec spec) : base(run, spec, spec.DeviceId) { }

    public SpscFloatRing? Ring { get; private set; }
    public ClockMatchedReader? Reader { get; private set; }

    /// <summary>Zwischenpuffer des Masters (verschachtelt), in den die Ausgaenge vor dem Schreiben in den Ring gelegt werden.</summary>
    public float[]? Stage { get; private set; }

    public override DeviceStat GetStat()
    {
        ClockMatchedReader? r = Reader;
        if (r == null) return new DeviceStat(Spec!.DeviceId, Name, 0, 0, 0);
        return new DeviceStat(Spec!.DeviceId, Name, r.FillFraction, r.CorrectionPpm, r.Underruns + r.Overruns);
    }

    protected override void OpenDevice()
    {
        _ep = WasapiEndpoint.Open(Spec!.DeviceId, capture: false, loopback: false, Run.EngineRate, Run.Chunk, Run.Config.UseExclusiveMode);
        Name = _ep.Name;
        ValidateMaps(_ep);

        int rate = _ep.SampleRate;
        int engineRate = Run.EngineRate;
        // Ring liegt in Engine-Frames. Sollwert des mittleren Fuellstands wie beim Capture (Produzent = Master, Konsument = dieses Geraet)
        int producer = Run.MasterPeriodFrames;
        int consumer = (int)Math.Ceiling(_ep.PeriodFrames * (double)engineRate / rate);
        int target = (producer + consumer) / 2 + consumer + engineRate * 4 / 1000;
        Ring = new SpscFloatRing(Spec.Slots, target * 5);
        Reader = new ClockMatchedReader(Ring, engineRate / (double)rate, rate, target, (int)_ep.BufferFrames);
        _slots = new float[Spec.Slots][];
        for (int i = 0; i < _slots.Length; i++) _slots[i] = new float[_ep.BufferFrames];
        Stage = new float[Run.Chunk * Spec.Slots];
        PrimeSilence(_ep);
    }

    protected override void StartDevice() => _ep.Start();

    protected override void CloseDevice() => _ep?.Dispose();

    protected override void RunLoop()
    {
        WasapiEndpoint ep = _ep;
        IAudioClient client = ep.Client;
        IAudioRenderClient render = ep.RenderClient!;
        ClockMatchedReader reader = Reader!;
        nint* handles = stackalloc nint[2];
        handles[0] = ep.EventHandle;
        handles[1] = Run.StopEvent;
        int blockAlign = ep.BlockAlign;
        SampleKind kind = ep.Kind;
        uint bufFrames = ep.BufferFrames;
        bool exclusive = ep.Exclusive;
        Mapping[] maps = Spec!.Maps;
        int timeouts = 0;

        while (true)
        {
            uint r = Native.WaitForMultipleObjects(2, handles, 0, 1000);
            if (r == Native.WaitObject0 + 1) break;
            if (r == Native.WaitTimeout)
            {
                if (++timeouts >= 3) throw new AudioException(0, "Das Geraet liefert keinen Takt mehr (Event-Timeout).");
                if (!exclusive) Hr.Check(client.GetCurrentPadding(out _), "GetCurrentPadding");
                continue;
            }
            if (r != Native.WaitObject0) throw new AudioException(0, "Warten auf das Geraete-Event ist fehlgeschlagen.");
            timeouts = 0;

            int avail;
            if (exclusive)
            {
                avail = (int)bufFrames;
            }
            else
            {
                Hr.Check(client.GetCurrentPadding(out uint pad), "GetCurrentPadding");
                avail = (int)(bufFrames - pad);
            }
            if (avail <= 0) continue;

            reader.Read(_slots, 0, avail); // schreibt immer alle Frames (bei Unterlauf Stille)

            Hr.Check(render.GetBuffer((uint)avail, out nint data), "GetBuffer");
            byte* p = (byte*)data;
            new Span<byte>(p, avail * blockAlign).Clear();
            foreach (Mapping m in maps)
            {
                for (int k = 0; k < m.Count; k++)
                {
                    fixed (float* src = _slots[m.SlotOffset + k])
                        PcmConvert.WriteChannel(src, avail, p, blockAlign, m.DeviceFirstChannel + k, kind);
                }
            }
            Hr.Check(render.ReleaseBuffer((uint)avail, 0), "ReleaseBuffer");
            ProbeAllocation();
        }
    }
}
