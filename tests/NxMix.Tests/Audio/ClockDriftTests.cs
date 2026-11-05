using NxMix.Audio.Dsp;
using Xunit.Abstractions;

namespace NxMix.Tests.Audio;

/// <summary>Simulation zweier Takte (Produzent und Konsument) mit einem Ringpuffer und dem echten ClockMatchedReader (Regler + Resampler).
/// Der Produzent liefert in seinem Takt einen 1-kHz-Sinus; ein driftender Takt muss vom PI-Regler ausgeglichen werden,
/// ohne Unter-/Ueberlaeufe und ohne dass sich die Tonhoehe aus Sicht des Konsumenten aendert.</summary>
public class ClockDriftTests
{
    private readonly ITestOutputHelper _out;

    public ClockDriftTests(ITestOutputHelper output) => _out = output;

    private sealed record SimResult(double FinalPpm, double FilteredFill, int Target, long Underruns, long Overruns,
        double CyclesPerSample, double MaxStep, long MinLagAfterConvergence, double MaxAbsFillErrorAfterConvergence, double MaxAbsPpm);

    private static SimResult Simulate(double driftPpm, double seconds, int prodChunk = 960, int consChunk = 480,
        int rate = 96000, double toneHz = 1000, double amplitude = 0.5)
    {
        const int ch = 2;
        // wie im Host: Sollwert des mittleren Fuellstands = halbe Summe der Paketgroessen + Verbrauchsburst + 4 ms
        int target = (prodChunk + consChunk) / 2 + consChunk + rate * 4 / 1000;
        var ring = new SpscFloatRing(ch, target * 5);
        long simTicks = 0; // simulierte Zeit in ns
        var reader = new ClockMatchedReader(ring, 1.0, rate, target, consChunk, clock: () => simTicks, ticksPerSecond: 1e9);

        double prodRate = rate * (1.0 + driftPpm * 1e-6);
        double prodPeriod = prodChunk / prodRate;
        double consPeriod = consChunk / (double)rate;
        double tp = prodPeriod * 0.37; // beliebige Phasenlage der beiden Takte
        double tc = consPeriod;

        var block = new float[prodChunk * ch];
        var dst = new[] { new float[consChunk], new float[consChunk] };
        double phase = 0, phaseInc = 2 * Math.PI * toneHz / prodRate;

        double convergedAt = seconds * 0.6;
        double measureFrom = seconds - Math.Min(20.0, seconds * 0.3);
        double prev = 0, maxStep = 0;
        bool havePrev = false;
        double firstCross = double.NaN, lastCross = double.NaN;
        long cycles = 0, consumed = 0;
        long minLag = long.MaxValue;
        double maxFillErr = 0;
        double maxAbsPpm = 0;

        while (Math.Min(tp, tc) < seconds)
        {
            if (tp <= tc)
            {
                for (int i = 0; i < prodChunk; i++)
                {
                    float v = (float)(amplitude * Math.Sin(phase));
                    phase += phaseInc;
                    if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                    block[i * ch] = v;
                    block[i * ch + 1] = v;
                }
                ring.Write(block, prodChunk, (long)(tp * 1e9));
                tp += prodPeriod;
            }
            else
            {
                if (tc > convergedAt)
                {
                    minLag = Math.Min(minLag, ring.Lag);
                    maxFillErr = Math.Max(maxFillErr, Math.Abs(reader.FilteredFill - target));
                }
                simTicks = (long)(tc * 1e9);
                reader.Read(dst, 0, consChunk);
                maxAbsPpm = Math.Max(maxAbsPpm, Math.Abs(reader.CorrectionPpm));
                if (tc >= measureFrom)
                {
                    for (int i = 0; i < consChunk; i++)
                    {
                        double y = dst[0][i];
                        long idx = consumed + i;
                        if (havePrev)
                        {
                            maxStep = Math.Max(maxStep, Math.Abs(y - prev));
                            if (prev < 0 && y >= 0)
                            {
                                double t = idx - 1 + (0 - prev) / (y - prev);
                                if (double.IsNaN(firstCross)) firstCross = t; else cycles++;
                                lastCross = t;
                            }
                        }
                        prev = y;
                        havePrev = true;
                    }
                }
                consumed += consChunk;
                tc += consPeriod;
            }
        }

        return new SimResult(reader.CorrectionPpm, reader.FilteredFill, target, reader.Underruns, reader.Overruns,
            cycles / (lastCross - firstCross), maxStep, minLag, maxFillErr, maxAbsPpm);
    }

