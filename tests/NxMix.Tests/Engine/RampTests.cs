using NxMix.Engine;
using static NxMix.Tests.Engine.EngineTestKit;

namespace NxMix.Tests.Engine;

/// <summary>Gain-/Mute-/EQ-Aenderungen duerfen nie als Sprung hoerbar werden.</summary>
public class RampTests
{
    const int Sr = 96000;
    const int Look = 96;

    /// <summary>Spielt `before` Frames, uebernimmt dann `next`, spielt `after` Frames. Liefert den ganzen Ausgang.</summary>
    static float[] RunWithChange(MixerSettings start, MixerSettings next, float[] x, int before, int block = 480)
    {
        var e = new MixerEngine(start);
        var y = new float[x.Length];
        var outs = new[] { y };
        RunInto(e, new[] { x }, outs, 0, before, block);
        Assert.True(e.TrySetSettings(next));
        RunInto(e, new[] { x }, outs, before, x.Length, block);
        return y;
    }

    [Fact]
    public void Mute_on_sine_has_no_discontinuity_and_reaches_silence()
    {
        double f = 1000, a = 0.5;
        var x = Sine(Sr / 2, f, a, Sr);
        var s = Mono(Sr);
        var muted = s with { Inputs = new[] { s.Inputs[0] with { Mute = true } } };

        // an verschiedenen Phasenlagen (Blockgrenze trifft Nulldurchgang, Maximum ...)
        foreach (int before in new[] { 9600, 9601, 9625, 9650, 9677, 9713 })
        {
            var y = RunWithChange(s, muted, x, before, block: before % 480 == 0 ? 480 : 100 + before % 7);
            double maxSlope = a * 2 * Math.PI * f / Sr;
            float step = MaxStep(y, Look + 2);
            Assert.True(step <= maxSlope * 1.01 + 2e-4, $"before={before}: Sprung {step} > {maxSlope}");
            Assert.True(MaxAbs(y, before + Look + 1100, 2000) < 1e-6f, "nach Rampe + Lookahead muss Stille herrschen");
        }
    }

    [Fact]
    public void Unmute_ramps_in()
    {
        double f = 440, a = 0.5;
        var x = Sine(Sr / 2, f, a, Sr);
        var s = Mono(Sr);
        var muted = s with { Inputs = new[] { s.Inputs[0] with { Mute = true } } };
        var y = RunWithChange(muted, s, x, 12345);
        Assert.True(MaxStep(y, Look + 2) <= a * 2 * Math.PI * f / Sr * 1.01 + 2e-4);
        Assert.True(MaxAbs(y, 20000, 4000) > 0.49f);
    }

    [Theory]
    [InlineData(0.0, 12.0)]
    [InlineData(0.0, -40.0)]
    [InlineData(-20.0, 6.0)]
    public void Gain_change_on_sine_is_a_ramp(double fromDb, double toDb)
    {
        double f = 1000, a = 0.25;
        var x = Sine(Sr / 2, f, a, Sr);
        var s = Mono(Sr);
        var from = s with { Inputs = new[] { s.Inputs[0] with { GainDb = fromDb } } };
        var to = s with { Inputs = new[] { s.Inputs[0] with { GainDb = toDb } } };
        double gmax = Math.Max(Math.Pow(10, fromDb / 20), Math.Pow(10, toDb / 20));
        double gdelta = Math.Abs(Math.Pow(10, toDb / 20) - Math.Pow(10, fromDb / 20));
        double bound = a * gmax * 2 * Math.PI * f / Sr + a * gdelta / (Sr * 0.010) * 1.5 + 1e-4;
        foreach (int before in new[] { 9600, 9619, 9650, 9677 })
        {
            var y = RunWithChange(from, to, x, before);
            float step = MaxStep(y, Look + 2);
            Assert.True(step <= bound, $"before={before}: Sprung {step} > {bound}");
        }
    }

    [Fact]
    public void Gain_change_on_dc_is_linear_over_10ms()
    {
        var x = new float[Sr / 5];
        Array.Fill(x, 0.5f);
        var s = Mono(Sr);
        var to = s with { Inputs = new[] { s.Inputs[0] with { GainDb = -12 } } };
        var y = RunWithChange(s, to, x, 4800);
        float step = MaxStep(y, Look + 2);
        double expectedStep = 0.5 * (1 - Math.Pow(10, -12 / 20.0)) / 960;
        Assert.True(step <= expectedStep * 1.05, $"{step} vs {expectedStep}");
        // monoton fallend
        for (int i = 4800 + Look; i < 4800 + Look + 1000; i++) Assert.True(y[i + 1] <= y[i] + 1e-7f);
        Assert.Equal(0.5 * Math.Pow(10, -12 / 20.0), y[^1], 4);
    }

