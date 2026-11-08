using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NxMix.App.Model;
using NxMix.App.Native;
using NxMix.App.Pages;
using NxMix.App.Ui;
using NxMix.Daemon;
using NxMix.Engine;
using NxMix.Ipc;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.WIC;
using Velopack;

namespace NxMix.App;

internal sealed class HeadlessSession : IDisposable
{
    public DaemonEngineController Controller { get; }
    public AppModel Model { get; }
    public string TempDirectory { get; }

    public HeadlessSession(DaemonEngineController controller, AppModel model, string tempDirectory)
    {
        Controller = controller;
        Model = model;
        TempDirectory = tempDirectory;
    }

    public void Dispose()
    {
        try { Controller.Dispose(); } catch { }
        try
        {
            if (Directory.Exists(TempDirectory))
                Directory.Delete(TempDirectory, recursive: true);
        }
        catch { }
    }
}

internal static class Program
{
    sealed class Options
    {
        public string? Shot, SelfTest, DataDir, Show, UpdateTest, Channel;
        public string Page = "mixer";
        public int W = 1600, H = 900;
        public float Scale = 1f;
        public int UiScale = 100;
        public bool Demo, HostRunning;
        public string? Restorer;
        public string Theme = "dark";
        public bool Touch, Expanded;
        public string? Update;
        public double T = 4.2;
        public int ExitAfterMs;
    }

    static Options Parse(string[] a)
    {
        var o = new Options();
        string? Next(ref int i) => i + 1 < a.Length ? a[++i] : null;
        for (int i = 0; i < a.Length; i++)
        {
            switch (a[i].ToLowerInvariant())
            {
                case "--shot": o.Shot = Next(ref i); break;
                case "--page": o.Page = (Next(ref i) ?? "mixer").ToLowerInvariant(); break;
                case "--size":
                    {
                        var s = (Next(ref i) ?? "").ToLowerInvariant().Split('x');
                        if (s.Length == 2 && int.TryParse(s[0], out int w) && int.TryParse(s[1], out int h)) { o.W = w; o.H = h; }
                        break;
                    }
                case "--scale": float.TryParse(Next(ref i), NumberStyles.Float, CultureInfo.InvariantCulture, out o.Scale); break;
                case "--ui-scale": int.TryParse(Next(ref i), out o.UiScale); break;
                case "--t": double.TryParse(Next(ref i), NumberStyles.Float, CultureInfo.InvariantCulture, out o.T); break;
                case "--empty": o.Demo = false; break;
                case "--demo": o.Demo = true; break;
                case "--host-running": o.HostRunning = true; break;
                case "--show": o.Show = Next(ref i); break;
                case "--selftest": o.SelfTest = Next(ref i); break;
                case "--update-check-test": o.UpdateTest = Next(ref i); break;
                case "--channel": o.Channel = Next(ref i); break;
                case "--data-dir": o.DataDir = Next(ref i); break;
                case "--exit-after": int.TryParse(Next(ref i), out o.ExitAfterMs); break;
                case "--engine": _ = Next(ref i); break;
                case "--restorer": o.Restorer = Next(ref i); break;
                case "--theme": o.Theme = (Next(ref i) ?? "dark").ToLowerInvariant(); break;
                case "--touch": o.Touch = true; break;
                case "--expanded": o.Expanded = true; break;
                case "--update": o.Update = Next(ref i); break;
            }
        }
        return o;
    }

    [STAThread]
    static int Main(string[] args)
    {
        try
        {
            bool launchedByInstaller = false;
            VelopackApp.Build()
                .OnFirstRun(_ => launchedByInstaller = true)
                .OnRestarted(_ => launchedByInstaller = true)
                .OnBeforeUpdateFastCallback(_ => StopDaemonForInstaller(removeAutostart: false))
                .OnBeforeUninstallFastCallback(_ => StopDaemonForInstaller(removeAutostart: true))
                .Run();

            var o = Parse(args);
            if (o.DataDir != null) UiPrefs.DataDirectory = o.DataDir;
            if (o.Shot != null) return RunShot(o);
            if (o.SelfTest != null) return SelfTest.Run(o.SelfTest);
            if (o.UpdateTest != null) return RunUpdateTest(o.UpdateTest, o.Channel);
            if (launchedByInstaller) NativeDriverSetup.InstallIfNeeded();
            return RunWindow(o);
        }
        catch (Exception ex)
        {
            LogCrash(ex);
            return 1;
        }
    }

