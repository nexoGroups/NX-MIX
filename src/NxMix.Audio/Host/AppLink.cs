using NxMix.Audio.Apps;
using NxMix.Audio.Interop;

namespace NxMix.Audio.Host;

/// <summary>Ein App-Link (eine AppBinding) im laufenden Host. Der Master-Thread liest die Spuren (ReadInto), der Verwaltungs-Thread tauscht die
/// Spuren aus (SetLanes, copy-on-write) und pflegt den Status. Der Audio-Thread nimmt keine Locks und allokiert nichts.</summary>
internal sealed class AppLinkSlot
{
    private AppLane[] _lanes = Array.Empty<AppLane>();
    private float[][]? _dst;
    private float[][]? _scratch;
    private volatile string? _message;
    private int _state = (int)AppLinkState.WaitingForApp;
    private int _processCount;

    public AppLinkSlot(AppBinding binding)
    {
        Binding = binding;
        RingChannels = binding.Count <= 1 ? 1 : 2;
    }

    public AppBinding Binding { get; }
    public int RingChannels { get; }

    /// <summary>true: dauerhaft unbrauchbar (z. B. Parkplatz-Geraet fehlt); dann wird nie umgeleitet und nichts aufgenommen.</summary>
    public volatile bool Disabled;

    /// <summary>true: die Kanalangabe selbst ist ungueltig (existiert nicht / doppelt belegt): der Link wird nie mit der Engine verdrahtet.</summary>
    public bool Invalid;

    public AppLane[] Lanes => Volatile.Read(ref _lanes);

    public void SetLanes(AppLane[] lanes) => Volatile.Write(ref _lanes, lanes);

    public void SetStatus(AppLinkState state, string? message, int processCount)
    {
        Volatile.Write(ref _state, (int)state);
        _message = message;
        Volatile.Write(ref _processCount, processCount);
    }

    public AppLinkState State => (AppLinkState)Volatile.Read(ref _state);
    public string? Message => _message;

    public AppLinkStatus GetStatus()
    {
        AppLane[] lanes = Lanes;
        double fill = 0;
        int n = 0;
        foreach (AppLane l in lanes)
        {
            if (l.Reader is { } r) { fill += r.FillFraction; n++; }
        }
        return new AppLinkStatus(Binding.ExeName, State, Volatile.Read(ref _processCount), _message, n > 0 ? fill / n : 0.0);
    }

    /// <summary>Summe der Unterlaeufe/Ueberlaeufe der Leser (Diagnose).</summary>
    public (long Underruns, long Overruns) Counters()
    {
        long u = 0, o = 0;
        foreach (AppLane l in Lanes)
            if (l.Reader is { } r) { u += r.Underruns; o += r.Overruns; }
        return (u, o);
    }

    /// <summary>Verdrahtet die Zielpuffer mit den Engine-Eingaengen (zero-copy). Vom MixCore.Prepare().</summary>
    public void Bind(float[][] inputs, int chunk)
    {
        _dst = new float[RingChannels][];
        _scratch = new float[RingChannels][];
        for (int k = 0; k < RingChannels; k++)
        {
            _dst[k] = inputs[Binding.EngineFirstChannel + k];
            _scratch[k] = new float[chunk];
        }
    }

    /// <summary>Audio-Thread: schreibt n Frames in die Engine-Eingaenge dieses Links (Stille, wenn keine Spur offen ist; mehrere Spuren werden summiert).</summary>
    public void ReadInto(float[][] inputs, int n)
    {
        AppLane[] lanes = Volatile.Read(ref _lanes);
        int first = Binding.EngineFirstChannel;
        int count = Binding.Count;
        float[][] dst = _dst!;

        if (lanes.Length == 0 || Disabled)
        {
            for (int k = 0; k < count; k++) Array.Clear(inputs[first + k], 0, n);
            return;
        }

        lanes[0].Reader!.Read(dst, 0, n);
        for (int k = RingChannels; k < count; k++) Array.Clear(inputs[first + k], 0, n);

        for (int i = 1; i < lanes.Length; i++)
        {
            lanes[i].Reader!.Read(_scratch!, 0, n);
            for (int k = 0; k < RingChannels; k++)
            {
                float[] d = dst[k], s = _scratch![k];
                for (int j = 0; j < n; j++) d[j] += s[j];
            }
        }
    }
}

