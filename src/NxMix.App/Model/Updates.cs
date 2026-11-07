using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using NxMix.Daemon.Persistence;
using NxMix.Ipc;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

[assembly: InternalsVisibleTo("NxMix.Tests")]

namespace NxMix.App.Model;

/// <summary>Regeln, Kanal-Zuordnung und SemVer-2.0-Vergleich für das zweistufige NX-MIX Release-System (Stable &amp; Beta).</summary>
public static class ReleaseChannelInfo
{
    public const string StableVelopackChannel = "win";
    public const string BetaVelopackChannel = "win-beta";

    public const string StableSetupExeName = "NX-MIX-Setup.exe";
    public const string BetaSetupExeName = "NX-MIX-Beta-Setup.exe";

    private static readonly Regex s_stableTagRegex = new(@"^\d+\.\d+\.\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex s_betaTagRegex = new(@"^\d+\.\d+\.\d+-(beta|rc|alpha)(\.\d+)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static string ToVelopackChannel(ReleaseChannel channel) =>
        channel == ReleaseChannel.Beta ? BetaVelopackChannel : StableVelopackChannel;

    public static string DisplayName(ReleaseChannel channel) =>
        channel == ReleaseChannel.Beta ? "Beta" : "Stable";

    public static ReleaseChannel FromVelopackChannel(string? channelName)
    {
        if (!string.IsNullOrWhiteSpace(channelName) &&
            channelName.Contains("beta", StringComparison.OrdinalIgnoreCase))
        {
            return ReleaseChannel.Beta;
        }
        return ReleaseChannel.Stable;
    }

    public static bool IsPrereleaseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        string clean = version.Trim().TrimStart('v', 'V').Split('+')[0];
        if (SemanticVersion.TryParse(clean, out var sv))
            return !string.IsNullOrEmpty(sv.Release);
        return clean.Contains('-', StringComparison.Ordinal);
    }

    public static ReleaseChannel DetectInstalledChannel(string? velopackDefaultChannel, string? currentVersion)
    {
        if (FromVelopackChannel(velopackDefaultChannel) == ReleaseChannel.Beta)
            return ReleaseChannel.Beta;
        if (IsPrereleaseVersion(currentVersion))
            return ReleaseChannel.Beta;
        return ReleaseChannel.Stable;
    }

    /// <summary>Prüft streng, ob eine Versionsnummer zum Ziel-Release-Kanal passt (verhindert Beta-Veröffentlichung als Stable).</summary>
    public static bool IsValidVersionForChannel(string? version, ReleaseChannel channel)
    {
        if (string.IsNullOrWhiteSpace(version)) return false;
        string clean = version.Trim().TrimStart('v', 'V');
        if (!SemanticVersion.TryParse(clean, out var sv)) return false;

        return channel == ReleaseChannel.Stable
            ? string.IsNullOrEmpty(sv.Release) && s_stableTagRegex.IsMatch(clean)
            : !string.IsNullOrEmpty(sv.Release) && s_betaTagRegex.IsMatch(clean);
    }

    /// <summary>SemVer-2.0-konformer Versionsvergleich (z. B. 2.0.0 &lt; 2.1.0-beta.1 &lt; 2.1.0-beta.2 &lt; 2.1.0).</summary>
    public static int CompareVersions(string? left, string? right)
    {
        string a = (left ?? "0.0.0").Trim().TrimStart('v', 'V').Split('+')[0];
        string b = (right ?? "0.0.0").Trim().TrimStart('v', 'V').Split('+')[0];
        bool okA = SemanticVersion.TryParse(a, out var svA);
        bool okB = SemanticVersion.TryParse(b, out var svB);
        if (okA && okB) return svA!.CompareTo(svB);
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Ein gefundenes Update inklusive Kanalherkunft, Prerelease-/Downgrade-Kennzeichnung und Prüfsummen-Metadaten.</summary>
internal sealed record UpdateOffer(
    string Version,
    string? Notes,
    object? Native = null,
    ReleaseChannel SourceChannel = ReleaseChannel.Stable,
    bool IsPrerelease = false,
    bool IsDowngrade = false,
    string? Sha256 = null,
    long SizeBytes = 0);

/// <summary>Quelle für Updates (echt: Velopack + GitHub/Lokaler Feed; im Selbsttest/Screenshot: Attrappe).</summary>
internal interface IUpdateBackend
{
    bool IsInstalled { get; }
    string CurrentVersion { get; }
    ReleaseChannel InstalledChannel { get; }
    Task<UpdateOffer?> CheckAsync(ReleaseChannel channel, CancellationToken ct = default);
    Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct = default);
    void ApplyAndRestart(UpdateOffer offer);
    void ApplyOnExit(UpdateOffer offer);
}

/// <summary>
/// Kanalbewusstes Velopack-Update-Backend für NX-MIX (Stable = "win" auf regulären GitHub-Releases,
/// Beta = "win-beta" auf GitHub-Prereleases).
/// </summary>
internal sealed class VelopackUpdateBackend : IUpdateBackend
{
    public const string RepositoryUrl = "https://github.com/nexoGroups/NX-MIX";
    public const string ReleasesPageUrl = "https://github.com/nexoGroups/NX-MIX/releases";

