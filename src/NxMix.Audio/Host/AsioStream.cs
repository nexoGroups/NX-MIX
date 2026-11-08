using System.Diagnostics;
using NxMix.Audio.Dsp;
using NxMix.Audio.Interop;

namespace NxMix.Audio.Host;

/// <summary>Nativer Duplex-ASIO-Stream.
/// Im Master-Modus (IsMaster = true) treibt der ASIO-BufferSwitch-Callback direkt und ohne Ringpuffer/Resampling
/// MixCore.ProcessChunk an (minimale Hardware-Latenz, bitgenauer Takt).
/// Im Hilfs-Modus (IsMaster = false, z. B. zweites ASIO-Interface oder erzwungener WASAPI-Master) wird wie bei
/// Hilfs-WASAPI-Geraeten ueber SpscFloatRing + ClockMatchedReader adaptiv synchronisiert.</summary>
internal sealed class AsioStream : StreamBase
{
    private readonly DeviceBinding[] _bindings;
    private readonly string _driver;
    private IAsioSession? _device;
    private (int Device, int Engine)[] _inputs = Array.Empty<(int, int)>();
    private (int Device, int Engine)[] _outputs = Array.Empty<(int, int)>();
    private SpscFloatRing? _inRing, _outRing;
    private ClockMatchedReader? _inReader, _outReader;
    private float[] _inStage = Array.Empty<float>(), _outStage = Array.Empty<float>();
    private float[][] _inputTargets = Array.Empty<float[]>(), _outBuffers = Array.Empty<float[]>();
    private float[][] _masterEngineInputs = Array.Empty<float[]>();

    // Zustand fuer Master-Clock-Callback (nur auf dem ASIO-Callback-Thread benutzt)
    private readonly ManualResetEventSlim _masterDrainDone = new(false);
    private nint _cbMmcss;
    private bool _cbEnteredMmcss;
    private bool _fading;
    private bool _draining;
    private int _drainLeft;
    private double _load;
    private long _lastCallback;

    public bool IsMaster { get; }
    public string DriverName => _driver;
    public string PrimaryDeviceId => _bindings.FirstOrDefault()?.DeviceId ?? _driver;
    public IEnumerable<int> BoundInputs => _bindings.Where(b => b.Flow == DeviceFlow.Capture).SelectMany(b => Enumerable.Range(b.EngineFirstChannel, b.Count));
    public double OutputLatencyMs { get; private set; }
    public long Underruns => (_inReader?.Underruns ?? 0) + (_outReader?.Underruns ?? 0);
    public long Overruns => (_inReader?.Overruns ?? 0) + (_outReader?.Overruns ?? 0);

    public AsioStream(HostRun run, string driver, DeviceBinding[] bindings, bool isMaster)
        : base(run, null, driver)
    {
        _driver = driver;
        _bindings = bindings;
        IsMaster = isMaster;
    }

    public static bool CanActAsMaster(DeviceBinding[] bindings, int engineRate)
    {
        bool hasRender = bindings.Any(b => b.Flow == DeviceFlow.Render && b.Count > 0);
        if (!hasRender) return false;
        var rates = bindings.Where(b => b.SampleRate.HasValue).Select(b => b.SampleRate!.Value).Distinct().ToArray();
        return rates.Length == 0 || (rates.Length == 1 && rates[0] == engineRate);
    }

