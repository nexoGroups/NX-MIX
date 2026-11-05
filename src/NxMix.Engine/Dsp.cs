using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NxMix.Engine;

/// <summary>Allokationsfreie Vektor-Bausteine (Audio-Thread).</summary>
internal static class Dsp
{
    /// <summary>Werte unterhalb (-600 dBFS) werden zu 0 geflusht: vermeidet Denormals in float.</summary>
    public const float Tiny = 1e-30f;
    /// <summary>Gueltiger Eingangsbereich (+120 dBFS). Alles darueber ist Muell und wird 0 (verhindert, dass ein Fehlwert die Filter minutenlang ausschwingen laesst).</summary>
    public const float HugeIn = 1e6f;
    /// <summary>Gueltiger Bereich nach Filterketten/Gain (+180 dBFS). Darueber gelten die Filterzustaende als vergiftet.</summary>
    public const float HugeOut = 1e9f;

    public static double DbToLin(double db)
    {
        if (double.IsNaN(db) || double.IsNegativeInfinity(db)) return 0.0;
        return Math.Pow(10.0, db / 20.0);
    }

    /// <summary>Kopiert src[srcOff..+n) nach dst[0..n), ersetzt NaN/Inf/Winziges durch 0. Liefert den Spitzenbetrag.</summary>
    public static float SanitizeCopy(float[] src, int srcOff, float[] dst, int n, float limit)
    {
        ref float s = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(src), srcOff);
        ref float d = ref MemoryMarshal.GetArrayDataReference(dst);
        int W = Vector<float>.Count;
        int j = 0;
        var vmax = Vector<float>.Zero;
        var tiny = new Vector<float>(Tiny);
        var inf = new Vector<float>(limit);
        for (; j <= n - W; j += W)
        {
            var v = Vector.LoadUnsafe(ref s, (nuint)j);
            var a = Vector.Abs(v);
            var ok = Vector.GreaterThan(a, tiny) & Vector.LessThan(a, inf);   // NaN -> false
            v = Vector.ConditionalSelect(ok, v, Vector<float>.Zero);
            v.StoreUnsafe(ref d, (nuint)j);
            vmax = Vector.Max(vmax, Vector.Abs(v));
        }
        float peak = HMax(vmax);
        for (; j < n; j++)
        {
            float x = Unsafe.Add(ref s, j);
            float a = MathF.Abs(x);
            if (!(a > Tiny && a < limit)) x = 0f; else if (a > peak) peak = a;
            Unsafe.Add(ref d, j) = x;
        }
        return peak;
    }

    /// <summary>Ersetzt NaN/Inf/Winziges in x[0..n) durch 0. bad = es gab NaN/Inf. Liefert den Spitzenbetrag.</summary>
    public static float SanitizeInPlace(float[] x, int n, float limit, out bool bad)
    {
        ref float p = ref MemoryMarshal.GetArrayDataReference(x);
        int W = Vector<float>.Count;
        int j = 0;
        var vmax = Vector<float>.Zero;
        var badAcc = Vector<int>.Zero;
        var tiny = new Vector<float>(Tiny);
        var inf = new Vector<float>(limit);
        for (; j <= n - W; j += W)
        {
            var v = Vector.LoadUnsafe(ref p, (nuint)j);
            var a = Vector.Abs(v);
            var finite = Vector.LessThan(a, inf);                              // NaN/Inf -> false
            badAcc |= Vector.OnesComplement(finite);
            var ok = Vector.GreaterThan(a, tiny) & finite;
            v = Vector.ConditionalSelect(ok, v, Vector<float>.Zero);
            v.StoreUnsafe(ref p, (nuint)j);
            vmax = Vector.Max(vmax, Vector.Abs(v));
        }
        bool anyBad = badAcc != Vector<int>.Zero;
        float peak = HMax(vmax);
        for (; j < n; j++)
        {
            float v = Unsafe.Add(ref p, j);
            float a = MathF.Abs(v);
            if (!(a < limit)) { anyBad = true; v = 0f; }
            else if (!(a > Tiny)) v = 0f;
            else if (a > peak) peak = a;
            Unsafe.Add(ref p, j) = v;
        }
        bad = anyBad;
        return peak;
    }

    /// <summary>Spitzenbetrag von x[off..off+n) (nur endliche Werte erwartet).</summary>
    public static float Peak(float[] x, int off, int n)
    {
        ref float p = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(x), off);
        int W = Vector<float>.Count;
        int j = 0;
        var vmax = Vector<float>.Zero;
        for (; j <= n - W; j += W) vmax = Vector.Max(vmax, Vector.Abs(Vector.LoadUnsafe(ref p, (nuint)j)));
        float peak = HMax(vmax);
        for (; j < n; j++) peak = MathF.Max(peak, MathF.Abs(Unsafe.Add(ref p, j)));
        return peak;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static float HMax(Vector<float> v)
    {
        float m = 0f;
        for (int i = 0; i < Vector<float>.Count; i++) m = MathF.Max(m, v[i]);
        return m;
    }

    // ---------------------------------------------------------------- Mischen

    /// <summary>dst[j] (=|+=) x[j] * g fuer j in [start, end).</summary>
    public static void MulConst(float[] dst, float[] x, int start, int end, float g, bool assign)
    {
        if (end <= start) return;
        ref float d = ref MemoryMarshal.GetArrayDataReference(dst);
        ref float s = ref MemoryMarshal.GetArrayDataReference(x);
        int W = Vector<float>.Count;
        var vg = new Vector<float>(g);
        int j = start;
        if (assign)
        {
            for (; j <= end - W; j += W) (Vector.LoadUnsafe(ref s, (nuint)j) * vg).StoreUnsafe(ref d, (nuint)j);
            for (; j < end; j++) Unsafe.Add(ref d, j) = Unsafe.Add(ref s, j) * g;
        }
        else
        {
            for (; j <= end - W; j += W)
                (Vector.LoadUnsafe(ref d, (nuint)j) + Vector.LoadUnsafe(ref s, (nuint)j) * vg).StoreUnsafe(ref d, (nuint)j);
            for (; j < end; j++) Unsafe.Add(ref d, j) += Unsafe.Add(ref s, j) * g;
        }
    }

    /// <summary>dst[j] (=|+=) x[j] * (cur + step*(j+1)) fuer j in [0, end). rampIdx[j] = j+1.</summary>
    public static void MulRamp(float[] dst, float[] x, float[] rampIdx, int end, float cur, float step, bool assign)
    {
        if (end <= 0) return;
        ref float d = ref MemoryMarshal.GetArrayDataReference(dst);
        ref float s = ref MemoryMarshal.GetArrayDataReference(x);
        ref float r = ref MemoryMarshal.GetArrayDataReference(rampIdx);
        int W = Vector<float>.Count;
        var vc = new Vector<float>(cur);
        var vs = new Vector<float>(step);
        int j = 0;
        if (assign)
        {
            for (; j <= end - W; j += W)
            {
                var g = vc + vs * Vector.LoadUnsafe(ref r, (nuint)j);
                (Vector.LoadUnsafe(ref s, (nuint)j) * g).StoreUnsafe(ref d, (nuint)j);
            }
            for (; j < end; j++) Unsafe.Add(ref d, j) = Unsafe.Add(ref s, j) * (cur + step * (j + 1));
        }
        else
        {
            for (; j <= end - W; j += W)
            {
                var g = vc + vs * Vector.LoadUnsafe(ref r, (nuint)j);
                (Vector.LoadUnsafe(ref d, (nuint)j) + Vector.LoadUnsafe(ref s, (nuint)j) * g).StoreUnsafe(ref d, (nuint)j);
            }
            for (; j < end; j++) Unsafe.Add(ref d, j) += Unsafe.Add(ref s, j) * (cur + step * (j + 1));
        }
    }

    /// <summary>x[j] *= g fuer j in [start, end).</summary>
    public static void Scale(float[] x, int start, int end, float g)
    {
        if (end <= start) return;
        ref float p = ref MemoryMarshal.GetArrayDataReference(x);
        int W = Vector<float>.Count;
        var vg = new Vector<float>(g);
        int j = start;
        for (; j <= end - W; j += W) (Vector.LoadUnsafe(ref p, (nuint)j) * vg).StoreUnsafe(ref p, (nuint)j);
        for (; j < end; j++) Unsafe.Add(ref p, j) *= g;
    }

    /// <summary>x[j] *= cur + step*(j+1) fuer j in [0, end).</summary>
    public static void ScaleRamp(float[] x, float[] rampIdx, int end, float cur, float step)
    {
        if (end <= 0) return;
        ref float p = ref MemoryMarshal.GetArrayDataReference(x);
        ref float r = ref MemoryMarshal.GetArrayDataReference(rampIdx);
        int W = Vector<float>.Count;
        var vc = new Vector<float>(cur);
        var vs = new Vector<float>(step);
        int j = 0;
        for (; j <= end - W; j += W)
            (Vector.LoadUnsafe(ref p, (nuint)j) * (vc + vs * Vector.LoadUnsafe(ref r, (nuint)j))).StoreUnsafe(ref p, (nuint)j);
        for (; j < end; j++) Unsafe.Add(ref p, j) *= cur + step * (j + 1);
    }
}
