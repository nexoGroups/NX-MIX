using NxMix.Engine;
using static NxMix.Tests.Engine.EngineTestKit;

namespace NxMix.Tests.Engine;

/// <summary>Vergleicht die (SIMD-)Filterketten mehrerer Kanaele mit einer einfachen Referenz (Direktform I, double).</summary>
public class MultiChannelChainTests
{
    const int Sr = 96000;
    const int Look = 96;

    static FilterSpec[][] Chains() => new[]
    {
        Array.Empty<FilterSpec>(),
        new[] { new FilterSpec(FilterType.Peak, 1000, 2, 6) },
        new[] { new FilterSpec(FilterType.HighPass, 80, 0.7071, 0, 4, true), new FilterSpec(FilterType.Peak, 3000, 1.2, -4) },
        new[]
        {
            new FilterSpec(FilterType.HighPass, 30, 0.7071, 0, 4, true), new FilterSpec(FilterType.Peak, 120, 1.5, 3),
            new FilterSpec(FilterType.Peak, 1000, 2.0, -4), new FilterSpec(FilterType.Peak, 4000, 1.0, 2),
            new FilterSpec(FilterType.HighShelf, 9000, 0.7071, -2), new FilterSpec(FilterType.LowPass, 18000, 0.7071, 0, 4, true),
        },
        new[] { new FilterSpec(FilterType.HighPass, 200, 0.7071, 0, 8, true), new FilterSpec(FilterType.LowPass, 5000, 0.7071, 0, 8, true) },
        new[] { new FilterSpec(FilterType.LowPass, 900, 0.7071, 0, 3) },
        new[] { new FilterSpec(FilterType.Notch, 1500, 4), new FilterSpec(FilterType.AllPass, 700, 1), new FilterSpec(FilterType.BandPass, 2000, 0.8), new FilterSpec(FilterType.LowShelf, 250, 0.7, 5) },
    };

    static float[] Reference(float[] x, FilterSpec[] chain)
    {
        var y = x.Select(v => (double)v).ToArray();
        foreach (var spec in chain)
            foreach (var c in FilterDesign.Design(spec, Sr))
            {
                double x1 = 0, x2 = 0, y1 = 0, y2 = 0;
                for (int i = 0; i < y.Length; i++)
                {
                    double xi = y[i];
                    double yo = c.B0 * xi + c.B1 * x1 + c.B2 * x2 - c.A1 * y1 - c.A2 * y2;
                    x2 = x1; x1 = xi; y2 = y1; y1 = yo;
                    y[i] = yo;
                }
            }
        return y.Select(v => (float)v).ToArray();
    }

    static float[][] Noise(int channels, int n)
    {
        var x = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            var rnd = new Random(100 + c);
            x[c] = new float[n];
            for (int i = 0; i < n; i++) x[c][i] = (float)(rnd.NextDouble() * 2 - 1) * 0.3f;
        }
        return x;
    }

    [Theory]
    [InlineData(true)]     // Filter an den Eingaengen
    [InlineData(false)]    // Filter an den Ausgaengen
    public void Each_channel_matches_reference_filter(bool onInputs)
    {
        var chains = Chains();
        int ch = chains.Length;
        var s = Diagonal(Sr, ch);
        s = s with
        {
            Inputs = Enumerable.Range(0, ch).Select(c => Ch("i", 0, HighCeiling, onInputs ? chains[c] : Array.Empty<FilterSpec>())).ToArray(),
            Outputs = Enumerable.Range(0, ch).Select(c => Ch("o", 0, HighCeiling, onInputs ? Array.Empty<FilterSpec>() : chains[c])).ToArray(),
        };
        int n = 20000;
        var x = Noise(ch, n);
        var y = Run(new MixerEngine(s), x, block: 480);
        for (int c = 0; c < ch; c++)
        {
            var r = Reference(x[c], chains[c]);
            double maxErr = 0, maxRef = 0;
            for (int i = 0; i + Look < n; i++)
            {
                maxErr = Math.Max(maxErr, Math.Abs(y[c][i + Look] - r[i]));
                maxRef = Math.Max(maxRef, Math.Abs(r[i]));
            }
            Assert.True(maxErr <= 2e-5 + 2e-6 * maxRef, $"Kanal {c}: Fehler {maxErr:E2} (Referenzspitze {maxRef:F3})");
        }
    }

    [Fact]
    public void Channels_do_not_leak_into_each_other()
    {
        // Nur Kanal 2 bekommt Signal; alle anderen muessen bei jeder Filterkette exakt still bleiben
        var chains = Chains();
        int ch = chains.Length;
        var s = Diagonal(Sr, ch);
        s = s with { Inputs = Enumerable.Range(0, ch).Select(c => Ch("i", 0, HighCeiling, chains[c])).ToArray() };
        var x = Noise(ch, 5000);
        for (int c = 0; c < ch; c++) if (c != 2) Array.Clear(x[c]);
        var y = Run(new MixerEngine(s), x);
        for (int c = 0; c < ch; c++)
            if (c != 2) Assert.Equal(0f, MaxAbs(y[c]));
        Assert.True(MaxAbs(y[2]) > 0.05f);
    }
}
