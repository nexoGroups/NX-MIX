namespace NxMix.Engine;

// ===========================================================================================
// NX-MIX Vertraege zwischen Engine, Audio-Schicht, Daemon und Oberflaechen.
// Signalweg:
//   Eingangskanaele (Rohpegel -> Delay -> Eingangs-Filter -> Kanal-Trim/Gain * Eingangs-Device-Master)
//   -> Direkte Kreuzpunkte (MatrixDb) + Echte Mix-Busse (mit Summenpuffer & Bus-Metering)
//   -> Ausgangskanaele (Ausgangs-DSP: Crossover/PEQ + REW-Korrektur -> Delay -> Ausgangskanal-Fader
//      * Ausgangs-Device-Master -> Brickwall-Limiter -> Ausgangs- & Device-Metering).
// Alles im Kern ist planar float (-1..1). Die Engine hat KEINE Windows-Abhaengigkeit.
// Process() allokiert im eingeschwungenen Betrieb 0 Bytes und nimmt keine Locks.
// ===========================================================================================

/// <summary>Was die Audio-Schicht pro Block aufruft. Wird auf dem Echtzeit-Audio-Thread gerufen.</summary>
public interface IBlockProcessor
{
    int InputChannels { get; }
    int OutputChannels { get; }
    int SampleRate { get; }
    /// <summary>inputs[InputChannels][>=frames], outputs[OutputChannels][>=frames]. Allokationsfrei.</summary>
    void Process(float[][] inputs, float[][] outputs, int frames);
}

/// <summary>Verarbeitet Eingänge wahlweise nur für Pegel und Spektrum, ohne sie auf Busse oder Ausgänge zu mischen.</summary>
public interface IInputMonitorBlockProcessor : IBlockProcessor
{
    void Process(float[][] inputs, float[][] outputs, int frames, ReadOnlySpan<bool> monitorOnly);
}

public enum FilterType { Peak, LowShelf, HighShelf, HighPass, LowPass, Notch, AllPass, BandPass }

/// <summary>Ein Filter. Order gilt nur fuer HighPass/LowPass (1..8 = Butterworth-Kette, Order 2 mit Q wie angegeben).
/// LinkwitzRiley=true: LR-Charakteristik (Order muss gerade sein, z. B. 2 = LR12, 4 = LR24, 6 = LR36, 8 = LR48).</summary>
public sealed record FilterSpec(
    FilterType Type, double FrequencyHz, double Q = 0.7071, double GainDb = 0, int Order = 2,
    bool LinkwitzRiley = false, bool Enabled = true);

/// <summary>Importierte Lautsprecher-/Raumkorrektur eines Ausgangskanals (z. B. REW). Separat schaltbar.</summary>
public sealed record OutputCorrection(string SourceName, IReadOnlyList<FilterSpec> Filters, bool Bypassed = false, double PreampDb = 0.0);

/// <summary>Schutzmaske gegen Ueberschreiben einzelner Parameter beim Snapshot-Recall.</summary>
[Flags]
public enum RecallSafeFlags
{
    None = 0,
    Fader = 1 << 0,
    Mute = 1 << 1,
    Pan = 1 << 2,
    Routing = 1 << 3,
    Dsp = 1 << 4,
    All = Fader | Mute | Pan | Routing | Dsp,
}

/// <summary>Eigenstaendige Master-Gain-Stufe eines mehrkanaligen Eingangs-, Ausgangs- oder VAIO-Geraets.
/// Skaliert die zugeordneten Einzelkanaele relativ, OHNE deren gespeicherte ChannelDsp.GainDb-Werte zu ueberschreiben.</summary>
public sealed record DeviceMasterDsp
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "";
    public bool IsInput { get; init; }
    public double GainDb { get; init; } = 0.0;
    public bool Mute { get; init; }
    public bool Dim { get; init; }
    public double DimDb { get; init; } = -20.0;
    public RecallSafeFlags RecallSafe { get; init; } = RecallSafeFlags.None;
}

