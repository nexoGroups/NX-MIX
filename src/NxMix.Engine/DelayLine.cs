namespace NxMix.Engine;

/// <summary>
/// Ringpuffer-Verzoegerung mit ganzzahligem Anteil + linearer Interpolation fuer Bruchteile.
/// Aenderungen der Verzoegerung werden ueber ~10 ms ueberblendet (Kreuzblende zweier Abgriffe), nie als Sprung.
/// Ablauf pro Block: Push(x) schreibt den Block, Read(x) ersetzt ihn durch die verzoegerte Version.
/// </summary>
internal sealed class DelayLine
{
    readonly float[] _buf;
    readonly int _mask;
    readonly int _maxDelay;
    int _w;           // naechste Schreibposition
    int _chunkStart;  // Schreibposition des zuletzt gepushten Blocks
    double _cur;      // aktuelle Verzoegerung in Samples
    double _fadeTo;
    int _fadeLeft;

    public DelayLine(int maxDelaySamples, int maxChunk)
    {
        _maxDelay = Math.Max(0, maxDelaySamples);
        int need = _maxDelay + maxChunk + 8;
        int cap = 16;
        while (cap < need) cap <<= 1;
        _buf = new float[cap];
        _mask = cap - 1;
    }

    public void Clear() => Array.Clear(_buf);

    /// <summary>Setzt die Verzoegerung sofort (nur beim Aufbau).</summary>
    public void Snap(double samples)
    {
        _cur = samples > 0 ? Math.Min(samples, _maxDelay) : 0;
        _fadeLeft = 0;
    }

    /// <summary>Schreibt x[0..n) in den Ring (x bleibt unveraendert).</summary>
    public void Push(float[] x, int n)
    {
        _chunkStart = _w;
        int first = Math.Min(n, _buf.Length - _w);
        Array.Copy(x, 0, _buf, _w, first);
        if (first < n) Array.Copy(x, first, _buf, 0, n - first);
        _w = (_w + n) & _mask;
    }

    /// <summary>Ersetzt x[0..n) (zuvor mit Push uebergeben) durch die um das Ziel verzoegerte Version.</summary>
    public void Read(float[] x, int n, double targetSamples, int rampLen)
    {
        if (!(targetSamples > 0)) targetSamples = 0;
        if (targetSamples > _maxDelay) targetSamples = _maxDelay;
        int pos = 0;
        while (pos < n)
        {
            if (_fadeLeft == 0 && targetSamples != _cur)
            {
                _fadeTo = targetSamples;
                _fadeLeft = Math.Max(1, rampLen);
            }
            if (_fadeLeft == 0)
            {
                ReadStatic(x, pos, n, _cur);
                return;
            }
            int seg = Math.Min(_fadeLeft, n - pos);
            ReadFade(x, pos, seg, rampLen);
            pos += seg;
            _fadeLeft -= seg;
            if (_fadeLeft == 0) _cur = _fadeTo;
        }
    }

    void ReadStatic(float[] x, int from, int to, double d)
    {
        int di = (int)d;
        float f = (float)(d - di);
        if (f < 1e-6f) f = 0f;
        else if (f > 1f - 1e-6f) { di++; f = 0f; }

        if (f == 0f)
        {
            if (di == 0) return;                    // x enthaelt bereits das aktuelle Signal
            int len = to - from;
            int src = (_chunkStart + from - di) & _mask;
            int first = Math.Min(len, _buf.Length - src);
            Array.Copy(_buf, src, x, from, first);
            if (first < len) Array.Copy(_buf, 0, x, from + first, len - first);
            return;
        }

        float g = 1f - f;
        int baseIdx = _chunkStart - di;
        var buf = _buf;
        int mask = _mask;
        for (int j = from; j < to; j++)
        {
            int i0 = (baseIdx + j) & mask;
            int i1 = (i0 - 1) & mask;
            x[j] = buf[i0] * g + buf[i1] * f;
        }
    }

    void ReadFade(float[] x, int from, int count, int rampLen)
    {
        int da = (int)_cur, db = (int)_fadeTo;
        float fa = (float)(_cur - da), fb = (float)(_fadeTo - db);
        float ga = 1f - fa, gb = 1f - fb;
        int done = rampLen - _fadeLeft;       // bereits ueberblendete Samples
        float inv = 1f / rampLen;
        var buf = _buf;
        int mask = _mask;
        for (int jj = 0; jj < count; jj++)
        {
            int j = from + jj;
            float a = (done + jj + 1) * inv;
            if (a > 1f) a = 1f;
            int ia0 = (_chunkStart + j - da) & mask;
            int ia1 = (ia0 - 1) & mask;
            int ib0 = (_chunkStart + j - db) & mask;
            int ib1 = (ib0 - 1) & mask;
            float ta = buf[ia0] * ga + buf[ia1] * fa;
            float tb = buf[ib0] * gb + buf[ib1] * fb;
            x[j] = ta + (tb - ta) * a;
        }
    }
}