    [Theory]
    [InlineData(100.0)]
    [InlineData(-100.0)]
    [InlineData(400.0)]
    [InlineData(0.0)]
    public void PiControl_Converges_OnDriftingClocks(double driftPpm)
    {
        SimResult r = Simulate(driftPpm, seconds: 120);
        _out.WriteLine($"drift {driftPpm} ppm -> ppm {r.FinalPpm:F2}, fill {r.FilteredFill:F1}/{r.Target}, minLag {r.MinLagAfterConvergence}, " +
                       $"maxFillErr {r.MaxAbsFillErrorAfterConvergence:F1}, f {r.CyclesPerSample * 96000:F4} Hz, maxStep {r.MaxStep:F5}");

        // Regler hat die Drift gefunden
        Assert.InRange(r.FinalPpm, driftPpm - 2.0, driftPpm + 2.0);
        // Fuellstand sitzt auf dem Sollwert
        Assert.True(Math.Abs(r.FilteredFill - r.Target) < 0.01 * r.Target, $"Fuellstand {r.FilteredFill} vs Soll {r.Target}");
        Assert.True(r.MaxAbsFillErrorAfterConvergence < 0.05 * r.Target, $"Fuellstandsabweichung {r.MaxAbsFillErrorAfterConvergence}");
        // Nie leer / nie voll
        Assert.Equal(0, r.Underruns);
        Assert.Equal(0, r.Overruns);
        Assert.True(r.MinLagAfterConvergence > 480 + 8, $"minLag {r.MinLagAfterConvergence}");
        // Der Konsument hoert exakt 1 kHz (im Takt des Konsumenten) und keinen Sprung
        Assert.True(Math.Abs(r.CyclesPerSample * 96000 - 1000.0) < 0.02, $"Frequenz {r.CyclesPerSample * 96000} Hz");
        double theoreticalStep = 0.5 * 2 * Math.PI * 1000.0 / 96000;
        Assert.True(r.MaxStep <= theoreticalStep * 1.02, $"Sprung {r.MaxStep} > {theoreticalStep}");
        // Sanfter Start: das Verhaeltnis ueberschwingt nicht weit ueber die tatsaechliche Drift hinaus (kein Tonhoehen-Ausschlag beim Anlauf)
        Assert.True(r.MaxAbsPpm <= Math.Abs(driftPpm) * 1.3 + 120, $"Anlauf-Ausschlag {r.MaxAbsPpm:F1} ppm bei Drift {driftPpm}");
    }

    [Fact]
    public void PiControl_Converges_WithDifferentBlockSizes()
    {
        // Produzent 10 ms @ 96 kHz, Konsument 2,5 ms; Drift +250 ppm
        SimResult r = Simulate(250, seconds: 120, prodChunk: 960, consChunk: 240);
        _out.WriteLine($"ppm {r.FinalPpm:F2}, fill {r.FilteredFill:F1}/{r.Target}");
        Assert.InRange(r.FinalPpm, 248, 252);
        Assert.Equal(0, r.Underruns);
        Assert.Equal(0, r.Overruns);
    }

    // ---- Unterlauf / Ueberlauf ---------------------------------------------------------------------------------

    private static (SpscFloatRing Ring, ClockMatchedReader Reader) MakeReader(int target = 1200, int consChunk = 480)
    {
        var ring = new SpscFloatRing(1, target * 5);
        return (ring, new ClockMatchedReader(ring, 1.0, 96000, target, consChunk));
    }

