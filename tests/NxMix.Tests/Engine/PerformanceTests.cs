using System.Diagnostics;
using NxMix.Engine;
using Xunit.Abstractions;
using static NxMix.Tests.Engine.EngineTestKit;

namespace NxMix.Tests.Engine;

/// <summary>Echtzeitfaktor des Kerns: Sekunden Audio pro Sekunde Rechenzeit (ein Kern). Soll &gt; 20 sein.</summary>
public class PerformanceTests
{
    const int Sr = 96000;
    const int Block = 480;
    const int Ins = 16, Outs = 12;

    readonly ITestOutputHelper _out;
    public PerformanceTests(ITestOutputHelper output) => _out = output;

    static FilterSpec[] SixFilters() => new[]
    {
        new FilterSpec(FilterType.HighPass, 30, 0.7071, 0, 4, true),   // LR24 = 2 Sektionen
        new FilterSpec(FilterType.Peak, 120, 1.5, 3),
        new FilterSpec(FilterType.Peak, 1000, 2.0, -4),
        new FilterSpec(FilterType.Peak, 4000, 1.0, 2),
        new FilterSpec(FilterType.HighShelf, 9000, 0.7071, -2),
        new FilterSpec(FilterType.LowPass, 18000, 0.7071, 0, 4, true), // LR24 = 2 Sektionen
    };

    static FilterSpec[] SixHeavyFilters() => Enumerable.Range(0, 6)
        .Select(i => new FilterSpec(i % 2 == 0 ? FilterType.HighPass : FilterType.LowPass, i % 2 == 0 ? 20 + 5 * i : 15000 - 500 * i, 0.7071, 0, 8, true))
        .ToArray();      // 6 x LR48 = 24 Sektionen je Kanal (Extremfall)

    static MixerSettings Build(FilterSpec[] filters, bool fullMatrix)
    {
        var s = MixerSettings.Create(Sr, Ins, Outs);
        for (int i = 0; i < Ins; i++)
            for (int o = 0; o < Outs; o++)
                if (fullMatrix || o == i % Outs || o == (i + 3) % Outs) s.MatrixDb[i][o] = -6 - (i + o) % 5;
        return s with
        {
            Inputs = Enumerable.Range(0, Ins).Select(i => Ch("in" + i, 0, HighCeiling, filters) with { DelayMs = i % 3 == 0 ? 1.5 : 0 }).ToArray(),
            Outputs = Enumerable.Range(0, Outs).Select(o => Ch("out" + o, 0, -1.0, filters) with { DelayMs = o % 2 == 0 ? 0.7 : 0 }).ToArray(),
        };
    }

    double Measure(string name, MixerSettings s, float level)
    {
        var e = new MixerEngine(s);
        var inputs = NewBuffers(Ins, Block);
        var outputs = NewBuffers(Outs, Block);
        var rnd = new Random(1);
        for (int c = 0; c < Ins; c++)
            for (int i = 0; i < Block; i++) inputs[c][i] = (float)(rnd.NextDouble() * 2 - 1) * level;

        for (int i = 0; i < 300; i++) e.Process(inputs, outputs, Block);     // Warmup / Tiering
        Thread.Sleep(200);
        for (int i = 0; i < 300; i++) e.Process(inputs, outputs, Block);

        const int blocks = 2000;                       // 10 s Audio
        double best = double.MaxValue;
        for (int rep = 0; rep < 5; rep++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < blocks; i++) e.Process(inputs, outputs, Block);
            best = Math.Min(best, sw.Elapsed.TotalSeconds);
        }
        double audioSeconds = blocks * (double)Block / Sr;
        double rtf = audioSeconds / best;
        _out.WriteLine($"{name}: {rtf:F1}x Echtzeit  ({best / blocks * 1e6:F0} us pro 5-ms-Block, CPU-Last {100.0 / rtf:F1} %)");
        return rtf;
    }

    [Fact]
    public void Realtime_factor_16in_12out_6_filters_96k_is_over_20x()
    {
        double typical = Measure("16x12, je 6 Filter (8 Sektionen), dichte Matrix, ohne Limiter-Eingriff", Build(SixFilters(), true), 0.1f);
        double sparse = Measure("16x12, je 6 Filter (8 Sektionen), 2 Wege je Eingang", Build(SixFilters(), false), 0.1f);
        double limiting = Measure("16x12, je 6 Filter, Limiter greift dauernd ein", Build(SixFilters(), true), 2.0f);
        double heavy = Measure("16x12, je 6 x LR48 (24 Sektionen), dichte Matrix (Extremfall)", Build(SixHeavyFilters(), true), 0.1f);
        _out.WriteLine($"Echtzeitfaktor (typisch): {typical:F1}x");

        // Andere Tests/Builds laufen parallel auf der Maschine -> Bestwert aus 5 Laeufen; Grenze = Vorgabe 20x
        Assert.True(typical > 20, $"Echtzeitfaktor {typical:F1}x");
        Assert.True(sparse > 20, $"Echtzeitfaktor {sparse:F1}x");
        Assert.True(limiting > 20, $"Echtzeitfaktor {limiting:F1}x");
        Assert.True(heavy > 5, $"Echtzeitfaktor {heavy:F1}x");
    }
}
