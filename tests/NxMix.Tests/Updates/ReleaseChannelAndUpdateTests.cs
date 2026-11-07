using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Daemon;
using NxMix.Daemon.Persistence;
using NxMix.Engine;
using NxMix.Ipc;
using Velopack;
using Velopack.Locators;

namespace NxMix.Tests.Updates;

/// <summary>
/// Deckt sämtliche 10 Qualitätssicherungspunkte des Stable-/Beta-Release-Systems sowie die gefundenen
/// Integrationspunkte (Persistenz, Backup/Restore, Snapshot-Rename, Out-of-Process-Daemon-Shutdown) ab.
/// </summary>
public sealed class ReleaseChannelAndUpdateTests : IDisposable
{
    private readonly string _tempRoot;

    public ReleaseChannelAndUpdateTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "nxmix-reltests-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        UiPrefs.PersistenceEnabled = true;
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch
        {
        }
    }

    private TestVelopackLocator CreateInstalledLocator(string version, string velopackChannel, out string packagesDir)
    {
        string installRoot = Path.Combine(_tempRoot, "install-" + Guid.NewGuid().ToString("N")[..8]);
        string appDir = Path.Combine(installRoot, "current");
        packagesDir = Path.Combine(installRoot, "packages");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(packagesDir);

        string updateExe = Path.Combine(installRoot, "Update.exe");
        File.WriteAllBytes(updateExe, new byte[] { 0x4D, 0x5A, 0x90, 0x00 }); // gültiger Dummy-Header für Existenzprüfung

        return new TestVelopackLocator(
            "NxMix",
            version,
            packagesDir,
            appDir,
            installRoot,
            updateExe,
            velopackChannel,
            null,
            null,
            "Velopack.NxMix");
    }

    private static (string NupkgPath, string Sha256Hex, long Size) WriteFakeReleaseFeed(
        string feedDir,
        string velopackChannel,
        params (string Version, string Notes, byte[] Payload)[] releases)
    {
        Directory.CreateDirectory(feedDir);
        var assets = new List<object>();
        string lastPath = "";
        string lastSha256 = "";
        long lastSize = 0;

        foreach (var rel in releases)
        {
            string fileName = $"NxMix-{rel.Version}-{velopackChannel}-full.nupkg";
            string fullPath = Path.Combine(feedDir, fileName);
            File.WriteAllBytes(fullPath, rel.Payload);

            string sha1 = Convert.ToHexString(SHA1.HashData(rel.Payload));
            string sha256 = Convert.ToHexString(SHA256.HashData(rel.Payload));

            assets.Add(new
            {
                PackageId = "NxMix",
                Version = rel.Version,
                Type = "Full",
                FileName = fileName,
                SHA1 = sha1,
                SHA256 = sha256,
                Size = (long)rel.Payload.Length,
                NotesMarkdown = rel.Notes,
                NotesHTML = $"<p>{rel.Notes}</p>",
            });

            lastPath = fullPath;
            lastSha256 = sha256;
            lastSize = rel.Payload.Length;
        }

        string feedJson = JsonSerializer.Serialize(new { Assets = assets });
        File.WriteAllText(Path.Combine(feedDir, $"releases.{velopackChannel}.json"), feedJson, Encoding.UTF8);
        return (lastPath, lastSha256, lastSize);
    }

    // ---------------------------------------------------------------------------------------------
    // QA 1 & QA 2: Stable erkennt ausschließlich freigegebene Stable-Updates; Beta erkennt Beta-Updates
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task StableIgnoresBetaFeed_And_BetaDetectsPrereleaseUpdates()
    {
        string feedDir = Path.Combine(_tempRoot, "feeds");
        WriteFakeReleaseFeed(feedDir, ReleaseChannelInfo.StableVelopackChannel,
            ("2.0.0", "Stable 2.0.0", Encoding.UTF8.GetBytes("stable-200-payload")),
            ("2.1.0-beta.1", "Versehentlicher Beta-Eintrag", Encoding.UTF8.GetBytes("accidental-beta")));

        WriteFakeReleaseFeed(feedDir, ReleaseChannelInfo.BetaVelopackChannel,
            ("2.1.0-beta.1", "Beta 1", Encoding.UTF8.GetBytes("beta-1-payload")),
            ("2.1.0-beta.2", "Beta 2 mit neuem Spektrum", Encoding.UTF8.GetBytes("beta-2-payload")));

        // Fall A: Stable 1.9.0 installiert -> Stable-Kanal darf NIEMALS 2.1.0-beta.1 akzeptieren;
        // bei reinem Stable-Feed mit 2.0.0 wird exakt 2.0.0 angeboten.
        string cleanStableDir = Path.Combine(_tempRoot, "clean-stable");
        WriteFakeReleaseFeed(cleanStableDir, ReleaseChannelInfo.StableVelopackChannel,
            ("2.0.0", "Offizielles Live-Release 2.0.0", Encoding.UTF8.GetBytes("stable-200-clean")));

        var stableLocator = CreateInstalledLocator("1.9.0", ReleaseChannelInfo.StableVelopackChannel, out _);
        var backend = new VelopackUpdateBackend(
            stablePathOverride: cleanStableDir,
            betaPathOverride: feedDir,
            locator: stableLocator);

        var stableOffer = await backend.CheckAsync(ReleaseChannel.Stable);
        Assert.NotNull(stableOffer);
        Assert.Equal("2.0.0", stableOffer!.Version);
        Assert.False(stableOffer.IsPrerelease);
        Assert.False(stableOffer.IsDowngrade);
        Assert.Equal(ReleaseChannel.Stable, stableOffer.SourceChannel);

        // Sicherheitsprüfung: Selbst wenn im Ordner ein fehlerhaftes Prerelease läge, verwirft Stable es strikt!
        var pollutedBackend = new VelopackUpdateBackend(
            stablePathOverride: feedDir,
            betaPathOverride: feedDir,
            locator: CreateInstalledLocator("2.0.0", ReleaseChannelInfo.StableVelopackChannel, out _));
        var rejectedOffer = await pollutedBackend.CheckAsync(ReleaseChannel.Stable);
        Assert.Null(rejectedOffer);

        // Fall B: Beta-Kanal erkennt 2.1.0-beta.2 sauber als Prerelease-Update
        var betaOffer = await backend.CheckAsync(ReleaseChannel.Beta);
        Assert.NotNull(betaOffer);
        Assert.Equal("2.1.0-beta.2", betaOffer!.Version);
        Assert.True(betaOffer.IsPrerelease);
        Assert.Equal(ReleaseChannel.Beta, betaOffer.SourceChannel);
    }

    // ---------------------------------------------------------------------------------------------
    // QA 3 & QA 4: Kanalwahl bleibt über Neustarts persistent & manuelle Update-Prüfung nutzt Kanal
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void ChannelSelectionPersistsAcrossRestart_And_ManualCheckUsesSelectedChannel()
    {
        string dataDir = Path.Combine(_tempRoot, "prefs-persist");
        Directory.CreateDirectory(dataDir);
        UiPrefs.DataDirectory = dataDir;
        UiPrefs.PersistenceEnabled = true;

        var fake = new FakeUpdateBackend
        {
            IsInstalled = true,
            CurrentVersion = "2.0.0",
            InstalledChannel = ReleaseChannel.Stable,
            StableOffer = null,
            BetaOffer = new UpdateOffer("2.1.0-beta.1", "Beta Notes", SourceChannel: ReleaseChannel.Beta, IsPrerelease: true),
        };

        using var session = NxMix.App.Program.CreateHeadlessSession(demo: false, hostRunning: false);
        // CreateHeadlessSession schaltet PersistenceEnabled aus -> für diesen Persistenztest gezielt aktivieren:
        UiPrefs.DataDirectory = dataDir;
        UiPrefs.PersistenceEnabled = true;

        var prefs1 = UiPrefs.Load();
        var root1 = new UiRoot(session.Model, new NullShell(), prefs1, fake);
        root1.Updater.RunInline = true;

        Assert.Equal(ReleaseChannel.Stable, root1.Updater.SelectedChannel);
        root1.SetUpdateChannel(ReleaseChannel.Beta, autoCheckNow: false);
        root1.SetAutoUpdateCheck(false);

        // Manuelle Prüfung im gewählten Beta-Kanal
        root1.Updater.Check();
        Assert.Equal(ReleaseChannel.Beta, fake.LastCheckedChannel);
        Assert.Equal(UpdateStage.Available, root1.Updater.Stage);
        Assert.Equal("2.1.0-beta.1", root1.Updater.Offer?.Version);

        // Neustart simulieren: UiPrefs neu von Platte laden
        var prefsReloaded = UiPrefs.Load();
        Assert.True(prefsReloaded.UpdateChannelChosen);
        Assert.Equal(ReleaseChannel.Beta, prefsReloaded.UpdateChannel);
        Assert.False(prefsReloaded.AutoUpdateCheck);

        var root2 = new UiRoot(session.Model, new NullShell(), prefsReloaded, fake);
        Assert.Equal(ReleaseChannel.Beta, root2.Updater.SelectedChannel);
    }

    // ---------------------------------------------------------------------------------------------
    // QA 6: SemVer-2.0-Versionsvergleich und strikte Kanal-Tag-Validierung
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public void SemVerComparison_And_StrictChannelValidation_AreCorrect()
    {
        Assert.True(ReleaseChannelInfo.CompareVersions("2.0.0", "2.1.0-beta.1") < 0);
        Assert.True(ReleaseChannelInfo.CompareVersions("2.1.0-beta.1", "2.1.0-beta.2") < 0);
        Assert.True(ReleaseChannelInfo.CompareVersions("2.1.0-beta.2", "2.1.0-rc.1") < 0);
        Assert.True(ReleaseChannelInfo.CompareVersions("2.1.0-rc.1", "2.1.0") < 0);
        Assert.Equal(0, ReleaseChannelInfo.CompareVersions("v2.0.0+abc123", "2.0.0"));

        // Stable erlaubt ausschließlich X.Y.Z
        Assert.True(ReleaseChannelInfo.IsValidVersionForChannel("2.0.0", ReleaseChannel.Stable));
        Assert.True(ReleaseChannelInfo.IsValidVersionForChannel("v2.3.1", ReleaseChannel.Stable));
        Assert.False(ReleaseChannelInfo.IsValidVersionForChannel("2.1.0-beta.1", ReleaseChannel.Stable));
        Assert.False(ReleaseChannelInfo.IsValidVersionForChannel("2.1.0-rc.1", ReleaseChannel.Stable));

        // Beta erfordert Prerelease-Suffix
        Assert.True(ReleaseChannelInfo.IsValidVersionForChannel("2.1.0-beta.1", ReleaseChannel.Beta));
        Assert.True(ReleaseChannelInfo.IsValidVersionForChannel("v2.1.0-rc.2", ReleaseChannel.Beta));
        Assert.False(ReleaseChannelInfo.IsValidVersionForChannel("2.0.0", ReleaseChannel.Beta));
    }

    // ---------------------------------------------------------------------------------------------
    // QA 8: Beta -> Stable Downgrade, automatisches Backup & verlustfreie Konfigurations-Persistenz
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task BetaToStableDowngrade_CreatesBackup_PreservesSnapshots_And_KeepsUnknownJsonFields()
    {
        string feedDir = Path.Combine(_tempRoot, "downgrade-feed");
        byte[] stablePayload = Encoding.UTF8.GetBytes("official-stable-2.0.0-package-bytes");
        WriteFakeReleaseFeed(feedDir, ReleaseChannelInfo.StableVelopackChannel,
            ("2.0.0", "Offizielles Stable 2.0.0", stablePayload));

        var betaLocator = CreateInstalledLocator("2.1.0-beta.2", ReleaseChannelInfo.BetaVelopackChannel, out _);
        var backend = new VelopackUpdateBackend(stablePathOverride: feedDir, betaPathOverride: feedDir, locator: betaLocator);

        Assert.Equal(ReleaseChannel.Beta, backend.InstalledChannel);

        // Wechsel auf Stable muss 2.0.0 als kontrolliertes Downgrade erkennen
        var offer = await backend.CheckAsync(ReleaseChannel.Stable);
        Assert.NotNull(offer);
        Assert.Equal("2.0.0", offer!.Version);
        Assert.True(offer.IsDowngrade);
        Assert.False(offer.IsPrerelease);

        await backend.DownloadAsync(offer, _ => { });

        // Benutzerdatenverzeichnis mit SystemConfig (inkl. unbekanntem zukünftigen Beta-Feld!), LiveState, Decks & Snapshot anlegen
        string dataDir = Path.Combine(_tempRoot, "user-data");
        var paths = new DaemonPaths(dataDir);

        string futureSysJson = """
            {
              "schemaVersion": 2,
              "sampleRate": 48000,
              "blockFrames": 256,
              "experimentalBetaSpatialFeature": { "enabled": true, "mode": "binaural" }
            }
            """;
        File.WriteAllText(paths.SystemConfigPath, futureSysJson, Encoding.UTF8);

        var snapMgr = new SnapshotManager(paths);
        var savedSnap = snapMgr.Save(null, "Sonntag-Live", "Hauptszene", "#10B981", 250.0, SnapshotScopeFlags.All, MixerSettings.Create(48000, 2, 2));

        // Vorwärtskompatibles Laden & Speichern durch ältere Stable-Version darf unbekannte Beta-Felder NICHT löschen
        var sysStore = new SystemConfigStore(paths);
        var loadedCfg = sysStore.Load();
        Assert.NotNull(loadedCfg.ExtensionData);
        Assert.True(loadedCfg.ExtensionData!.ContainsKey("experimentalBetaSpatialFeature"));
        sysStore.Save(loadedCfg with { BlockFrames = 128 });

        string roundtrippedJson = File.ReadAllText(paths.SystemConfigPath, Encoding.UTF8);
        Assert.Contains("experimentalBetaSpatialFeature", roundtrippedJson);
        Assert.Contains("binaural", roundtrippedJson);

        // Automatisches Pre-Downgrade-Backup erzeugen & Wiederherstellung prüfen
        string? backupDir = ConfigBackupManager.CreateBackup(dataDir, "downgrade_2.1.0-beta.2_to_2.0.0");
        Assert.False(string.IsNullOrWhiteSpace(backupDir));
        Assert.True(File.Exists(Path.Combine(backupDir!, "system-config.json")));
        Assert.True(File.Exists(Path.Combine(backupDir, "snapshots", $"{savedSnap.Id:D}.json")));

        // Beschädigte Datei simulieren -> muss als .corrupt-*.bak gesichert werden, Backup stellt alles wieder her
        File.WriteAllText(paths.SystemConfigPath, "{ kaputtes json !!!", Encoding.UTF8);
        var fallbackCfg = sysStore.Load();
        Assert.Equal(48000, fallbackCfg.SampleRate);
        Assert.NotEmpty(Directory.GetFiles(dataDir, "system-config.json.corrupt-*.bak"));

        Assert.True(ConfigBackupManager.RestoreBackup(backupDir, dataDir));
        var restoredSnaps = snapMgr.ListSummaries();
        Assert.Contains(restoredSnaps, s => s.Id == savedSnap.Id && s.Name == "Sonntag-Live");
    }

    // ---------------------------------------------------------------------------------------------
    // QA 10: Abgebrochene / manipulierte Update-Pakete scheitern bei SHA256-Prüfung & räumen sauber auf
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task CorruptedPackageFailsIntegrityCheck_CleansUpPartialFiles_And_LeavesInstallIntact()
    {
        string feedDir = Path.Combine(_tempRoot, "corrupt-feed");
        byte[] validBytes = Encoding.UTF8.GetBytes("valid-package-content-v2.0.1");
        var (nupkgPath, _, _) = WriteFakeReleaseFeed(feedDir, ReleaseChannelInfo.StableVelopackChannel,
            ("2.0.1", "Bugfix Release", validBytes));

        // Paket nach Erstellung der Prüfsumme absichtlich manipulieren (gleiche Länge, falscher Inhalt)
        byte[] tampered = Encoding.UTF8.GetBytes("TAMPERED-pack-content-v2.0.1");
        File.WriteAllBytes(nupkgPath, tampered);

        var locator = CreateInstalledLocator("2.0.0", ReleaseChannelInfo.StableVelopackChannel, out string packagesDir);
        // Alten abgebrochenen .partial-Rest ablegen
        string stalePartial = Path.Combine(packagesDir, "NxMix-2.0.0-win-full.nupkg.partial");
        File.WriteAllText(stalePartial, "interrupted");

        var backend = new VelopackUpdateBackend(stablePathOverride: feedDir, locator: locator);
        var updater = new AppUpdater(backend, () => a => a(), ReleaseChannel.Stable) { RunInline = true };

        updater.Check();
        Assert.Equal(UpdateStage.Available, updater.Stage);

        updater.Download();
        Assert.Equal(UpdateStage.Failed, updater.Stage);
        Assert.False(string.IsNullOrWhiteSpace(updater.Error));
        Assert.False(File.Exists(stalePartial));
        Assert.Empty(Directory.GetFiles(packagesDir, "*.nupkg"));
        Assert.Equal("2.0.0", updater.CurrentVersion);
    }

    // ---------------------------------------------------------------------------------------------
    // Integrationsfix I-5: Snapshot umbenennen darf CapturedState eines inaktiven Snapshots NICHT überschreiben
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task RenameSnapshot_PreservesOriginalCapturedMixState()
    {
        using var controller = new DaemonEngineController(new DaemonEngineOptions
        {
            CustomDataDir = Path.Combine(_tempRoot, "snap-rename"),
            CustomIpcKey = "sr-" + Guid.NewGuid().ToString("N")[..8],
        });

        var baseMix = MixerSettings.Create(48000, 2, 2);
        var inA = baseMix.Inputs.ToArray();
        inA[0] = inA[0] with { GainDb = -3.5 };
        var mixA = baseMix with { Inputs = inA };
        await controller.ApplyMixerAsync(mixA);
        var snap = await controller.SaveSnapshotAsync(null, "Ursprung", "Notiz", "#3B82F6", 150, SnapshotScopeFlags.All);

        // Live-Mix stark verändern (-24 dB)
        var inB = controller.Snapshot.Mixer.Inputs.ToArray();
        inB[0] = inB[0] with { GainDb = -24.0 };
        var mixB = controller.Snapshot.Mixer with { Inputs = inB };
        await controller.ApplyMixerAsync(mixB);

        // Snapshot umbenennen -> gespeicherter GainDb muss -3.5 dB bleiben!
        var renamed = await controller.RenameSnapshotAsync(snap.Id, "Umbenannte Szene");
        Assert.NotNull(renamed);
        Assert.Equal("Umbenannte Szene", renamed!.Name);
        Assert.Equal(-3.5, renamed.CapturedState.Inputs[0].GainDb, precision: 3);
    }

    // ---------------------------------------------------------------------------------------------
    // QA 7 & Integrationsprüfung: Echter Out-of-Process NxMix.Daemon.exe Lebenszyklus & Update-Shutdown
    // ---------------------------------------------------------------------------------------------
    [Fact]
    public async Task OutOfProcessDaemon_SurvivesClientDisconnect_And_ExitsCleanlyBeforeUpdate()
    {
        string daemonExe = Path.Combine(AppContext.BaseDirectory, "NxMix.Daemon.exe");
        Assert.True(File.Exists(daemonExe), $"NxMix.Daemon.exe nicht gefunden unter {daemonExe}");

        string dataDir = Path.Combine(_tempRoot, "oop-daemon");
        Directory.CreateDirectory(dataDir);
        string ipcKey = "oop-" + Guid.NewGuid().ToString("N")[..10];

        var psi = new ProcessStartInfo(daemonExe)
        {
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add("--data-dir");
        psi.ArgumentList.Add(dataDir);
        psi.ArgumentList.Add("--key");
        psi.ArgumentList.Add(ipcKey);

        using var proc = Process.Start(psi);
        Assert.NotNull(proc);

        try
        {
            using var client1 = new NamedPipeBackendClient(ipcKey);
            await client1.ConnectAsync(TimeSpan.FromSeconds(5));
            Assert.True(client1.IsConnected);
            Assert.Equal(proc!.Id, client1.Snapshot.DaemonPid);

            // 1. UI-Client trennen -> Daemon MUSS weiterlaufen
            var model1 = new AppModel(client1) { Post = a => a(), RunInline = true };
            model1.DisconnectClientOnly();
            await Task.Delay(200);
            Assert.False(proc.HasExited);

            // 2. Zweiter Client verbindet sich und führt kontrollierten Update-Shutdown aus -> Daemon beendet sich innerhalb von 5 s
            using var client2 = new NamedPipeBackendClient(ipcKey);
            await client2.ConnectAsync(TimeSpan.FromSeconds(4));
            Assert.True(client2.IsConnected);

            var model2 = new AppModel(client2) { Post = a => a(), RunInline = true };
            bool exited = model2.ShutdownDaemonExplicitly(waitForProcessExitMs: 5000);
            Assert.True(exited || proc.WaitForExit(2000));
            Assert.True(proc.HasExited);
        }
        finally
        {
            if (!proc!.HasExited)
            {
                try { proc.Kill(); } catch { }
            }
        }
    }
}
