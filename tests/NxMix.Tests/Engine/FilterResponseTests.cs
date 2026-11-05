using System.Numerics;
using NxMix.Engine;
using static NxMix.Tests.Engine.EngineTestKit;

namespace NxMix.Tests.Engine;

/// <summary>Frequenzgang der Biquads gegen die analytische Formel (analoger Prototyp + Bilinear-Transformation mit Vorverzerrung).</summary>
public class FilterResponseTests
{
    /// <summary>Normierte analoge Frequenz Omega = tan(w/2)/tan(w0/2). Genau so bildet der RBJ-Entwurf ab.</summary>
    static double Omega(double f, double f0, int sr) => Math.Tan(Math.PI * f / sr) / Math.Tan(Math.PI * f0 / sr);

    /// <summary>Unabhaengige analytische Referenz: |H| (linear) des Filters bei f.</summary>
    static double Analytic(FilterSpec s, double f, int sr)
    {
        double om = Omega(f, s.FrequencyHz, sr);
        var S = new Complex(0, om);
        double q = s.Q;
        double A = Math.Pow(10, s.GainDb / 40);
        Complex h;
        switch (s.Type)
        {
            case FilterType.Peak:
                h = (S * S + S * (A / q) + 1) / (S * S + S / (A * q) + 1); break;
            case FilterType.LowShelf:
                h = A * (S * S + S * (Math.Sqrt(A) / q) + A) / (A * S * S + S * (Math.Sqrt(A) / q) + 1); break;
            case FilterType.HighShelf:
                h = A * (A * S * S + S * (Math.Sqrt(A) / q) + 1) / (S * S + S * (Math.Sqrt(A) / q) + A); break;
            case FilterType.Notch:
                h = (S * S + 1) / (S * S + S / q + 1); break;
            case FilterType.AllPass:
                h = (S * S - S / q + 1) / (S * S + S / q + 1); break;
            case FilterType.BandPass:
                h = (S / q) / (S * S + S / q + 1); break;
            case FilterType.LowPass:
            case FilterType.HighPass:
            {
                bool hp = s.Type == FilterType.HighPass;
                if (s.LinkwitzRiley)
                {
                    // LR(N) = zwei Butterworth(N/2): Betrag = 1/(1+Omega^N) (HP: Omega^N/(1+Omega^N))
                    double p = Math.Pow(om, s.Order);
                    return hp ? p / (1 + p) : 1 / (1 + p);
                }
                if (s.Order == 2)
                {
                    h = hp ? S * S / (S * S + S / q + 1) : 1 / (S * S + S / q + 1);
                    break;
                }
                double p2 = Math.Pow(om, 2 * s.Order);
                return Math.Sqrt(hp ? p2 / (1 + p2) : 1 / (1 + p2));
            }
            default: throw new ArgumentOutOfRangeException();
        }
        return h.Magnitude;
    }

    static readonly double[] Freqs =
        { 20, 31.5, 63, 125, 250, 500, 800, 1000, 1250, 2000, 3150, 5000, 8000, 12500, 16000, 20000, 30000 };

