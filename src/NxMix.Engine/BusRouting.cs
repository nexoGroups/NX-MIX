namespace NxMix.Engine;

public sealed record MixBus
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Bus";
    public int Channels { get; init; } = 2;
    public double GainDb { get; init; }
    public bool Mute { get; init; }
}

public enum RouteKind { Input, Bus, Output }
public sealed record RoutePort(RouteKind Kind, Guid Id, int Channel = 0);
public sealed record BusRoute(RoutePort Source, RoutePort Target, double GainDb = 0);

/// <summary>Compiles an acyclic, linear bus network into input/output coefficients outside the audio thread.
/// Buses deliberately have no DSP. Summing paths before the final output DSP is algebraically identical
/// to separate bus buffers; the existing audio-thread gain ramps also smooth bus fader changes.</summary>
public static class BusRouting
{
    public static double[,] Compile(MixerSettings s)
    {
        var buses = s.Buses.ToDictionary(b => b.Id);
        if (buses.Values.Any(b => b.Channels is < 1 or > 64 || !double.IsFinite(b.GainDb) || b.GainDb is < -120 or > 12))
            throw new ArgumentException("Bus: 1–64 Kanäle und Pegel zwischen −120 und +12 dB erforderlich.");
        var inputs = s.Inputs.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var outputs = s.Outputs.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);
        bool Exists(RoutePort p) => p.Kind switch
        {
            RouteKind.Input => p.Channel == 0 && inputs.ContainsKey(p.Id),
            RouteKind.Output => p.Channel == 0 && outputs.ContainsKey(p.Id),
            RouteKind.Bus => buses.TryGetValue(p.Id, out var b) && p.Channel >= 0 && p.Channel < b.Channels,
            _ => false,
        };
        var routes = s.BusRoutes;
        foreach (var r in routes)
            if (!Exists(r.Source) || !Exists(r.Target) || r.Source.Kind == RouteKind.Output || r.Target.Kind == RouteKind.Input ||
                (r.Source.Kind != RouteKind.Bus && r.Target.Kind != RouteKind.Bus) || !double.IsFinite(r.GainDb) || r.GainDb is < -120 or > 24)
                throw new ArgumentException("Ungültige Bus-Zuordnung oder fehlender Kanal.");
        if (routes.Select(r => (r.Source, r.Target)).Distinct().Count() != routes.Count)
            throw new ArgumentException("Diese Bus-Zuordnung ist bereits vorhanden.");

        // Topological order at bus level rejects feedback even across different channels of the same bus.
        var edges = routes.Where(r => r.Source.Kind == RouteKind.Bus && r.Target.Kind == RouteKind.Bus)
            .Select(r => (r.Source.Id, Target: r.Target.Id)).Distinct().ToArray();
        var degree = buses.Keys.ToDictionary(id => id, id => edges.Count(e => e.Target == id));
        var queue = new Queue<Guid>(degree.Where(e => e.Value == 0).Select(e => e.Key));
        var order = new List<Guid>();
        while (queue.TryDequeue(out var id))
        {
            order.Add(id);
            foreach (var e in edges.Where(e => e.Id == id)) if (--degree[e.Target] == 0) queue.Enqueue(e.Target);
        }
        if (order.Count != buses.Count) throw new ArgumentException("Diese Verbindung würde eine Rückkopplung zwischen Bussen erzeugen.");
        var coefficients = new Dictionary<RoutePort, double[]>();
        foreach (var (id, i) in inputs)
        {
            var a = new double[inputs.Count]; a[i] = 1;
            coefficients[new RoutePort(RouteKind.Input, id)] = a;
        }
        foreach (var id in order)
        {
            var b = buses[id];
            double gain = b.Mute ? 0 : Math.Pow(10, b.GainDb / 20);
            for (int ch = 0; ch < b.Channels; ch++)
            {
                var port = new RoutePort(RouteKind.Bus, id, ch);
                var a = new double[inputs.Count];
                foreach (var r in routes.Where(r => r.Target == port))
                {
                    double g = gain * Math.Pow(10, r.GainDb / 20);
                    var source = coefficients[r.Source];
                    for (int i = 0; i < a.Length; i++) a[i] += source[i] * g;
                }
                coefficients[port] = a;
            }
        }
        var result = new double[inputs.Count, outputs.Count];
        foreach (var r in routes.Where(r => r.Target.Kind == RouteKind.Output))
        {
            int o = outputs[r.Target.Id];
            double g = Math.Pow(10, r.GainDb / 20);
            var source = coefficients[r.Source];
            for (int i = 0; i < source.Length; i++) result[i, o] += source[i] * g;
        }
        return result;
    }
}
