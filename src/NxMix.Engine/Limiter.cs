namespace NxMix.Engine;

/// <summary>
/// Brickwall-Limiter mit ~1 ms Lookahead. Garantiert |y| &lt;= Ceiling.
/// Verfahren: Verstaerkungsbedarf r[n] = min(1, c/|x[n]|); gleitendes Minimum m[n] ueber L+1 Samples (Monotone Deque);
/// Release (exponentiell, ~100 ms) auf dem Minimum; danach gleitender Mittelwert ueber L Samples (Rampe);
/// Audio wird um L Samples verzoegert. Da jeder Mittelwert-Term &lt;= r des gerade ausgegebenen Samples ist, gilt
/// g[n] &lt;= r[n-L]  =>  |y| &lt;= c. Eine abschliessende harte Begrenzung faengt nur Rundungsfehler und Ceiling-Wechsel ab.
/// </summary>
internal sealed class Limiter
{
    const double ReleaseSeconds = 0.100;

    readonly int _l;                 // Lookahead in Samples
    readonly double _invL;
    readonly float[] _audio;         // Verzoegerungsring (L)
    readonly double[] _envRing;      // Huellkurvenring (L)
    readonly float[] _dqVal;
    readonly long[] _dqIdx;
    readonly int _dqCap;
    readonly double _relCoef;
    int _dqHead, _dqCount;
    int _pos;
    double _env = 1.0;
    double _sum;
    long _t;
    int _hold;                       // aufeinanderfolgende Samples mit env == 1
    bool _active;                    // Zustand ist nicht "ruhig" (Deque nicht leer, env < 1 oder Ring nicht lauter Einsen)

    /// <summary>Grenze (linear), ist garantiert &lt;= dem exakten 10^(dB/20) (in double gerechnet).</summary>
    public float Ceiling = 0.89125094f;

    public int LookaheadSamples => _l;

    public Limiter(int sampleRate)
    {
        _l = Math.Max(1, (int)Math.Round(sampleRate * 0.001));
        _invL = 1.0 / _l;
        _audio = new float[_l];
        _envRing = new double[_l];
        Array.Fill(_envRing, 1.0);
        _sum = _l;
        _hold = _l;
        _dqCap = _l + 2;
        _dqVal = new float[_dqCap];
        _dqIdx = new long[_dqCap];
        _relCoef = 1.0 - Math.Exp(-1.0 / (ReleaseSeconds * sampleRate));
    }

    public static float CeilingToLinear(double dbfs)
    {
        if (double.IsNaN(dbfs)) dbfs = -1.0;
        dbfs = Math.Clamp(dbfs, -120.0, 60.0);
        double c = Math.Pow(10.0, dbfs / 20.0);
        float f = (float)c;
        if ((double)f > c) f = MathF.BitDecrement(f);
        return f;
    }

    /// <summary>Verarbeitet x[0..n) und schreibt nach dst[dstOff..+n). peak = Spitzenbetrag von x. True, wenn begrenzt wurde.</summary>
    public bool Process(float[] x, float[] dst, int dstOff, int n, float peak)
    {
        float c = Ceiling;
        if (!_active && peak <= c)
        {
            FastPath(x, dst, dstOff, n, c);
            return false;
        }
        return SlowPath(x, dst, dstOff, n, c);
    }

    void FastPath(float[] x, float[] dst, int dstOff, int n, float c)
    {
        var audio = _audio;
        int pos = _pos, l = _l;
        for (int j = 0; j < n; j++)
        {
            float yd = audio[pos];
            audio[pos] = x[j];
            if (yd > c) yd = c; else if (yd < -c) yd = -c;
            dst[dstOff + j] = yd;
            if (++pos == l) pos = 0;
        }
        _pos = pos;
        _t += n;
    }

    bool SlowPath(float[] x, float[] dst, int dstOff, int n, float c)
    {
        var audio = _audio;
        var envRing = _envRing;
        var dqVal = _dqVal;
        var dqIdx = _dqIdx;
        int cap = _dqCap, l = _l;
        int pos = _pos, head = _dqHead, count = _dqCount;
        double env = _env, sum = _sum, invL = _invL, rel = _relCoef;
        long t = _t;
        int hold = _hold;
        double minG = 1.0;

        for (int j = 0; j < n; j++)
        {
            float xv = x[j];
            float a = MathF.Abs(xv);

            if (a > c)
            {
                float r = c / a;
                // Monotone Deque: von hinten alles >= r entfernen
                while (count > 0)
                {
                    int back = head + count - 1; if (back >= cap) back -= cap;
                    if (dqVal[back] >= r) count--; else break;
                }
                int tail = head + count; if (tail >= cap) tail -= cap;
                dqVal[tail] = r;
                dqIdx[tail] = t;
                count++;
            }
            // abgelaufene Eintraege vorn entfernen (Fenster [t-L, t])
            long lim = t - l;
            while (count > 0 && dqIdx[head] < lim)
            {
                if (++head == cap) head = 0;
                count--;
            }
            double m = count > 0 ? dqVal[head] : 1.0;

            double rise = env + (1.0 - env) * rel;
            env = m < rise ? m : rise;
            if (m >= 1.0 && 1.0 - env < 1e-7) env = 1.0;

            double old = envRing[pos];
            envRing[pos] = env;
            sum += env - old;
            double g = sum * invL;
            if (g > 1.0) g = 1.0;
            if (g < minG) minG = g;

            float yd = audio[pos];
            audio[pos] = xv;
            float y = yd * (float)g;
            if (y > c) y = c; else if (y < -c) y = -c;
            dst[dstOff + j] = y;

            if (env < 1.0) hold = 0; else if (hold < l) hold++;
            if (++pos == l) pos = 0;
            t++;
        }

        _pos = pos; _dqHead = head; _dqCount = count; _env = env; _sum = sum; _t = t; _hold = hold;
        _active = count > 0 || env < 1.0 || hold < l;
        if (!_active) { _sum = l; _env = 1.0; }
        return minG < 0.9999;
    }
}