    [Fact]
    public void Output_gain_mute_and_solo_ramp()
    {
        double f = 700, a = 0.4;
        var x = Sine(Sr / 2, f, a, Sr);
        var s = Mono(Sr);
        var next = s with { Outputs = new[] { s.Outputs[0] with { Mute = true } } };
        var y = RunWithChange(s, next, x, 10001);
        Assert.True(MaxStep(y, Look + 2) <= a * 2 * Math.PI * f / Sr * 1.01 + 2e-4);
        Assert.True(MaxAbs(y, 10001 + Look + 1100, 2000) < 1e-6f);

        var solo = s with { Inputs = new[] { s.Inputs[0] with { Solo = true } } };   // Solo allein aendert nichts am Pegel
        var y2 = RunWithChange(s, solo, x, 10001);
        Assert.True(MaxStep(y2, Look + 2) <= a * 2 * Math.PI * f / Sr * 1.01 + 1e-5);
    }

    [Fact]
    public void Phase_invert_passes_through_zero_without_jump()
    {
        double f = 300, a = 0.5;
        var x = Sine(Sr / 2, f, a, Sr);
        var s = Mono(Sr);
        var inv = s with { Inputs = new[] { s.Inputs[0] with { InvertPhase = true } } };
        var y = RunWithChange(s, inv, x, 10000);
        Assert.True(MaxStep(y, Look + 2) <= a * 2 * Math.PI * f / Sr * 1.01 + 2 * a / 960.0 + 1e-4);
        // danach ist das Signal gegenphasig
        Assert.True(y[30000] * x[30000 - Look] < 0 || Math.Abs(x[30000 - Look]) < 1e-4);
    }

    [Fact]
    public void Eq_dragging_keeps_filter_state_without_clicks()
    {
        double f = 1000, a = 0.5;
        int total = Sr;
        var x = Sine(total, f, a, Sr);
        var s = Mono(Sr, new FilterSpec(FilterType.Peak, 1000, 4.0, 0), new FilterSpec(FilterType.LowPass, 2500, 0.7071, 0, 4, true));
        var e = new MixerEngine(s);
        var y = new float[total];
        var outs = new[] { y };

        // alle 480 Frames wird ein neuer Snapshot gesetzt: Peak-Gain und -Frequenz wandern, Tiefpass-Frequenz auch
        int block = 480;
        for (int pos = 0; pos < total; pos += block)
        {
            double t = (double)pos / total;
            var peak = new FilterSpec(FilterType.Peak, 800 + 400 * Math.Sin(t * 20), 4.0, 12 * Math.Sin(t * 13));
            var lp = new FilterSpec(FilterType.LowPass, 2500 + 800 * Math.Sin(t * 17), 0.7071, 0, 4, true);
            var ns = s with { Inputs = new[] { s.Inputs[0] with { Filters = new[] { peak, lp } } } };
            Assert.True(e.TrySetSettings(ns));
            RunInto(e, new[] { x }, outs, pos, Math.Min(total, pos + block), block);
        }
        // Verstaerkung im Peak bis +12 dB (x4) -> Amplitude <= 2.0 ; erlaubte Steilheit entsprechend
        double bound = a * 4.0 * 2 * Math.PI * f / Sr * 1.3;
        float step = MaxStep(y, Sr / 10);
        Assert.True(step <= bound, $"Sprung {step} > {bound}");
        Assert.True(MaxAbs(y) < 3f);
    }

    [Fact]
    public void Structure_change_fades_in_instead_of_clicking()
    {
        double f = 1000, a = 0.5;
        var x = Sine(Sr / 2, f, a, Sr);
        var s = Mono(Sr);
        var withLp = s with { Inputs = new[] { s.Inputs[0] with { Filters = new[] { new FilterSpec(FilterType.LowPass, 3000, 0.7071, 0, 4, true) } } } };
        var y = RunWithChange(s, withLp, x, 10000);
        double maxSlope = a * 2 * Math.PI * f / Sr;
        Assert.True(MaxStep(y, Look + 2) <= maxSlope * 1.05 + 2 * a / 960.0, $"{MaxStep(y, Look + 2)}");
        // eingeschwungen: LR4 @3 kHz, 1 kHz => ~0 dB
        Assert.Equal(a, MaxAbs(y, 40000, 2000), 1);
    }
}
