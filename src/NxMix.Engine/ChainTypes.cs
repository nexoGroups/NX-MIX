using System.Runtime.CompilerServices;

namespace NxMix.Engine;

/// <summary>
/// Eine Biquad-Sektion (Transposed Direct Form II, double). Gespeichert wird die umgeformte Fassung
///   y = b0 x + s1;  s1' = c1 x - a1 s1 + s2;  s2' = c2 x - a2 s1   mit c1 = b1 - a1 b0, c2 = b2 - a2 b0
/// (mathematisch identisch zu TDF II, aber mit kuerzerem rekursivem Pfad).
/// Ziel-Koeffizienten (T*) werden ueber die Rampe linear angefahren (Mischung zweier stabiler Nenner bleibt stabil).
/// </summary>
internal sealed class Section
{
    public readonly int Kind;
    public double TB0, TC1, TC2, TA1, TA2;   // Ziel
    public double B0, C1, C2, A1, A2;        // aktuell
    public double S1, S2;                    // Zustand

    public Section(int kind, in BiquadCoefficients c)
    {
        Kind = kind;
        TB0 = c.B0; TC1 = c.B1 - c.A1 * c.B0; TC2 = c.B2 - c.A2 * c.B0; TA1 = c.A1; TA2 = c.A2;
        B0 = TB0; C1 = TC1; C2 = TC2; A1 = TA1; A2 = TA2;
    }

    public bool NeedsRamp => B0 != TB0 || C1 != TC1 || C2 != TC2 || A1 != TA1 || A2 != TA2;

    public void SnapToTarget() { B0 = TB0; C1 = TC1; C2 = TC2; A1 = TA1; A2 = TA2; }

    public void ResetState() { S1 = 0; S2 = 0; }

    /// <summary>Kleine Zustaende auf 0 (Denormals vermeiden).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void FlushDenormals()
    {
        if (Math.Abs(S1) < 1e-20) S1 = 0;
        if (Math.Abs(S2) < 1e-20) S2 = 0;
    }
}

/// <summary>Laufzeitzustand eines Kanals, ueberlebt Einstellungswechsel (gehoert dem Audio-Thread).</summary>
internal sealed class ChannelRt
{
    public Section[] Sections = Array.Empty<Section>();
    public readonly DelayLine Delay;
    public Section[] OldSections = Array.Empty<Section>();   // alte Kette, laeuft waehrend der Kreuzblende nach Strukturwechsel weiter
    public int XfadeLeft;       // verbleibende Samples der Kreuzblende alt -> neu (gegen Knacken)
    public bool Stale;          // Filterzustaende veraltet (Kanal wurde uebersprungen)
    public float GainCur;       // nur Ausgaenge: aktuelle Verstaerkung (Kanal-Gain * Device-Master * Global-Master)
    public readonly Limiter? Lim;

    public ChannelRt(DelayLine delay, Limiter? lim) { Delay = delay; Lim = lim; }
}

/// <summary>Fertig uebersetzter, unveraenderlicher Plan (Ziele) aus MixerSettings. Wird auf dem Setter-Thread gebaut.</summary>
internal sealed class Compiled
{
    public MixerSettings Settings = null!;
    public int ActiveInputs;
    public int ActiveOutputs;
    public int RampFrames;
    /// <summary>Ziel-Verstaerkung je direktem Kreuzungspunkt [o * capIn + i]: EingangsGain * EingangsDeviceMaster * Matrix * Pan.</summary>
    public float[] Cross = Array.Empty<float>();
    public CompiledBusPlan? BusPlan;
    public Section[][] InSections = Array.Empty<Section[]>();
    public Section[][] OutSections = Array.Empty<Section[]>();
    public double[] InDelay = Array.Empty<double>();    // Samples
    public double[] OutDelay = Array.Empty<double>();
    public float[] OutGain = Array.Empty<float>();       // OutChannelGain * OutDeviceMaster * GlobalMaster
    public float[] Ceiling = Array.Empty<float>();       // linear
    /// <summary>Zuordnung jedes Eingangs-/Ausgangskanals auf seinen DeviceMaster-Index (-1 = keiner) fuer aggregiertes Device-Metering.</summary>
    public int[] InDeviceIndex = Array.Empty<int>();
    public int[] OutDeviceIndex = Array.Empty<int>();
    public int DeviceMasterCount;
}
