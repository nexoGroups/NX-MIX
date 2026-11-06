using System.Numerics;

namespace NxMix.App.Model;

/// <summary>Fensterung und FFT für die Anzeige. Läuft außerhalb des Audiothreads.</summary>
internal sealed class SpectrumAnalyzer
{
    public const int Points = 144;
    public const double MinHz = 20;
    public const float FloorDb = -100;
    readonly Complex[] _fft, _roots;
    readonly double[] _window, _power;
    readonly int[] _reversed;
    readonly (int First, int Last, double Center)[] _bands = new (int, int, double)[Points];

    public static double MaxHz(int rate) => Math.Max(MinHz * 1.01, Math.Min(20000, rate * 0.49));
    public static double Frequency(double fraction, int rate) => MinHz * Math.Pow(MaxHz(rate) / MinHz, fraction);

    public SpectrumAnalyzer(int size, int rate)
    {
        _fft = new Complex[size];
        _roots = new Complex[size / 2];
        _window = new double[size];
        _reversed = new int[size];
        _power = new double[size / 2 + 1];
        for (int i = 0; i < size; i++)
        {
            // Periodisches Hann-Fenster, auf den Spitzenwert eines Sinus normiert.
            _window[i] = 2.0 / size * (1 - Math.Cos(2 * Math.PI * i / size));
            _reversed[i] = (_reversed[i >> 1] >> 1) | ((i & 1) * (size >> 1));
        }
        for (int i = 0; i < _roots.Length; i++) _roots[i] = Complex.FromPolarCoordinates(1, -2 * Math.PI * i / size);

        double binsPerHz = (double)size / rate;
        for (int i = 0; i < Points; i++)
        {
            double lo = Frequency(Math.Max(0, (i - 0.5) / (Points - 1)), rate) * binsPerHz;
            double hi = Frequency(Math.Min(1, (i + 0.5) / (Points - 1)), rate) * binsPerHz;
            _bands[i] = ((int)Math.Ceiling(lo), (int)Math.Floor(hi), Frequency((double)i / (Points - 1), rate) * binsPerHz);
        }
    }

    public void Analyze(ReadOnlySpan<float> samples, Span<float> levels)
    {
        int n = _fft.Length;
        for (int i = 0; i < n; i++) _fft[_reversed[i]] = new Complex(samples[i] * _window[i], 0);
        for (int length = 2; length <= n; length *= 2)
        {
            int half = length / 2, step = n / length;
            for (int start = 0; start < n; start += length)
                for (int i = 0; i < half; i++)
                {
                    var a = _fft[start + i];
                    var b = _fft[start + i + half] * _roots[i * step];
                    _fft[start + i] = a + b;
                    _fft[start + i + half] = a - b;
                }
        }
        for (int bin = 0; bin < _power.Length; bin++)
        {
            var v = _fft[bin];
            _power[bin] = v.Real * v.Real + v.Imaginary * v.Imaginary;
        }
        for (int i = 0; i < Points; i++)
        {
            var (first, last, center) = _bands[i];
            double power = 0;
            if (first > last)
            {
                int bin = (int)center;
                power = _power[bin] + (_power[bin + 1] - _power[bin]) * (center - bin);
            }
            else
                for (int bin = first; bin <= last; bin++) power = Math.Max(power, _power[bin]);
            levels[i] = double.IsFinite(power) ? Math.Max(FloorDb, (float)(10 * Math.Log10(power))) : FloorDb;
        }
    }
}
