using NAudio.Wave;
using NxMix.Audio.Dsp;
using NxMix.Audio.Interop;

namespace NxMix.Audio.Host;

/// <summary>One native duplex ASIO driver, bridged to the common mixer clock through adaptive rings.
/// Input and output bindings for the same driver share one driver instance and sample rate.</summary>
internal sealed class AsioStream : StreamBase
{
    readonly DeviceBinding[] _bindings;
    readonly string _driver;
    AsioDevice? _device;
    (int Device, int Engine)[] _inputs = Array.Empty<(int, int)>(), _outputs = Array.Empty<(int, int)>();
    SpscFloatRing? _inRing, _outRing;
    ClockMatchedReader? _inReader, _outReader;
    float[] _inStage = Array.Empty<float>(), _outStage = Array.Empty<float>();
    float[][] _inputTargets = Array.Empty<float[]>(), _outBuffers = Array.Empty<float[]>();
    public IEnumerable<int> BoundInputs => _bindings.Where(b => b.Flow == DeviceFlow.Capture).SelectMany(b => Enumerable.Range(b.EngineFirstChannel, b.Count));
    public double OutputLatencyMs { get; private set; }
    public long Underruns => (_inReader?.Underruns ?? 0) + (_outReader?.Underruns ?? 0);
    public long Overruns => (_inReader?.Overruns ?? 0) + (_outReader?.Overruns ?? 0);
    public AsioStream(HostRun run, string driver, DeviceBinding[] bindings) : base(run, null, driver)
    { _driver = driver; _bindings = bindings; }
    protected override void OpenDevice()
    {
        _device = AsioDevice.Open(_driver);
        _inputs = _bindings.Where(b => b.Flow == DeviceFlow.Capture).SelectMany(b => Enumerable.Range(0, b.Count).Select(k => (b.DeviceFirstChannel + k, b.EngineFirstChannel + k))).ToArray();
        _outputs = _bindings.Where(b => b.Flow == DeviceFlow.Render).SelectMany(b => Enumerable.Range(0, b.Count).Select(k => (b.DeviceFirstChannel + k, b.EngineFirstChannel + k))).ToArray();
        if (_inputs.Select(x => x.Device).Distinct().Count() != _inputs.Length || _outputs.Select(x => x.Device).Distinct().Count() != _outputs.Length)
            throw new ArgumentException("ASIO-Gerätekanäle dürfen je Richtung nur einmal zugeordnet sein.");
        var rates = _bindings.Where(b => b.SampleRate.HasValue).Select(b => b.SampleRate!.Value).Distinct().ToArray();
        var sizes = _bindings.Where(b => b.BufferFrames.HasValue).Select(b => b.BufferFrames!.Value).Distinct().ToArray();
        if (rates.Length > 1 || sizes.Length > 1) throw new ArgumentException("Ein ASIO-Gerät benötigt eine gemeinsame Abtastrate und Puffergröße für Ein- und Ausgänge.");
        int rate = rates.FirstOrDefault();
        if (rate == 0) rate = _device.IsSampleRateSupported(Run.EngineRate) ? Run.EngineRate : _device.CurrentSampleRate;
        int? buffer = sizes.Length == 0 ? null : sizes[0];
        _device.DriverResetRequest += (_, _) => Run.Fault(_driver + ": Treibereinstellungen geändert. Audio bitte neu starten.");
        _device.Stopped += (_, e) => { if (!Run.StopRequested && !Run.Aborting) Run.Fault(_driver + ": " + (e.Exception?.Message ?? "ASIO wurde beendet.")); };
        if (_outputs.Length == 0)
        {
            _device.InitRecording(new AsioRecordingOptions { InputChannels = _inputs.Select(x => x.Device).ToArray(), SampleRate = rate, BufferSize = buffer });
            _device.AudioCaptured += (_, e) =>
            {
                if (Run.IsFaulted) return;
                for (int c = 0; c < _inputs.Length; c++)
                {
                    var src = e.GetChannel(c);
                    for (int f = 0; f < e.Frames; f++) _inStage[f * _inputs.Length + c] = float.IsFinite(src[f]) ? src[f] : 0;
                }
                _inRing!.Write(_inStage, e.Frames);
            };
        }
        else _device.InitDuplex(new AsioDuplexOptions
        {
            InputChannels = _inputs.Select(x => x.Device).ToArray(), OutputChannels = _outputs.Select(x => x.Device).ToArray(),
            SampleRate = rate, BufferSize = buffer, Processor = Process,
        });
        int period = _device.FramesPerBuffer;
        if (_inputs.Length > 0)
        {
            int target = period * 2 + (int)Math.Ceiling(Run.Chunk * (double)rate / Run.EngineRate) + rate / 250;
            _inRing = new SpscFloatRing(_inputs.Length, target * 5);
            _inReader = new ClockMatchedReader(_inRing, rate / (double)Run.EngineRate, Run.EngineRate, target, Run.Chunk);
            _inStage = new float[period * _inputs.Length];
        }
        if (_outputs.Length > 0)
        {
            int target = Run.Chunk * 2 + (int)Math.Ceiling(period * (double)Run.EngineRate / rate) + Run.EngineRate / 250;
            _outRing = new SpscFloatRing(_outputs.Length, target * 5);
            _outReader = new ClockMatchedReader(_outRing, Run.EngineRate / (double)rate, rate, target, period);
            _outStage = new float[Run.Chunk * _outputs.Length];
            _outBuffers = _outputs.Select(_ => new float[period]).ToArray();
            OutputLatencyMs = target * 1000.0 / Run.EngineRate + _device.OutputLatencySamples * 1000.0 / rate;
        }
    }
    void Process(in AsioProcessBuffers buffer)
    {
        if (Run.IsFaulted) return; // library clears output before callback; normal Stop drains the mixer's fade-out
        if (_inputs.Length > 0)
        {
            for (int c = 0; c < _inputs.Length; c++)
            {
                var source = buffer.GetInput(c);
                for (int f = 0; f < buffer.Frames; f++) _inStage[f * _inputs.Length + c] = float.IsFinite(source[f]) ? source[f] : 0;
            }
            _inRing!.Write(_inStage, buffer.Frames);
        }
        _outReader!.Read(_outBuffers, 0, buffer.Frames);
        for (int c = 0; c < _outputs.Length; c++) _outBuffers[c].AsSpan(0, buffer.Frames).CopyTo(buffer.GetOutput(c));
    }
    public void Bind(float[][] inputs) => _inputTargets = _inputs.Select(i => inputs[i.Engine]).ToArray();
    public void ReadInputs(int frames) => _inReader?.Read(_inputTargets, 0, frames);
    public void WriteOutputs(float[][] output, int frames)
    {
        if (_outRing == null) return;
        for (int c = 0; c < _outputs.Length; c++)
        {
            var source = output[_outputs[c].Engine];
            for (int f = 0; f < frames; f++) _outStage[f * _outputs.Length + c] = float.IsFinite(source[f]) ? source[f] : 0;
        }
        _outRing.Write(_outStage, frames);
    }
    protected override void StartDevice() => _device!.Start();
    protected override void RunLoop() => Native.WaitForSingleObject(Run.StopEvent, uint.MaxValue);
    protected override void CloseDevice() { try { _device?.Stop(); } finally { _device?.Dispose(); } }
    public override DeviceStat GetStat()
    {
        var r = _outReader ?? _inReader;
        return new DeviceStat(_bindings[0].DeviceId, Name, r?.FillFraction ?? 0, r?.CorrectionPpm ?? 0,
            (_inReader?.Underruns ?? 0) + (_inReader?.Overruns ?? 0) + (_outReader?.Underruns ?? 0) + (_outReader?.Overruns ?? 0));
    }
}
