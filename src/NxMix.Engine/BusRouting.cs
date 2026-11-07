namespace NxMix.Engine;

public sealed record MixBus
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; init; } = "Bus";
    public int Channels { get; init; } = 2;
    public double GainDb { get; init; }
    public bool Mute { get; init; }
    public bool Solo { get; init; }
    public RecallSafeFlags RecallSafe { get; init; } = RecallSafeFlags.None;
}

public enum RouteKind { Input, Bus, Output }
public sealed record RoutePort(RouteKind Kind, Guid Id, int Channel = 0);
public sealed record BusRoute(RoutePort Source, RoutePort Target, double GainDb = 0);

/// <summary>Warnhinweis, wenn derselbe Eingang denselben Ausgang sowohl direkt ueber MatrixDb als auch ueber einen Mix-Bus speist.</summary>
public sealed record ParallelPathWarning(Guid InputId, Guid OutputId, Guid BusId);

internal readonly record struct BusSendEdge(int SourceIndex, int TargetBusSlot, float TargetGain);

internal sealed class CompiledBusPlan
{
    /// <summary>Anzahl real belegter Einzel-Buskanaele (Summe ueber Bus.Channels in topologischer Reihenfolge).</summary>
    public int TotalSlots;
    /// <summary>Fuer jeden Bus in Settings.Buses: (erster Slot im Bus-Puffer, Kanalanzahl).</summary>
    public (int FirstSlot, int Channels)[] BusRanges = Array.Empty<(int, int)>();
    /// <summary>Ziel-Faderverstaerkung je Bus-Slot (inkl. Mute/Solo).</summary>
    public float[] SlotGain = Array.Empty<float>();
    /// <summary>Dichte Send-Matrizen im Kapazitaetsraster fuer zustandsbehaftete Rampen im Echtzeitpfad.</summary>
    public float[] InToBus = Array.Empty<float>();   // [busSlot * capIn + inIdx]
    public float[] BusToBus = Array.Empty<float>();  // [targetSlot * capBus + sourceSlot] (nur sourceSlot < targetSlot da topologisch!)
    public float[] BusToOut = Array.Empty<float>();  // [outIdx * capBus + busSlot]
}

/// <summary>Validiert und uebersetzt azyklische Mix-Bus-Netzwerke ausserhalb des Audio-Threads.</summary>
public static class BusRouting
{
    /// <summary>Validiert das Bus-Netzwerk und berechnet die effektive lineare Verstaerkungsmatrix [Input, Output].
    /// (Wird fuer algebraische Verifikation und Kompatibilitaetstests genutzt.)</summary>
    public static double[,] Compile(MixerSettings s)
    {
        var (buses, inputs, outputs, order) = ValidateCore(s);
        bool anyBusSolo = s.Buses.Any(b => b.Solo);

        var coefficients = new Dictionary<RoutePort, double[]>();
        foreach (var (id, i) in inputs)
        {
            var a = new double[inputs.Count];
            a[i] = 1;
            coefficients[new RoutePort(RouteKind.Input, id)] = a;
        }
        foreach (var id in order)
        {
            var b = buses[id];
            bool muted = b.Mute || (anyBusSolo && !b.Solo);
            double gain = muted ? 0 : Math.Pow(10, b.GainDb / 20);
            for (int ch = 0; ch < b.Channels; ch++)
            {
                var port = new RoutePort(RouteKind.Bus, id, ch);
                var a = new double[inputs.Count];
                foreach (var r in s.BusRoutes.Where(r => r.Target == port))
                {
                    double g = gain * Math.Pow(10, r.GainDb / 20);
                    var source = coefficients[r.Source];
                    for (int i = 0; i < a.Length; i++) a[i] += source[i] * g;
                }
                coefficients[port] = a;
            }
        }
        var result = new double[inputs.Count, outputs.Count];
        foreach (var r in s.BusRoutes.Where(r => r.Target.Kind == RouteKind.Output))
        {
            int o = outputs[r.Target.Id];
            double g = Math.Pow(10, r.GainDb / 20);
            var source = coefficients[r.Source];
            for (int i = 0; i < source.Length; i++) result[i, o] += source[i] * g;
        }
        return result;
    }