/// <summary>Testbare Abhaengigkeiten des App-Link-Verwalters.</summary>
internal sealed class AppLinkDeps
{
    public IAppRouter? Router;
    public AppRouteJournal? Journal;
    public IProcessLister? Lister;
    /// <summary>Erzeugt/oeffnet eine Spur fuer (Wurzel-PID, Ring-Kanaele). null = echter Prozess-Loopback.</summary>
    public Func<int, int, LaneContext, AppLane>? LaneFactory;
    /// <summary>Ersetzt die echte Sitzungsabfrage der "Ton laeuft noch woanders"-Pruefung.</summary>
    public Func<List<SessionRef>>? Sessions;
    public IReadOnlyList<AudioDeviceInfo>? RenderDevices;
}

/// <summary>Verwaltet die App-Links eines Host-Laufs auf einem eigenen Hintergrund-Thread (1 Hz): Prozesse suchen, ERST umleiten, DANN
/// Prozess-Loopback starten; verschwundene Prozesse -> Spur schliessen. Beim Beenden (und bei Host-Fehlern) werden alle Umleitungen zurueckgesetzt.
///
/// Umleitungs-Verhalten (gemessen auf Windows 11 Build 26300): Der Wert gilt je PROGRAMM (Exe) und ueberlebt das Prozessende, daher bleibt die
/// Umleitung waehrend des Laufs bestehen (ein neu gestarteter Prozess ist von der ersten Sekunde an umgeleitet) und wird erst beim Beenden des
/// Hosts zurueckgesetzt - das geht nur ueber eine PID eines laufenden Prozesses der Exe; laeuft keiner, bleibt der Journal-Eintrag "Pending".</summary>
internal sealed class AppLinkEngine
{
    private const int TickMs = 1000;
    private const int LeakCheckEveryTicks = 3;

    private readonly HostRun? _run;
    private readonly AppLinkDeps _deps;
    private readonly Func<LaneContext> _ctx;
    private readonly string? _parkingId;
    private readonly string? _parkingPolicyId;
    private readonly ManualResetEventSlim _stop = new(false);
    private Thread? _thread;
    private AppRouteService? _routes;
    private PolicyAppRouter? _ownRouter;
    private string? _globalError;
    private int _tick;
    private bool _released;

    public AppLinkSlot[] Slots { get; }

    /// <summary>Alle Link-Eintraege gelten als Ausfall, wenn dieser Fehler gesetzt ist (z. B. kein Parkplatz-Geraet).</summary>
    public string? GlobalError => _globalError;

    public AppLinkEngine(HostRun? run, IReadOnlyList<AppBinding> bindings, string? parkingDeviceId, int inputChannels,
        ISet<int> takenInputs, Func<LaneContext> ctx, IReadOnlyList<string?> renderBindingDeviceIds, AppLinkDeps? deps = null)
    {
        _run = run;
        _deps = deps ?? new AppLinkDeps();
        _ctx = ctx;
        _parkingId = string.IsNullOrWhiteSpace(parkingDeviceId) ? null : parkingDeviceId;
        _parkingPolicyId = _parkingId == null ? null : AudioPolicyConfig.PolicyDeviceId(_parkingId);

        var slots = new List<AppLinkSlot>();
        var used = new HashSet<int>(takenInputs);
        foreach (AppBinding b in bindings)
        {
            var slot = new AppLinkSlot(b);
            slots.Add(slot);
            if (string.IsNullOrWhiteSpace(b.ExeName)) { Fail(slot, "Kein Programmname (ExeName) angegeben."); continue; }
            if (b.Count < 1 || b.EngineFirstChannel < 0) { Fail(slot, "Ungueltige Kanalangabe."); continue; }
            if (b.EngineFirstChannel + b.Count > inputChannels)
            {
                Fail(slot, $"Engine-Eingang {b.EngineFirstChannel}..{b.EngineFirstChannel + b.Count - 1} existiert nicht (Prozessor hat {inputChannels}).");
                continue;
            }
            int clash = Enumerable.Range(b.EngineFirstChannel, b.Count).FirstOrDefault(c => used.Contains(c), -1);
            if (clash >= 0) { Fail(slot, $"Engine-Eingang {clash} ist bereits belegt."); continue; }
            for (int c = b.EngineFirstChannel; c < b.EngineFirstChannel + b.Count; c++) used.Add(c);
        }
        Slots = slots.ToArray();

        _globalError = ValidateParking(renderBindingDeviceIds);
        if (_globalError != null)
            foreach (AppLinkSlot s in Slots)
                if (!s.Disabled) Fail(s, _globalError);
    }

