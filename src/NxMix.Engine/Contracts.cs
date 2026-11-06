namespace NxMix.Engine;

// ===========================================================================================
// NX-MIX Vertraege zwischen Engine, Audio-Schicht und Oberflaeche.
// Signalweg:  Eingangskanaele -> [Eingangs-DSP] -> Matrix (Gain je Kreuzungspunkt) -> [Ausgangs-DSP] -> Ausgangskanaele
// Alles ist MONO-Kanal-basiert (Stereo = 2 Kanaele). Alle Puffer sind planar float (-1..1), eine float[] je Kanal.
// Die Engine hat KEINE Windows-Abhaengigkeit. Process() darf NICHTS allozieren und nichts sperren.
// ===========================================================================================

/// <summary>Was die Audio-Schicht pro Block aufruft. Wird auf dem Audio-Thread gerufen.</summary>
public interface IBlockProcessor
{
    int InputChannels { get; }
    int OutputChannels { get; }
    int SampleRate { get; }
    /// <summary>inputs[InputChannels][>=frames], outputs[OutputChannels][>=frames]. Allokationsfrei.</summary>
    void Process(float[][] inputs, float[][] outputs, int frames);
}

public enum FilterType { Peak, LowShelf, HighShelf, HighPass, LowPass, Notch, AllPass, BandPass }

/// <summary>Ein Filter. Order gilt nur fuer HighPass/LowPass (1..8 = Butterworth-Kette, Order 2 mit Q wie angegeben).
/// LinkwitzRiley=true: LR-Charakteristik (Order muss gerade sein, z. B. 4 = LR24, 8 = LR48).</summary>
public sealed record FilterSpec(
    FilterType Type, double FrequencyHz, double Q = 0.7071, double GainDb = 0, int Order = 2,
    bool LinkwitzRiley = false, bool Enabled = true);

/// <summary>Importierte Raumkorrektur eines Ausgangs. Separat von der normalen Filterkette schaltbar.</summary>
public sealed record OutputCorrection(string SourceName, IReadOnlyList<FilterSpec> Filters, bool Bypassed = false);

/// <summary>DSP eines einzelnen Kanals (Eingang oder Ausgang).</summary>
public sealed record ChannelDsp
{
    public string Name { get; init; } = "";
    public double GainDb { get; init; } = 0;
    public bool Mute { get; init; }
    public bool Solo { get; init; }
    public bool InvertPhase { get; init; }
    public double DelayMs { get; init; }
    public IReadOnlyList<FilterSpec> Filters { get; init; } = Array.Empty<FilterSpec>();
    public OutputCorrection? Correction { get; init; }
    /// <summary>Harte Sicherheitsgrenze: Spitzen werden auf diesen Pegel (dBFS) begrenzt. Nur AUSGANGSKANAELE nutzen das.</summary>
    public double CeilingDbfs { get; init; } = -1.0;
    /// <summary>Obergrenze fuer GainDb. Der Aufrufer (UI) und die Engine klemmen darauf. Schutz z. B. fuer Hochtoener.</summary>
    public double MaxGainDb { get; init; } = 12.0;
}

/// <summary>Kompletter, unveraenderlicher Zustand. Die UI baut einen neuen Snapshot und uebergibt ihn per SetSettings.</summary>
public sealed record MixerSettings
{
    public int SampleRate { get; init; } = 96000;
    public IReadOnlyList<ChannelDsp> Inputs { get; init; } = Array.Empty<ChannelDsp>();
    public IReadOnlyList<ChannelDsp> Outputs { get; init; } = Array.Empty<ChannelDsp>();
    /// <summary>Matrix[input][output] in dB. double.NegativeInfinity oder NaN = nicht verbunden. Form: Inputs x Outputs.</summary>
    public double[][] MatrixDb { get; init; } = Array.Empty<double[]>();
    public double MasterGainDb { get; init; } = 0;

    public static MixerSettings Create(int sampleRate, int inputs, int outputs)
    {
        var m = new double[inputs][];
        for (int i = 0; i < inputs; i++) { m[i] = new double[outputs]; Array.Fill(m[i], double.NegativeInfinity); }
        return new MixerSettings
        {
            SampleRate = sampleRate,
            Inputs = Enumerable.Range(0, inputs).Select(i => new ChannelDsp { Name = $"In {i + 1}" }).ToArray(),
            Outputs = Enumerable.Range(0, outputs).Select(i => new ChannelDsp { Name = $"Out {i + 1}" }).ToArray(),
            MatrixDb = m,
        };
    }
}

/// <summary>Die Engine. Settings werden lock-frei uebernommen; Gain-Aenderungen laufen als Rampe (kein Knacken).</summary>
public interface IMixerEngine : IBlockProcessor
{
    /// <summary>Thread-sicher aus jedem Thread aufrufbar. Kanalanzahl und Samplerate duerfen sich NUR hier aendern,
    /// wenn die Audio-Schicht gestoppt ist; sonst muessen sie gleich bleiben (wird abgelehnt: false).</summary>
    bool TrySetSettings(MixerSettings settings);
    MixerSettings Settings { get; }
    /// <summary>Spitzenpegel seit dem letzten Lesen (linear 0..), je Kanal. Von der UI gelesen, vom Audio-Thread geschrieben.</summary>
    void ReadInputPeaks(Span<float> destination);
    void ReadOutputPeaks(Span<float> destination);
    /// <summary>Zaehlt, wie oft ein Ausgang durch den Sicherheits-Limiter begrenzt wurde (zum Anzeigen).</summary>
    long LimiterHits { get; }
}
