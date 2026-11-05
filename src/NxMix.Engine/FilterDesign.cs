using System.Numerics;

namespace NxMix.Engine;

/// <summary>Biquad-Koeffizienten, a0 auf 1 normiert:  y = b0 x + b1 x1 + b2 x2 - a1 y1 - a2 y2.</summary>
public readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2)
{
    /// <summary>Komplexer Frequenzgang H(e^jw) bei freqHz.</summary>
    public Complex Response(double freqHz, int sampleRate)
    {
        double w = 2.0 * Math.PI * freqHz / sampleRate;
        var z1 = new Complex(Math.Cos(w), -Math.Sin(w));   // z^-1
        var z2 = z1 * z1;
        var num = B0 + B1 * z1 + B2 * z2;
        var den = 1.0 + A1 * z1 + A2 * z2;
        return num / den;
    }
}

/// <summary>
/// Entwurf der Filter (RBJ-Audio-EQ-Cookbook, Butterworth-Ketten, Linkwitz-Riley) fuer eine gegebene Samplerate.
/// Reine Funktionen, allokieren - nur ausserhalb des Audio-Threads benutzen.
/// </summary>
public static class FilterDesign
{
    public const double MinFrequencyHz = 1.0;
    public const double MaxFilterGainDb = 36.0;

    /// <summary>Alle Biquad-Sektionen (a0 = 1) fuer einen Filter. Deaktivierte/ungueltige Filter liefern eine leere Liste.</summary>
    public static IReadOnlyList<BiquadCoefficients> Design(FilterSpec spec, int sampleRate)
    {
        var c = new List<BiquadCoefficients>(4);
        var k = new List<int>(4);
        Design(spec, sampleRate, c, k);
        return c;
    }

    /// <summary>Betrag in dB des gesamten Filters (alle Sektionen) bei freqHz.</summary>
    public static double MagnitudeDb(FilterSpec spec, double freqHz, int sampleRate)
        => 20.0 * Math.Log10(Response(spec, freqHz, sampleRate).Magnitude);

    /// <summary>Komplexer Frequenzgang des gesamten Filters bei freqHz (Produkt aller Sektionen).</summary>
    public static Complex Response(FilterSpec spec, double freqHz, int sampleRate)
    {
        Complex h = Complex.One;
        foreach (var s in Design(spec, sampleRate)) h *= s.Response(freqHz, sampleRate);
        return h;
    }

    /// <summary>Komplexer Frequenzgang einer Filterkette.</summary>
    public static Complex Response(IEnumerable<FilterSpec> chain, double freqHz, int sampleRate)
    {
        Complex h = Complex.One;
        foreach (var f in chain) h *= Response(f, freqHz, sampleRate);
        return h;
    }

    /// <summary>Wie oben, zusaetzlich mit einem Strukturschluessel je Sektion (gleicher Schluessel = Zustand uebernehmbar).</summary>
    internal static void Design(FilterSpec spec, int sampleRate, List<BiquadCoefficients> coefficients, List<int> kinds)
    {
        if (spec is null || !spec.Enabled || sampleRate <= 0) return;
        double f0 = spec.FrequencyHz;
        if (double.IsNaN(f0) || double.IsInfinity(f0)) return;
        f0 = Math.Clamp(f0, MinFrequencyHz, 0.49 * sampleRate);

        double q = spec.Q;
        if (double.IsNaN(q) || q <= 0) q = 0.7071067811865476;
        q = Math.Clamp(q, 0.05, 100.0);

        double gainDb = spec.GainDb;
        if (double.IsNaN(gainDb)) gainDb = 0;
        gainDb = Math.Clamp(gainDb, -MaxFilterGainDb, MaxFilterGainDb);

        switch (spec.Type)
        {
            case FilterType.HighPass:
            case FilterType.LowPass:
                DesignPass(spec, f0, q, sampleRate, coefficients, kinds);
                break;
            default:
                coefficients.Add(Rbj(spec.Type, f0, q, gainDb, sampleRate));
                kinds.Add(MakeKind(spec.Type, 0, false, 0));
                break;
        }
    }

    static int MakeKind(FilterType type, int order, bool lr, int index)
        => (int)type | (order << 4) | ((lr ? 1 : 0) << 8) | (index << 9);

    static void DesignPass(FilterSpec spec, double f0, double q, int sr, List<BiquadCoefficients> c, List<int> k)
    {
        bool high = spec.Type == FilterType.HighPass;
        int order = Math.Clamp(spec.Order, 1, 8);
        bool lr = spec.LinkwitzRiley;
        int idx = 0;

        if (lr)
        {
            // LR-Ordnung N (gerade) = zwei kaskadierte Butterworth der Ordnung N/2.
            if (order < 2) order = 2;
            if ((order & 1) != 0) order--;
            int m = order / 2;
            for (int pass = 0; pass < 2; pass++)
                AddButterworth(high, m, f0, sr, c, k, spec.Type, order, true, ref idx, q, useQ: false);
        }
        else
        {
            AddButterworth(high, order, f0, sr, c, k, spec.Type, order, false, ref idx, q, useQ: order == 2);
        }
    }

