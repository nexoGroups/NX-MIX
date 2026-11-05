using System.Diagnostics;
using NxMix.Audio;
using NxMix.App.Ui;
using NxMix.Engine;

namespace NxMix.App.Model;

/// <summary>Baut die Engine: versuchsweise die echte NxMix.Engine.MixerEngine (per Reflection), sonst die eingebaute DemoEngine.</summary>
internal static class EngineFactory
{
    /// <summary>True: immer DemoEngine nehmen (Screenshots, Selbsttest).</summary>
    public static bool ForceDemo { get; set; }
    /// <summary>Feste Zeit fuer die DemoEngine (Sekunden), fuer reproduzierbare Screenshots. null = Echtzeit.</summary>
    public static Func<double>? DemoClock { get; set; }

    public static IMixerEngine Create(MixerSettings settings)
    {
        if (!ForceDemo)
        {
            try
            {
                var t = typeof(IMixerEngine).Assembly.GetType("NxMix.Engine.MixerEngine");
                if (t != null && typeof(IMixerEngine).IsAssignableFrom(t))
                {
                    var e = (IMixerEngine)Activator.CreateInstance(t, settings)!;
                    // Sonden-Aufrufe: ein unfertiges Geruest (NotImplemented) faellt sofort auf die Demo zurueck
                    _ = e.Settings;
                    Span<float> pi = stackalloc float[Math.Max(1, settings.Inputs.Count)];
                    Span<float> po = stackalloc float[Math.Max(1, settings.Outputs.Count)];
                    e.ReadInputPeaks(pi.Slice(0, settings.Inputs.Count));
                    e.ReadOutputPeaks(po.Slice(0, settings.Outputs.Count));
                    return e;
                }
            }
            catch { /* Demo */ }
        }
        return new DemoEngine(settings, DemoClock);
    }

    public static string Describe(IMixerEngine e) => e is DemoEngine ? "Demo-Engine (simulierte Pegel)" : e.GetType().FullName ?? "Engine";
}

/// <summary>Platzhalter-Engine mit animierten Fantasie-Pegeln, damit die Oberflaeche ohne echte Engine lebt. Gibt NIE Audio aus.</summary>
internal sealed class DemoEngine : IMixerEngine
{
    static readonly Stopwatch s_clock = Stopwatch.StartNew();
    readonly Func<double> _time;
    volatile MixerSettings _settings;
    long _hits;

    public DemoEngine(MixerSettings settings, Func<double>? clock = null)
    {
        _settings = settings;
        _time = clock ?? (() => s_clock.Elapsed.TotalSeconds);
    }

    public int InputChannels => _settings.Inputs.Count;
    public int OutputChannels => _settings.Outputs.Count;
    public int SampleRate => _settings.SampleRate;
    public MixerSettings Settings => _settings;
    public long LimiterHits => Interlocked.Read(ref _hits);

    public void Process(float[][] inputs, float[][] outputs, int frames)
    {
        foreach (var o in outputs) Array.Clear(o, 0, Math.Min(frames, o.Length)); // Stille: die Demo erzeugt kein Audio
    }

    public bool TrySetSettings(MixerSettings settings)
    {
        if (settings.Inputs.Count != _settings.Inputs.Count || settings.Outputs.Count != _settings.Outputs.Count
            || settings.SampleRate != _settings.SampleRate) return false;
        _settings = settings;
        return true;
    }

    static double Hash(int i, long k)
    {
        unchecked
        {
            ulong x = (ulong)k * 0x9E3779B97F4A7C15UL ^ (ulong)i * 0xBF58476D1CE4E5B9UL;
            x ^= x >> 31; x *= 0x94D049BB133111EBUL; x ^= x >> 29;
            return (x & 0xFFFFFF) / (double)0x1000000;
        }
    }

    /// <summary>Fantasie-Eingangspegel in dBFS.</summary>
    static double InputDb(int i, double t)
    {
        double a = 0.5 + 0.5 * Math.Sin(t * (1.1 + 0.13 * i) + i * 1.7);
        double b = 0.5 + 0.5 * Math.Sin(t * (3.7 + 0.31 * i) + i * 0.9);
        double n = Hash(i, (long)(t * 32));
        double db = -46 + 40 * a * (0.55 + 0.45 * b) + 7 * (n - 0.5);
        if (i == 0 && Math.Sin(t * 0.45) > 0.985) db = 0.4; // gelegentliches Clippen zum Vorzeigen der Clip-Anzeige
        return Math.Min(db, 0.4);
    }

    public void ReadInputPeaks(Span<float> destination)
    {
        double t = _time();
        int n = Math.Min(destination.Length, InputChannels);
        for (int i = 0; i < n; i++) destination[i] = (float)Db.ToLinear(InputDb(i, t));
        for (int i = n; i < destination.Length; i++) destination[i] = 0;
    }

    public void ReadOutputPeaks(Span<float> destination)
    {
        var s = _settings;
        double t = _time();
        int ni = s.Inputs.Count, no = s.Outputs.Count;
        bool anyInSolo = s.Inputs.Any(c => c.Solo), anyOutSolo = s.Outputs.Any(c => c.Solo);
        double master = Db.ToLinear(s.MasterGainDb);
        var inLin = new double[ni];
        for (int i = 0; i < ni; i++)
        {
            var c = s.Inputs[i];
            bool audible = !c.Mute && (!anyInSolo || c.Solo);
            inLin[i] = audible ? Db.ToLinear(InputDb(i, t)) * Db.ToLinear(c.GainDb) * (c.InvertPhase ? 1 : 1) : 0;
        }
        for (int j = 0; j < destination.Length; j++)
        {
            if (j >= no) { destination[j] = 0; continue; }
            var oc = s.Outputs[j];
            bool audible = !oc.Mute && (!anyOutSolo || oc.Solo);
            double sum = 0;
            for (int i = 0; i < ni; i++)
            {
                double m = s.MatrixDb[i][j];
                if (Db.IsOff(m)) continue;
                sum += inLin[i] * Db.ToLinear(m);
            }
            double v = audible ? sum * 0.8 * Db.ToLinear(oc.GainDb) * master : 0;
            double ceil = Db.ToLinear(oc.CeilingDbfs);
            if (v > ceil) { v = ceil; Interlocked.Increment(ref _hits); }
            destination[j] = (float)v;
        }
    }
}
