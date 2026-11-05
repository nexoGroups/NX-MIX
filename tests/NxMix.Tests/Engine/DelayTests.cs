using NxMix.Engine;
using static NxMix.Tests.Engine.EngineTestKit;

namespace NxMix.Tests.Engine;

public class DelayTests
{
    static MixerSettings WithDelays(int sr, double inMs, double outMs)
    {
        var s = Mono(sr);
        return s with
        {
            Inputs = new[] { s.Inputs[0] with { DelayMs = inMs } },
            Outputs = new[] { s.Outputs[0] with { DelayMs = outMs } },
        };
    }

    static int Look(int sr) => (int)Math.Round(sr * 0.001);

    [Fact]
    public void Integer_delay_is_sample_exact()
    {
        const int sr = 48000;
        var x = new float[4800];
        x[100] = 1f;
        // 5 ms am Eingang (240 Samples) + 1 ms am Ausgang (48 Samples) + Limiter-Lookahead
        var y = Run(new MixerEngine(WithDelays(sr, 5.0, 1.0)), new[] { x })[0];
        int expected = 100 + 240 + 48 + Look(sr);
        Assert.Equal(1f, y[expected], 5);
        Assert.Equal(1f, y.Sum(), 4);
        for (int i = 0; i < y.Length; i++) if (i != expected) Assert.True(Math.Abs(y[i]) < 1e-6f, $"i={i} {y[i]}");
    }

    [Fact]
    public void Fractional_delay_interpolates_linearly()
    {
        const int sr = 48000;
        var x = new float[4800];
        x[100] = 1f;
        double ms = 10.25 / sr * 1000;                         // 10.25 Samples
        var y = Run(new MixerEngine(WithDelays(sr, ms, 0)), new[] { x })[0];
        int b = 100 + 10 + Look(sr);
        Assert.Equal(0.75f, y[b], 4);
        Assert.Equal(0.25f, y[b + 1], 4);
        Assert.Equal(1f, y.Sum(), 4);
    }

    [Fact]
    public void Delay_works_across_block_boundaries_and_ring_wrap()
    {
        const int sr = 48000;
        var rnd = new Random(3);
        var x = new float[sr * 4];
        for (int i = 0; i < x.Length; i++) x[i] = (float)(rnd.NextDouble() - 0.5) * 0.5f;
        int d = 7919;   // Primzahl: nie blockbuendig
        double ms = d * 1000.0 / sr;
        var y = Run(new MixerEngine(WithDelays(sr, ms, 0)), new[] { x }, block: 333)[0];
        int off = d + Look(sr);
        for (int i = off; i < x.Length; i++) Assert.True(Math.Abs(y[i] - x[i - off]) < 1e-6f, $"i={i}");
    }

    [Fact]
    public void Maximum_delay_is_clamped()
    {
        const int sr = 48000;
        var x = new float[sr * 2 + 100];
        x[0] = 1f;
        var y = Run(new MixerEngine(WithDelays(sr, 5000.0, 0)), new[] { x })[0];
        int expected = sr + Look(sr);                         // 1000 ms
        Assert.Equal(1f, y[expected], 4);
    }

    [Fact]
    public void Changing_delay_crossfades_without_a_jump()
    {
        const int sr = 48000;
        double f = 100, a = 0.5;
        var x = Sine(sr, f, a, sr);
        var s = WithDelays(sr, 0, 0);
        var e = new MixerEngine(s);
        var y = new float[x.Length];
        RunInto(e, new[] { x }, new[] { y }, 0, 12000);
        Assert.True(e.TrySetSettings(WithDelays(sr, 3.3, 0)));
        RunInto(e, new[] { x }, new[] { y }, 12000, x.Length);

        float step = MaxStep(y, Look(sr) + 2);
        double bound = a * 2 * Math.PI * f / sr * 1.2 + 2 * a / (sr * 0.010) * 1.2;
        Assert.True(step <= bound, $"Sprung {step} > {bound}");
        // danach ist das Signal um 3.3 ms verschoben
        int shift = (int)Math.Round(3.3e-3 * sr);
        double err = 0;
        for (int i = 30000; i < 40000; i++) err = Math.Max(err, Math.Abs(y[i] - x[i - shift - Look(sr)]));
        Assert.True(err < 0.005, $"Abweichung {err}");
    }
}
