using NxMix.Engine;
using static NxMix.Tests.Engine.EngineTestKit;

namespace NxMix.Tests.Engine;

public class MatrixRoutingTests
{
    const int Sr = 96000;
    const int Frames = 9600;           // 100 ms
    static readonly int Lookahead = (int)Math.Round(Sr * 0.001);   // Limiter-Latenz (96 Samples)

    static float[] Dc(double v) { var x = new float[Frames]; Array.Fill(x, (float)v); return x; }

    static float Tail(float[] y) => y[Frames - 10];

    static MixerSettings ThreeByTwo()
    {
        var s = MixerSettings.Create(Sr, 3, 2);
        s.MatrixDb[0][0] = 0;                              // in0 -> out0
        s.MatrixDb[1][0] = 20 * Math.Log10(0.5);           // in1 -> out0  (x0.5)
        s.MatrixDb[1][1] = 0;                              // in1 -> out1
        s.MatrixDb[2][1] = double.NegativeInfinity;        // in2 -> out1 aus
        s.MatrixDb[2][0] = double.NaN;                     // in2 -> out0 aus
        return s with
        {
            Inputs = new[] { Ch("a"), Ch("b"), Ch("c") },
            Outputs = new[] { Ch("x", 0, 20), Ch("y", 0, 20) },
        };
    }

    [Fact]
    public void Matrix_routes_and_sums_correctly()
    {
        var e = new MixerEngine(ThreeByTwo());
        var y = Run(e, new[] { Dc(0.1), Dc(0.2), Dc(0.4) });
        Assert.Equal(0.1f + 0.2f * 0.5f, Tail(y[0]), 5);   // 0.2
        Assert.Equal(0.2f, Tail(y[1]), 5);                 // 0.2   (in2 trägt nichts bei)
        // Latenz des Limiters: davor Stille
        Assert.Equal(0f, y[0][Lookahead - 1]);
        Assert.Equal(0.2f, y[0][Lookahead + 5], 5);
    }

    [Fact]
    public void Matrix_gain_in_dB_is_applied()
    {
        var s = MixerSettings.Create(Sr, 1, 3);
        s.MatrixDb[0][0] = 6.0206;    // x2
        s.MatrixDb[0][1] = -20;       // x0.1
        s.MatrixDb[0][2] = 0;
        s = s with { Inputs = new[] { Ch() }, Outputs = new[] { Ch("1", 0, 20), Ch("2", 0, 20), Ch("3", 0, 20) } };
        var y = Run(new MixerEngine(s), new[] { Dc(0.25) });
        Assert.Equal(0.5f, Tail(y[0]), 3);
        Assert.Equal(0.025f, Tail(y[1]), 4);
        Assert.Equal(0.25f, Tail(y[2]), 5);
    }

    [Fact]
    public void Input_and_output_gain_master_and_phase()
    {
        var s = ThreeByTwo() with { MasterGainDb = -6.0206 };
        s = s with
        {
            Inputs = new[] { Ch("a", 6.0206), Ch("b") with { InvertPhase = true }, Ch("c") },
            Outputs = new[] { Ch("x", -6.0206, 20), Ch("y", 0, 20) with { InvertPhase = true } },
        };
        var y = Run(new MixerEngine(s), new[] { Dc(0.1), Dc(0.2), Dc(0.4) });
        // out0 = ((0.1*2) + (-0.2*0.5)) * master 0.5 * out 0.5 = 0.1*0.25 = 0.025
        Assert.Equal(0.025f, Tail(y[0]), 4);
        // out1 = (-0.2) * master 0.5 * (-1) = 0.1
        Assert.Equal(0.1f, Tail(y[1]), 4);
    }