    /// <summary>Baut den mehrstufigen Echtzeit-Ausfuehrungsplan fuer echte Bus-Summenpuffer und echtes Post-Fader-Bus-Metering.</summary>
    internal static CompiledBusPlan? BuildExecutionPlan(MixerSettings s, int capIn, int capBus, int capOut, ReadOnlySpan<float> effectiveInGain)
    {
        if (s.Buses.Count == 0 || capBus <= 0) return null;
        var (buses, inputs, outputs, order) = ValidateCore(s);

        int totalSlots = 0;
        var topoSlotBase = new Dictionary<Guid, int>(order.Count);
        foreach (var id in order)
        {
            int ch = buses[id].Channels;
            if (totalSlots + ch > capBus) break; // Sicherheitskappe auf vorallokierte Bus-Kapazitaet
            topoSlotBase[id] = totalSlots;
            totalSlots += ch;
        }
        if (totalSlots == 0) return null;

        bool anyBusSolo = false;
        for (int i = 0; i < s.Buses.Count; i++) if (s.Buses[i].Solo) { anyBusSolo = true; break; }

        var ranges = new (int FirstSlot, int Channels)[s.Buses.Count];
        for (int i = 0; i < s.Buses.Count; i++)
        {
            var b = s.Buses[i];
            ranges[i] = topoSlotBase.TryGetValue(b.Id, out int first) ? (first, b.Channels) : (-1, 0);
        }

        var slotGain = new float[capBus];
        foreach (var (id, baseSlot) in topoSlotBase)
        {
            var b = buses[id];
            bool muted = b.Mute || (anyBusSolo && !b.Solo);
            float g = muted ? 0f : (float)Dsp.DbToLin(Math.Clamp(b.GainDb, -120.0, 12.0));
            if (g < 1e-9f) g = 0f;
            for (int c = 0; c < b.Channels; c++) slotGain[baseSlot + c] = g;
        }

        var inToBus = new float[capBus * capIn];
        var busToBus = new float[capBus * capBus];
        var busToOut = new float[capOut * capBus];

        foreach (var r in s.BusRoutes)
        {
            double routeLin = Dsp.DbToLin(Math.Clamp(r.GainDb, -120.0, 24.0));
            if (routeLin < 1e-9) continue;

            if (r.Source.Kind == RouteKind.Input && r.Target.Kind == RouteKind.Bus)
            {
                if (!inputs.TryGetValue(r.Source.Id, out int inIdx) || inIdx >= capIn) continue;
                if (!topoSlotBase.TryGetValue(r.Target.Id, out int tBase)) continue;
                int tSlot = tBase + r.Target.Channel;
                double v = effectiveInGain[inIdx] * routeLin;
                if (Math.Abs(v) >= 1e-9) inToBus[tSlot * capIn + inIdx] += (float)v;
            }
            else if (r.Source.Kind == RouteKind.Bus && r.Target.Kind == RouteKind.Bus)
            {
                if (!topoSlotBase.TryGetValue(r.Source.Id, out int sBase) || !topoSlotBase.TryGetValue(r.Target.Id, out int tBase)) continue;
                int sSlot = sBase + r.Source.Channel;
                int tSlot = tBase + r.Target.Channel;
                busToBus[tSlot * capBus + sSlot] += (float)routeLin;
            }
            else if (r.Source.Kind == RouteKind.Bus && r.Target.Kind == RouteKind.Output)
            {
                if (!topoSlotBase.TryGetValue(r.Source.Id, out int sBase)) continue;
                if (!outputs.TryGetValue(r.Target.Id, out int outIdx) || outIdx >= capOut) continue;
                int sSlot = sBase + r.Source.Channel;
                busToOut[outIdx * capBus + sSlot] += (float)routeLin;
            }
        }

        return new CompiledBusPlan
        {
            TotalSlots = totalSlots,
            BusRanges = ranges,
            SlotGain = slotGain,
            InToBus = inToBus,
            BusToBus = busToBus,
            BusToOut = busToOut,
        };
    }

