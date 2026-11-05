using System.Numerics;
using NxMix.Engine;

namespace NxMix.Tests.Engine;

/// <summary>Hilfen fuer die Engine-Tests. Gibt niemals Ton auf Geraeten aus - rechnet nur Puffer.</summary>
internal static class EngineTestKit
{
    /// <summary>Ceiling weit ueber allem, was die Tests erzeugen: der Limiter greift dann nie ein.</summary>
    public const double HighCeiling = 40.0;

    public static ChannelDsp Ch(string name = "", double gainDb = 0, double ceiling = HighCeiling, params FilterSpec[] filters)
        => new() { Name = name, GainDb = gainDb, CeilingDbfs = ceiling, Filters = filters };

    /// <summary>Alle Kreuzungspunkte i == o auf 0 dB, Rest aus.</summary>
    public static MixerSettings Diagonal(int sr, int channels, ChannelDsp? proto = null)
    {
        var s = MixerSettings.Create(sr, channels, channels);
        for (int i = 0; i < channels; i++) s.MatrixDb[i][i] = 0;
        return s with
        {
            Inputs = Enumerable.Range(0, channels).Select(_ => proto ?? Ch()).ToArray(),
            Outputs = Enumerable.Range(0, channels).Select(_ => Ch()).ToArray(),
        };
    }

    /// <summary>1 Eingang, 1 Ausgang, 0 dB, Filter am Eingang.</summary>
    public static MixerSettings Mono(int sr, params FilterSpec[] inputFilters)
    {
        var s = MixerSettings.Create(sr, 1, 1);
        s.MatrixDb[0][0] = 0;
        return s with
        {
            Inputs = new[] { Ch("in", 0, HighCeiling, inputFilters) },
            Outputs = new[] { Ch("out") },
        };
    }

    public static float[] Sine(int n, double freq, double amp, int sr, double phase = 0)
    {
        var x = new float[n];
        for (int i = 0; i < n; i++) x[i] = (float)(amp * Math.Sin(2 * Math.PI * freq * i / sr + phase));
        return x;
    }

    public static float[][] NewBuffers(int channels, int frames)
    {
        var b = new float[channels][];
        for (int c = 0; c < channels; c++) b[c] = new float[frames];
        return b;
    }

    /// <summary>Verarbeitet das komplette Signal in Bloecken; liefert die Ausgaenge in voller Laenge.</summary>
    public static float[][] Run(MixerEngine e, float[][] inputs, int block = 480)
    {
        int total = inputs.Length > 0 ? inputs[0].Length : 0;
        var outs = NewBuffers(e.OutputChannels, total);
        RunInto(e, inputs, outs, 0, total, block);
        return outs;
    }

    /// <summary>Verarbeitet inputs[*][from..to) in Bloecken nach outs[*][from..to) (Offset-sicher ueber Zwischenpuffer).</summary>
    public static void RunInto(MixerEngine e, float[][] inputs, float[][] outs, int from, int to, int block = 480)
    {
        var bi = NewBuffers(e.InputChannels, block);
        var bo = NewBuffers(e.OutputChannels, block);
        for (int pos = from; pos < to; pos += block)
        {
            int n = Math.Min(block, to - pos);
            for (int c = 0; c < bi.Length; c++) Array.Copy(inputs[c], pos, bi[c], 0, n);
            e.Process(bi, bo, n);
            for (int c = 0; c < bo.Length; c++) Array.Copy(bo[c], 0, outs[c], pos, n);
        }
    }

    /// <summary>Amplitude der Grundwelle bei freq ueber [start, start+count) (Projektion; ganze Perioden = exakt).</summary>
    public static double Amplitude(float[] x, int start, int count, double freq, int sr)
    {
        double w = 2 * Math.PI * freq / sr;
        double re = 0, im = 0;
        for (int i = 0; i < count; i++)
        {
            double v = x[start + i];
            re += v * Math.Cos(w * (start + i));
            im -= v * Math.Sin(w * (start + i));
        }
        return 2.0 * Math.Sqrt(re * re + im * im) / count;
    }

    /// <summary>Komplexer Zeiger der Grundwelle (fuer Summen zweier Ausgaenge).</summary>
    public static Complex Phasor(float[] x, int start, int count, double freq, int sr)
    {
        double w = 2 * Math.PI * freq / sr;
        double re = 0, im = 0;
        for (int i = 0; i < count; i++)
        {
            double v = x[start + i];
            re += v * Math.Cos(w * (start + i));
            im -= v * Math.Sin(w * (start + i));
        }
        return new Complex(re, im) * (2.0 / count);
    }

    public static double ToDb(double lin) => 20 * Math.Log10(lin);

    /// <summary>Misst den Betragsgang des Eingangsfilters durch die Engine (Sinus durch, Amplitude am Ausgang / Eingang).</summary>
    public static double MeasureGainDb(MixerSettings settings, double freq, double amp = 0.1)
    {
        int sr = settings.SampleRate;
        var e = new MixerEngine(settings);
        int settle = sr / 2, measure = sr;       // 1 s Messfenster, f ganzzahlig => ganze Perioden
        var x = Sine(settle + measure, freq, amp, sr);
        var y = Run(e, new[] { x })[0];
        return ToDb(Amplitude(y, settle, measure, freq, sr) / amp);
    }

    public static float MaxAbs(float[] x, int start = 0, int count = -1)
    {
        if (count < 0) count = x.Length - start;
        float m = 0;
        for (int i = 0; i < count; i++) m = MathF.Max(m, MathF.Abs(x[start + i]));
        return m;
    }

    public static float MaxStep(float[] x, int start = 1, int count = -1)
    {
        if (count < 0) count = x.Length - start;
        float m = 0;
        for (int i = 0; i < count; i++) m = MathF.Max(m, MathF.Abs(x[start + i] - x[start + i - 1]));
        return m;
    }

    public static double Ceil(double dbfs) => Math.Pow(10, dbfs / 20);
}
