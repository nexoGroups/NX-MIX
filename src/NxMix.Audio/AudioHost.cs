using NxMix.Audio.Host;
using NxMix.Engine;

namespace NxMix.Audio;

/// <summary>Eigene Windows-Audio-Engine (WASAPI, eventgesteuert): Master-Takt = erste Render-Anbindung, alle anderen Geraete laufen ueber
/// lock-freie Ringpuffer mit adaptivem Resampling (PI-Regler auf den Fuellstand gegen Taktdrift).
///
/// Verhalten:
///  * Start() beginnt IMMER mit exakter Stille und blendet in ~50 ms ein. Start() bei laufendem Betrieb startet neu.
///  * Fehler beim Oeffnen oder im Betrieb (Geraet entfernt, Format-Wechsel, ...) setzen State=Faulted, loesen das Faulted-Event aus
///    (auf einem Thread-Pool-Thread) und schalten den Ausgang auf Stille; es gibt keine Exception aus dem Audio-Thread.
///    Start() wirft nur bei null-Argumenten oder nach Dispose(); ungueltige Konfiguration/Geraete werden als Faulted gemeldet.
///  * Stop() blendet kurz aus, schiebt Stille nach und raeumt auf. Stop() und Dispose() sind mehrfach aufrufbar.
///  * Audio-Threads: hohe Prioritaet + MMCSS ("Pro Audio"), keine Allokationen, keine Locks, kein Logging im Betrieb.
///  * App-Links (AudioHostConfig.AppBindings + ParkingDeviceId; Status ueber IAppLinkMonitor.GetAppLinks): ein eigener Hintergrund-Thread (1 Hz)
///    sucht die Programme, leitet sie ERST per Pro-App-Umleitung auf das Parkplatz-Geraet um und startet DANN den Prozess-Loopback (Ringpuffer +
///    adaptives Resampling wie bei jeder Quelle). Ohne gueltiges Parkplatz-Geraet (fehlt / ist Standardgeraet / ist Engine-Ausgang) wird nichts
///    umgeleitet (Status Failed mit Klartext). Stop()/Dispose() und ein Host-Fehler setzen alle eigenen Umleitungen zurueck; ein Journal
///    (%APPDATA%\NX-MIX\app-routes.json, siehe AppRouteRestorer) repariert sie auch nach einem Absturz.</summary>
public sealed class AudioHost : IAudioHost, IAppLinkMonitor
{
    private readonly object _gate = new();
    private HostRun? _run;
    private int _state = (int)HostState.Stopped;
    private string? _lastError;
    private bool _disposed;

    public HostState State => (HostState)Volatile.Read(ref _state);

    /// <summary>Nur fuer Tests/Diagnose: der aktuelle Durchlauf (Streams, Ringpuffer, Zaehler).</summary>
    internal HostRun? CurrentRun => Volatile.Read(ref _run);

    public event Action<string>? Faulted;

    public void Start(AudioHostConfig config, IBlockProcessor processor)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(processor);
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StopCore();
            _lastError = null;
            Volatile.Write(ref _state, (int)HostState.Starting);

            HostRun? run = null;
            try
            {
                run = new HostRun(this, config, processor);
                _run = run;
                run.Launch();
                Interlocked.CompareExchange(ref _state, (int)HostState.Running, (int)HostState.Starting);
            }
            catch (Exception ex)
            {
                if (run != null)
                {
                    _run = null; // spaete Fehlermeldungen der Threads ignorieren
                    try { run.Shutdown(); } catch { /* nichts mehr zu retten */ }
                }
                SetFaulted(ex.Message);
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopCore();
        }
    }

    /// <summary>Status je AppBinding (Reihenfolge wie in AudioHostConfig.AppBindings). Leer, solange der Host nicht laeuft oder keine AppBindings hat.</summary>
    public IReadOnlyList<AppLinkStatus> GetAppLinks()
    {
        AppLinkEngine? apps = Volatile.Read(ref _run)?.Apps;
        return apps == null ? Array.Empty<AppLinkStatus>() : apps.GetStatuses();
    }

    public HostStats GetStats()
    {
        HostRun? run = Volatile.Read(ref _run);
        HostState state = State;
        string? error = _lastError;
        if (run == null)
            return new HostStats(state, error, 0, 0, 0, 0, 0, Array.Empty<DeviceStat>());
        return run.Snapshot(state, error);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopCore();
        }
    }

    private void StopCore()
    {
        HostRun? run = _run;
        _run = null; // ab hier werden Fehlermeldungen des alten Laufs ignoriert
        if (run != null)
        {
            try { run.Shutdown(); }
            catch { /* Aufraeumen darf nie werfen */ }
        }
        Volatile.Write(ref _state, (int)HostState.Stopped);
    }

    /// <summary>Wird von den Audio-Threads (ueber HostRun.Fault) gerufen. Nimmt keine Locks.</summary>
    internal void OnRunFault(HostRun run, string message)
    {
        if (!ReferenceEquals(Volatile.Read(ref _run), run)) return;
        SetFaulted(message);
    }

    private void SetFaulted(string message)
    {
        _lastError = message;
        Volatile.Write(ref _state, (int)HostState.Faulted);
        Action<string>? handler = Faulted;
        if (handler == null) return;
        ThreadPool.UnsafeQueueUserWorkItem(static state =>
        {
            var (h, msg) = ((Action<string>, string))state!;
            try { h(msg); } catch { /* Handler-Fehler duerfen den Host nicht beeintraechtigen */ }
        }, (handler, message));
    }
}
