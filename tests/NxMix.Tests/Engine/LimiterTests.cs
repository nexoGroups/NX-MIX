using NxMix.Engine;
using static NxMix.Tests.Engine.EngineTestKit;

namespace NxMix.Tests.Engine;

public class LimiterTests
{
    const int Sr = 96000;
    const int Look = 96;

    static MixerSettings Overdriven(double outGainDb, double ceilingDbfs)
    {
        var s = MixerSettings.Create(Sr, 1, 1);
        s.MatrixDb[0][0] = 0;
        return s with
        {
            Inputs = new[] { Ch("in") },
            Outputs = new[] { Ch("out", outGainDb, ceilingDbfs) },
        };
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(-0.3)]
    [InlineData(-6.0)]
    [InlineData(0.0)]
    [InlineData(-20.0)]
    public void Sine_plus_12dB_overdrive_never_exceeds_ceiling(double ceilingDb)
    {
        var s = Overdriven(12.0, ceilingDb);       // Ausgang +12 dB (x3.98)
        var e = new MixerEngine(s);
        var x = Sine(Sr * 2, 1000, 1.0, Sr);       // Vollaussteuerung -> 3.98 vor dem Limiter
        var y = Run(e, new[] { x })[0];
        double ceil = Ceil(ceilingDb);
        float max = MaxAbs(y);
        Assert.True(max <= ceil, $"max {max:R} > ceiling {ceil:R}");
        // eingeschwungen soll die Spitze das Ceiling auch erreichen (kein uebertriebenes Wegregeln)
        Assert.True(MaxAbs(y, Sr, Sr) >= ceil * 0.97, $"Spitze nur {MaxAbs(y, Sr, Sr)} bei Ceiling {ceil}");
        Assert.True(e.LimiterHits > 0);
    }

    [Fact]
    public void Limiter_acts_by_gain_reduction_not_by_clipping()
    {
        // Eingeschwungen: Ausgang ist eine saubere Sinuswelle (Klirr < -30 dB), keine abgeschnittene
        var s = Overdriven(12.0, -1.0);
        var e = new MixerEngine(s);
        int n = Sr * 2;
        var y = Run(e, new[] { Sine(n, 1000, 1.0, Sr) })[0];
        int start = Sr, count = Sr;
        var p = Phasor(y, start, count, 1000, Sr);
        double a1 = p.Magnitude;
        double total = 0;
        for (int i = 0; i < count; i++) total += (double)y[start + i] * y[start + i];
        double rms = Math.Sqrt(total / count);
        double fundamentalRms = a1 / Math.Sqrt(2);
        double distortion = Math.Sqrt(Math.Max(0, rms * rms - fundamentalRms * fundamentalRms)) / fundamentalRms;
        Assert.True(distortion < 0.03, $"Klirr {distortion:P2}");
    }

    [Fact]
    public void Random_noise_and_square_overdrive_never_exceeds_ceiling()
    {
        var rnd = new Random(1234);
        int n = Sr;
        var noise = new float[n];
        for (int i = 0; i < n; i++) noise[i] = (float)((rnd.NextDouble() * 2 - 1) * 4.0);   // +12 dBFS Spitzen
        var square = new float[n];
        for (int i = 0; i < n; i++) square[i] = (i / 37) % 2 == 0 ? 5f : -5f;
        var spikes = new float[n];
        for (int i = 0; i < n; i += 4999) spikes[i] = 100f;                                   // einzelne Nadeln +40 dBFS
        foreach (var x in new[] { noise, square, spikes })
        {
            foreach (double ceilingDb in new[] { -1.0, -12.0 })
            {
                var e = new MixerEngine(Overdriven(0, ceilingDb));
                var y = Run(e, new[] { x }, block: 333)[0];
                Assert.True(MaxAbs(y) <= Ceil(ceilingDb), $"max {MaxAbs(y):R}");
                Assert.True(e.LimiterHits > 0);
            }
        }
    }