    [Fact]
    public void Mute_and_solo_semantics()
    {
        var baseSettings = ThreeByTwo();
        var ins = new[] { Ch("a"), Ch("b"), Ch("c") };
        var x = new[] { Dc(0.1), Dc(0.2), Dc(0.4) };

        // Mute in0 -> out0 nur noch in1*0.5
        var m = baseSettings with { Inputs = new[] { ins[0] with { Mute = true }, ins[1], ins[2] } };
        var y = Run(new MixerEngine(m), x);
        Assert.Equal(0.1f, Tail(y[0]), 5);

        // Solo in1: in0 und in2 stumm
        var s1 = baseSettings with { Inputs = new[] { ins[0], ins[1] with { Solo = true }, ins[2] } };
        y = Run(new MixerEngine(s1), x);
        Assert.Equal(0.1f, Tail(y[0]), 5);
        Assert.Equal(0.2f, Tail(y[1]), 5);

        // Solo in0 + in1: beide hoerbar, in2 stumm; Mute schlaegt Solo
        var s2 = baseSettings with { Inputs = new[] { ins[0] with { Solo = true }, ins[1] with { Solo = true, Mute = true }, ins[2] } };
        y = Run(new MixerEngine(s2), x);
        Assert.Equal(0.1f, Tail(y[0]), 5);
        Assert.Equal(0f, Tail(y[1]), 6);

        // Ausgangs-Solo: nur out1 hoerbar
        var outs = baseSettings.Outputs;
        var s3 = baseSettings with { Outputs = new[] { outs[0], outs[1] with { Solo = true } } };
        y = Run(new MixerEngine(s3), x);
        Assert.Equal(0f, Tail(y[0]), 6);
        Assert.Equal(0.2f, Tail(y[1]), 5);

        // Ausgangs-Mute
        var s4 = baseSettings with { Outputs = new[] { outs[0] with { Mute = true }, outs[1] } };
        y = Run(new MixerEngine(s4), x);
        Assert.Equal(0f, Tail(y[0]), 6);
        Assert.Equal(0.2f, Tail(y[1]), 5);
    }

    [Fact]
    public void Gain_is_clamped_to_MaxGainDb()
    {
        var s = Mono(Sr);
        s = s with { Inputs = new[] { Ch("in", 30.0) } };                          // Default MaxGainDb = 12
        var y = Run(new MixerEngine(s), new[] { Dc(0.1) });
        Assert.Equal(0.1 * Math.Pow(10, 12 / 20.0), Tail(y[0]), 3);

        var s2 = s with { Inputs = new[] { Ch("in", 30.0) with { MaxGainDb = 0 } } };  // z. B. Hochtoener-Schutz
        y = Run(new MixerEngine(s2), new[] { Dc(0.1) });
        Assert.Equal(0.1f, Tail(y[0]), 5);

        var s3 = s with { Outputs = new[] { Ch("out", 50.0, 40) with { MaxGainDb = -6.0206 } } };
        y = Run(new MixerEngine(s3), new[] { Dc(0.1) });
        // Eingang +12 dB (Klemme) * Ausgang -6 dB (Klemme)
        Assert.Equal(0.1 * Math.Pow(10, 12 / 20.0) * 0.5, Tail(y[0]), 3);
    }

    [Fact]
    public void Settings_with_other_channel_count_or_samplerate_are_rejected()
    {
        var s = ThreeByTwo();
        var e = new MixerEngine(s);
        Assert.True(e.TrySetSettings(s with { MasterGainDb = -3 }));
        Assert.Equal(-3, e.Settings.MasterGainDb);
        Assert.False(e.TrySetSettings(s with { SampleRate = 48000 }));
        Assert.False(e.TrySetSettings(MixerSettings.Create(Sr, 4, 2)));
        Assert.False(e.TrySetSettings(MixerSettings.Create(Sr, 3, 3)));
        Assert.False(e.TrySetSettings(null!));
        Assert.Equal(-3, e.Settings.MasterGainDb);          // abgelehnte Settings aendern nichts
        Assert.Equal(3, e.InputChannels);
        Assert.Equal(2, e.OutputChannels);
        Assert.Equal(Sr, e.SampleRate);
    }

    [Fact]
    public void Matrix_change_is_ramped_not_stepped()
    {
        var s = Mono(Sr);
        var e = new MixerEngine(s);
        var x = Dc(0.5);
        var outs = NewBuffers(1, Frames);
        RunInto(e, new[] { x }, outs, 0, Frames / 2);

        var s2 = MixerSettings.Create(Sr, 1, 1);
        s2.MatrixDb[0][0] = -20;
        s2 = s2 with { Inputs = s.Inputs, Outputs = s.Outputs };
        Assert.True(e.TrySetSettings(s2));
        RunInto(e, new[] { x }, outs, Frames / 2, Frames);

        float step = MaxStep(outs[0], Lookahead + 2);
        Assert.True(step < 0.5f * 0.9f / (Sr * 0.010f) * 1.6f, $"groesster Sprung {step}");   // lineare 10-ms-Rampe
        Assert.Equal(0.05f, Tail(outs[0]), 4);
    }