    protected override void OpenDevice()
    {
        _device = AsioBackendRegistry.Current.Open(_driver);
        _inputs = _bindings.Where(b => b.Flow == DeviceFlow.Capture)
            .SelectMany(b => Enumerable.Range(0, b.Count).Select(k => (b.DeviceFirstChannel + k, b.EngineFirstChannel + k))).ToArray();
        _outputs = _bindings.Where(b => b.Flow == DeviceFlow.Render)
            .SelectMany(b => Enumerable.Range(0, b.Count).Select(k => (b.DeviceFirstChannel + k, b.EngineFirstChannel + k))).ToArray();

        if (_inputs.Select(x => x.Device).Distinct().Count() != _inputs.Length ||
            _outputs.Select(x => x.Device).Distinct().Count() != _outputs.Length)
            throw new ArgumentException("ASIO-Gerätekanäle dürfen je Richtung nur einmal zugeordnet sein.");

        var cap = _device.Capabilities;
        if (_inputs.Any(x => x.Device >= cap.InputChannels))
            throw new AudioException(0, $"ASIO-Treiber '{_driver}' hat nur {cap.InputChannels} Eingänge.");
        if (_outputs.Any(x => x.Device >= cap.OutputChannels))
            throw new AudioException(0, $"ASIO-Treiber '{_driver}' hat nur {cap.OutputChannels} Ausgänge.");

        var rates = _bindings.Where(b => b.SampleRate.HasValue).Select(b => b.SampleRate!.Value).Distinct().ToArray();
        var sizes = _bindings.Where(b => b.BufferFrames.HasValue).Select(b => b.BufferFrames!.Value).Distinct().ToArray();
        if (rates.Length > 1 || sizes.Length > 1)
            throw new ArgumentException("Ein ASIO-Gerät benötigt eine gemeinsame Abtastrate und Puffergröße für Ein- und Ausgänge.");

        int rate = rates.FirstOrDefault();
        if (rate == 0)
        {
            if (IsMaster)
            {
                if (!_device.IsSampleRateSupported(Run.EngineRate))
                    throw new AudioException(0, $"Master-ASIO-Treiber '{_driver}' unterstützt {Run.EngineRate} Hz nicht.");
                rate = Run.EngineRate;
            }
            else
            {
                rate = _device.IsSampleRateSupported(Run.EngineRate) ? Run.EngineRate : _device.CurrentSampleRate;
            }
        }
        else if (IsMaster && rate != Run.EngineRate)
        {
            throw new AudioException(0, $"Master-ASIO-Treiber '{_driver}' ({rate} Hz) muss mit der Engine-Samplerate ({Run.EngineRate} Hz) laufen.");
        }

        int? buffer = sizes.Length == 0 ? null : sizes[0];
        _device.DriverResetRequest += (_, _) => Run.Fault(_driver + ": ASIO-Treibereinstellungen geändert (Reset angefordert).");
        _device.UnexpectedStop += (_, msg) =>
        {
            if (!Run.StopRequested && !Run.Aborting)
                Run.Fault(_driver + ": " + (msg ?? "ASIO wurde vom Treiber beendet."));
        };

        if (IsMaster)
        {
            _device.InitDuplex(
                _inputs.Select(x => x.Device).ToArray(),
                _outputs.Select(x => x.Device).ToArray(),
                rate,
                buffer,
                ProcessMaster);

            int period = Math.Max(1, _device.FramesPerBuffer);
            Run.MasterPeriodFrames = period;
            OutputLatencyMs = (period + Math.Max(0, _device.OutputLatencySamples)) * 1000.0 / rate;
            Run.OutputLatencyMs = OutputLatencyMs;
            return;
        }

        // Hilfs-ASIO-Modus (ueber SPSC-Ring + ClockMatchedReader)
        if (_outputs.Length == 0)
        {
            _device.InitRecording(
                _inputs.Select(x => x.Device).ToArray(),
                rate,
                buffer,
                ProcessAuxCapture);
        }
        else
        {
            _device.InitDuplex(
                _inputs.Select(x => x.Device).ToArray(),
                _outputs.Select(x => x.Device).ToArray(),
                rate,
                buffer,
                ProcessAuxDuplex);
        }

        int auxPeriod = Math.Max(1, _device.FramesPerBuffer);
        if (_inputs.Length > 0)
        {
            int target = auxPeriod * 2 + (int)Math.Ceiling(Run.Chunk * (double)rate / Run.EngineRate) + rate / 250;
            _inRing = new SpscFloatRing(_inputs.Length, target * 5);
            _inReader = new ClockMatchedReader(_inRing, rate / (double)Run.EngineRate, Run.EngineRate, target, Run.Chunk);
            _inStage = new float[auxPeriod * _inputs.Length];
        }
        if (_outputs.Length > 0)
        {
            int target = Run.Chunk * 2 + (int)Math.Ceiling(auxPeriod * (double)Run.EngineRate / rate) + Run.EngineRate / 250;
            _outRing = new SpscFloatRing(_outputs.Length, target * 5);
            _outReader = new ClockMatchedReader(_outRing, Run.EngineRate / (double)rate, rate, target, auxPeriod);
            _outStage = new float[Run.Chunk * _outputs.Length];
            _outBuffers = _outputs.Select(_ => new float[auxPeriod]).ToArray();
            OutputLatencyMs = target * 1000.0 / Run.EngineRate + _device.OutputLatencySamples * 1000.0 / rate;
        }
    }

