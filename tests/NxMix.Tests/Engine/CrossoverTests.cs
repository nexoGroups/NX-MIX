using NxMix.Engine;
using static NxMix.Tests.Engine.EngineTestKit;

namespace NxMix.Tests.Engine;

public class CrossoverTests
{
    static readonly double[] Freqs =
        { 20, 50, 100, 200, 300, 400, 500, 600, 700, 800, 900, 1000, 1100, 1300, 1600, 2000, 3000, 5000, 10000, 20000 };

    [Theory]
    [InlineData(4)]   // LR24
    [InlineData(8)]   // LR48
    public void Design_LR_sum_is_flat_and_minus6dB_at_crossover(int order)
    {
        const int sr = 96000;
        const double fc = 1000;
        var lp = new FilterSpec(FilterType.LowPass, fc, 0.7071, 0, order, true);
        var hp = new FilterSpec(FilterType.HighPass, fc, 0.7071, 0, order, true);

        foreach (double f in Freqs)
        {
            var sum = FilterDesign.Response(lp, f, sr) + FilterDesign.Response(hp, f, sr);
            Assert.True(Math.Abs(ToDb(sum.Magnitude)) <= 0.1, $"LR{order * 6} Summe bei {f} Hz: {ToDb(sum.Magnitude):F4} dB");
        }
        Assert.Equal(-6.0206, FilterDesign.MagnitudeDb(lp, fc, sr), 3);
        Assert.Equal(-6.0206, FilterDesign.MagnitudeDb(hp, fc, sr), 3);
    }

    /// <summary>Ein Eingang, zwei Ausgaenge (Tief/Hoch) - die Summe beider Ausgaenge wird im Zeitbereich gebildet.</summary>
    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void Engine_LR_split_sums_flat_and_minus6dB_at_crossover(int order)
    {
        const int sr = 48000;
        const double fc = 1000;
        var s = MixerSettings.Create(sr, 1, 2);
        s.MatrixDb[0][0] = 0;
        s.MatrixDb[0][1] = 0;
        s = s with
        {
            Inputs = new[] { Ch("in") },
            Outputs = new[]
            {
                Ch("lo", 0, HighCeiling, new FilterSpec(FilterType.LowPass, fc, 0.7071, 0, order, true)),
                Ch("hi", 0, HighCeiling, new FilterSpec(FilterType.HighPass, fc, 0.7071, 0, order, true)),
            },
        };

        foreach (double f in new[] { 50.0, 200, 500, 900, 1000, 1100, 2000, 5000, 12000 })
        {
            var e = new MixerEngine(s);
            int settle = sr / 2, measure = sr;
            var x = Sine(settle + measure, f, 0.2, sr);
            var y = Run(e, new[] { x });
            var lo = Phasor(y[0], settle, measure, f, sr);
            var hi = Phasor(y[1], settle, measure, f, sr);
            double sumDb = ToDb((lo + hi).Magnitude / 0.2);
            Assert.True(Math.Abs(sumDb) <= 0.1, $"LR{order * 6} Summe bei {f} Hz: {sumDb:F4} dB");
            if (f == 1000)
            {
                Assert.Equal(-6.02, ToDb(lo.Magnitude / 0.2), 1);
                Assert.Equal(-6.02, ToDb(hi.Magnitude / 0.2), 1);
            }
        }
    }

    [Fact]
    public void LR_slopes_are_24_and_48_dB_per_octave()
    {
        const int sr = 96000;
        foreach (var (order, slope) in new[] { (4, 24.0), (8, 48.0) })
        {
            var lp = new FilterSpec(FilterType.LowPass, 1000, 0.7071, 0, order, true);
            double a = FilterDesign.MagnitudeDb(lp, 8000, sr);
            double b = FilterDesign.MagnitudeDb(lp, 16000, sr);
            // weit im Sperrbereich: Abfall pro Oktave ~ Flankensteilheit (Bilinear-Verzerrung nahe Nyquist -> grobe Toleranz)
            Assert.InRange(a - b, slope * 0.9, slope * 1.5);
        }
    }
}