/// <summary>DSP und Steuerwerte eines einzelnen Mono-Kanals (Eingang oder Ausgang).</summary>
public sealed record ChannelDsp
{
    public Guid Id { get; init; } = Guid.NewGuid();
    /// <summary>Optionale Zugehoerigkeit zu einem physischen oder virtuellen Geraet (DeviceMasterDsp.Id).</summary>
    public Guid? DeviceId { get; init; }
    /// <summary>0-basierter Hardware-Kanalindex innerhalb des zugeordneten Geraets.</summary>
    public int DeviceChannelIndex { get; init; }
    public string Name { get; init; } = "";
    /// <summary>Digitaler Vorverstaerkungs-Trim (dB), getrennt vom Arbeits-Fader GainDb.</summary>
    public double TrimDb { get; init; } = 0.0;
    public double GainDb { get; init; } = 0.0;
    /// <summary>Panorama/Balance (-1.0 = ganz links, 0.0 = Mitte, +1.0 = ganz rechts). Wirkt bei geraden/ungeraden Zielpaaren.</summary>
    public double Pan { get; init; } = 0.0;
    public bool Mute { get; init; }
    public bool Solo { get; init; }
    public bool InvertPhase { get; init; }
    public double DelayMs { get; init; }
    public IReadOnlyList<FilterSpec> Filters { get; init; } = Array.Empty<FilterSpec>();
    public OutputCorrection? Correction { get; init; }
    /// <summary>Harte Sicherheitsgrenze: Spitzen werden auf diesen Pegel (dBFS) begrenzt. Nur AUSGANGSKANAELE nutzen das.</summary>
    public double CeilingDbfs { get; init; } = -1.0;
    /// <summary>Obergrenze fuer GainDb. Der Aufrufer und die Engine klemmen darauf (Schutz z. B. fuer Hochtoener).</summary>
    public double MaxGainDb { get; init; } = 12.0;
    /// <summary>Explizite optionale Kanal-Kopplungsgruppe (z. B. Stereo-Paar mit Beibehaltung relativer Pegelabstaende).</summary>
    public Guid? LinkGroupId { get; init; }
    public RecallSafeFlags RecallSafe { get; init; } = RecallSafeFlags.None;
}

/// <summary>Kompletter, unveraenderlicher DSP-/Routing-Zustand des Mischkerns.</summary>
public sealed record MixerSettings
{
    public IReadOnlyList<DeviceMasterDsp> DeviceMasters { get; init; } = Array.Empty<DeviceMasterDsp>();
    public IReadOnlyList<MixBus> Buses { get; init; } = Array.Empty<MixBus>();
    public IReadOnlyList<BusRoute> BusRoutes { get; init; } = Array.Empty<BusRoute>();
    public int SampleRate { get; init; } = 96000;
    public IReadOnlyList<ChannelDsp> Inputs { get; init; } = Array.Empty<ChannelDsp>();
    public IReadOnlyList<ChannelDsp> Outputs { get; init; } = Array.Empty<ChannelDsp>();
    /// <summary>Matrix[input][output] in dB. double.NegativeInfinity oder NaN = nicht verbunden. Form: Inputs x Outputs.</summary>
    public double[][] MatrixDb { get; init; } = Array.Empty<double[]>();
    public double MasterGainDb { get; init; } = 0;
    /// <summary>Optionale Ziel-Uebergangszeit in Millisekunden fuer diesen Zustandswechsel (z. B. Snapshot-Fade 0..10000 ms).
    /// Null nutzt die Standard-Klickschutzrampe (10 ms).</summary>
    public double? TransitionMs { get; init; }

    public static MixerSettings Create(int sampleRate, int inputs, int outputs)
    {
        var m = new double[inputs][];
        for (int i = 0; i < inputs; i++) { m[i] = new double[outputs]; Array.Fill(m[i], double.NegativeInfinity); }
        return new MixerSettings
        {
            SampleRate = sampleRate,
            Inputs = Enumerable.Range(0, inputs).Select(i => new ChannelDsp { Name = $"In {i + 1}", DeviceChannelIndex = i }).ToArray(),
            Outputs = Enumerable.Range(0, outputs).Select(i => new ChannelDsp { Name = $"Out {i + 1}", DeviceChannelIndex = i }).ToArray(),
            MatrixDb = m,
        };
    }
}

/// <summary>Die Echtzeit-Engine. Settings werden lock-frei uebernommen; Gain-Aenderungen laufen als Sample-genaue Rampe.</summary>
public interface IMixerEngine : IBlockProcessor
{
    /// <summary>Thread-sicher aus jedem Thread aufrufbar. Im Kapazitaetsmodus (CreatePooled) duerfen Kanal-, Geraete- und Bus-Zahlen
    /// sich im laufenden Betrieb bis zur vorallokierten Kapazitaet unterbrechungsfrei aendern.</summary>
    bool TrySetSettings(MixerSettings settings);
    MixerSettings Settings { get; }
    /// <summary>Spitzenpegel seit dem letzten Lesen (linear 0..), je Eingangskanal.</summary>
    void ReadInputPeaks(Span<float> destination);
    /// <summary>Spitzenpegel seit dem letzten Lesen (linear 0..), je Ausgangskanal.</summary>
    void ReadOutputPeaks(Span<float> destination);
    /// <summary>Spitzenpegel je Mix-Bus (Maximum ueber alle Kanaele des jeweiligen Busses in Settings.Buses-Reihenfolge).</summary>
    void ReadBusPeaks(Span<float> destination) => destination.Clear();
    /// <summary>Spitzenpegel je DeviceMasterDsp (Maximum ueber alle zugeordneten Kanaele in Settings.DeviceMasters-Reihenfolge).</summary>
    void ReadDeviceMasterPeaks(Span<float> destination) => destination.Clear();
    /// <summary>Zaehlt, wie oft ein Ausgang durch den Sicherheits-Limiter begrenzt wurde.</summary>
    long LimiterHits { get; }
}