    [Fact]
    public void Single_spike_is_attenuated_smoothly_before_it_arrives()
    {
        var x = new float[Sr * 2];
        Array.Fill(x, 0.1f);
        x[5000] = 4.0f;                          // Nadel auf 0.1-Gleichanteil
        var y = Run(new MixerEngine(Overdriven(0, -1.0)), new[] { x })[0];
        double ceil = Ceil(-1);
        Assert.True(MaxAbs(y) <= ceil);
        // Gleichanteil wird in der Naehe der Nadel abgesenkt (Lookahead-Rampe) und erholt sich wieder
        Assert.True(y[5000 + Look - 5] < 0.1f);
        Assert.Equal(0.1f, y[^1], 4);
        // keine harten Spruenge im Gleichanteil ausserhalb der Nadel selbst
        float maxStep = 0;
        for (int i = Look + 2; i < y.Length; i++)
            if (Math.Abs(i - (5000 + Look)) > 3) maxStep = Math.Max(maxStep, Math.Abs(y[i] - y[i - 1]));
        Assert.True(maxStep < 0.01f, $"Sprung {maxStep}");
    }

    [Fact]
    public void Signal_below_ceiling_passes_unchanged_with_1ms_latency()
    {
        var e = new MixerEngine(Overdriven(0, -1.0));
        var x = Sine(Sr / 2, 440, 0.8, Sr);                  // 0.8 < 0.891
        var y = Run(e, new[] { x })[0];
        for (int i = 0; i < Look; i++) Assert.Equal(0f, y[i]);
        for (int i = Look; i < y.Length; i++) Assert.True(Math.Abs(y[i] - x[i - Look]) < 1e-6f, $"i={i}");
        Assert.Equal(0, e.LimiterHits);
    }

    [Fact]
    public void Limiter_releases_after_overload()
    {
        var e = new MixerEngine(Overdriven(0, -1.0));
        int n = Sr * 3;
        var x = new float[n];
        for (int i = 0; i < n; i++)
            x[i] = (float)((i < Sr ? 3.0 : 0.5) * Math.Sin(2 * Math.PI * 1000 * i / Sr));
        var y = Run(e, new[] { x })[0];
        Assert.True(MaxAbs(y, 0, Sr) <= Ceil(-1));
        // 2 s nach der Uebersteuerung: voller Pegel wieder da (innerhalb 0.01 dB)
        double a = Amplitude(y, 2 * Sr, Sr, 1000, Sr);
        Assert.True(Math.Abs(ToDb(a / 0.5)) < 0.01, $"{ToDb(a / 0.5)} dB");
    }

    [Fact]
    public void Limiter_hits_count_only_when_limiting()
    {
        var e = new MixerEngine(Overdriven(0, -1.0));
        Run(e, new[] { Sine(Sr / 2, 1000, 0.5, Sr) });
        Assert.Equal(0, e.LimiterHits);
        Run(e, new[] { Sine(Sr / 2, 1000, 2.0, Sr) });
        long hits = e.LimiterHits;
        Assert.True(hits > 0);
    }

    [Fact]
    public void Limiter_guards_all_outputs_independently_and_with_own_ceiling()
    {
        var s = MixerSettings.Create(Sr, 1, 2);
        s.MatrixDb[0][0] = 0;
        s.MatrixDb[0][1] = 0;
        s = s with { Inputs = new[] { Ch("in") }, Outputs = new[] { Ch("a", 0, -1.0), Ch("b", 0, -12.0) } };
        var y = Run(new MixerEngine(s), new[] { Sine(Sr, 200, 1.5, Sr) });
        Assert.True(MaxAbs(y[0]) <= Ceil(-1.0));
        Assert.True(MaxAbs(y[1]) <= Ceil(-12.0));
        Assert.True(MaxAbs(y[0]) > Ceil(-1.0) * 0.97);
        Assert.True(MaxAbs(y[1]) > Ceil(-12.0) * 0.97);
    }

    [Fact]
    public void Ceiling_change_while_running_is_respected()
    {
        var s = Overdriven(0, -1.0);
        var e = new MixerEngine(s);
        var x = Sine(Sr, 500, 0.9, Sr);
        var y = new float[Sr];
        RunInto(e, new[] { x }, new[] { y }, 0, Sr / 2);
        Assert.True(e.TrySetSettings(Overdriven(0, -12.0) with { Inputs = s.Inputs }));
        RunInto(e, new[] { x }, new[] { y }, Sr / 2, Sr);
        Assert.True(MaxAbs(y, Sr / 2 + 10, Sr / 2 - 10) <= Ceil(-12.0));
    }
}