    static void StopDaemonForInstaller(bool removeAutostart)
    {
        try
        {
            if (removeAutostart)
                NxMix.Daemon.Persistence.SystemConfigStore.RemoveWindowsLogonAutostart();

            // 1. Sauberes IPC-Shutdown an laufenden Standard-Daemon senden (hebt App-Loopbacks auf & sichert Mix)
            try
            {
                using var client = new NamedPipeBackendClient();
                client.ConnectAsync(TimeSpan.FromMilliseconds(900)).GetAwaiter().GetResult();
                if (client.IsConnected)
                    client.RequestDaemonShutdownAsync().GetAwaiter().GetResult();
            }
            catch { }

            // 2. Warten bis alle NxMix.Daemon-Prozesse im selben Installationsstamm beendet sind
            string installRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, ".."));
            var sw = Stopwatch.StartNew();
            foreach (var proc in Process.GetProcessesByName("NxMix.Daemon"))
            {
                using (proc)
                {
                    try
                    {
                        string? exePath = proc.MainModule?.FileName;
                        if (exePath == null || !exePath.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase))
                            continue;

                        int remain = Math.Max(250, 4000 - (int)sw.ElapsedMilliseconds);
                        if (!proc.WaitForExit(remain))
                            proc.Kill();
                    }
                    catch { }
                }
            }
        }
        catch { }
    }

    static int RunUpdateTest(string outFile, string? channelArg)
    {
        var log = new StringBuilder();
        int code = 1;
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(VelopackUpdateBackend.SourceOverrideVariable)))
            log.AppendLine($"ABBRUCH: {VelopackUpdateBackend.SourceOverrideVariable} ist nicht gesetzt");
        else
        {
            var initialChannel = string.Equals(channelArg, "beta", StringComparison.OrdinalIgnoreCase)
                ? ReleaseChannel.Beta
                : ReleaseChannel.Stable;
            var updater = new AppUpdater(new VelopackUpdateBackend(), () => a => a(), initialChannel) { RunInline = true };
            log.AppendLine($"Installiert: {updater.IsInstalled}  Version: {updater.CurrentVersion}  Kanal: {updater.SelectedChannel}");
            updater.Check();
            log.AppendLine($"Suche: {updater.Stage} {updater.Offer?.Version} (Downgrade={updater.Offer?.IsDowngrade}) {updater.Error}");
            if (updater.Stage == UpdateStage.Available)
            {
                updater.Download();
                log.AppendLine($"Laden: {updater.Stage} {updater.Progress} % {updater.Error}");
                updater.InstallOnExit();
                log.AppendLine($"Beim Beenden installieren: {updater.InstallOnExitPending}");
                if (updater.InstallOnExitPending) code = 0;
            }
        }
        File.WriteAllText(outFile, log.ToString());
        return code;
    }

    static string? ComputeCustomKey(string? dataDir)
    {
        if (string.IsNullOrWhiteSpace(dataDir)) return null;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataDir).ToUpperInvariant()));
        return "dir-" + Convert.ToHexString(hash)[..12].ToLowerInvariant();
    }

    static string QuoteArg(string value) =>
        "\"" + value.Replace("\"", "\\\"") + "\"";

    // ---- Regulärer Start (verbindet sich ohne Bestätigungsdialoge mit NxMix.Daemon.exe) --------
    static int RunWindow(Options o)
    {
        string? customKey = ComputeCustomKey(o.DataDir);

        // Einzelinstanz-Schutz für das Konsolenfenster (Tray-/Shortcut-Doppelklick holt bestehendes Fenster nach vorn)
        Mutex? uiMutex = null;
        if (o.ExitAfterMs <= 0)
        {
            uiMutex = new Mutex(initiallyOwned: true, IpcNames.UiWindowMutexName(customKey), out bool createdNew);
            if (!createdNew)
            {
                uiMutex.Dispose();
                if (customKey == null)
                {
                    nint existingHwnd = Win32.FindWindow("NxMixMainWindow", null);
                    if (existingHwnd != 0)
                    {
                        if (Win32.IsIconic(existingHwnd))
                            Win32.ShowWindow(existingHwnd, Win32.SW_RESTORE);
                        Win32.SetForegroundWindow(existingHwnd);
                    }
                }
                return 0;
            }
        }

        try
        {
            var prefs = UiPrefs.Load();

            var client = new NamedPipeBackendClient(customKey, async ct =>
            {
                string daemonExe = Path.Combine(AppContext.BaseDirectory, "NxMix.Daemon.exe");
                if (!File.Exists(daemonExe)) return;

                var argParts = new List<string>();
                if (!string.IsNullOrWhiteSpace(o.DataDir))
                {
                    argParts.Add("--data-dir");
                    argParts.Add(QuoteArg(Path.GetFullPath(o.DataDir)));
                }
                if (!string.IsNullOrWhiteSpace(customKey))
                {
                    argParts.Add("--key");
                    argParts.Add(QuoteArg(customKey));
                }

                // UseShellExecute = true löst NxMix.Daemon.exe vollständig vom Eltern-Job/Konsolen-Handle ab
                var psi = new ProcessStartInfo(daemonExe)
                {
                    WorkingDirectory = AppContext.BaseDirectory,
                    UseShellExecute = true,
                    Arguments = string.Join(" ", argParts),
                };

                try
                {
                    Process.Start(psi);
                    await Task.Delay(260, ct).ConfigureAwait(false);
                }
                catch { }
            });

            // Kurz auf den ersten Laufzeit-Snapshot warten, damit das Fenster direkt vollständig gefüllt öffnet
            client.ConnectAsync(TimeSpan.FromSeconds(4)).GetAwaiter().GetResult();

            var model = new AppModel(client);
            var win = new MainWindow(model, prefs.Window, o.ExitAfterMs, prefs, new VelopackUpdateBackend());
            if (model.IsConnected && model.Config.AutoStartAudioOnBoot && !model.HostRunning &&
                model.Runtime.AudioStartBlockedReason == null)
                model.StartAudio();
            return win.Run();
        }
        finally
        {
            if (uiMutex != null)
            {
                try { uiMutex.ReleaseMutex(); } catch { }
                uiMutex.Dispose();
            }
        }
    }

    // ---- Deterministische Headless-Sitzung für --shot und --selftest ---------------------------
    public static HeadlessSession CreateHeadlessSession(bool demo, bool hostRunning, bool restorerSupported = true, string? restorerReason = null)
    {
        UiPrefs.PersistenceEnabled = false;
        string tempDir = Path.Combine(Path.GetTempPath(), "nxmix-hl-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(tempDir);

        SystemConfig cfg;
        MixerSettings mix;
        UiDeckCollection decks;
        if (demo)
        {
            (cfg, mix, decks) = DemoProject.Create(runningOnBoot: hostRunning);
        }
        else
        {
            cfg = new SystemConfig { AutoStartAudioOnBoot = hostRunning, CompanionApiEnabled = false };
            mix = MixerSettings.Create(48000, 0, 0);
            decks = new UiDeckCollection();
        }

        var controller = new DaemonEngineController(new DaemonEngineOptions
        {
            CustomDataDir = tempDir,
            CustomIpcKey = "hl-" + Guid.NewGuid().ToString("N")[..8],
            DeviceCatalog = new DemoCatalog(),
            AppCatalog = new DemoAppCatalog(),
            AppRouteRestorer = new DemoRestorer(restorerSupported, restorerReason),
            HostFactory = () => new DemoHost(initiallyRunning: false),
            InitialSystemConfigOverride = cfg,
            InitialMixerOverride = mix,
        });

        if (demo)
        {
            // Zwei Beispiel-Snapshots anlegen, damit TopBar und Snapshots-Tab realistisch gefüllt sind
            controller.SaveSnapshotAsync(null, "Streaming Live", "Standard-Sendemischung mit Musik-Bus", "#3B82F6", 150, SnapshotScopeFlags.All)
                .GetAwaiter().GetResult();
            controller.SaveSnapshotAsync(null, "Talk-Runde", "Musik abgesenkt, Fokus auf Mikrofone", "#10B981", 500, SnapshotScopeFlags.All)
                .GetAwaiter().GetResult();
        }

        if (hostRunning && controller.Snapshot.HostStats.State != Audio.HostState.Running)
            controller.StartAudioAsync().GetAwaiter().GetResult();

        controller.PumpTelemetryNow();

        var model = new AppModel(controller, decks)
        {
            RunInline = true,
            Post = a => a(),
        };
        return new HeadlessSession(controller, model, tempDir);
    }

    static int ParsePageName(string page) => page switch
    {
        "mixer" or "0" => UiRoot.TabMixer,
        "decks" or "1" => UiRoot.TabDecks,
        "devices" or "geraete" or "2" => UiRoot.TabDevices,
        "matrix" or "patching" or "3" => UiRoot.TabMatrix,
        "vaio" or "virtual-io" or "virtualio" or "4" => UiRoot.TabVirtualIo,
        "dsp" or "outputs" or "outputs-dsp" or "5" => UiRoot.TabOutputsDsp,
        "snapshots" or "scenes" or "6" => UiRoot.TabSnapshots,
        "engine" or "settings" or "7" => UiRoot.TabEngine,
        _ => UiRoot.TabMixer,
    };

    static int RunShot(Options o)
    {
        bool restorerOk = !string.Equals(o.Restorer, "unsupported", StringComparison.OrdinalIgnoreCase);
        using var session = CreateHeadlessSession(
            o.Demo,
            o.HostRunning,
            restorerOk,
            restorerOk ? null : "Demo: Diese Windows-Version unterstützt kein ProcessLoopback.");

        var prefs = new UiPrefs
        {
            Theme = o.Theme == "light" ? ThemeMode.Light : ThemeMode.Dark,
            Density = o.Touch ? Density.Touch : Density.Mouse,
            SidebarExpanded = o.Expanded,
            UiScalePercent = UiPrefs.ClampScale(o.UiScale),
        };
        var fake = new FakeUpdateBackend { Offer = o.Update != null ? new UpdateOffer("0.2.0", "## NX-MIX 0.2.0\n\n- Live-Konsolen-Architektur") : null };
        var root = new UiRoot(session.Model, new NullShell(), prefs, fake);
        root.Updater.RunInline = true;
        if (o.Update != null) { root.Updater.Check(); if (o.Update == "ready") root.Updater.Download(); }

        float effectiveScale = o.Scale * prefs.ScaleFactor;
        float dipW = o.W / effectiveScale, dipH = o.H / effectiveScale;
        root.Resize(dipW, dipH);
        root.SwitchPage(ParsePageName(o.Page));

        for (int k = 0; k < 45; k++) root.Tick(1f / 60f);
        if (o.Show != null) ShowOverlay(root, o.Show);

        RenderRoot(root, o.W, o.H, effectiveScale, o.Shot);
        return 0;
    }

    public static void RenderRoot(UiRoot root, int pw, int ph, float scale, string? pngPath)
    {
        float dpi = 96f * scale;
        using var wic = new IWICImagingFactory();
        using var bmp = wic.CreateBitmap((uint)pw, (uint)ph, Vortice.WIC.PixelFormat.Format32bppPBGRA, BitmapCreateCacheOption.CacheOnLoad);
        var props = new RenderTargetProperties(RenderTargetType.Default,
            new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied), dpi, dpi, RenderTargetUsage.None, FeatureLevel.Default);
        using (var rt = Renderer.D2D.CreateWicBitmapRenderTarget(bmp, props))
        using (var g = new Renderer(rt, scale, false))
        {
            rt.BeginDraw();
            root.Paint(g);
            rt.EndDraw().CheckError();
        }
        if (pngPath == null) return;
        string full = Path.GetFullPath(pngPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        using var stream = wic.CreateStream(full, FileAccess.Write);
        using var enc = wic.CreateEncoder(ContainerFormatGuids.Png);
        enc.Initialize(stream);
        using var frame = enc.CreateNewFrame(out _);
        frame.Initialize(null);
        frame.SetSize((uint)pw, (uint)ph);
        var pf = Vortice.WIC.PixelFormat.Format32bppPBGRA;
        frame.SetPixelFormat(ref pf);
        frame.WriteSource(bmp);
        frame.Commit();
        enc.Commit();
    }

    public static ChannelSheet? Sheet(UiRoot root, bool input, int index)
    {
        root.SwitchPage(UiRoot.TabMixer);
        return ((MixerPage)root.Pages[UiRoot.TabMixer]).OpenSheet(input, Math.Clamp(index, 0, Math.Max(0, (input ? root.Model.InputCount : root.Model.OutputCount) - 1)));
    }

    public static void Picker(UiRoot root, int input, bool manual = false)
    {
        var s = Sheet(root, true, input);
        for (int i = 0; i < 15; i++) root.Tick(1f / 60f);
        s?.OpenPicker(manual);
    }

    static void ShowOverlay(UiRoot root, string what)
    {
        switch (what)
        {
            case "snapshots-menu": root.ShowQuickSnapshotMenu(root.Top.Bounds); break;
            case "sheet-out": Sheet(root, false, 2); break;
            case "sheet-in": Sheet(root, true, 6); break;
            case "eq-out": root.OpenOutputDsp(0); break;
            case "rename": Sheet(root, true, 0)?.BeginRename(); break;
            case "applink-picker": Picker(root, 6); break;
            case "applink-manual": Picker(root, 6, true); break;
            case "update": root.ShowUpdate(); break;
            case "tooltip":
                {
                    var strips = ((MixerPage)root.Pages[UiRoot.TabMixer]).OutputChannelStrips;
                    if (strips.Count > 0)
                    {
                        var b = strips[0].Bounds;
                        root.MouseMove(b.CX, b.CY, false, false, false);
                        for (int i = 0; i < 60; i++) root.Tick(1f / 60f);
                    }
                    break;
                }
        }
        root.Tick(1f / 60f);
    }

    public static void LogCrash(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "nx-mix-error.log"), $"[{DateTime.Now:s}] {ex}\n\n");
        }
        catch { }
    }
}