    [Fact]
    public void Underrun_ProducesExactSilence_NeverRepeatsOrGarbage_ThenRecovers()
    {
        var (ring, reader) = MakeReader();
        var block = new float[480];
        var dst = new[] { new float[480] };
        double phase = 0;

        void Produce(int blocks)
        {
            for (int b = 0; b < blocks; b++)
            {
                for (int i = 0; i < 480; i++) { block[i] = (float)(0.5 * Math.Sin(phase)); phase += 2 * Math.PI * 440 / 96000; }
                ring.Write(block, 480);
            }
        }

        // Aufwaermen: lange genug einspeisen, dass der Leser laeuft und Signal liefert
        for (int i = 0; i < 20; i++) { Produce(1); reader.Read(dst, 0, 480); }
        Assert.True(reader.IsRunning);
        Assert.Contains(dst[0], v => Math.Abs(v) > 0.1f);
        Assert.Equal(0, reader.Underruns);

        // Produzent faellt aus: Konsument liest weiter
        bool sawSilence = false;
        bool signalEnded = false;
        for (int i = 0; i < 30; i++)
        {
            reader.Read(dst, 0, 480);
            float[] o = dst[0];
            int lastNonZero = -1;
            for (int k = 0; k < o.Length; k++) if (o[k] != 0f) lastNonZero = k;
            if (signalEnded)
            {
                // nach dem Signalende NUR noch exakte Nullen (keine Wiederholung alter Daten)
                Assert.True(lastNonZero < 0, $"Block {i}: nach dem Unterlauf kam wieder Signal");
            }
            else if (lastNonZero < o.Length - 1)
            {
                signalEnded = true; // erster Block mit Stille am Ende
                sawSilence = true;
            }
        }
        Assert.True(sawSilence, "Der Unterlauf muss zu Stille fuehren.");
        Assert.Equal(1, reader.Underruns);
        Assert.False(reader.IsRunning);

        // Produzent kommt zurueck: Leser fuellt erst bis zum Sollwert (Stille) und liefert dann wieder Signal
        bool signalBack = false;
        for (int i = 0; i < 40; i++)
        {
            Produce(1);
            reader.Read(dst, 0, 480);
            if (dst[0].Any(v => Math.Abs(v) > 0.05f)) { signalBack = true; break; }
        }
        Assert.True(signalBack, "Nach dem Unterlauf muss der Betrieb wieder aufgenommen werden.");
        Assert.Equal(1, reader.Underruns);
    }

    [Fact]
    public void Underrun_EndOfSignalIsFadedOut()
    {
        var (ring, reader) = MakeReader(target: 1000, consChunk: 480);
        var block = new float[480];
        var dst = new[] { new float[480] };
        Array.Fill(block, 0.5f); // Gleichanteil: ein harter Abbruch waere ein Sprung von 0,5 auf 0
        for (int i = 0; i < 6; i++) { ring.Write(block, 480); reader.Read(dst, 0, 480); }
        Assert.True(reader.IsRunning);
        // Ab jetzt nichts mehr nachliefern: bis zum Unterlauf lesen
        float lastBeforeSilence = 0;
        for (int i = 0; i < 10 && reader.IsRunning; i++)
        {
            reader.Read(dst, 0, 480);
            for (int k = 0; k < 480; k++)
            {
                if (dst[0][k] != 0f) lastBeforeSilence = dst[0][k];
            }
        }
        Assert.False(reader.IsRunning);
        Assert.True(lastBeforeSilence < 0.1f, "Das Signal muss vor der Stille ausgeblendet sein, ist aber " + lastBeforeSilence);
    }

    [Fact]
    public void Overrun_DropsOldestFrames_AndCounts()
    {
        var (ring, reader) = MakeReader(target: 1000, consChunk: 480);
        var block = new float[480];
        var dst = new[] { new float[480] };
        // Leser laufen lassen
        for (int i = 0; i < 6; i++) { ring.Write(block, 480); reader.Read(dst, 0, 480); }
        Assert.Equal(0, reader.Overruns);

        // Konsument "haengt": der Produzent schreibt viel mehr als hineinpasst
        for (int i = 0; i < 40; i++) ring.Write(block, 480);
        long before = ring.Lag;
        reader.Read(dst, 0, 480);
        Assert.Equal(1, reader.Overruns);
        Assert.True(reader.DroppedFrames > 0);
        Assert.True(ring.Lag < before);
        Assert.True(ring.Lag < 3 * 1000, "nach dem Verwerfen darf nur noch etwa der Sollwert uebrig sein: " + ring.Lag);
    }

    [Fact]
    public void PrimingWaitsForTargetFill_AndOutputsSilenceMeanwhile()
    {
        var (ring, reader) = MakeReader(target: 1200, consChunk: 480);
        var block = new float[480];
        Array.Fill(block, 0.25f);
        var dst = new[] { new float[480] };
        Array.Fill(dst[0], 9f);

        ring.Write(block, 480);
        reader.Read(dst, 0, 480);
        Assert.All(dst[0], v => Assert.Equal(0f, v)); // noch zu wenig Fuellung: Stille
        Assert.False(reader.IsRunning);
        Assert.Equal(0, reader.Underruns); // Anlaufen zaehlt nicht als Unterlauf

        ring.Write(block, 480);
        ring.Write(block, 480);
        reader.Read(dst, 0, 480);
        Assert.True(reader.IsRunning);
    }
}
