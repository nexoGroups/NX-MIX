using NxMix.Audio.Interop;

namespace NxMix.Audio.Apps;

/// <summary>Pro-App-Umleitung (drei Rollen: Console, Multimedia, Communications). Austauschbar fuer Tests.
/// Windows speichert den Wert je PROGRAMM (Exe), nicht je Prozess: er gilt fuer alle Prozesse der Exe, auch fuer spaeter gestartete,
/// und ueberlebt das Prozessende. Zum Lesen/Setzen wird eine PID eines LAUFENDEN Prozesses der Exe gebraucht, der Audio schon benutzt hat.</summary>
internal interface IAppRouter
{
    /// <summary>Liest die drei Rollenwerte (null = kein Pro-App-Wert). hr &lt; 0: PID unbekannt/keine Audio-Nutzung (E_INVALIDARG).</summary>
    int Get(int pid, string?[] roles);

    /// <summary>Setzt die drei Rollenwerte (null/leer = zuruecksetzen). Gibt den ersten Fehler zurueck (0 = OK).</summary>
    int Set(int pid, string?[] roles);
}

internal sealed class PolicyAppRouter : IAppRouter, IDisposable
{
    private readonly AudioPolicyConfig _cfg;

    public PolicyAppRouter(AudioPolicyConfig cfg) => _cfg = cfg;

    /// <summary>Auf dem aufrufenden (MTA-)Thread erzeugen. null + error, wenn Windows die Pro-App-Umleitung nicht anbietet.</summary>
    public static PolicyAppRouter? TryCreate(out string? error)
    {
        AudioPolicyConfig? cfg = AudioPolicyConfig.TryCreate(out error);
        return cfg == null ? null : new PolicyAppRouter(cfg);
    }

    public int Get(int pid, string?[] roles)
    {
        for (int r = 0; r < 3; r++)
        {
            int hr = _cfg.Get(pid, out string? v, r);
            if (hr < 0) return hr;
            roles[r] = string.IsNullOrEmpty(v) ? null : v;
        }
        return 0;
    }

    public int Set(int pid, string?[] roles)
    {
        int first = 0;
        for (int r = 0; r < 3; r++)
        {
            int hr = _cfg.SetRole(pid, r, roles[r]);
            if (hr < 0 && first == 0) first = hr;
        }
        return first;
    }

    public void Dispose() => _cfg.Dispose();
}

internal enum RouteResult
{
    /// <summary>Umgeleitet (Journal geschrieben, per Get verifiziert).</summary>
    Routed,
    /// <summary>Stand schon auf dem Parkplatz (z. B. aus einem frueheren Lauf).</summary>
    AlreadyRouted,
    /// <summary>Kein Prozess der Exe war fuer Windows ansprechbar (hat noch kein Audio benutzt). Spaeter erneut versuchen.</summary>
    NotReady,
    Failed,
}

/// <summary>Logik rund um Umleitung + Journal. Reihenfolge ist wichtig: ERST Journal (inkl. VORHER-Wert), DANN umleiten; ERST zuruecksetzen
/// und per Get pruefen, DANN austragen. Nichts hier ruft jemals "alle Umleitungen loeschen" auf.</summary>
internal sealed class AppRouteService
{
    private readonly IAppRouter _router;
    private readonly AppRouteJournal _journal;

    public AppRouteService(IAppRouter router, AppRouteJournal journal)
    {
        _router = router;
        _journal = journal;
    }

    public AppRouteJournal Journal => _journal;

    private static bool Same(string? a, string? b) =>
        string.Equals(string.IsNullOrEmpty(a) ? null : a, string.IsNullOrEmpty(b) ? null : b, StringComparison.OrdinalIgnoreCase);

    private static bool AllRoles(string?[] roles, string? expected)
    {
        for (int i = 0; i < 3; i++) if (!Same(roles[i], expected)) return false;
        return true;
    }

    private static bool AnyRole(string?[] roles, string? expected)
    {
        for (int i = 0; i < 3; i++) if (Same(roles[i], expected)) return true;
        return false;
    }