    /// <summary>Lenkt den Updater auf einen lokalen Ordner oder eine Test-URL (für lokale Release-Prüfungen &amp; CI).</summary>
    public const string SourceOverrideVariable = "NXMIX_UPDATE_SOURCE";
    public const string BetaSourceOverrideVariable = "NXMIX_BETA_UPDATE_SOURCE";

    private sealed record NativeHandle(UpdateManager Manager, UpdateInfo Info);

    private readonly string? _stablePathOverride;
    private readonly string? _betaPathOverride;
    private readonly IUpdateSource? _customStableSource;
    private readonly IUpdateSource? _customBetaSource;
    private readonly IVelopackLocator? _locator;
    private readonly UpdateManager _probeManager;

    public VelopackUpdateBackend(
        string? stablePathOverride = null,
        string? betaPathOverride = null,
        IUpdateSource? customStableSource = null,
        IUpdateSource? customBetaSource = null,
        IVelopackLocator? locator = null)
    {
        _stablePathOverride = string.IsNullOrWhiteSpace(stablePathOverride)
            ? Environment.GetEnvironmentVariable(SourceOverrideVariable)
            : stablePathOverride;
        _betaPathOverride = string.IsNullOrWhiteSpace(betaPathOverride)
            ? (Environment.GetEnvironmentVariable(BetaSourceOverrideVariable) ?? _stablePathOverride)
            : betaPathOverride;

        _customStableSource = customStableSource;
        _customBetaSource = customBetaSource;
        _locator = locator;
        _probeManager = CreateManager(ReleaseChannel.Stable, allowDowngrade: false);
    }

    public bool IsInstalled => _probeManager.IsInstalled;
    public string CurrentVersion => _probeManager.CurrentVersion?.ToString() ?? AppVersion.Build;

    private IVelopackLocator? EffectiveLocator =>
        _locator ?? (VelopackLocator.IsCurrentSet ? VelopackLocator.Current : null);

    public ReleaseChannel InstalledChannel =>
        ReleaseChannelInfo.DetectInstalledChannel(EffectiveLocator?.Channel, CurrentVersion);