    private static void Fail(AppLinkSlot slot, string message)
    {
        slot.Invalid = true;
        slot.Disabled = true;
        slot.SetStatus(AppLinkState.Failed, message, 0);
    }

    /// <summary>Prueft das Parkplatz-Geraet. null = in Ordnung.</summary>
    private string? ValidateParking(IReadOnlyList<string?> renderBindingDeviceIds)
    {
        if (_parkingId == null)
            return "Parkplatz-Geraet fehlt (AudioHostConfig.ParkingDeviceId): ohne ein stilles Geraet, auf das das Programm umgeleitet wird, wuerde sein Ton doppelt hoerbar sein.";
        try
        {
            IReadOnlyList<AudioDeviceInfo> devices = _deps.RenderDevices ?? new DeviceCatalog().List();
            AudioDeviceInfo? dev = devices.FirstOrDefault(d => d.Flow == DeviceFlow.Render && string.Equals(d.Id, _parkingId, StringComparison.OrdinalIgnoreCase));
            if (dev == null)
                return "Parkplatz-Geraet nicht gefunden oder nicht aktiv: " + _parkingId;
            if (dev.IsDefault)
                return $"Parkplatz-Geraet '{dev.Name}' ist das Standard-Wiedergabegeraet: die Umleitung wuerde den Ton dort hoerbar lassen.";
            foreach (string? id in renderBindingDeviceIds)
                if (id != null && string.Equals(id, _parkingId, StringComparison.OrdinalIgnoreCase))
                    return $"Parkplatz-Geraet '{dev.Name}' ist zugleich ein Ausgang der Engine: der Ton wuerde direkt dorthin laufen.";
        }
        catch (Exception ex)
        {
            return "Parkplatz-Geraet konnte nicht geprueft werden: " + ex.Message;
        }

        if (_deps.Router == null)
        {
            try
            {
                string? err = ComThread.Run(() =>
                {
                    using AudioPolicyConfig? c = AudioPolicyConfig.TryCreate(out string? e);
                    return c == null ? e : null;
                }, 20000);
                if (err != null) return "Pro-App-Umleitung nicht verfuegbar: " + err;
            }
            catch (Exception ex)
            {
                return "Pro-App-Umleitung konnte nicht geprueft werden: " + ex.Message;
            }
        }
        return null;
    }

    public IReadOnlyList<AppLinkStatus> GetStatuses() => Slots.Select(s => s.GetStatus()).ToArray();