    public static IEnumerable<object[]> SpecCases()
    {
        foreach (int sr in new[] { 48000, 96000 })
        {
            yield return new object[] { new FilterSpec(FilterType.Peak, 1000, 1.4, 9), sr };
            yield return new object[] { new FilterSpec(FilterType.Peak, 3000, 0.5, -12), sr };
            yield return new object[] { new FilterSpec(FilterType.Peak, 80, 4.0, 6), sr };
            yield return new object[] { new FilterSpec(FilterType.LowShelf, 200, 0.7071, 8), sr };
            yield return new object[] { new FilterSpec(FilterType.LowShelf, 120, 1.0, -9), sr };
            yield return new object[] { new FilterSpec(FilterType.HighShelf, 4000, 0.7071, 6), sr };
            yield return new object[] { new FilterSpec(FilterType.HighShelf, 8000, 0.9, -10), sr };
            yield return new object[] { new FilterSpec(FilterType.Notch, 1000, 5.0), sr };
            yield return new object[] { new FilterSpec(FilterType.AllPass, 2500, 0.8), sr };
            yield return new object[] { new FilterSpec(FilterType.BandPass, 1000, 2.0), sr };
            yield return new object[] { new FilterSpec(FilterType.HighPass, 100, 0.7071), sr };
            yield return new object[] { new FilterSpec(FilterType.LowPass, 5000, 1.2), sr };
            for (int order = 1; order <= 8; order++)
            {
                yield return new object[] { new FilterSpec(FilterType.HighPass, 250, 0.7071, 0, order) , sr };
                yield return new object[] { new FilterSpec(FilterType.LowPass, 3000, 0.7071, 0, order), sr };
            }
            for (int order = 2; order <= 8; order += 2)
            {
                yield return new object[] { new FilterSpec(FilterType.HighPass, 500, 0.7071, 0, order, true), sr };
                yield return new object[] { new FilterSpec(FilterType.LowPass, 500, 0.7071, 0, order, true), sr };
            }
        }
    }

    [Theory]
    [MemberData(nameof(SpecCases))]
    public void Design_matches_analytic_formula(FilterSpec spec, int sr)
    {
        foreach (double f in Freqs)
        {
            if (f >= 0.49 * sr) continue;
            double expected = Analytic(spec, f, sr);
            double actual = FilterDesign.Response(spec, f, sr).Magnitude;
            // relativ 1e-6 (bzw. absolut, falls der Sollwert ~0 ist: Notch/Sperrbereich)
            Assert.True(Math.Abs(actual - expected) <= 1e-6 * Math.Max(expected, 1e-3),
                $"{spec.Type} f0={spec.FrequencyHz} @ {f} Hz sr={sr}: erwartet {expected}, ist {actual}");
        }
    }

    [Theory]
    [InlineData(FilterType.Peak, 6.0)]
    [InlineData(FilterType.Peak, -9.0)]
    public void Peak_has_exactly_the_requested_gain_at_center(FilterType type, double gainDb)
    {
        var spec = new FilterSpec(type, 1000, 1.0, gainDb);
        Assert.Equal(gainDb, FilterDesign.MagnitudeDb(spec, 1000, 96000), 6);
    }

    [Fact]
    public void Special_points()
    {
        const int sr = 96000;
        // Butterworth 2. Ordnung -3.01 dB bei fc, Notch tief bei f0, BandPass 0 dB bei f0, AllPass flach
        Assert.Equal(-3.0103, FilterDesign.MagnitudeDb(new FilterSpec(FilterType.LowPass, 1000), 1000, sr), 3);
        Assert.Equal(-3.0103, FilterDesign.MagnitudeDb(new FilterSpec(FilterType.HighPass, 1000, Order: 5), 1000, sr), 3);
        Assert.True(FilterDesign.MagnitudeDb(new FilterSpec(FilterType.Notch, 1000, 3), 1000, sr) < -100);
        Assert.Equal(0.0, FilterDesign.MagnitudeDb(new FilterSpec(FilterType.BandPass, 1000, 3), 1000, sr), 6);
        foreach (double f in Freqs)
            Assert.Equal(0.0, FilterDesign.MagnitudeDb(new FilterSpec(FilterType.AllPass, 2000, 1.3), f, sr), 6);
    }

    [Fact]
    public void Disabled_or_invalid_filters_design_nothing_and_never_throw()
    {
        Assert.Empty(FilterDesign.Design(new FilterSpec(FilterType.Peak, 1000, Enabled: false), 96000));
        Assert.Empty(FilterDesign.Design(new FilterSpec(FilterType.Peak, double.NaN), 96000));
        // absurde Werte werden geklemmt
        var odd = FilterDesign.Design(new FilterSpec(FilterType.HighPass, 1e9, -3, 500, 99, true), 96000);
        Assert.NotEmpty(odd);
        foreach (var c in odd) Assert.True(double.IsFinite(c.B0) && double.IsFinite(c.A1) && double.IsFinite(c.A2));
    }