    private UpdateManager CreateManager(ReleaseChannel channel, bool allowDowngrade)
    {
        var opts = new UpdateOptions
        {
            ExplicitChannel = ReleaseChannelInfo.ToVelopackChannel(channel),
            AllowVersionDowngrade = allowDowngrade,
        };

        if (channel == ReleaseChannel.Beta)
        {
            if (_customBetaSource != null)
                return new UpdateManager(_customBetaSource, opts, _locator);
            if (!string.IsNullOrWhiteSpace(_betaPathOverride))
                return new UpdateManager(_betaPathOverride, opts, _locator);
            return new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: true), opts, _locator);
        }
        else
        {
            if (_customStableSource != null)
                return new UpdateManager(_customStableSource, opts, _locator);
            if (!string.IsNullOrWhiteSpace(_stablePathOverride))
                return new UpdateManager(_stablePathOverride, opts, _locator);
            return new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false), opts, _locator);
        }
    }

    public async Task<UpdateOffer?> CheckAsync(ReleaseChannel channel, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string curVer = CurrentVersion;
        bool currentlyOnBeta = InstalledChannel == ReleaseChannel.Beta || ReleaseChannelInfo.IsPrereleaseVersion(curVer);

        if (channel == ReleaseChannel.Stable)
        {
            // Im Stable-Kanal nur freigegebene Releases prüfen; Downgrade nur erlauben, wenn aktuell eine Beta läuft!
            var stableMgr = CreateManager(ReleaseChannel.Stable, allowDowngrade: currentlyOnBeta);
            var info = await stableMgr.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info == null) return null;

            var target = info.TargetFullRelease;
            string targetVer = target.Version.ToString();

            // Harte Sicherheitsbarriere: Stable akzeptiert niemals Prerelease-Versionen!
            if (ReleaseChannelInfo.IsPrereleaseVersion(targetVer))
                return null;

            int cmp = ReleaseChannelInfo.CompareVersions(targetVer, curVer);
            if (cmp == 0) return null;
            if (cmp < 0 && !currentlyOnBeta) return null;

            return BuildOffer(stableMgr, info, ReleaseChannel.Stable, curVer);
        }
        else
        {
            // Im Beta-Kanal sowohl den Beta-Feed ("win-beta" inkl. Prereleases) als auch etwaige neuere Stable-Releases prüfen
            // und nach SemVer 2.0 die neueste verfügbare Version wählen (z. B. 2.1.0 > 2.1.0-beta.2 > 2.0.0).
            UpdateOffer? betaOffer = null;
            Exception? betaErr = null;
            try
            {
                var betaMgr = CreateManager(ReleaseChannel.Beta, allowDowngrade: false);
                var betaInfo = await betaMgr.CheckForUpdatesAsync().ConfigureAwait(false);
                if (betaInfo != null && ReleaseChannelInfo.CompareVersions(betaInfo.TargetFullRelease.Version.ToString(), curVer) > 0)
                    betaOffer = BuildOffer(betaMgr, betaInfo, ReleaseChannel.Beta, curVer);
            }
            catch (Exception ex)
            {
                betaErr = ex;
            }

            UpdateOffer? stableOffer = null;
            Exception? stableErr = null;
            try
            {
                var stableMgr = CreateManager(ReleaseChannel.Stable, allowDowngrade: false);
                var stableInfo = await stableMgr.CheckForUpdatesAsync().ConfigureAwait(false);
                if (stableInfo != null &&
                    !ReleaseChannelInfo.IsPrereleaseVersion(stableInfo.TargetFullRelease.Version.ToString()) &&
                    ReleaseChannelInfo.CompareVersions(stableInfo.TargetFullRelease.Version.ToString(), curVer) > 0)
                {
                    stableOffer = BuildOffer(stableMgr, stableInfo, ReleaseChannel.Stable, curVer);
                }
            }
            catch (Exception ex)
            {
                stableErr = ex;
            }

            if (betaOffer != null && stableOffer != null)
            {
                return ReleaseChannelInfo.CompareVersions(betaOffer.Version, stableOffer.Version) >= 0
                    ? betaOffer
                    : stableOffer;
            }
            if (betaOffer != null) return betaOffer;
            if (stableOffer != null) return stableOffer;

            // Falls der Beta-Feed einen echten Netzwerk-/Integritätsfehler meldete und auch Stable fehlschlug, Fehler sichtbar machen:
            if (betaErr != null && stableErr != null)
                throw betaErr;

            return null;
        }
    }

    private static UpdateOffer BuildOffer(UpdateManager mgr, UpdateInfo info, ReleaseChannel sourceChannel, string currentVersion)
    {
        var t = info.TargetFullRelease;
        string ver = t.Version.ToString();
        bool isPre = ReleaseChannelInfo.IsPrereleaseVersion(ver) || sourceChannel == ReleaseChannel.Beta;
        bool isDowngrade = ReleaseChannelInfo.CompareVersions(ver, currentVersion) < 0;
        return new UpdateOffer(
            Version: ver,
            Notes: t.NotesMarkdown,
            Native: new NativeHandle(mgr, info),
            SourceChannel: sourceChannel,
            IsPrerelease: isPre,
            IsDowngrade: isDowngrade,
            Sha256: t.SHA256,
            SizeBytes: t.Size);
    }

    public async Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct = default)
    {
        if (offer.Native is not NativeHandle handle)
            throw new InvalidOperationException("Ungültiges Update-Paket-Handle.");

        var mgr = handle.Manager;
        var target = handle.Info.TargetFullRelease;
        string? packagesDir = EffectiveLocator?.PackagesDir;

        // Unvollständige (.partial) Reste früherer abgebrochener Downloads vorab bereinigen:
        CleanupIncompletePackages(packagesDir, keepFileName: target.FileName);

        try
        {
            await mgr.DownloadUpdatesAsync(handle.Info, progress, ct).ConfigureAwait(false);
            // Explizite kryptografische Integritätsprüfung (Größe + SHA256/SHA1) vor Freigabe zur Installation:
            await VerifyDownloadedPackageIntegrityAsync(packagesDir, target, ct).ConfigureAwait(false);
        }
        catch
        {
            // Beschädigte oder abgebrochene Teil-Pakete aufräumen, aktive Installation bleibt unberührt:
            CleanupIncompletePackages(packagesDir, keepFileName: null);
            throw;
        }
    }

    internal static void CleanupIncompletePackages(string? packagesDir, string? keepFileName)
    {
        if (string.IsNullOrWhiteSpace(packagesDir) || !Directory.Exists(packagesDir))
            return;

        try
        {
            foreach (string file in Directory.GetFiles(packagesDir))
            {
                string name = Path.GetFileName(file);
                bool isPartial = name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) ||
                                 name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                bool shouldRemove = isPartial || (keepFileName == null && name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)) ||
                                    (keepFileName != null && name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase) &&
                                     !string.Equals(name, keepFileName, StringComparison.OrdinalIgnoreCase));
                if (shouldRemove)
                {
                    try { File.Delete(file); } catch { }
                }
            }
        }
        catch
        {
        }
    }

    internal static async Task VerifyDownloadedPackageIntegrityAsync(string? packagesDir, VelopackAsset target, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(packagesDir))
            return;

        string pkgPath = Path.Combine(packagesDir, target.FileName);
        if (!File.Exists(pkgPath))
            throw new IOException($"Heruntergeladenes Update-Paket fehlt ({target.FileName}).");

        var fi = new FileInfo(pkgPath);
        if (target.Size > 0 && fi.Length != target.Size)
            throw new InvalidDataException($"Paketgröße weicht ab (erwartet: {target.Size} Bytes, erhalten: {fi.Length} Bytes).");

        if (!string.IsNullOrWhiteSpace(target.SHA256))
        {
            await using var fs = new FileStream(pkgPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            byte[] hash = await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false);
            string actualHex = Convert.ToHexString(hash);
            if (!string.Equals(actualHex, target.SHA256.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Checksum mismatch (SHA256): erwartet {target.SHA256}, berechnet {actualHex}.");
        }
        else if (!string.IsNullOrWhiteSpace(target.SHA1))
        {
            await using var fs = new FileStream(pkgPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, useAsync: true);
            byte[] hash = await SHA1.HashDataAsync(fs, ct).ConfigureAwait(false);
            string actualHex = Convert.ToHexString(hash);
            if (!string.Equals(actualHex, target.SHA1.Trim(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Checksum mismatch (SHA1): erwartet {target.SHA1}, berechnet {actualHex}.");
        }
    }

    public void ApplyAndRestart(UpdateOffer offer)
    {
        if (offer.Native is not NativeHandle handle)
            throw new InvalidOperationException("Ungültiges Update-Paket-Handle.");
        handle.Manager.ApplyUpdatesAndRestart(handle.Info.TargetFullRelease);
    }

    public void ApplyOnExit(UpdateOffer offer)
    {
        if (offer.Native is not NativeHandle handle)
            throw new InvalidOperationException("Ungültiges Update-Paket-Handle.");
        handle.Manager.WaitExitThenApplyUpdates(handle.Info.TargetFullRelease, silent: true, restart: false);
    }
}

/// <summary>Attrappe für Headless-Selbsttests, Screenshots und UI-Zustandstests.</summary>
internal sealed class FakeUpdateBackend : IUpdateBackend
{
    public bool IsInstalled { get; set; } = true;
    public string CurrentVersion { get; set; } = AppVersion.Build;
    public ReleaseChannel InstalledChannel { get; set; } = ReleaseChannel.Stable;
    public UpdateOffer? Offer { get; set; }
    public UpdateOffer? StableOffer { get; set; }
    public UpdateOffer? BetaOffer { get; set; }
    public string? FailWith { get; set; }
    public string? DownloadFailWith { get; set; }
    public int Restarts, ExitInstalls, CheckCalls;
    public ReleaseChannel LastCheckedChannel { get; private set; }

    public Task<UpdateOffer?> CheckAsync(ReleaseChannel channel, CancellationToken ct = default)
    {
        CheckCalls++;
        LastCheckedChannel = channel;
        if (FailWith != null)
            return Task.FromException<UpdateOffer?>(new InvalidOperationException(FailWith));

        var candidate = channel == ReleaseChannel.Beta
            ? (BetaOffer ?? Offer)
            : (StableOffer ?? (Offer is { IsPrerelease: false } ? Offer : null));
        return Task.FromResult(candidate);
    }

    public Task DownloadAsync(UpdateOffer offer, Action<int> progress, CancellationToken ct = default)
    {
        if (DownloadFailWith != null)
        {
            progress(35);
            return Task.FromException(new IOException(DownloadFailWith));
        }
        foreach (int p in new[] { 10, 50, 100 }) progress(p);
        return Task.CompletedTask;
    }

    public void ApplyAndRestart(UpdateOffer offer) => Restarts++;
    public void ApplyOnExit(UpdateOffer offer) => ExitInstalls++;
}

internal enum UpdateStage { Idle, NotInstalled, Checking, UpToDate, Available, Downloading, Ready, Failed }

/// <summary>
/// Kanalbewusste Update-Steuerung für die NX-MIX Oberfläche.
/// Installiert niemals unangekündigt im laufenden Live-Audiobetrieb und sichert vor Kanal-Downgrades automatisch alle Benutzerdaten.
/// </summary>
internal sealed class AppUpdater
{
    private readonly IUpdateBackend _backend;
    private readonly Func<Action<Action>> _post;
    private CancellationTokenSource? _opCts;

    public AppUpdater(IUpdateBackend backend, Func<Action<Action>> post, ReleaseChannel initialChannel = ReleaseChannel.Stable)
    {
        _backend = backend;
        _post = post;
        SelectedChannel = initialChannel;
    }

    public ReleaseChannel SelectedChannel { get; private set; }
    public ReleaseChannel InstalledChannel => _backend.InstalledChannel;
    public UpdateStage Stage { get; private set; }
    public UpdateOffer? Offer { get; private set; }
    public int Progress { get; private set; }
    public string? Error { get; private set; }
    public DateTimeOffset? LastCheckedUtc { get; private set; }
    public string? LastBackupDirectory { get; private set; }
    public bool InstallOnExitPending { get; private set; }
    public bool IsInstalled => _backend.IsInstalled;
    public string CurrentVersion => _backend.CurrentVersion;
    public bool IsCurrentBetaBuild => InstalledChannel == ReleaseChannel.Beta || ReleaseChannelInfo.IsPrereleaseVersion(CurrentVersion);

    /// <summary>Nur Selbsttest/Screenshots: Arbeit sofort synchron statt im Hintergrund.</summary>
    public bool RunInline { get; set; }

    public event Action? Changed;

    private void Set(Action a) => _post()(() => { a(); Changed?.Invoke(); });

    private void Run(Func<CancellationToken, Task> work)
    {
        _opCts?.Cancel();
        _opCts?.Dispose();
        var cts = new CancellationTokenSource();
        _opCts = cts;

        if (RunInline) work(cts.Token).GetAwaiter().GetResult();
        else _ = Task.Run(() => work(cts.Token));
    }

    /// <summary>Wechselt den aktiven Update-Kanal und verwirft nicht mehr passende Update-Angebote.</summary>
    public void SetChannel(ReleaseChannel channel, bool autoCheckNow = true)
    {
        if (SelectedChannel == channel) return;
        _opCts?.Cancel();
        SelectedChannel = channel;
        Offer = null;
        Error = null;
        Progress = 0;
        InstallOnExitPending = false;
        Stage = _backend.IsInstalled ? UpdateStage.Idle : UpdateStage.NotInstalled;
        Changed?.Invoke();

        if (autoCheckNow && _backend.IsInstalled)
            Check();
    }

    /// <summary>Sucht im gewählten Kanal (Stable oder Beta) nach einer passenden Version.</summary>
    public void Check()
    {
        if (Stage is UpdateStage.Checking or UpdateStage.Downloading) return;
        if (!_backend.IsInstalled)
        {
            Stage = UpdateStage.NotInstalled;
            Changed?.Invoke();
            return;
        }

        var targetChannel = SelectedChannel;
        Stage = UpdateStage.Checking;
        Error = null;
        InstallOnExitPending = false;
        Changed?.Invoke();

        Run(async ct =>
        {
            try
            {
                var offer = await _backend.CheckAsync(targetChannel, ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                Set(() =>
                {
                    LastCheckedUtc = DateTimeOffset.UtcNow;
                    Offer = offer;
                    Stage = offer == null ? UpdateStage.UpToDate : UpdateStage.Available;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                Set(() =>
                {
                    Stage = UpdateStage.Failed;
                    Error = FormatFriendlyError(ex);
                });
            }
        });
    }

    public void Download()
    {
        var offer = Offer;
        if (offer == null || Stage != UpdateStage.Available) return;
        Stage = UpdateStage.Downloading;
        Progress = 0;
        Error = null;
        Changed?.Invoke();

        Run(async ct =>
        {
            try
            {
                await _backend.DownloadAsync(offer, p => Set(() => Progress = Math.Clamp(p, 0, 100)), ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;
                Set(() =>
                {
                    Stage = UpdateStage.Ready;
                    Progress = 100;
                });
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (ct.IsCancellationRequested) return;
                Set(() =>
                {
                    Stage = UpdateStage.Failed;
                    Error = FormatFriendlyError(ex);
                });
            }
        });
    }

    /// <summary>Erstellt vor jedem Update (insb. vor Beta → Stable Downgrade) eine vollständige Sicherung in %APPDATA%\NX-MIX\backups.</summary>
    public string? CreatePreUpdateBackup(string dataDirectory)
    {
        var offer = Offer;
        string kind = offer?.IsDowngrade == true ? "downgrade" : "update";
        string tag = $"{kind}_{CurrentVersion}_to_{offer?.Version ?? "next"}";
        LastBackupDirectory = ConfigBackupManager.CreateBackup(dataDirectory, tag);
        return LastBackupDirectory;
    }

    public void InstallNow(string? backupDataDirectory = null)
    {
        if (Stage != UpdateStage.Ready || Offer == null) return;
        if (!string.IsNullOrWhiteSpace(backupDataDirectory))
            CreatePreUpdateBackup(backupDataDirectory);
        _backend.ApplyAndRestart(Offer);
    }

    public void InstallOnExit(string? backupDataDirectory = null)
    {
        if (Stage != UpdateStage.Ready || Offer == null || InstallOnExitPending) return;
        if (!string.IsNullOrWhiteSpace(backupDataDirectory))
            CreatePreUpdateBackup(backupDataDirectory);
        _backend.ApplyOnExit(Offer);
        InstallOnExitPending = true;
        Changed?.Invoke();
    }

    public static string FormatFriendlyError(Exception ex)
    {
        if (ex is HttpRequestException hre)
        {
            return hre.StatusCode.HasValue
                ? $"GitHub-Server antwortete mit HTTP {(int)hre.StatusCode.Value}. Bitte Verbindung oder Release-Feed prüfen."
                : "Keine Verbindung zu GitHub möglich. Bitte Internetverbindung prüfen.";
        }
        if (ex is TaskCanceledException or TimeoutException)
            return "Zeitüberschreitung beim Abrufen des Update-Feeds.";
        string msg = ex.Message?.Trim() ?? "Unbekannter Update-Fehler.";
        if (msg.Contains("checksum", StringComparison.OrdinalIgnoreCase) || msg.Contains("hash", StringComparison.OrdinalIgnoreCase))
            return "Integritätsprüfung (SHA256) fehlgeschlagen: Paket unvollständig oder beschädigt. Aktive Installation blieb unverändert.";
        return msg;
    }
}

internal static class AppVersion
{
    /// <summary>Version dieses Builds (ohne "+commit"), z. B. "2.0.0" oder "2.1.0-beta.1".</summary>
    public static string Build
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            return v?.Split('+')[0] ?? "dev";
        }
    }

    public static bool IsBetaBuild => ReleaseChannelInfo.IsPrereleaseVersion(Build);
}
