using System.Runtime.Intrinsics.X86;

namespace NxMix.Engine;

/// <summary>Biquad-Kerne (double, Transposed Direct Form II in umgeformter Fassung, siehe Section).</summary>
internal static class Biquad
{
    /// <summary>
    /// Verarbeitet d[0..n) in place. Die ersten k Samples laufen auf der Koeffizientenrampe (falls die Sektion eine braucht),
    /// rampBefore = verbleibende Rampenlaenge zu Blockbeginn.
    /// </summary>
    public static void Run(double[] d, int n, Section s, int k, int rampBefore)
    {
        int pos = 0;
        if (k > 0 && s.NeedsRamp)
        {
            RunRamp(d, k, rampBefore, s);
            pos = k;
        }
        if (pos < n) RunStatic(d, pos, n, s);
    }

    /// <summary>Zwei Sektionen in einer Schleife (zwei unabhaengige Rekursionen laufen ueberlappt - schneller). Beide ohne Rampe.</summary>
    public static void RunPair(double[] d, int start, int end, Section a, Section b)
    {
        double b0a = a.B0, c1a = a.C1, c2a = a.C2, na1a = -a.A1, na2a = -a.A2, s1a = a.S1, s2a = a.S2;
        double b0b = b.B0, c1b = b.C1, c2b = b.C2, na1b = -b.A1, na2b = -b.A2, s1b = b.S1, s2b = b.S2;
        if (Fma.IsSupported)
        {
            for (int j = start; j < end; j++)
            {
                double x = d[j];
                double y1 = Math.FusedMultiplyAdd(b0a, x, s1a);
                double ta = Math.FusedMultiplyAdd(na1a, s1a, Math.FusedMultiplyAdd(c1a, x, s2a));
                s2a = Math.FusedMultiplyAdd(na2a, s1a, c2a * x);
                s1a = ta;
                double y2 = Math.FusedMultiplyAdd(b0b, y1, s1b);
                double tb = Math.FusedMultiplyAdd(na1b, s1b, Math.FusedMultiplyAdd(c1b, y1, s2b));
                s2b = Math.FusedMultiplyAdd(na2b, s1b, c2b * y1);
                s1b = tb;
                d[j] = y2;
            }
        }
        else
        {
            for (int j = start; j < end; j++)
            {
                double x = d[j];
                double y1 = b0a * x + s1a;
                double ta = c1a * x + s2a + na1a * s1a;
                s2a = c2a * x + na2a * s1a;
                s1a = ta;
                double y2 = b0b * y1 + s1b;
                double tb = c1b * y1 + s2b + na1b * s1b;
                s2b = c2b * y1 + na2b * s1b;
                s1b = tb;
                d[j] = y2;
            }
        }
        a.S1 = s1a; a.S2 = s2a; b.S1 = s1b; b.S2 = s2b;
    }

    public static void RunStatic(double[] d, int start, int end, Section s)
    {
        double b0 = s.B0, c1 = s.C1, c2 = s.C2, na1 = -s.A1, na2 = -s.A2, s1 = s.S1, s2 = s.S2;
        if (Fma.IsSupported)
        {
            for (int j = start; j < end; j++)
            {
                double x = d[j];
                double y = Math.FusedMultiplyAdd(b0, x, s1);
                double t = Math.FusedMultiplyAdd(na1, s1, Math.FusedMultiplyAdd(c1, x, s2));
                s2 = Math.FusedMultiplyAdd(na2, s1, c2 * x);
                s1 = t;
                d[j] = y;
            }
        }
        else
        {
            for (int j = start; j < end; j++)
            {
                double x = d[j];
                double y = b0 * x + s1;
                double t = c1 * x + s2 + na1 * s1;
                s2 = c2 * x + na2 * s1;
                s1 = t;
                d[j] = y;
            }
        }
        s.S1 = s1; s.S2 = s2;
    }

    static void RunRamp(double[] d, int k, int rampBefore, Section s)
    {
        double inv = 1.0 / rampBefore;
        double db0 = (s.TB0 - s.B0) * inv, dc1 = (s.TC1 - s.C1) * inv, dc2 = (s.TC2 - s.C2) * inv;
        double da1 = (s.TA1 - s.A1) * inv, da2 = (s.TA2 - s.A2) * inv;
        double b0 = s.B0, c1 = s.C1, c2 = s.C2, a1 = s.A1, a2 = s.A2, s1 = s.S1, s2 = s.S2;
        for (int j = 0; j < k; j++)
        {
            b0 += db0; c1 += dc1; c2 += dc2; a1 += da1; a2 += da2;
            double x = d[j];
            double y = b0 * x + s1;
            double t = c1 * x + s2 - a1 * s1;
            s2 = c2 * x - a2 * s1;
            s1 = t;
            d[j] = y;
        }
        s.S1 = s1; s.S2 = s2;
        if (k == rampBefore) s.SnapToTarget();
        else { s.B0 = b0; s.C1 = c1; s.C2 = c2; s.A1 = a1; s.A2 = a2; }
    }
}
