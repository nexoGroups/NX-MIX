using NxMix.Engine;
using static NxMix.Tests.Engine.EngineTestKit;

namespace NxMix.Tests.Engine;

public class RobustnessTests
{
    const int Sr = 96000;

    static MixerSettings Chain(int channels = 2)
    {
        var s = MixerSettings.Create(Sr, channels, channels);
        for (int i = 0; i < channels; i++) s.MatrixDb[i][i] = 0;
        var f = new[]
        {
            new FilterSpec(FilterType.HighPass, 30, 0.7071, 0, 4, true),
            new FilterSpec(FilterType.Peak, 1000, 2, 4),
            new FilterSpec(FilterType.LowPass, 15000, 0.7071, 0, 4, true),
        };
        return s with
        {
            Inputs = Enumerable.Range(0, channels).Select(i => Ch("in" + i, 0, HighCeiling, f) with { DelayMs = 1.234 }).ToArray(),
            Outputs = Enumerable.Range(0, channels).Select(i => Ch("out" + i, 0, -1.0, f) with { DelayMs = 0.5 }).ToArray(),
        };
    }

    [Fact]
    public void NaN_and_Inf_inputs_never_reach_the_output_nor_poison_the_filters()
    {
        var e = new MixerEngine(Chain(2));
        int n = Sr;
        var x = new[] { Sine(n, 1000, 0.3, Sr), Sine(n, 440, 0.3, Sr) };
        for (int i = 20000; i < 20500; i++)
        {
            x[0][i] = float.NaN;
            x[1][i + 3] = i % 3 == 0 ? float.PositiveInfinity : float.NegativeInfinity;
        }
        x[0][30000] = float.MaxValue;               // endlich, aber riesig
        x[1][30001] = 1e-45f;                       // Denormal

        var y = Run(e, x, block: 257);
        foreach (var ch in y)
            for (int i = 0; i < n; i++)
                Assert.True(float.IsFinite(ch[i]) && Math.Abs(ch[i]) <= Ceil(-1.0), $"i={i} v={ch[i]}");

        // Filter sind nicht vergiftet: nach der Stoerung kommt wieder sauberes Signal
        double a = Amplitude(y[0], n - 9600, 9600, 1000, Sr);
        Assert.True(a > 0.70 && a < 0.80, $"Amplitude {a}");   // 0.3 * (2 x Peak +4 dB)

        var peaks = new float[2];
        e.ReadInputPeaks(peaks);
        Assert.True(float.IsFinite(peaks[0]) && float.IsFinite(peaks[1]));
        e.ReadOutputPeaks(peaks);
        Assert.True(float.IsFinite(peaks[0]) && float.IsFinite(peaks[1]) && peaks[0] <= Ceil(-1.0));
    }

    [Fact]
    public void Missing_or_short_buffers_are_treated_as_silence_and_do_not_throw()
    {
        var e = new MixerEngine(Chain(2));
        var inputs = new float[][] { new float[100], null! };       // zweiter Eingang fehlt, erster zu kurz fuer 480
        var outputs = new float[][] { new float[480], new float[10] };
        e.Process(inputs, outputs, 480);
        e.Process(new float[2][], new float[2][], 480);
        e.Process(null!, null!, 480);
        e.Process(new float[2][] { new float[480], new float[480] }, new float[2][] { new float[480], new float[480] }, 0);
    }

    [Fact]
    public void Long_blocks_are_chunked_transparently()
    {
        // 5000 Frames am Stueck == 480er-Bloecke (bitgleich), auch mit Delay/Filtern
        var x = new[] { Sine(5000, 700, 0.4, Sr), Sine(5000, 300, 0.4, Sr) };
        var a = Run(new MixerEngine(Chain(2)), x, block: 5000);
        var b = Run(new MixerEngine(Chain(2)), x, block: 480);
        for (int c = 0; c < 2; c++)
            for (int i = 0; i < 5000; i++)
                Assert.True(a[c][i] == b[c][i], $"c={c} i={i}: {a[c][i]} vs {b[c][i]}");
    }