    [Fact]
    public void Butterworth_and_LR_sections_are_stable()
    {
        foreach (int sr in new[] { 44100, 48000, 96000, 192000 })
        foreach (double f in new[] { 5.0, 20, 100, 1000, 10000, 20000 })
        foreach (var type in new[] { FilterType.HighPass, FilterType.LowPass })
        foreach (bool lr in new[] { false, true })
        for (int order = 1; order <= 8; order++)
        {
            foreach (var c in FilterDesign.Design(new FilterSpec(type, f, 0.7071, 0, order, lr), sr))
            {
                Assert.True(Math.Abs(c.A2) < 1.0, $"a2 {c.A2}");
                Assert.True(Math.Abs(c.A1) < 1.0 + c.A2, $"a1 {c.A1} a2 {c.A2}");
            }
        }
    }

    // ------------------------------------------------ durch die Engine gemessen (Sinus -> Ausgang)

    [Fact]
    public void Engine_measured_response_matches_analytic_formula()
    {
        const int sr = 48000;
        var specs = new[]
        {
            new FilterSpec(FilterType.Peak, 1000, 2.0, 10),
            new FilterSpec(FilterType.LowShelf, 300, 0.7071, -8),
            new FilterSpec(FilterType.HighShelf, 5000, 0.7071, 7),
            new FilterSpec(FilterType.Notch, 2000, 4.0),
            new FilterSpec(FilterType.BandPass, 1000, 1.5),
            new FilterSpec(FilterType.AllPass, 1500, 1.0),
            new FilterSpec(FilterType.HighPass, 200, 0.7071, 0, 3),
            new FilterSpec(FilterType.LowPass, 4000, 0.7071, 0, 6),
            new FilterSpec(FilterType.HighPass, 300, 0.7071, 0, 4, true),
            new FilterSpec(FilterType.LowPass, 2500, 0.7071, 0, 8, true),
        };
        foreach (var spec in specs)
        {
            var settings = Mono(sr, spec);
            foreach (double f in new[] { 100.0, 250, 500, 1000, 1500, 2000, 3000, 6000, 12000 })
            {
                double expectedDb = ToDb(Math.Max(Analytic(spec, f, sr), 1e-6));
                double measuredDb = MeasureGainDb(settings, f);
                // Tiefe Sperrbereiche (< -60 dB) sind durch float-Quantisierung begrenzt -> dort muss nur "tief genug" gemessen werden
                bool ok = expectedDb < -60 ? measuredDb < -57 : Math.Abs(measuredDb - expectedDb) <= 0.02;
                Assert.True(ok,
                    $"{spec.Type} order {spec.Order} lr={spec.LinkwitzRiley} @ {f} Hz: erwartet {expectedDb:F4} dB, gemessen {measuredDb:F4} dB");
            }
        }
    }

    [Fact]
    public void Engine_filter_chain_multiplies_responses()
    {
        const int sr = 96000;
        var chain = new[]
        {
            new FilterSpec(FilterType.HighPass, 40, 0.7071, 0, 4, true),
            new FilterSpec(FilterType.Peak, 100, 1.0, 6),
            new FilterSpec(FilterType.Peak, 1000, 2.0, -5),
            new FilterSpec(FilterType.HighShelf, 6000, 0.7071, 4),
            new FilterSpec(FilterType.Notch, 9000, 8.0),
            new FilterSpec(FilterType.LowPass, 18000, 0.7071, 0, 4, true),
        };
        var settings = Mono(sr, chain);
        foreach (double f in new[] { 30.0, 60, 100, 400, 1000, 3000, 8000, 15000 })
        {
            double expectedDb = ToDb(FilterDesign.Response(chain, f, sr).Magnitude);
            double measuredDb = MeasureGainDb(settings, f);
            Assert.True(Math.Abs(measuredDb - expectedDb) <= 0.02, $"@ {f} Hz: erwartet {expectedDb:F4}, gemessen {measuredDb:F4}");
        }
    }
}
