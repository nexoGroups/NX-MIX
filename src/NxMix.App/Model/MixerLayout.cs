using NxMix.Engine;

namespace NxMix.App.Model;

internal sealed record FaderSlot(RouteKind Kind, Guid ChannelId);
internal sealed record MixerDeck
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Deck";
    public int Columns { get; init; } = 12;
    public int Rows { get; init; } = 1;
    public IReadOnlyList<FaderSlot?> Slots { get; init; } = Array.Empty<FaderSlot?>();
}
internal sealed record MixerLayout
{
    public Guid? ActiveDeck { get; init; }
    public IReadOnlyList<MixerDeck> Decks { get; init; } = Array.Empty<MixerDeck>();
}

internal sealed partial class AppModel
{
    public void SetDeviceOptions(string id, int? rate, int? buffer, bool? exclusive)
    {
        if (HostRunning || HostBusy) return;
        bool Match(string other) => other == id || (NxMix.Audio.AsioCatalog.IsAsio(id) && NxMix.Audio.AsioCatalog.IsAsio(other) &&
            NxMix.Audio.AsioCatalog.DriverName(id) == NxMix.Audio.AsioCatalog.DriverName(other));
        SetBindings(Bindings.Select(b => Match(b.DeviceId) ? b with { SampleRate = rate, BufferFrames = buffer, Exclusive = exclusive } : b).ToArray());
    }
    public MixerLayout LayoutSettings { get; private set; } = new();
    public event Action? LayoutChanged;
    public void InitLayout(MixerLayout? layout) => LayoutSettings = layout ?? new();
    public void SetLayout(MixerLayout layout)
    {
        LayoutSettings = layout;
        LayoutChanged?.Invoke(); Changed?.Invoke();
    }
    public void SelectDeck(Guid? id) => SetLayout(LayoutSettings with { ActiveDeck = id });
    public void SaveDeck(MixerDeck deck)
    {
        deck = deck with { Columns = Math.Clamp(deck.Columns, 1, 32), Rows = Math.Clamp(deck.Rows, 1, 4) };
        var list = LayoutSettings.Decks.ToList();
        int index = list.FindIndex(d => d.Id == deck.Id);
        if (index < 0) list.Add(deck); else list[index] = deck;
        SetLayout(LayoutSettings with { Decks = list, ActiveDeck = deck.Id });
    }
    public void DeleteDeck(Guid id) => SetLayout(LayoutSettings with
    {
        Decks = LayoutSettings.Decks.Where(d => d.Id != id).ToArray(),
        ActiveDeck = LayoutSettings.ActiveDeck == id ? null : LayoutSettings.ActiveDeck,
    });
    public string FaderName(FaderSlot slot)
    {
        if (slot.Kind == RouteKind.Bus) return Settings.Buses.FirstOrDefault(b => b.Id == slot.ChannelId)?.Name ?? "Bus fehlt";
        bool input = slot.Kind == RouteKind.Input;
        var channels = input ? Settings.Inputs : Settings.Outputs;
        int index = channels.ToList().FindIndex(c => c.Id == slot.ChannelId);
        if (index >= 0 && input && string.IsNullOrWhiteSpace(channels[index].Name) && LinkAt(index) is { } link)
            return link.Title + (link.Count == 2 ? (index == link.FirstChannel ? " L" : " R") : "");
        return index < 0 ? "Kanal fehlt" : DisplayName(input, index);
    }
    public string FaderDescription(FaderSlot slot)
    {
        string name = FaderName(slot);
        if (slot.Kind == RouteKind.Bus) return name;
        bool input = slot.Kind == RouteKind.Input;
        int index = (input ? Settings.Inputs : Settings.Outputs).ToList().FindIndex(c => c.Id == slot.ChannelId);
        var binding = Bindings.FirstOrDefault(b => b.Flow == (input ? NxMix.Audio.DeviceFlow.Capture : NxMix.Audio.DeviceFlow.Render) &&
            index >= b.EngineFirstChannel && index < b.EngineFirstChannel + b.Count);
        return binding == null ? name : $"{name} · {FindDevice(binding.DeviceId)?.Name ?? "Gerät offline"} / {binding.DeviceFirstChannel + index - binding.EngineFirstChannel + 1}";
    }
    public Guid AddBus(int channels)
    {
        var b = new MixBus { Name = $"Bus {Settings.Buses.Count + 1}", Channels = Math.Clamp(channels, 1, 64) };
        Commit(Settings with { Buses = Settings.Buses.Append(b).ToArray() });
        LayoutChanged?.Invoke();
        return b.Id;
    }
    public void UpdateBus(Guid id, Func<MixBus, MixBus> update)
    {
        Commit(Settings with { Buses = Settings.Buses.Select(b => b.Id == id ? update(b) : b).ToArray() });
    }
    public void RemoveBus(Guid id)
    {
        Commit(Settings with
        {
            Buses = Settings.Buses.Where(b => b.Id != id).ToArray(),
            BusRoutes = Settings.BusRoutes.Where(r => r.Source.Id != id && r.Target.Id != id).ToArray(),
        });
        LayoutChanged?.Invoke();
    }
    public string? AddBusRoute(BusRoute route)
    {
        var next = Settings with { BusRoutes = Settings.BusRoutes.Append(route).ToArray() };
        try { BusRouting.Compile(next); }
        catch (ArgumentException e) { return e.Message; }
        // Assigning an input to a bus transfers its output routing to the bus, avoiding duplicate playback.
        if (route.Source.Kind == RouteKind.Input)
        {
            int i = Settings.Inputs.ToList().FindIndex(c => c.Id == route.Source.Id);
            var matrix = Settings.MatrixDb.Select(r => (double[])r.Clone()).ToArray();
            Array.Fill(matrix[i], double.NegativeInfinity);
            next = next with { MatrixDb = matrix };
        }
        Commit(next);
        LayoutChanged?.Invoke();
        return null;
    }
    public void RemoveBusRoute(BusRoute route)
    {
        Commit(Settings with { BusRoutes = Settings.BusRoutes.Where(r => r != route).ToArray() });
        LayoutChanged?.Invoke();
    }
}