    /// <summary>Leitet die Exe auf das Parkplatz-Geraet um (parkingPolicyId = AudioPolicyConfig.PolicyDeviceId(...)). pids: laufende Prozesse der Exe,
    /// Wurzel zuerst; der erste, den Windows kennt, wird benutzt.</summary>
    public RouteResult EnsureRouted(string exe, IReadOnlyList<int> pids, string parkingPolicyId, out string? message)
    {
        message = null;
        try
        {
            RouteEntry? entry = _journal.Find(exe);
            var cur = new string?[3];
            string? lastError = null;
            foreach (int pid in pids)
            {
                int hr = _router.Get(pid, cur);
                if (hr < 0) { lastError = $"PID {pid}: 0x{hr:X8}"; continue; }

                if (AllRoles(cur, parkingPolicyId))
                {
                    if (entry == null)
                    {
                        // Steht schon auf dem Parkplatz, aber kein Journal (geloescht?): eintragen, damit wir es wieder freigeben
                        _journal.Upsert(NewEntry(exe, parkingPolicyId, new string?[3]));
                    }
                    else if (entry.Pending || entry.Parking != parkingPolicyId)
                    {
                        RouteEntry e = entry.Clone();
                        e.Pending = false;
                        e.Parking = parkingPolicyId;
                        _journal.Upsert(e);
                    }
                    return RouteResult.AlreadyRouted;
                }

                // Den VORHER gesetzten Wert nur beim ersten Umleiten merken (ein Eintrag aus einem frueheren Lauf behaelt seinen Originalwert)
                string?[] previous = entry != null && AnyRole(cur, entry.Parking) ? (string?[])entry.Previous.Clone() : (string?[])cur.Clone();
                for (int i = 0; i < 3; i++)
                    if (Same(previous[i], parkingPolicyId) || (entry != null && Same(previous[i], entry.Parking))) previous[i] = null;
                _journal.Upsert(NewEntry(exe, parkingPolicyId, previous)); // VOR dem Umleiten

                var target = new[] { parkingPolicyId, parkingPolicyId, parkingPolicyId };
                hr = _router.Set(pid, target);
                var check = new string?[3];
                int hr2 = _router.Get(pid, check);
                if (hr >= 0 && hr2 >= 0 && AllRoles(check, parkingPolicyId))
                    return RouteResult.Routed;

                // Umleitung hat nicht gegriffen: Zustand wiederherstellen und Eintrag austragen
                _router.Set(pid, previous);
                var back = new string?[3];
                if (_router.Get(pid, back) >= 0 && Equal(back, previous))
                    _journal.Remove(exe);
                message = $"Umleitung auf das Parkplatz-Geraet wurde von Windows nicht uebernommen (Set 0x{hr:X8}).";
                return RouteResult.Failed;
            }
            message = lastError == null ? "Kein laufender Prozess." : "Das Programm hat noch keine Audio-Sitzung (Windows kennt den Prozess noch nicht): " + lastError;
            return RouteResult.NotReady;
        }
        catch (Exception ex)
        {
            message = "Umleitung fehlgeschlagen: " + ex.Message;
            return RouteResult.Failed;
        }
    }

    private static bool Equal(string?[] a, string?[] b)
    {
        for (int i = 0; i < 3; i++) if (!Same(a[i], b[i])) return false;
        return true;
    }

    private static RouteEntry NewEntry(string exe, string parking, string?[] previous) => new()
    {
        Exe = ProcessSelector.NormalizeExe(exe),
        Parking = parking,
        Previous = previous,
        OwnerPid = Environment.ProcessId,
        SetAtUtc = DateTime.UtcNow.ToString("o"),
    };

    /// <summary>Stellt den VORHER gesetzten Wert der Exe wieder her (nicht pauschal "Standard") und traegt sie aus dem Journal aus, aber erst
    /// nachdem Get den Zielwert bestaetigt hat. Laeuft kein Prozess der Exe, geht es nicht: der Eintrag bleibt als "Pending" stehen. Wirft nie.</summary>
    public bool Release(string exe, IReadOnlyList<ProcInfo> processes, out string? message)
    {
        message = null;
        try
        {
            RouteEntry? entry = _journal.Find(exe);
            if (entry == null) return true;

            var cur = new string?[3];
            foreach (ProcessGroup g in ProcessSelector.FindGroups(processes, exe))
            {
                foreach (int pid in g.Members.OrderBy(p => p == g.RootPid ? 0 : 1))
                {
                    if (_router.Get(pid, cur) < 0) continue;
                    if (!AnyRole(cur, entry.Parking))
                    {
                        // Wurde zwischenzeitlich von jemand anderem geaendert: nichts anfassen
                        _journal.Remove(exe);
                        return true;
                    }
                    var target = (string?[])entry.Previous.Clone();
                    // Rollen, die nicht (mehr) auf dem Parkplatz stehen, bleiben unangetastet
                    for (int i = 0; i < 3; i++) if (!Same(cur[i], entry.Parking)) target[i] = cur[i];
                    int hr = _router.Set(pid, target);
                    var check = new string?[3];
                    if (hr >= 0 && _router.Get(pid, check) >= 0 && Equal(check, target))
                    {
                        _journal.Remove(exe);
                        return true;
                    }
                    message = $"Zuruecksetzen fuer '{exe}' nicht bestaetigt (0x{hr:X8}).";
                }
            }

            if (!entry.Pending)
            {
                RouteEntry e = entry.Clone();
                e.Pending = true;
                _journal.Upsert(e);
            }
            message ??= $"'{exe}' laeuft nicht: die Umleitung laesst sich erst zuruecksetzen, wenn das Programm wieder laeuft.";
            return false;
        }
        catch (Exception ex)
        {
            message = "Zuruecksetzen fehlgeschlagen: " + ex.Message;
            return false;
        }
    }

    /// <summary>Setzt alle im Journal stehenden Umleitungen zurueck. Gibt die Zahl der noch offenen (nicht ladbaren oder nicht laufenden) zurueck.</summary>
    public int RestoreAll(IReadOnlyList<ProcInfo> processes, out string? summary)
    {
        summary = null;
        int open = 0;
        try
        {
            var notes = new List<string>();
            foreach (RouteEntry e in _journal.Load())
            {
                if (!Release(e.Exe, processes, out string? msg))
                {
                    open++;
                    if (msg != null) notes.Add(msg);
                }
            }
            if (notes.Count > 0) summary = string.Join(" ", notes);
        }
        catch (Exception ex)
        {
            summary = ex.Message;
            open++;
        }
        return open;
    }
}