    public void StartThread()
    {
        if (_globalError != null || Slots.All(s => s.Disabled)) return;
        _thread = new Thread(Loop, 512 * 1024) { IsBackground = true, Name = "NxMix.AppLinks" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    /// <summary>Beendet den Verwalter: schliesst alle Spuren und setzt alle Umleitungen zurueck. Mehrfach aufrufbar.</summary>
    public void Shutdown()
    {
        _stop.Set();
        Thread? t = _thread;
        if (t is { IsAlive: true } && t != Thread.CurrentThread) t.Join(15000);
    }

    private void Loop()
    {
        try
        {
            if (!PrepareRoutes(out string? routeError))
            {
                FailAll(routeError ?? "Pro-App-Umleitung nicht verfuegbar.");
                return;
            }

            IProcessLister lister = _deps.Lister ?? new SystemProcessLister();
            while (!_stop.IsSet)
            {
                if (_run != null && _run.IsFaulted)
                {
                    FailAll("Die Audio-Engine ist ausgefallen: Umleitungen wurden zurueckgesetzt.");
                    break;
                }
                try { Tick(lister.Snapshot()); }
                catch (Exception ex) { FailAllSoft("Interner Fehler: " + ex.Message); }
                _stop.Wait(TickMs);
            }
        }
        catch (Exception ex)
        {
            FailAll("Interner Fehler: " + ex.Message);
        }
        finally
        {
            ReleaseAll();
        }
    }

    /// <summary>Erzeugt den Umleitungs-Dienst (auf dem Thread, der ihn benutzt). Intern oeffentlich fuer Tests.</summary>
    internal bool PrepareRoutes(out string? error)
    {
        error = null;
        if (_deps.Router != null)
        {
            _routes = new AppRouteService(_deps.Router, _deps.Journal ?? new AppRouteJournal());
            return true;
        }
        _ownRouter = PolicyAppRouter.TryCreate(out string? err);
        if (_ownRouter == null)
        {
            error = "Pro-App-Umleitung nicht verfuegbar: " + err;
            return false;
        }
        _routes = new AppRouteService(_ownRouter, _deps.Journal ?? new AppRouteJournal());
        return true;
    }

    private void FailAll(string message)
    {
        foreach (AppLinkSlot s in Slots) { s.Disabled = true; s.SetStatus(AppLinkState.Failed, message, 0); }
    }

    private void FailAllSoft(string message)
    {
        foreach (AppLinkSlot s in Slots)
            if (!s.Disabled) s.SetStatus(AppLinkState.Failed, message, 0);
    }

    /// <summary>Schliesst alle Spuren und setzt alle Umleitungen der eigenen Links zurueck.</summary>
    internal void ReleaseAll()
    {
        if (_released) return;
        _released = true;
        foreach (AppLinkSlot s in Slots)
        {
            AppLane[] lanes = s.Lanes;
            s.SetLanes(Array.Empty<AppLane>());
            foreach (AppLane l in lanes) { try { l.Stop(); } catch { /* egal */ } }
        }
        try
        {
            if (_routes != null)
            {
                IReadOnlyList<ProcInfo> procs = (_deps.Lister ?? new SystemProcessLister()).Snapshot();
                foreach (string exe in Slots.Select(s => ProcessSelector.NormalizeExe(s.Binding.ExeName)).Where(e => e.Length > 0).Distinct())
                    _routes.Release(exe, procs, out _);
            }
        }
        catch
        {
            // Aufraeumen darf nie werfen
        }
        try { _ownRouter?.Dispose(); } catch { /* egal */ }
        _ownRouter = null;
    }

    /// <summary>Ein Durchlauf: je Link Prozesse suchen, umleiten, Spuren oeffnen/schliessen. Intern oeffentlich fuer Tests.</summary>
    internal void Tick(IReadOnlyList<ProcInfo> procs)
    {
        if (_routes == null) throw new InvalidOperationException("Verwalter nicht initialisiert.");
        _tick++;
        List<SessionRef>? sessions = null;
        bool leakCheck = _tick % LeakCheckEveryTicks == 1;

        foreach (AppLinkSlot slot in Slots)
        {
            if (slot.Disabled) continue;
            string exe = slot.Binding.ExeName;
            IReadOnlyList<ProcessGroup> groups = ProcessSelector.FindGroups(procs, exe);
            int count = groups.Sum(g => g.Members.Count);

            // Spuren schliessen, deren Prozessbaum weg ist oder deren Thread ausgefallen ist
            var keep = new List<AppLane>();
            string? laneError = null;
            foreach (AppLane lane in slot.Lanes)
            {
                if (lane.IsAlive && groups.Any(g => g.RootPid == lane.Pid)) keep.Add(lane);
                else
                {
                    if (!lane.IsAlive) laneError = lane.Error;
                    try { lane.Stop(); } catch { /* egal */ }
                }
            }
            if (keep.Count != slot.Lanes.Length) slot.SetLanes(keep.ToArray());

            if (groups.Count == 0)
            {
                slot.SetStatus(AppLinkState.WaitingForApp, null, 0);
                continue;
            }

            // ERST umleiten ...
            var pids = groups.SelectMany(g => g.Members.OrderBy(p => p == g.RootPid ? 0 : 1)).ToList();
            RouteResult rr = _routes.EnsureRouted(exe, pids, _parkingPolicyId!, out string? routeMsg);
            if (rr == RouteResult.NotReady)
            {
                slot.SetStatus(AppLinkState.WaitingForApp, routeMsg, count);
                continue;
            }
            if (rr == RouteResult.Failed)
            {
                slot.SetStatus(AppLinkState.Failed, routeMsg, count);
                continue;
            }

            // ... DANN Capture starten (je Gruppe eine Spur)
            string? startError = null;
            foreach (ProcessGroup g in groups)
            {
                if (keep.Any(l => l.Pid == g.RootPid)) continue;
                try
                {
                    AppLane lane = _deps.LaneFactory != null
                        ? _deps.LaneFactory(g.RootPid, slot.RingChannels, _ctx())
                        : AppLane.Start(g.RootPid, slot.RingChannels, _ctx());
                    keep.Add(lane);
                    slot.SetLanes(keep.ToArray());
                }
                catch (Exception ex)
                {
                    startError = $"Prozess-Loopback fuer PID {g.RootPid} konnte nicht gestartet werden: {ex.Message}";
                }
            }

            if (keep.Count > 0)
            {
                string? hint = slot.Message;
                if (leakCheck)
                {
                    sessions ??= ReadSessions();
                    hint = LeakHint(sessions, groups);
                }
                slot.SetStatus(AppLinkState.Linked, hint, count);
            }
            else
            {
                slot.SetStatus(AppLinkState.Failed, startError ?? laneError ?? "Prozess-Loopback nicht verfuegbar.", count);
            }
        }

        RetryPending(procs);
    }

    private List<SessionRef> ReadSessions()
    {
        try { return _deps.Sessions != null ? _deps.Sessions() : SessionEnumeration.Enumerate(); }
        catch { return new List<SessionRef>(); }
    }

    /// <summary>Hinweis, wenn das Programm trotz Umleitung noch AKTIV auf einem anderen Geraet als dem Parkplatz ausgibt (fest gewaehltes Geraet,
    /// oder der Stream wurde vor der Umleitung geoeffnet - bereits laufende Streams ziehen nicht um).</summary>
    private string? LeakHint(List<SessionRef> sessions, IReadOnlyList<ProcessGroup> groups)
    {
        var members = new HashSet<int>(groups.SelectMany(g => g.Members));
        foreach (SessionRef s in sessions)
        {
            if (!members.Contains(s.ProcessId) || s.State != 1) continue;
            if (_parkingId != null && string.Equals(s.DeviceId, _parkingId, StringComparison.OrdinalIgnoreCase)) continue;
            return $"Programm laeuft: der Ton kommt noch direkt auf '{s.DeviceName}' (fest gewaehltes Geraet oder Stream vor der Umleitung geoeffnet) - Wiedergabe neu starten.";
        }
        return null;
    }

    /// <summary>Eintraege aus frueheren Laeufen, die nicht zurueckgesetzt werden konnten, weil das Programm nicht lief: nachholen, sobald es wieder da ist.</summary>
    private void RetryPending(IReadOnlyList<ProcInfo> procs)
    {
        try
        {
            foreach (RouteEntry e in _routes!.Journal.Load())
            {
                if (!e.Pending) continue;
                if (Slots.Any(s => !s.Disabled && ProcessSelector.NormalizeExe(s.Binding.ExeName) == e.Exe)) continue; // gehoert zu einem aktiven Link
                if (ProcessSelector.FindGroups(procs, e.Exe).Count > 0) _routes.Release(e.Exe, procs, out _);
            }
        }
        catch
        {
            // egal
        }
    }
}