    private void ProcessMaster(in AsioFrameBlock buffer)
    {
        int totalFrames = buffer.Frames;
        if (totalFrames <= 0) return;
        Volatile.Write(ref _lastCallback, Environment.TickCount64);

        if (!_cbEnteredMmcss)
        {
            _cbEnteredMmcss = true;
            _cbMmcss = Mmcss.Enter();
        }

        // Solange Launch() die Hilfs-Streams noch vorbereitet: exakte Stille ausgeben
        if (!Run.Go.IsSet || Run.Core == null)
        {
            for (int c = 0; c < _outputs.Length; c++)
                buffer.GetOutput(c).Clear();
            return;
        }

        long t0 = Stopwatch.GetTimestamp();
        MixCore core = Run.Core;
        int chunk = Run.Chunk;

        if (!_fading && (Run.StopRequested || Run.IsFaulted))
        {
            _fading = true;
            core.BeginFadeOut();
        }

        int offset = 0;
        while (offset < totalFrames)
        {
            int n = Math.Min(chunk, totalFrames - offset);
            if (_draining)
            {
                for (int c = 0; c < _outputs.Length; c++)
                    buffer.GetOutput(c).Slice(offset, n).Clear();
                _drainLeft -= n;
            }
            else
            {
                for (int c = 0; c < _inputs.Length; c++)
                {
                    ReadOnlySpan<float> src = buffer.GetInput(c).Slice(offset, n);
                    float[] dst = _masterEngineInputs[_inputs[c].Engine];
                    for (int f = 0; f < n; f++)
                    {
                        float v = src[f];
                        dst[f] = float.IsFinite(v) ? v : 0f;
                    }
                }

                core.ProcessChunk(n);

                for (int c = 0; c < _outputs.Length; c++)
                {
                    ReadOnlySpan<float> src = core.Outputs[_outputs[c].Engine].AsSpan(0, n);
                    Span<float> dst = buffer.GetOutput(c).Slice(offset, n);
                    for (int f = 0; f < n; f++)
                    {
                        float v = src[f];
                        dst[f] = float.IsFinite(v) ? v : 0f;
                    }
                }

                if (_fading && core.FadeOutDone)
                {
                    _draining = true;
                    _drainLeft = Math.Max(totalFrames, chunk);
                }
            }
            offset += n;
        }

        double budget = (double)totalFrames / Run.EngineRate * Stopwatch.Frequency;
        if (budget > 0)
        {
            double inst = (Stopwatch.GetTimestamp() - t0) / budget;
            _load += (inst - _load) * 0.1;
            Volatile.Write(ref Run.CpuLoad, _load);
        }

        ProbeAllocation();

        if (_draining && _drainLeft <= 0)
        {
            _masterDrainDone.Set();
            Native.SetEvent(Run.StopEvent);
        }
    }

    private void ProcessAuxCapture(in AsioCaptureBlock e)
    {
        if (Run.IsFaulted || e.Frames <= 0) return;
        Volatile.Write(ref _lastCallback, Environment.TickCount64);
        int maxFrames = _inStage.Length / Math.Max(1, _inputs.Length);
        int frames = Math.Min(e.Frames, maxFrames);
        for (int c = 0; c < _inputs.Length; c++)
        {
            var src = e.GetChannel(c);
            for (int f = 0; f < frames; f++)
                _inStage[f * _inputs.Length + c] = float.IsFinite(src[f]) ? src[f] : 0f;
        }
        _inRing!.Write(_inStage, frames);
        ProbeAllocation();
    }

