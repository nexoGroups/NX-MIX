using NxMix.Audio.Dsp;

namespace NxMix.Tests.Audio;

public class ResamplerTests
{
    private static float[] Sine(int frames, double cyclesPerSample, double amplitude = 0.8, int channels = 1)
    {
        var a = new float[frames * channels];
        for (int i = 0; i < frames; i++)
        {
            float v = (float)(amplitude * Math.Sin(2 * Math.PI * cyclesPerSample * i));
            for (int c = 0; c < channels; c++) a[i * channels + c] = v;
        }
        return a;
    }

    private static (float[] Out, int Produced, int Consumed) RunOnce(float[] input, int inFrames, double ratio, int maxOut)
    {
        var rs = new CubicResampler(1);
        var o = new[] { new float[maxOut] };
        int produced = rs.Process(input, inFrames, ratio, o, 0, maxOut, out int consumed);
        return (o[0], produced, consumed);
    }

    /// <summary>Frequenz in Zyklen pro Ausgangs-Sample aus den aufsteigenden Nulldurchgaengen (linear interpoliert).</summary>
    private static double MeasureCyclesPerSample(float[] x, int from, int to)
    {
        double first = double.NaN, last = double.NaN;
        int cycles = 0;
        for (int i = Math.Max(1, from); i < to; i++)
        {
            if (x[i - 1] < 0f && x[i] >= 0f)
            {
                double t = i - 1 + (0 - x[i - 1]) / (double)(x[i] - x[i - 1]);
                if (double.IsNaN(first)) first = t; else cycles++;
                last = t;
            }
        }
        return cycles / (last - first);
    }

    [Fact]
    public void RatioOne_IsExactDelayOfOneFrame()
    {
        float[] input = Sine(2000, 1000.0 / 48000);
        var (o, produced, _) = RunOnce(input, input.Length, 1.0, 1990);
        Assert.Equal(1990, produced);
        Assert.Equal(0f, o[0]);
        for (int k = 1; k < produced; k++)
            Assert.True(Math.Abs(o[k] - input[k - 1]) < 1e-6f, $"k={k}");
    }

    [Theory]
    [InlineData(200e-6)]
    [InlineData(-200e-6)]
    [InlineData(50e-6)]
    [InlineData(-5e-6)]
    [InlineData(1000e-6)]
    public void SinePitch_FollowsRatioExactly_AndHasNoJump(double delta)
    {
        const double fIn = 1000.0 / 96000; // 1 kHz bei 96 kHz
        int inFrames = 96000 * 3;
        float[] input = Sine(inFrames, fIn, 0.8);
        double ratio = 1.0 + delta;

        var (o, produced, consumed) = RunOnce(input, input.Length, ratio, inFrames);
        Assert.True(produced > 96000 * 2.9, "produced " + produced);
        Assert.True(consumed <= inFrames);

        // Erwartung: Ausgangsfrequenz (Zyklen/Sample) = fIn * ratio
        double measured = MeasureCyclesPerSample(o, 100, produced - 100);
        double expected = fIn * ratio;
        double relErr = Math.Abs(measured - expected) / expected;
        Assert.True(relErr < 1e-6, $"Tonhoehenfehler {relErr * 1e6:F3} ppm (gemessen {measured}, erwartet {expected})");

        // Kein Sprung: groesste Aenderung zwischen zwei Samples <= theoretische Steigung (+ kleine Reserve)
        double maxStep = 0;
        for (int k = 1; k < produced; k++) maxStep = Math.Max(maxStep, Math.Abs(o[k] - o[k - 1]));
        double theoretical = 0.8 * 2 * Math.PI * expected;
        Assert.True(maxStep <= theoretical * 1.01 + 1e-6, $"Sprung: {maxStep} > {theoretical}");

        // Restfehler gegenueber der idealen Sinuskurve (Interpolationsfehler) < -70 dB
        double sumErr = 0, sumSig = 0;
        int n0 = 5000;
        // Phase/Verzoegerung: out[k] = x(1 + k*ratio - 1... ) -> Position im Eingang: (k - 1)*ratio + 1 - 1 ... Anfang: out[0]=0 wegen Startfenster.
        // Wir passen die Verzoegerung per Ausgleichsrechnung an (sin/cos-Fit bei der erwarteten Frequenz).
        double sc = 0, ss = 0, cc = 0, sy = 0, cy = 0;
        for (int k = n0; k < produced - n0; k++)
        {
            double ph = 2 * Math.PI * expected * k;
            double s = Math.Sin(ph), c = Math.Cos(ph);
            sc += s * c; ss += s * s; cc += c * c; sy += s * o[k]; cy += c * o[k];
        }
        double det = ss * cc - sc * sc;
        double a = (sy * cc - cy * sc) / det, b = (cy * ss - sy * sc) / det;
        for (int k = n0; k < produced - n0; k++)
        {
            double ph = 2 * Math.PI * expected * k;
            double fit = a * Math.Sin(ph) + b * Math.Cos(ph);
            sumErr += (o[k] - fit) * (o[k] - fit);
            sumSig += fit * fit;
        }
        double amp = Math.Sqrt(a * a + b * b);
        Assert.InRange(amp, 0.79, 0.81);
        Assert.True(sumErr / sumSig < 1e-7, $"Restfehler {10 * Math.Log10(sumErr / sumSig):F1} dB");
    }