    [Fact]
    public void Peaks_are_read_and_reset()
    {
        var e = new MixerEngine(ThreeByTwo());
        var x = new[] { Sine(Frames, 1000, 0.5, Sr), Sine(Frames, 1000, 0.25, Sr), Sine(Frames, 1000, 0.1, Sr) };
        Run(e, x);

        var inP = new float[3];
        var outP = new float[2];
        e.ReadInputPeaks(inP);
        e.ReadOutputPeaks(outP);
        Assert.InRange(inP[0], 0.499f, 0.5f);
        Assert.InRange(inP[1], 0.249f, 0.25f);
        Assert.InRange(inP[2], 0.099f, 0.1f);
        Assert.InRange(outP[0], 0.60f, 0.63f);     // 0.5 + 0.125 (gleichphasig)
        Assert.InRange(outP[1], 0.249f, 0.25f);

        e.ReadInputPeaks(inP);
        e.ReadOutputPeaks(outP);
        Assert.All(inP, p => Assert.Equal(0f, p));
        Assert.All(outP, p => Assert.Equal(0f, p));

        // zu grosser Zielpuffer wird mit 0 aufgefuellt, zu kleiner ist erlaubt
        var big = new float[5]; Array.Fill(big, 9f);
        e.ReadInputPeaks(big);
        Assert.Equal(0f, big[4]);
        e.ReadOutputPeaks(new float[1]);
    }

    [Fact]
    public void Input_and_device_meters_follow_faders_and_mute()
    {
        var master = new DeviceMasterDsp { Id = Guid.NewGuid(), Name = "Testquelle", IsInput = true };
        var settings = ThreeByTwo() with
        {
            Inputs = new[] { Ch("a") with { DeviceId = master.Id }, Ch("b"), Ch("c") },
            DeviceMasters = new[] { master },
        };
        var engine = new MixerEngine(settings);
        var source = new[] { Dc(0.5), Dc(0), Dc(0) };
        var inputPeaks = new float[3];
        var devicePeaks = new float[1];

        void CheckPeaks(float expected)
        {
            var outputs = Run(engine, source);
            engine.ReadInputPeaks(inputPeaks);
            engine.ReadDeviceMasterPeaks(devicePeaks);
            Assert.Equal(expected, inputPeaks[0], 5);
            Assert.Equal(expected, devicePeaks[0], 5);
            Assert.Equal(expected, Tail(outputs[0]), 5);
        }

        CheckPeaks(0.5f);
        settings = settings with { Inputs = new[] { settings.Inputs[0] with { GainDb = -6.0206 }, settings.Inputs[1], settings.Inputs[2] } };
        Assert.True(engine.TrySetSettings(settings));
        CheckPeaks(0.25f);
        Assert.True(engine.TrySetSettings(settings with { DeviceMasters = new[] { master with { GainDb = -6.0206 } } }));
        CheckPeaks(0.125f);
        Assert.True(engine.TrySetSettings(settings with { DeviceMasters = new[] { master with { Mute = true } } }));
        CheckPeaks(0.0f);
    }

    [Fact]
    public void Unused_input_returns_to_use_without_stale_state()
    {
        // Eingang 1 mit Filter wird lange nicht benutzt (Matrix aus), dann wieder eingeschaltet
        var s = MixerSettings.Create(Sr, 2, 1);
        s.MatrixDb[0][0] = 0;
        var f = new FilterSpec(FilterType.LowPass, 2000, 0.7071, 0, 4, true);
        s = s with { Inputs = new[] { Ch("a"), Ch("b", 0, HighCeiling, f) }, Outputs = new[] { Ch("o", 0, 20) } };
        var e = new MixerEngine(s);
        var x = new[] { Sine(Frames, 500, 0.3, Sr), Sine(Frames, 500, 0.3, Sr) };
        var outs = NewBuffers(1, Frames);
        RunInto(e, x, outs, 0, Frames / 3);

        var on = MixerSettings.Create(Sr, 2, 1);
        on.MatrixDb[0][0] = 0; on.MatrixDb[1][0] = 0;
        Assert.True(e.TrySetSettings(on with { Inputs = s.Inputs, Outputs = s.Outputs }));
        RunInto(e, x, outs, Frames / 3, Frames);

        // Nach dem Einschalten (Rampe 10 ms) erscheint in1 mit Amplitude 0.3 (LR4 bei 500 Hz ~ 0 dB): Summe ~ 0.6
        double a = Amplitude(outs[0], Frames - 4800, 4800, 500, Sr);
        Assert.InRange(a, 0.54, 0.62);
        Assert.True(MaxStep(outs[0], Lookahead + 2) < 0.12f);
    }
}
