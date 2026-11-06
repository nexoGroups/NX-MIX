namespace NxMix.Audio.Host;

/// <summary>Abbildung eines Bereichs von Geraetekanaelen auf Engine-Kanaele; SlotOffset = Position im Ringpuffer-Frame.</summary>
internal readonly record struct Mapping(int DeviceFirstChannel, int EngineFirstChannel, int Count, int SlotOffset);

/// <summary>Ein Geraete-Stream: alle Bindings, die dasselbe Geraet in derselben Richtung ansprechen, laufen ueber einen gemeinsamen Stream.</summary>
internal sealed class StreamSpec
{
    private readonly List<Mapping> _list = new();

    public StreamSpec(string deviceId, bool isCapture, bool loopback)
    {
        DeviceId = deviceId;
        IsCapture = isCapture;
        Loopback = loopback;
    }

    public string DeviceId { get; }
    public bool IsCapture { get; }
    public bool Loopback { get; }
    public int? SampleRate { get; init; }
    public int? BufferFrames { get; init; }
    public bool? Exclusive { get; init; }

    /// <summary>Summe aller Kanaele = Breite eines Ringpuffer-Frames.</summary>
    public int Slots { get; private set; }

    public Mapping[] Maps { get; private set; } = Array.Empty<Mapping>();

    public void Add(int deviceFirstChannel, int engineFirstChannel, int count)
    {
        _list.Add(new Mapping(deviceFirstChannel, engineFirstChannel, count, Slots));
        Slots += count;
        Maps = _list.ToArray();
    }

    public string Key => $"{(IsCapture ? "C" : "R")}{(Loopback ? "L" : "")}|{DeviceId.ToUpperInvariant()}";
}
