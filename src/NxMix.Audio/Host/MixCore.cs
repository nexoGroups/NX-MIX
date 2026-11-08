using System.Runtime.CompilerServices;
using NxMix.Engine;

namespace NxMix.Audio.Host;

/// <summary>Die Rechenschritte pro Block auf dem Master-Takt: Quellen aus den Ringpuffern ziehen (taktangepasst), Prozessor aufrufen,
/// Ein-/Ausblenden (Schutz der Lautsprecher), Ausgaenge fuer die uebrigen Render-Geraete in deren Ringpuffer schreiben.
/// Alle Puffer sind vorallokiert; ProcessChunk() allokiert nicht und sperrt nicht.</summary>
internal sealed class MixCore
{
    private const double HoldSeconds = 0.020;   // erst 20 ms exakte Stille ...
    private const double FadeInSeconds = 0.050; // ... dann in 50 ms einblenden
    private const double FadeOutSeconds = 0.020;

    private readonly IBlockProcessor _processor;
    private readonly IInputMonitorBlockProcessor? _monitorProcessor;
    private readonly int _rate;
    private readonly CaptureStream[] _captures;
    private readonly RenderStream[] _renders;
    private readonly AppLinkSlot[] _apps;
    private readonly AsioStream[] _asios;
    private readonly bool[] _inputBound;
    private readonly bool[] _monitorOnly;
    private readonly float[] _gains;

    private float _gain;
    private float _gainTarget = 1f;
    private float _gainStep;
    private int _hold;

    public readonly float[][] Inputs;
    public readonly float[][] Outputs;
    public long Blocks;
    public long Frames;

    public MixCore(IBlockProcessor processor, int rate, int chunk, CaptureStream[] captures, RenderStream[] renders, AppLinkSlot[]? apps = null, AsioStream[]? asios = null)
    {
        _asios = asios ?? Array.Empty<AsioStream>();
        _apps = apps ?? Array.Empty<AppLinkSlot>();
        _processor = processor;
        _monitorProcessor = _apps.Length > 0 ? processor as IInputMonitorBlockProcessor : null;
        _rate = rate;
        _captures = captures;
        _renders = renders;
        Inputs = new float[processor.InputChannels][];
        for (int i = 0; i < Inputs.Length; i++) Inputs[i] = new float[chunk];
        Outputs = new float[processor.OutputChannels][];
        for (int i = 0; i < Outputs.Length; i++) Outputs[i] = new float[chunk];
        _inputBound = new bool[Inputs.Length];
        _monitorOnly = _monitorProcessor != null ? new bool[Inputs.Length] : Array.Empty<bool>();
        _gains = new float[chunk];
        _gain = 0f;
        _gainTarget = 1f;
        _gainStep = (float)(1.0 / (FadeInSeconds * rate));
        _hold = (int)(HoldSeconds * rate);
        foreach (var asio in _asios) foreach (int ch in asio.BoundInputs) _inputBound[ch] = true;

        foreach (CaptureStream c in captures)
        {
            foreach (Mapping m in c.Spec!.Maps)
                for (int k = 0; k < m.Count; k++)
                    _inputBound[m.EngineFirstChannel + k] = true;
        }
        foreach (AppLinkSlot a in _apps)
        {
            if (a.Invalid) continue; // ungueltige Links liefern nie Daten und belegen keinen Eingang
            for (int k = 0; k < a.Binding.Count; k++)
                _inputBound[a.Binding.EngineFirstChannel + k] = true;
        }
    }

    /// <summary>Verdrahtet die Leser mit den Eingangspuffern (zero-copy). Nach dem Oeffnen aller Capture-Streams aufrufen.</summary>
    public void Prepare()
    {
        foreach (var asio in _asios) asio.Bind(Inputs);
        foreach (CaptureStream c in _captures)
        {
            var dst = new float[c.Spec!.Slots][];
            foreach (Mapping m in c.Spec!.Maps)
                for (int k = 0; k < m.Count; k++)
                    dst[m.SlotOffset + k] = Inputs[m.EngineFirstChannel + k];
            c.Dst = dst;
        }
        foreach (AppLinkSlot a in _apps)
            if (!a.Invalid) a.Bind(Inputs, Inputs.Length > 0 ? Inputs[0].Length : 0);
    }

    public bool FadeOutDone => _gainTarget == 0f && _gain == 0f;

    /// <summary>Beginnt das Ausblenden (Stop/Fehler). Nur vom Master-Thread aufrufen.</summary>
    public void BeginFadeOut()
    {
        _hold = 0;
        _gainTarget = 0f;
        _gainStep = (float)(1.0 / (FadeOutSeconds * _rate));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public void ProcessChunk(int n)
    {
        foreach (var asio in _asios) asio.ReadInputs(n);
        CaptureStream[] captures = _captures;
        for (int i = 0; i < captures.Length; i++)
            captures[i].Reader!.Read(captures[i].Dst!, 0, n);

        AppLinkSlot[] apps = _apps;
        for (int i = 0; i < apps.Length; i++)
        {
            var app = apps[i];
            if (app.Invalid) continue;
            app.ReadInto(Inputs, n);
            if (_monitorProcessor != null)
                _monitorOnly.AsSpan(app.Binding.EngineFirstChannel, app.Binding.Count)
                    .Fill(app.State is not (AppLinkState.Linked or AppLinkState.MixedWithoutRedirect));
        }

        for (int ch = 0; ch < Inputs.Length; ch++)
            if (!_inputBound[ch]) Array.Clear(Inputs[ch], 0, n);
        for (int ch = 0; ch < Outputs.Length; ch++)
            Array.Clear(Outputs[ch], 0, n);

        if (_monitorProcessor != null) _monitorProcessor.Process(Inputs, Outputs, n, _monitorOnly);
        else _processor.Process(Inputs, Outputs, n);

        ApplyGain(n);
        DistributeToRings(n);

        Blocks++;
        Frames += n;
    }

    private void ApplyGain(int n)
    {
        if (_gain == 1f && _gainTarget == 1f) return;

        float g = _gain, target = _gainTarget, step = _gainStep;
        int hold = _hold;
        float[] gains = _gains;
        for (int i = 0; i < n; i++)
        {
            if (hold > 0)
            {
                hold--;
                g = 0f;
            }
            else if (g < target)
            {
                g += step;
                if (g > target) g = target;
            }
            else if (g > target)
            {
                g -= step;
                if (g < target) g = target;
            }
            gains[i] = g;
        }
        _gain = g;
        _hold = hold;

        for (int ch = 0; ch < Outputs.Length; ch++)
        {
            float[] o = Outputs[ch];
            for (int i = 0; i < n; i++) o[i] *= gains[i];
        }
    }

    private void DistributeToRings(int n)
    {
        foreach (var asio in _asios) asio.WriteOutputs(Outputs, n);
        RenderStream[] renders = _renders;
        for (int r = 0; r < renders.Length; r++)
        {
            RenderStream rs = renders[r];
            int w = rs.Spec!.Slots;
            float[] stage = rs.Stage!;
            foreach (Mapping m in rs.Spec!.Maps)
            {
                for (int k = 0; k < m.Count; k++)
                {
                    float[] src = Outputs[m.EngineFirstChannel + k];
                    int slot = m.SlotOffset + k;
                    for (int i = 0; i < n; i++)
                    {
                        float v = src[i];
                        stage[i * w + slot] = float.IsFinite(v) ? v : 0f;
                    }
                }
            }
            rs.Ring!.Write(stage, n);
        }
    }
}