    private void ProcessAuxDuplex(in AsioFrameBlock buffer)
    {
        if (Run.IsFaulted || buffer.Frames <= 0) return;
        Volatile.Write(ref _lastCallback, Environment.TickCount64);
        int frames = buffer.Frames;
        if (_inputs.Length > 0)
        {
            int inFrames = Math.Min(frames, _inStage.Length / _inputs.Length);
            for (int c = 0; c < _inputs.Length; c++)
            {
                var source = buffer.GetInput(c);
                for (int f = 0; f < inFrames; f++)
                    _inStage[f * _inputs.Length + c] = float.IsFinite(source[f]) ? source[f] : 0f;
            }
            _inRing!.Write(_inStage, inFrames);
        }
        int outFrames = Math.Min(frames, _outBuffers[0].Length);
        _outReader!.Read(_outBuffers, 0, outFrames);
        for (int c = 0; c < _outputs.Length; c++)
        {
            _outBuffers[c].AsSpan(0, outFrames).CopyTo(buffer.GetOutput(c).Slice(0, outFrames));
            if (outFrames < frames) buffer.GetOutput(c).Slice(outFrames).Clear();
        }
        ProbeAllocation();
    }

    public void Bind(float[][] inputs)
    {
        if (IsMaster) _masterEngineInputs = inputs;
        else _inputTargets = _inputs.Select(i => inputs[i.Engine]).ToArray();
    }

    public void ReadInputs(int frames)
    {
        if (!IsMaster) _inReader?.Read(_inputTargets, 0, frames);
    }

    public void WriteOutputs(float[][] output, int frames)
    {
        if (IsMaster || _outRing == null) return;
        for (int c = 0; c < _outputs.Length; c++)
        {
            var source = output[_outputs[c].Engine];
            for (int f = 0; f < frames; f++)
                _outStage[f * _outputs.Length + c] = float.IsFinite(source[f]) ? source[f] : 0f;
        }
        _outRing.Write(_outStage, frames);
    }

    protected override void StartDevice()
    {
        Volatile.Write(ref _lastCallback, Environment.TickCount64);
        _device!.Start();
    }

    protected override void RunLoop()
    {
        while (!Run.StopRequested && !Run.IsFaulted)
        {
            if (Native.WaitForSingleObject(Run.StopEvent, 100) == Native.WaitObject0)
                break;
            if (Environment.TickCount64 - Volatile.Read(ref _lastCallback) > 3000)
            {
                Run.Fault(_driver + ": ASIO liefert keine Audio-Blöcke mehr.");
                break;
            }
        }
        if (IsMaster && Run.StopRequested && !Run.IsFaulted)
            _masterDrainDone.Wait(400);
    }

    protected override void CloseDevice()
    {
        try { _device?.Stop(); }
        finally
        {
            _device?.Dispose();
            if (_cbMmcss != 0)
            {
                Mmcss.Leave(_cbMmcss);
                _cbMmcss = 0;
            }
        }
    }

    protected override void OnExit()
    {
        if (IsMaster) Native.SetEvent(Run.StopEvent);
    }

    public override DeviceStat GetStat()
    {
        if (IsMaster)
        {
            return new DeviceStat(_bindings[0].DeviceId, _driver, 1.0, 0.0,
                Interlocked.Read(ref Run.MasterUnderruns), IsMasterClock: true);
        }
        var r = _outReader ?? _inReader;
        return new DeviceStat(_bindings[0].DeviceId, _driver, r?.FillFraction ?? 0, r?.CorrectionPpm ?? 0,
            (_inReader?.Underruns ?? 0) + (_inReader?.Overruns ?? 0) + (_outReader?.Underruns ?? 0) + (_outReader?.Overruns ?? 0));
    }
}
