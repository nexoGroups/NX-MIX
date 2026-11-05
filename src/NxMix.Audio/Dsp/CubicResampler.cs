using System.Runtime.CompilerServices;

namespace NxMix.Audio.Dsp;

/// <summary>Streaming-Resampler mit kubischer Hermite-Interpolation (Catmull-Rom, 4 Stuetzstellen) und zeitveraenderlichem Verhaeltnis.
/// ratio = Eingangs-Frames pro Ausgangs-Frame (&gt; 1: schneller verbrauchen). Die Position wird als double fortgeschrieben,
/// daher ist der Ausgang auch bei sich langsam aendernder ratio stetig (kein Sprung, keine Phasenreset).
/// Eingang verschachtelt (Frames x Kanaele), Ausgang planar. Allokationsfrei.</summary>
internal sealed class CubicResampler
{
    private readonly int _ch;
    private readonly float[] _h0, _h1, _h2, _h3; // Fenster x[n-1], x[n], x[n+1], x[n+2]
    private double _mu;

    public CubicResampler(int channels)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        _ch = channels;
        _h0 = new float[channels];
        _h1 = new float[channels];
        _h2 = new float[channels];
        _h3 = new float[channels];
        Reset();
    }

    public int Channels => _ch;

    /// <summary>Setzt das Fenster auf Stille zurueck. Das Signal beginnt dann weich bei 0.</summary>
    public void Reset()
    {
        Array.Clear(_h0);
        Array.Clear(_h1);
        Array.Clear(_h2);
        Array.Clear(_h3);
        _mu = 2.0; // zwei Frames einlesen, bevor der erste Ausgang berechnet wird
    }

    /// <summary>Erzeugt bis zu <paramref name="outFrames"/> Ausgangs-Frames in <paramref name="output"/>[kanal][outOffset..].
    /// Verbraucht dabei <paramref name="consumed"/> Eingangs-Frames. Gibt die Zahl erzeugter Frames zurueck; weniger als verlangt,
    /// wenn der Eingang erschoepft ist (Zustand bleibt gueltig, der Rest kann beim naechsten Aufruf erzeugt werden).</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int Process(float[] input, int inFrames, double ratio, float[][] output, int outOffset, int outFrames, out int consumed)
    {
        int ch = _ch;
        float[] h0 = _h0, h1 = _h1, h2 = _h2, h3 = _h3;
        double mu = _mu;
        int ip = 0, op = 0;

        while (op < outFrames)
        {
            while (mu >= 1.0)
            {
                if (ip >= inFrames) goto Done;
                int b = ip * ch;
                for (int c = 0; c < ch; c++)
                {
                    h0[c] = h1[c];
                    h1[c] = h2[c];
                    h2[c] = h3[c];
                    h3[c] = input[b + c];
                }
                ip++;
                mu -= 1.0;
            }

            float t = (float)mu;
            float t2 = t * t;
            float t3 = t2 * t;
            float a0 = 0.5f * (-t3 + 2f * t2 - t);
            float a1 = 0.5f * (3f * t3 - 5f * t2 + 2f);
            float a2 = 0.5f * (-3f * t3 + 4f * t2 + t);
            float a3 = 0.5f * (t3 - t2);
            int o = outOffset + op;
            for (int c = 0; c < ch; c++)
                output[c][o] = a0 * h0[c] + a1 * h1[c] + a2 * h2[c] + a3 * h3[c];

            op++;
            mu += ratio;
        }

    Done:
        _mu = mu;
        consumed = ip;
        return op;
    }
}
