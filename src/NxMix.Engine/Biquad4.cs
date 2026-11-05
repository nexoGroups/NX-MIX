using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace NxMix.Engine;

/// <summary>
/// Biquad-Ketten fuer VIER Kanaele gleichzeitig (AVX2/FMA, eine Spur je Lane in einem Vector256&lt;double&gt;).
/// Jede Lane hat eigene Koeffizienten und eigenen Zustand; Kanaele mit weniger Sektionen werden mit Durchgangs-Sektionen
/// (b0 = 1, Rest 0) aufgefuellt. Nur fuer den Beharrungszustand (keine Koeffizientenrampe, keine Kreuzblende) - sonst laeuft der
/// skalare Pfad in MixerEngine. Rechnet exakt dieselbe Gleichung wie Biquad.RunStatic.
/// </summary>
internal static class Biquad4
{
    public static bool Supported => Avx2.IsSupported && Fma.IsSupported;

    /// <summary>
    /// d[0..n) enthaelt je Sample die vier Kanaele (Lane = Kanal). lanes[0..count) liefern Sektionen und Zustand.
    /// Verarbeitet Sektion 0..maxSections-1 in place und schreibt die Zustaende zurueck.
    /// </summary>
    public static void Run(Vector256<double>[] d, int n, ChannelRt[] lanes, int count, int maxSections)
    {
        int s = 0;
        while (s < maxSections)
        {
            if (s + 1 < maxSections) { RunPair(d, n, lanes, count, s); s += 2; }
            else { RunSingle(d, n, lanes, count, s); s++; }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static Section? Get(ChannelRt[] lanes, int count, int lane, int s)
    {
        if (lane >= count) return null;
        var secs = lanes[lane].Sections;
        return s < secs.Length ? secs[s] : null;
    }

    static void RunSingle(Vector256<double>[] d, int n, ChannelRt[] lanes, int count, int si)
    {
        var a0 = Get(lanes, count, 0, si); var a1 = Get(lanes, count, 1, si);
        var a2 = Get(lanes, count, 2, si); var a3 = Get(lanes, count, 3, si);

        var b0 = Vector256.Create(a0?.B0 ?? 1.0, a1?.B0 ?? 1.0, a2?.B0 ?? 1.0, a3?.B0 ?? 1.0);
        var c1 = Vector256.Create(a0?.C1 ?? 0.0, a1?.C1 ?? 0.0, a2?.C1 ?? 0.0, a3?.C1 ?? 0.0);
        var c2 = Vector256.Create(a0?.C2 ?? 0.0, a1?.C2 ?? 0.0, a2?.C2 ?? 0.0, a3?.C2 ?? 0.0);
        var na1 = Vector256.Create(-(a0?.A1 ?? 0.0), -(a1?.A1 ?? 0.0), -(a2?.A1 ?? 0.0), -(a3?.A1 ?? 0.0));
        var na2 = Vector256.Create(-(a0?.A2 ?? 0.0), -(a1?.A2 ?? 0.0), -(a2?.A2 ?? 0.0), -(a3?.A2 ?? 0.0));
        var s1 = Vector256.Create(a0?.S1 ?? 0.0, a1?.S1 ?? 0.0, a2?.S1 ?? 0.0, a3?.S1 ?? 0.0);
        var s2 = Vector256.Create(a0?.S2 ?? 0.0, a1?.S2 ?? 0.0, a2?.S2 ?? 0.0, a3?.S2 ?? 0.0);

        ref var p = ref d[0];
        for (int j = 0; j < n; j++)
        {
            var x = Unsafe.Add(ref p, j);
            var y = Fma.MultiplyAdd(b0, x, s1);
            var t = Fma.MultiplyAdd(na1, s1, Fma.MultiplyAdd(c1, x, s2));
            s2 = Fma.MultiplyAdd(na2, s1, Avx.Multiply(c2, x));
            s1 = t;
            Unsafe.Add(ref p, j) = y;
        }

        Store(a0, s1, s2, 0); Store(a1, s1, s2, 1); Store(a2, s1, s2, 2); Store(a3, s1, s2, 3);
    }

    static void RunPair(Vector256<double>[] d, int n, ChannelRt[] lanes, int count, int si)
    {
        var a0 = Get(lanes, count, 0, si); var a1 = Get(lanes, count, 1, si);
        var a2 = Get(lanes, count, 2, si); var a3 = Get(lanes, count, 3, si);
        var e0 = Get(lanes, count, 0, si + 1); var e1 = Get(lanes, count, 1, si + 1);
        var e2 = Get(lanes, count, 2, si + 1); var e3 = Get(lanes, count, 3, si + 1);

        var b0a = Vector256.Create(a0?.B0 ?? 1.0, a1?.B0 ?? 1.0, a2?.B0 ?? 1.0, a3?.B0 ?? 1.0);
        var c1a = Vector256.Create(a0?.C1 ?? 0.0, a1?.C1 ?? 0.0, a2?.C1 ?? 0.0, a3?.C1 ?? 0.0);
        var c2a = Vector256.Create(a0?.C2 ?? 0.0, a1?.C2 ?? 0.0, a2?.C2 ?? 0.0, a3?.C2 ?? 0.0);
        var na1a = Vector256.Create(-(a0?.A1 ?? 0.0), -(a1?.A1 ?? 0.0), -(a2?.A1 ?? 0.0), -(a3?.A1 ?? 0.0));
        var na2a = Vector256.Create(-(a0?.A2 ?? 0.0), -(a1?.A2 ?? 0.0), -(a2?.A2 ?? 0.0), -(a3?.A2 ?? 0.0));
        var s1a = Vector256.Create(a0?.S1 ?? 0.0, a1?.S1 ?? 0.0, a2?.S1 ?? 0.0, a3?.S1 ?? 0.0);
        var s2a = Vector256.Create(a0?.S2 ?? 0.0, a1?.S2 ?? 0.0, a2?.S2 ?? 0.0, a3?.S2 ?? 0.0);

        var b0b = Vector256.Create(e0?.B0 ?? 1.0, e1?.B0 ?? 1.0, e2?.B0 ?? 1.0, e3?.B0 ?? 1.0);
        var c1b = Vector256.Create(e0?.C1 ?? 0.0, e1?.C1 ?? 0.0, e2?.C1 ?? 0.0, e3?.C1 ?? 0.0);
        var c2b = Vector256.Create(e0?.C2 ?? 0.0, e1?.C2 ?? 0.0, e2?.C2 ?? 0.0, e3?.C2 ?? 0.0);
        var na1b = Vector256.Create(-(e0?.A1 ?? 0.0), -(e1?.A1 ?? 0.0), -(e2?.A1 ?? 0.0), -(e3?.A1 ?? 0.0));
        var na2b = Vector256.Create(-(e0?.A2 ?? 0.0), -(e1?.A2 ?? 0.0), -(e2?.A2 ?? 0.0), -(e3?.A2 ?? 0.0));
        var s1b = Vector256.Create(e0?.S1 ?? 0.0, e1?.S1 ?? 0.0, e2?.S1 ?? 0.0, e3?.S1 ?? 0.0);
        var s2b = Vector256.Create(e0?.S2 ?? 0.0, e1?.S2 ?? 0.0, e2?.S2 ?? 0.0, e3?.S2 ?? 0.0);

        ref var p = ref d[0];
        for (int j = 0; j < n; j++)
        {
            var x = Unsafe.Add(ref p, j);
            var y1 = Fma.MultiplyAdd(b0a, x, s1a);
            var ta = Fma.MultiplyAdd(na1a, s1a, Fma.MultiplyAdd(c1a, x, s2a));
            s2a = Fma.MultiplyAdd(na2a, s1a, Avx.Multiply(c2a, x));
            s1a = ta;
            var y2 = Fma.MultiplyAdd(b0b, y1, s1b);
            var tb = Fma.MultiplyAdd(na1b, s1b, Fma.MultiplyAdd(c1b, y1, s2b));
            s2b = Fma.MultiplyAdd(na2b, s1b, Avx.Multiply(c2b, y1));
            s1b = tb;
            Unsafe.Add(ref p, j) = y2;
        }

        Store(a0, s1a, s2a, 0); Store(a1, s1a, s2a, 1); Store(a2, s1a, s2a, 2); Store(a3, s1a, s2a, 3);
        Store(e0, s1b, s2b, 0); Store(e1, s1b, s2b, 1); Store(e2, s1b, s2b, 2); Store(e3, s1b, s2b, 3);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    static void Store(Section? s, Vector256<double> s1, Vector256<double> s2, int lane)
    {
        if (s is null) return;
        s.S1 = s1.GetElement(lane);
        s.S2 = s2.GetElement(lane);
    }
}
