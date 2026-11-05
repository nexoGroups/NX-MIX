using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace NxMix.App.Model;

/// <summary>Ein gefundenes Update: Version, Aenderungstext (Markdown) und das Velopack-Objekt dahinter.</summary>
internal sealed record UpdateOffer(string Version, string? Notes, object? Native = null);

/// <summary>Quelle fuer Updates (echt: Velopack + GitHub; im Selbsttest/Screenshot: Attrappe).</summary>
internal interface IUpdateBackend
{
    bool IsInstalled { get; }
    string CurrentVersion { get; }
    Task<UpdateOffer?> CheckAsync();
    Task DownloadAsync(UpdateOffer offer, Action<int> progress);
    /// <summary>Beendet das Programm, installiert und startet die neue Version.</summary>
    void ApplyAndRestart(UpdateOffer offer);
    /// <summary>Installiert, nachdem das Programm normal beendet wurde (ohne Neustart).</summary>
    void ApplyOnExit(UpdateOffer offer);
}

/// <summary>
/// Findet, laedt und installiert neue Versionen von GitHub ueber Velopack. Funktioniert nur in einer Kopie, die mit der
/// Setup.exe eines Releases installiert wurde; ein Build aus dem Quellcode meldet <see cref="IsInstalled"/> = false.
/// </summary>
internal sealed class VelopackUpdateBackend : IUpdateBackend
{
    public const string RepositoryUrl = "https://github.com/nexoGroups/NX-MIX";

    /// <summary>Lenkt den Updater auf eine andere Quelle (Ordner oder URL), um ein Release vor dem Veroeffentlichen zu testen.</summary>
    public const string SourceOverrideVariable = "NXMIX_UPDATE_SOURCE";

    readonly UpdateManager _manager;

    public VelopackUpdateBackend()
    {
        var source = Environment.GetEnvironmentVariable(SourceOverrideVariable);
        _manager = string.IsNullOrWhiteSpace(source)
            ? new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false))
            : new UpdateManager(source);
    }

    public bool IsInstalled => _manager.IsInstalled;
    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? AppVersion.Build;

    public async Task<UpdateOffer?> CheckAsync()
    {
        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info == null) return null;
        var t = info.TargetFullRelease;
        return new UpdateOffer(t.Version.ToString(), t.NotesMarkdown, info);
    }

    public Task DownloadAsync(UpdateOffer offer, Action<int> progress) =>
        _manager.DownloadUpdatesAsync((UpdateInfo)offer.Native!, progress);

    public void ApplyAndRestart(UpdateOffer offer) => _manager.ApplyUpdatesAndRestart(((UpdateInfo)offer.Native!).TargetFullRelease);

    public void ApplyOnExit(UpdateOffer offer) =>
        _manager.WaitExitThenApplyUpdates(((UpdateInfo)offer.Native!).TargetFullRelease, silent: true, restart: false);
}

/// <summary>Attrappe fuer Selbsttest und Screenshots: meldet auf Wunsch ein Update und "laedt" es sofort.</summary>
internal sealed class FakeUpdateBackend : IUpdateBackend
{
    public bool IsInstalled { get; set; } = true;
    public string CurrentVersion { get; set; } = AppVersion.Build;
    public UpdateOffer? Offer { get; set; }
    public string? FailWith { get; set; }
    public int Restarts, ExitInstalls;

    public Task<UpdateOffer?> CheckAsync() =>
        FailWith != null ? Task.FromException<UpdateOffer?>(new InvalidOperationException(FailWith)) : Task.FromResult(Offer);

    public Task DownloadAsync(UpdateOffer offer, Action<int> progress)
    {
        foreach (int p in new[] { 10, 50, 100 }) progress(p);
        return Task.CompletedTask;
    }

    public void ApplyAndRestart(UpdateOffer offer) => Restarts++;
    public void ApplyOnExit(UpdateOffer offer) => ExitInstalls++;
}

internal enum UpdateStage { Idle, NotInstalled, Checking, UpToDate, Available, Downloading, Ready, Failed }

/// <summary>
/// Zustand der Update-Suche fuer die Oberflaeche. Sucht im Hintergrund, meldet Ergebnisse ueber <see cref="Changed"/> auf dem
/// UI-Thread. Installiert nie von selbst: Herunterladen, Neustart oder "beim Beenden" waehlt immer der Nutzer.
/// </summary>
internal sealed class AppUpdater
{
    readonly IUpdateBackend _backend;
    readonly Func<Action<Action>> _post;

    public AppUpdater(IUpdateBackend backend, Func<Action<Action>> post) { _backend = backend; _post = post; }

    public UpdateStage Stage { get; private set; }
    public UpdateOffer? Offer { get; private set; }
    public int Progress { get; private set; }
    public string? Error { get; private set; }
    /// <summary>Gewaehlt: wird beim Beenden installiert.</summary>
    public bool InstallOnExitPending { get; private set; }
    public bool IsInstalled => _backend.IsInstalled;
    public string CurrentVersion => _backend.CurrentVersion;
    /// <summary>Nur Selbsttest/Screenshots: Arbeit sofort statt im Hintergrund.</summary>
    public bool RunInline { get; set; }

    public event Action? Changed;

    void Set(Action a) => _post()(() => { a(); Changed?.Invoke(); });

    void Run(Func<Task> work)
    {
        if (RunInline) work().GetAwaiter().GetResult();
        else _ = Task.Run(work);
    }

    /// <summary>Sucht nach einer neuen Version (ohne Wirkung waehrend einer laufenden Suche/eines Downloads).</summary>
    public void Check()
    {
        if (Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Ready) return;
        if (!_backend.IsInstalled) { Stage = UpdateStage.NotInstalled; Changed?.Invoke(); return; }
        Stage = UpdateStage.Checking; Error = null;
        Changed?.Invoke();
        Run(async () =>
        {
            try
            {
                var offer = await _backend.CheckAsync().ConfigureAwait(false);
                Set(() => { Offer = offer; Stage = offer == null ? UpdateStage.UpToDate : UpdateStage.Available; });
            }
            catch (Exception ex) { Set(() => { Stage = UpdateStage.Failed; Error = ex.Message; }); }
        });
    }

    public void Download()
    {
        var offer = Offer;
        if (offer == null || Stage != UpdateStage.Available) return;
        Stage = UpdateStage.Downloading; Progress = 0;
        Changed?.Invoke();
        Run(async () =>
        {
            try
            {
                await _backend.DownloadAsync(offer, p => Set(() => Progress = p)).ConfigureAwait(false);
                Set(() => { Stage = UpdateStage.Ready; Progress = 100; });
            }
            catch (Exception ex) { Set(() => { Stage = UpdateStage.Failed; Error = ex.Message; }); }
        });
    }

    public void InstallNow()
    {
        if (Stage == UpdateStage.Ready && Offer != null) _backend.ApplyAndRestart(Offer);
    }

    public void InstallOnExit()
    {
        if (Stage != UpdateStage.Ready || Offer == null || InstallOnExitPending) return;
        _backend.ApplyOnExit(Offer);
        InstallOnExitPending = true;
        Changed?.Invoke();
    }
}

internal static class AppVersion
{
    /// <summary>Version dieses Builds (ohne "+commit"), z. B. "0.1.0".</summary>
    public static string Build
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return v?.Split('+')[0] ?? "dev";
        }
    }
}