    [Fact]
    public void StreamingWithRandomChunks_EqualsOneShot_BitExact()
    {
        const double ratio = 1.000137;
        const int ch = 2;
        float[] input = Sine(30000, 0.0123, 0.7, channels: ch);
        int inFrames = input.Length / ch;

        var rsRef = new CubicResampler(ch);
        var refOut = new[] { new float[40000], new float[40000] };
        int refProduced = rsRef.Process(input, inFrames, ratio, refOut, 0, 40000, out int refConsumed);
        Assert.Equal(inFrames, refConsumed);

        // Streaming: der Eingang wird in zufaelligen Stuecken geliefert; nicht verbrauchte Frames bleiben im Eingangsfenster (wie im Ringpuffer)
        var rs = new CubicResampler(ch);
        var got = new[] { new float[40000], new float[40000] };
        var rnd = new Random(3);
        var pending = new List<float>();
        int inPos = 0, outPos = 0;
        var tmp = new[] { new float[8], new float[8] };
        while (inPos < inFrames || pending.Count > 0)
        {
            int give = Math.Min(rnd.Next(1, 40), inFrames - inPos);
            for (int i = 0; i < give * ch; i++) pending.Add(input[inPos * ch + i]);
            inPos += give;

            int want = rnd.Next(1, 8);
            float[] window = pending.ToArray();
            int produced = rs.Process(window, window.Length / ch, ratio, tmp, 0, want, out int consumed);
            for (int i = 0; i < produced; i++)
            {
                got[0][outPos + i] = tmp[0][i];
                got[1][outPos + i] = tmp[1][i];
            }
            outPos += produced;
            pending.RemoveRange(0, consumed * ch);
            if (inPos >= inFrames && produced == 0 && consumed == 0) break;
        }

        Assert.Equal(refProduced, outPos);
        for (int k = 0; k < refProduced; k++)
        {
            Assert.Equal(refOut[0][k], got[0][k]);
            Assert.Equal(refOut[1][k], got[1][k]);
        }
    }

    [Fact]
    public void ChangingRatio_StaysContinuous()
    {
        const double fIn = 1000.0 / 96000;
        int inFrames = 96000 * 2;
        float[] input = Sine(inFrames, fIn, 0.8);
        var rs = new CubicResampler(1);
        var o = new[] { new float[480] };
        double maxStep = 0, prev = 0;
        int inPos = 0;
        var window = new float[600];
        int blocks = 0;
        while (inPos + 600 < inFrames)
        {
            double ratio = 1.0 + 1000e-6 * Math.Sin(blocks * 0.01); // +-1000 ppm, wechselt jeden Block
            Array.Copy(input, inPos, window, 0, 600);
            int produced = rs.Process(window, 600, ratio, o, 0, 480, out int consumed);
            Assert.Equal(480, produced);
            for (int k = 0; k < produced; k++)
            {
                maxStep = Math.Max(maxStep, Math.Abs(o[0][k] - prev));
                prev = o[0][k];
            }
            inPos += consumed;
            blocks++;
        }
        double theoretical = 0.8 * 2 * Math.PI * fIn * 1.002;
        Assert.True(maxStep <= theoretical * 1.01 + 1e-6, $"Sprung bei Verhaeltniswechsel: {maxStep} > {theoretical}");
    }

    [Fact]
    public void InputExhausted_ReturnsFewerFrames_AndStateStaysValid()
    {
        var rs = new CubicResampler(1);
        var o = new[] { new float[100] };
        float[] input = Sine(10, 0.01);
        int produced = rs.Process(input, 10, 1.0, o, 0, 100, out int consumed);
        Assert.Equal(10, consumed);
        Assert.True(produced < 100);
        Assert.True(produced >= 8);
    }
}