    // Butterworth-Kette der Ordnung n: bei ungerader Ordnung eine Sektion 1. Ordnung + (n-1)/2 Biquads.
    // Polwinkel (gegen die negative reelle Achse): gerade n: (2k-1)pi/(2n), ungerade n: k pi/n;  Q = 1/(2 cos(phi)).
    static void AddButterworth(bool high, int n, double f0, int sr, List<BiquadCoefficients> c, List<int> k,
        FilterType type, int kindOrder, bool lr, ref int idx, double userQ, bool useQ)
    {
        if ((n & 1) != 0)
        {
            c.Add(FirstOrder(high, f0, sr));
            k.Add(MakeKind(type, kindOrder, lr, idx++));
        }
        int pairs = n / 2;
        for (int i = 1; i <= pairs; i++)
        {
            double phi = (n & 1) == 0 ? (2 * i - 1) * Math.PI / (2.0 * n) : i * Math.PI / n;
            double q = useQ ? userQ : 1.0 / (2.0 * Math.Cos(phi));
            c.Add(Rbj(high ? FilterType.HighPass : FilterType.LowPass, f0, q, 0, sr));
            k.Add(MakeKind(type, kindOrder, lr, idx++));
        }
    }

    static BiquadCoefficients FirstOrder(bool high, double f0, int sr)
    {
        double K = Math.Tan(Math.PI * f0 / sr);
        double a1 = (K - 1.0) / (K + 1.0);
        if (high)
        {
            double b0 = 1.0 / (1.0 + K);
            return new BiquadCoefficients(b0, -b0, 0, a1, 0);
        }
        else
        {
            double b0 = K / (1.0 + K);
            return new BiquadCoefficients(b0, b0, 0, a1, 0);
        }
    }

    /// <summary>RBJ Audio-EQ-Cookbook. Shelf-Filter mit alpha = sin(w0)/(2Q).</summary>
    static BiquadCoefficients Rbj(FilterType type, double f0, double q, double gainDb, int sr)
    {
        double w0 = 2.0 * Math.PI * f0 / sr;
        double cw = Math.Cos(w0), sw = Math.Sin(w0);
        double alpha = sw / (2.0 * q);
        double A = Math.Pow(10.0, gainDb / 40.0);
        double b0, b1, b2, a0, a1, a2;
        switch (type)
        {
            case FilterType.Peak:
                b0 = 1 + alpha * A; b1 = -2 * cw; b2 = 1 - alpha * A;
                a0 = 1 + alpha / A; a1 = -2 * cw; a2 = 1 - alpha / A;
                break;
            case FilterType.LowShelf:
            {
                double sA = 2 * Math.Sqrt(A) * alpha;
                b0 = A * ((A + 1) - (A - 1) * cw + sA);
                b1 = 2 * A * ((A - 1) - (A + 1) * cw);
                b2 = A * ((A + 1) - (A - 1) * cw - sA);
                a0 = (A + 1) + (A - 1) * cw + sA;
                a1 = -2 * ((A - 1) + (A + 1) * cw);
                a2 = (A + 1) + (A - 1) * cw - sA;
                break;
            }
            case FilterType.HighShelf:
            {
                double sA = 2 * Math.Sqrt(A) * alpha;
                b0 = A * ((A + 1) + (A - 1) * cw + sA);
                b1 = -2 * A * ((A - 1) + (A + 1) * cw);
                b2 = A * ((A + 1) + (A - 1) * cw - sA);
                a0 = (A + 1) - (A - 1) * cw + sA;
                a1 = 2 * ((A - 1) - (A + 1) * cw);
                a2 = (A + 1) - (A - 1) * cw - sA;
                break;
            }
            case FilterType.HighPass:
                b0 = (1 + cw) / 2; b1 = -(1 + cw); b2 = (1 + cw) / 2;
                a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
                break;
            case FilterType.LowPass:
                b0 = (1 - cw) / 2; b1 = 1 - cw; b2 = (1 - cw) / 2;
                a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
                break;
            case FilterType.Notch:
                b0 = 1; b1 = -2 * cw; b2 = 1;
                a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
                break;
            case FilterType.AllPass:
                b0 = 1 - alpha; b1 = -2 * cw; b2 = 1 + alpha;
                a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
                break;
            case FilterType.BandPass: // konstante 0 dB Spitzenverstaerkung
                b0 = alpha; b1 = 0; b2 = -alpha;
                a0 = 1 + alpha; a1 = -2 * cw; a2 = 1 - alpha;
                break;
            default:
                return new BiquadCoefficients(1, 0, 0, 0, 0);
        }
        return new BiquadCoefficients(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }
}