    /// <summary>Erkennt zerstörungsfrei, ob ein Eingang sowohl direkt per MatrixDb als auch ueber einen MixBus auf denselben Ausgang gelangt.</summary>
    public static IReadOnlyList<ParallelPathWarning> FindParallelDirectAndBusPaths(MixerSettings s)
    {
        if (s.Buses.Count == 0 || s.BusRoutes.Count == 0) return Array.Empty<ParallelPathWarning>();
        var warnings = new List<ParallelPathWarning>();
        var inputMap = s.Inputs.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var outputMap = s.Outputs.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);

        // Erreichbarkeit je Bus von Inputs und zu Outputs
        var (_, _, _, order) = ValidateCore(s);
        var busInputs = order.ToDictionary(id => id, _ => new HashSet<Guid>());
        foreach (var r in s.BusRoutes)
        {
            if (r.Source.Kind == RouteKind.Input && r.Target.Kind == RouteKind.Bus && busInputs.TryGetValue(r.Target.Id, out var set))
                set.Add(r.Source.Id);
        }
        foreach (var id in order)
        {
            foreach (var r in s.BusRoutes.Where(r => r.Source.Kind == RouteKind.Bus && r.Source.Id == id && r.Target.Kind == RouteKind.Bus))
            {
                if (busInputs.TryGetValue(r.Target.Id, out var targetSet))
                    foreach (var srcIn in busInputs[id]) targetSet.Add(srcIn);
            }
        }
        foreach (var r in s.BusRoutes.Where(r => r.Source.Kind == RouteKind.Bus && r.Target.Kind == RouteKind.Output))
        {
            if (!outputMap.TryGetValue(r.Target.Id, out int outIdx)) continue;
            if (!busInputs.TryGetValue(r.Source.Id, out var inIds)) continue;
            foreach (var inId in inIds)
            {
                if (!inputMap.TryGetValue(inId, out int inIdx)) continue;
                if (inIdx < s.MatrixDb.Length && outIdx < s.MatrixDb[inIdx].Length &&
                    double.IsFinite(s.MatrixDb[inIdx][outIdx]) && s.MatrixDb[inIdx][outIdx] > -200)
                {
                    warnings.Add(new ParallelPathWarning(inId, r.Target.Id, r.Source.Id));
                }
            }
        }
        return warnings;
    }

    private static (Dictionary<Guid, MixBus> Buses, Dictionary<Guid, int> Inputs, Dictionary<Guid, int> Outputs, List<Guid> Order)
        ValidateCore(MixerSettings s)
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

        // Topologische Sortierung auf Bus-Ebene verhindert jede Rueckkopplung (auch kanaluebergreifend im selben Bus).
        var edges = routes.Where(r => r.Source.Kind == RouteKind.Bus && r.Target.Kind == RouteKind.Bus)
            .Select(r => (r.Source.Id, Target: r.Target.Id)).Distinct().ToArray();
        var degree = buses.Keys.ToDictionary(id => id, id => edges.Count(e => e.Target == id));
        var queue = new Queue<Guid>(degree.Where(e => e.Value == 0).Select(e => e.Key));
        var order = new List<Guid>(buses.Count);
        while (queue.TryDequeue(out var id))
        {
            order.Add(id);
            foreach (var e in edges.Where(e => e.Id == id)) if (--degree[e.Target] == 0) queue.Enqueue(e.Target);
        }
        if (order.Count != buses.Count) throw new ArgumentException("Diese Verbindung würde eine Rückkopplung zwischen Bussen erzeugen.");

        return (buses, inputs, outputs, order);
    }
}
