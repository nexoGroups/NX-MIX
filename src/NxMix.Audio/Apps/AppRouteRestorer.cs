using NxMix.Audio.Apps;
using NxMix.Audio.Interop;

namespace NxMix.Audio;

/// <summary>Stellt Pro-App-Umleitungen aus dem Journal wieder her (nach Absturz, beim Start und beim Beenden von NX-MIX).
/// Reflection-Name: NxMix.Audio.AppRouteRestorer. Nichts hier loescht jemals "alle" Einstellungen: nur Programme, die im Journal stehen,
/// und nur auf den Wert, der VOR unserer Umleitung galt.</summary>
public sealed class AppRouteRestorer : IAppRouteRestorer
{
    /// <summary>Prozess-Loopback gibt es ab Windows 10 Build 20348 / Windows 11.</summary>
    internal const int MinLoopbackBuild = 20348;

    private readonly string? _journalPath;
    private readonly IAppRouter? _router;
    private readonly IProcessLister? _lister;
    private readonly object _gate = new();
    private bool _probed;
    private bool _supported;
    private string? _reason;

    public AppRouteRestorer() { }

    internal AppRouteRestorer(string? journalPath, IAppRouter? router = null, IProcessLister? lister = null)
    {
        _journalPath = journalPath;
        _router = router;
        _lister = lister;
    }

    public bool IsSupported
    {
        get { Probe(); return _supported; }
    }

    public string? UnsupportedReason
    {
        get { Probe(); return _reason; }
    }

    private void Probe()
    {
        lock (_gate)
        {
            if (_probed) return;
            _probed = true;
            try
            {
                if (_router != null) { _supported = true; return; }
                if (Environment.OSVersion.Version.Build < MinLoopbackBuild)
                {
                    _reason = $"Windows-Build {Environment.OSVersion.Version.Build} ist zu alt: Prozess-Loopback braucht Build {MinLoopbackBuild} (Windows 10 21H2 / Windows 11) oder neuer.";
                    return;
                }
                var (ok, reason) = ComThread.Run(CheckRuntime, 20000);
                _supported = ok;
                _reason = reason;
            }
            catch (Exception ex)
            {
                _supported = false;
                _reason = "Pruefung fehlgeschlagen: " + ex.Message;
            }
        }
    }

    private static (bool Ok, string? Reason) CheckRuntime()
    {
        using AudioPolicyConfig? policy = AudioPolicyConfig.TryCreate(out string? policyError);
        if (policy == null) return (false, "Pro-App-Umleitung (IAudioPolicyConfig) nicht verfuegbar: " + policyError);
        if (!ProcessLoopbackEndpoint.ProbeSupported(out string? loopbackError))
            return (false, loopbackError);
        return (true, null);
    }

    /// <summary>Setzt ALLE im Journal stehenden Umleitungen zurueck (auf den Wert vor unserer Umleitung). Mehrfach aufrufbar, wirft nie.
    /// Programme, die gerade nicht laufen, bleiben im Journal ("Pending") und werden beim naechsten Aufruf bzw. beim naechsten Start eines
    /// App-Links nachgeholt: Windows laesst sich nur ueber einen laufenden Prozess des Programms umstellen.</summary>
    public void RestoreAllKnown()
    {
        try
        {
            var journal = new AppRouteJournal(_journalPath);
            if (journal.Load().Count == 0) return;
            if (_router != null)
            {
                new AppRouteService(_router, journal).RestoreAll((_lister ?? new SystemProcessLister()).Snapshot(), out _);
                return;
            }
            ComThread.Run(() =>
            {
                using PolicyAppRouter? router = PolicyAppRouter.TryCreate(out _);
                if (router == null) return 0;
                new AppRouteService(router, journal).RestoreAll((_lister ?? new SystemProcessLister()).Snapshot(), out _);
                return 0;
            }, 20000);
        }
        catch
        {
            // darf nie werfen
        }
    }
}