    [Fact]
    public void Process_allocates_zero_bytes_after_warmup()
    {
        const int block = 480;
        var s = Chain(8);
        // alle Kreuzungspunkte belegen, Limiter auch ansprechen lassen
        for (int i = 0; i < 8; i++) for (int o = 0; o < 8; o++) s.MatrixDb[i][o] = i == o ? 0 : -12;
        var e = new MixerEngine(s);
        var inputs = NewBuffers(8, block);
        var outputs = NewBuffers(8, block);
        var rnd = new Random(5);
        for (int c = 0; c < 8; c++)
            for (int i = 0; i < block; i++) inputs[c][i] = (float)(rnd.NextDouble() * 2 - 1) * (c == 3 ? 3f : 0.3f);

        var variants = new MixerSettings[4];
        for (int v = 0; v < variants.Length; v++)
            variants[v] = s with
            {
                MasterGainDb = -v,
                Inputs = s.Inputs.Select(ch => ch with
                {
                    GainDb = -v * 2,
                    DelayMs = 1 + v * 0.37,
                    Filters = new[] { new FilterSpec(FilterType.Peak, 500 + 100 * v, 2, 6), new FilterSpec(FilterType.LowPass, 8000 - 1000 * v, 0.7071, 0, 4, true) },
                }).ToArray(),
            };

        // Warmup inkl. mehrerer Settings-Wechsel (JIT, Tiering, erste Adoption)
        for (int i = 0; i < 400; i++)
        {
            if (i % 40 == 0) Assert.True(e.TrySetSettings(variants[(i / 40) % variants.Length]));
            e.Process(inputs, outputs, block);
        }
        Thread.Sleep(300);
        for (int i = 0; i < 200; i++) e.Process(inputs, outputs, block);

        long allocated = 0;
        for (int i = 0; i < 1500; i++)
        {
            if (i % 25 == 0) Assert.True(e.TrySetSettings(variants[(i / 25) % variants.Length]));   // Setter-Thread-Arbeit: nicht mitgezaehlt
            long before = GC.GetAllocatedBytesForCurrentThread();
            e.Process(inputs, outputs, block);                                                       // beinhaltet die Uebernahme neuer Settings
            allocated += GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0, allocated);
        Assert.True(e.LimiterHits > 0);       // der Limiter-Pfad war aktiv
    }

    [Fact]
    public void Concurrent_settings_changes_do_not_break_processing()
    {
        var s = Chain(4);
        for (int i = 0; i < 4; i++) for (int o = 0; o < 4; o++) s.MatrixDb[i][o] = i == o ? 0 : -9;
        var e = new MixerEngine(s);
        var stop = false;
        Exception? setterError = null;
        var setter = new Thread(() =>
        {
            try
            {
                var rnd = new Random(77);
                while (!Volatile.Read(ref stop))
                {
                    var ns = s with
                    {
                        MasterGainDb = rnd.NextDouble() * -20,
                        Inputs = s.Inputs.Select(ch => ch with
                        {
                            GainDb = rnd.NextDouble() * 24 - 12,
                            Mute = rnd.Next(5) == 0,
                            DelayMs = rnd.NextDouble() * 5,
                            Filters = rnd.Next(3) == 0
                                ? Array.Empty<FilterSpec>()
                                : new[] { new FilterSpec(FilterType.Peak, 100 + rnd.NextDouble() * 5000, 0.5 + rnd.NextDouble() * 5, rnd.NextDouble() * 24 - 12) },
                        }).ToArray(),
                    };
                    e.TrySetSettings(ns);
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex) { setterError = ex; }
        });
        setter.Start();

        var inputs = NewBuffers(4, 480);
        var outputs = NewBuffers(4, 480);
        var rnd2 = new Random(9);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        int blocks = 0;
        while (sw.ElapsedMilliseconds < 1500)
        {
            for (int c = 0; c < 4; c++) for (int i = 0; i < 480; i++) inputs[c][i] = (float)(rnd2.NextDouble() * 2 - 1) * 0.5f;
            e.Process(inputs, outputs, 480);
            foreach (var ch in outputs)
                foreach (var v in ch)
                {
                    Assert.True(float.IsFinite(v));
                    Assert.True(Math.Abs(v) <= Ceil(-1.0));
                }
            blocks++;
        }
        Volatile.Write(ref stop, true);
        setter.Join();
        Assert.Null(setterError);
        Assert.True(blocks > 100);
        Assert.NotNull(e.Settings);
    }

    [Fact]
    public void Zero_channel_engine_is_harmless()
    {
        var e = new MixerEngine(MixerSettings.Create(48000, 0, 0));
        e.Process(Array.Empty<float[]>(), Array.Empty<float[]>(), 480);
        e.ReadInputPeaks(new float[3]);
        Assert.True(e.TrySetSettings(MixerSettings.Create(48000, 0, 0)));
    }

    [Fact]
    public void Invalid_filter_values_do_not_produce_NaN()
    {
        var weird = new[]
        {
            new FilterSpec(FilterType.Peak, double.NaN, double.NaN, double.NaN),
            new FilterSpec(FilterType.Peak, -50, -1, 1e9),
            new FilterSpec(FilterType.HighPass, 1e9, 0, 0, 77, true),
            new FilterSpec(FilterType.LowPass, 0, 1e9, 0, -3),
            new FilterSpec(FilterType.AllPass, double.PositiveInfinity, 1),
        };
        var s = Mono(Sr, weird);
        s = s with { Inputs = new[] { s.Inputs[0] with { GainDb = double.NaN, DelayMs = double.NaN } } };
        var y = Run(new MixerEngine(s), new[] { Sine(Sr / 4, 1000, 0.5, Sr) })[0];
        Assert.All(y, v => Assert.True(float.IsFinite(v)));
    }
}
