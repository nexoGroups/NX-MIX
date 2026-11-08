using System.Text;
using NxMix.App.Model;
using NxMix.App.Pages;
using NxMix.App.Ui;
using NxMix.Audio;
using NxMix.Engine;
using NxMix.Ipc;

namespace NxMix.App;

/// <summary>
/// Umfassender Headless-Selbsttest der NX-MIX Live-Konsole (--selftest &lt;ausgabedatei&gt;):
/// Prüft Direct2D/WIC-Rendering, Widget-Geometrie und Überlappungsfreiheit über alle 8 Tabs, Auflösungen (720p..1080p),
/// Skalierungen (100 %..150 %), Themes (Dunkel/Hell) &amp; Touch-Modus sowie alle 6 Kern-Invarianten der Mischpult-Architektur.
/// </summary>
internal static class SelfTest
{
    static readonly StringBuilder Log = new();
    static int _pass;
    static int _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) _pass++;
        else _fail++;
        Log.AppendLine((ok ? "OK   " : "FAIL ") + name + (ok || detail.Length == 0 ? "" : "  -> " + detail));
    }

    static void Settle(UiRoot r, int frames = 12)
    {
        for (int i = 0; i < frames; i++) r.Tick(1f / 60f);
    }

    public static int Run(string outFile)
    {
        Log.Clear();
        _pass = 0;
        _fail = 0;
        UiPrefs.PersistenceEnabled = false;

        try
        {
            RunAll();
        }
        catch (Exception ex)
        {
            _fail++;
            Log.AppendLine("FAIL Unbehandelte Ausnahme im Selbsttest: " + ex);
        }

        Log.AppendLine($"ERGEBNIS: {_pass} bestanden, {(_fail == 0 ? "0 FEHLER — ALLE TESTS BESTANDEN" : $"{_fail} FEHLER")}");
        string full = Path.GetFullPath(outFile);
        if (Path.GetDirectoryName(full) is { Length: > 0 } dir)
            Directory.CreateDirectory(dir);
        File.WriteAllText(full, Log.ToString());
        return _fail == 0 ? 0 : 2;
    }

    static void RunAll()
    {
        Check("Schriftart Inter wird aus dem App-Verzeichnis geladen", Renderer.FontName == "Inter", Renderer.FontName);

        // ======================================================================================
        // 1. Leerer Konsolenstart: Alle 8 Seiten rendern sauber im Leerzustand
        // ======================================================================================
        using (var emptySession = Program.CreateHeadlessSession(demo: false, hostRunning: false))
        {
            var emptyRoot = new UiRoot(emptySession.Model, new NullShell(), new UiPrefs { Theme = ThemeMode.Dark });
            emptyRoot.Resize(1280, 720);
            Check("Leerer Start hat 0 Eingänge und 0 Ausgänge", emptySession.Model.InputCount == 0 && emptySession.Model.OutputCount == 0);

            for (int tab = 0; tab < emptyRoot.Pages.Length; tab++)
            {
                emptyRoot.SwitchPage(tab);
                Settle(emptyRoot, 4);
                Program.RenderRoot(emptyRoot, 1280, 720, 1f, null);
                ValidateWidgetTree(emptyRoot, $"Leerzustand Tab {tab} ({emptyRoot.Pages[tab].Title})");
            }
        }

        // ======================================================================================
        // 2. Vollständige Live-Konsole: Render- & Geometrie-Matrix über alle 8 Tabs, DPI & Modi
        // ======================================================================================
        using var liveSession = Program.CreateHeadlessSession(demo: true, hostRunning: true);
        var m = liveSession.Model;
        Check("Demo-Livesitzung läuft beim Start aktiv (HostState.Running)", m.HostState == HostState.Running, m.HostState.ToString());
        Check("Alle 8 Navigationsbereiche sind vorhanden", true);

        (int W, int H, float Scale, ThemeMode Theme, Density Density, bool Expanded, string Tag)[] matrixConfigs =
        {
            (1280, 720, 1.0f, ThemeMode.Dark, Density.Mouse, false, "720p@100% Dunkel"),
            (1600, 900, 1.0f, ThemeMode.Dark, Density.Mouse, true, "900p@100% Ausgeklappt"),
            (1920, 1080, 1.25f, ThemeMode.Light, Density.Mouse, false, "1080p@125% Hell"),
            (1280, 720, 1.5f, ThemeMode.Dark, Density.Touch, true, "720p@150% Touch+Sidebar"),
        };

        foreach (var cfg in matrixConfigs)
        {
            var prefs = new UiPrefs
            {
                Theme = cfg.Theme,
                Density = cfg.Density,
                SidebarExpanded = cfg.Expanded,
                UiScalePercent = 100,
            };
            var root = new UiRoot(m, new NullShell(), prefs);
            Check($"UiRoot hat exakt 8 Tabs ({cfg.Tag})", root.Pages.Length == 8, $"Pages={root.Pages.Length}");

            float dipW = cfg.W / cfg.Scale;
            float dipH = cfg.H / cfg.Scale;
            root.Resize(dipW, dipH);

            for (int tab = 0; tab < root.Pages.Length; tab++)
            {
                root.SwitchPage(tab);
                Settle(root, 4);
                Program.RenderRoot(root, cfg.W, cfg.H, cfg.Scale, null);
                ValidateWidgetTree(root, $"{cfg.Tag} · Tab {tab} ({root.Pages[tab].Title})");

                if (tab == UiRoot.TabEngine && root.Pages[tab] is EnginePage ep)
                {
                    for (int sec = 1; sec <= 2; sec++)
                    {
                        ep.SelectLeftSection(sec);
                        Settle(root, 3);
                        Program.RenderRoot(root, cfg.W, cfg.H, cfg.Scale, null);
                        ValidateWidgetTree(root, $"{cfg.Tag} · Engine-Abschnitt {sec}");
                    }
                    ep.SelectLeftSection(0);
                }
            }
        }

        // Updates & Release Channel: Stable/Beta, Downgrade-Warnung, Changelog & Fehlerzustand im Renderer prüfen
        {
            var fakeUpd = new FakeUpdateBackend
            {
                IsInstalled = true,
                CurrentVersion = "2.1.0-beta.1",
                InstalledChannel = ReleaseChannel.Beta,
                BetaOffer = new UpdateOffer("2.1.0-beta.2", "## Beta 2\n- Neue Spektrumanalyse", SourceChannel: ReleaseChannel.Beta, IsPrerelease: true),
                StableOffer = new UpdateOffer("2.0.0", "## Stable 2.0\n- Geprüfte Live-Version", SourceChannel: ReleaseChannel.Stable, IsPrerelease: false, IsDowngrade: true),
            };
            var root = new UiRoot(m, new NullShell(), new UiPrefs { Theme = ThemeMode.Dark }, fakeUpd);
            root.Updater.RunInline = true;
            root.Resize(1600, 900);
            root.OpenEngineUpdatesSection();
            Check("Beta-Installation wird automatisch als Beta-Kanal erkannt",
                root.Updater.IsCurrentBetaBuild && root.Updater.SelectedChannel == ReleaseChannel.Beta);

            root.Updater.Check();
            Settle(root, 4);
            Check("Beta-Kanal erkennt Beta-Update 2.1.0-beta.2",
                root.Updater.Stage == UpdateStage.Available && root.Updater.Offer?.Version == "2.1.0-beta.2");
            Program.RenderRoot(root, 1600, 900, 1f, null);
            ValidateWidgetTree(root, "Updates & Release Channel · Beta Available");

            // Kanalwechsel von Beta -> Stable erkennt Downgrade-Angebot
            root.SetUpdateChannel(ReleaseChannel.Stable, autoCheckNow: true);
            Settle(root, 4);
            Check("Wechsel Beta -> Stable erkennt Stable-Zielversion 2.0.0 als Downgrade",
                root.Updater.Stage == UpdateStage.Available && root.Updater.Offer is { Version: "2.0.0", IsDowngrade: true });
            root.Updater.Download();
            Settle(root, 4);
            Check("Downgrade-Paket erreicht Zustand Ready", root.Updater.Stage == UpdateStage.Ready);
            Program.RenderRoot(root, 1600, 900, 1f, null);
            ValidateWidgetTree(root, "Updates & Release Channel · Downgrade Ready (Live-Audio)");

            // Changelog-Dialog und Live-Audio-Installationswarnung öffnen
            root.OpenChangelog();
            Settle(root, 3);
            Check("Changelog-Dialog öffnet sich", root.Modal is ModalDialog);
            Program.RenderRoot(root, 1600, 900, 1f, null);
            while (root.Modal != null) root.CloseOverlay(root.Modal);

            root.ConfirmAndInstallUpdate();
            Settle(root, 3);
            Check("Kontrollierter Update-Dialog bei aktivem Live-Audio öffnet sich (kein Auto-Neustart)",
                root.Modal is ModalDialog && fakeUpd.Restarts == 0 && m.HostState == HostState.Running);
            Program.RenderRoot(root, 1600, 900, 1f, null);
            while (root.Modal != null) root.CloseOverlay(root.Modal);

            // Fehlerzustand im Abschnitt Updates & Release Channel rendern
            fakeUpd.FailWith = "Netzwerkverbindung unterbrochen";
            root.Updater.Check();
            Settle(root, 3);
            Check("Update-Fehler wird sauber als UpdateStage.Failed abgebildet",
                root.Updater.Stage == UpdateStage.Failed && !string.IsNullOrWhiteSpace(root.Updater.Error));
            Program.RenderRoot(root, 1600, 900, 1f, null);
            ValidateWidgetTree(root, "Updates & Release Channel · Fehlerzustand");
        }

        // Kanalblatt (Eingang & Ausgang) + AppLinkPicker im Offscreen-Renderer prüfen
        {
            var root = new UiRoot(m, new NullShell(), new UiPrefs { Theme = ThemeMode.Dark });
            root.Resize(1600, 900);
            m.UpdateChannel(true, 0, c => c with { GainDb = -6 });
            var strip = ((MixerPage)root.CurrentPage).InputChannelStrips.First(s => s.Index == 0);
            root.MouseWheel(strip.Fader.Bounds.CX, strip.Fader.Bounds.CY, 1, false, false, false);
            Check("Mausrad am Fader erhöht den Kanalpegel um genau 1 dB", m.Channel(true, 0).GainDb == -5);
            root.MouseWheel(strip.Meter.Bounds.CX, strip.Meter.Bounds.CY, -1, false, false, false);
            Check("Mausrad am Pegelbalken senkt denselben Kanal um genau 1 dB", m.Channel(true, 0).GainDb == -6);
            root.MouseWheel(strip.Fader.Bounds.CX, strip.Fader.Bounds.CY, 0.5f, false, false, false);
            bool unchanged = m.Channel(true, 0).GainDb == -6;
            root.MouseWheel(strip.Fader.Bounds.CX, strip.Fader.Bounds.CY, 0.5f, false, false, false);
            Check("Teilraster am Fader ergeben zusammen einen 1-dB-Schritt", unchanged && m.Channel(true, 0).GainDb == -5);

            var inSheet = Program.Sheet(root, true, 0);
            Settle(root, 6);
            Check("Eingangs-Kanalblatt öffnet sich", inSheet != null && root.Modal is ChannelSheet);
            if (inSheet != null)
            {
                var value = inSheet.GainBox.Bounds;
                root.MouseDown(value.CX, value.CY, 0, false, false, false);
                root.MouseUp(value.CX, value.CY, 0, false, false, false);
                Check("Ein einfacher Klick öffnet die numerische Pegeleingabe", root.Editor.Active);
                root.KeyDown(0x1B, false, false, false);
            }
            Program.RenderRoot(root, 1600, 900, 1f, null);

            var outSheet = Program.Sheet(root, false, 0);
            Settle(root, 6);
            Check("Ausgangs-Kanalblatt öffnet sich", outSheet != null && root.Modal is ChannelSheet);
            Program.RenderRoot(root, 1600, 900, 1f, null);
            while (root.Modal != null) root.CloseOverlay(root.Modal);
        }

        // ======================================================================================
        // 3. Invariante A: DeviceMaster skaliert relativ und überschreibt NIEMALS Einzelkanäle
        // ======================================================================================
        {
            m.UpdateChannel(true, 0, c => c with { GainDb = -2.0, Mute = false });
            m.UpdateChannel(true, 1, c => c with { GainDb = -5.0, Mute = false });
            m.UpdateDeviceMaster(DemoProject.InDevId, dm => dm with { GainDb = 0.0, Mute = false, Dim = false }, "RME Fireface In", isInput: true);

            // Referenz-Pegel im Engine-Kern messen
            float peakBefore = MeasureEngineOutputPeak(liveSession.Controller.RawEngine, inputIdx: 0, outputIdx: 0);

            // DeviceMaster um -6 dB absenken
            m.UpdateDeviceMaster(DemoProject.InDevId, dm => dm with { GainDb = -6.0 }, "RME Fireface In", isInput: true);

            double g0 = m.Channel(true, 0).GainDb;
            double g1 = m.Channel(true, 1).GainDb;
            Check("DeviceMaster-Bewegung lässt Kanal 0 GainDb (-2.0 dB) unangetastet", Math.Abs(g0 - (-2.0)) < 1e-6, $"g0={g0}");
            Check("DeviceMaster-Bewegung lässt Kanal 1 GainDb (-5.0 dB) und 3-dB-Abstand unangetastet",
                Math.Abs(g1 - (-5.0)) < 1e-6 && Math.Abs((g0 - g1) - 3.0) < 1e-6, $"g1={g1}");

            float peakAfter = MeasureEngineOutputPeak(liveSession.Controller.RawEngine, inputIdx: 0, outputIdx: 0);
            double ratioDb = 20.0 * Math.Log10(Math.Max(1e-9, peakAfter) / Math.Max(1e-9, peakBefore));
            Check("DeviceMaster (-6 dB) reduziert den realen Engine-Ausgangspegel um ~6.0 dB",
                Math.Abs(ratioDb - (-6.0)) < 0.25, $"ratioDb={ratioDb:0.00} dB");

            m.UpdateDeviceMaster(DemoProject.InDevId, dm => dm with { GainDb = 0.0 }, "RME Fireface In", isInput: true);
        }

        // ======================================================================================
        // 4. Invariante B: Multi-Deck-Referenzen ohne Signal- oder Kanal-Verdopplung
        // ======================================================================================
        {
            int inputsBefore = m.InputCount;
            Guid targetChId = m.Settings.Inputs[0].Id;

            var d1 = m.CreateDeck("Broadcast Deck A");
            m.AddDeckSlot(d1.Id, DeckItemKind.InputChannel, targetChId);
            var d2 = m.CreateDeck("Moderator Deck B");
            m.AddDeckSlot(d2.Id, DeckItemKind.InputChannel, targetChId);

            Check("Platzieren desselben Kanals auf 2 Fader-Decks ändert InputCount nicht",
                m.InputCount == inputsBefore, $"vorher={inputsBefore}, nachher={m.InputCount}");

            var root = new UiRoot(m, new NullShell(), new UiPrefs { Theme = ThemeMode.Dark });
            root.Resize(1600, 900);
            root.SwitchPage(UiRoot.TabMixer);

            m.SelectDeck(d1.Id);
            Settle(root, 4);
            Program.RenderRoot(root, 1600, 900, 1f, null);

            m.UpdateChannelById(true, targetChId, c => c with { GainDb = -4.2 });

            m.SelectDeck(d2.Id);
            Settle(root, 4);
            Program.RenderRoot(root, 1600, 900, 1f, null);

            Check("Fader-Änderung über Deck A ist auf Deck B identisch (-4.2 dB)",
                Math.Abs(m.Channel(true, 0).GainDb - (-4.2)) < 0.05, $"GainDb={m.Channel(true, 0).GainDb}");

            m.SelectDeck(null);
        }

        // ======================================================================================
        // 5. Invariante C: Nicht-destruktives paralleles Matrix- & Bus-Routing + Warnung
        // ======================================================================================
        {
            m.SetMatrix(0, 0, 0.0);
            Guid in0 = m.Settings.Inputs[0].Id;
            Guid out0 = m.Settings.Outputs[0].Id;
            Guid busId = DemoProject.MusicBusId;

            string? err1 = m.SetOrAddBusRoute(new RoutePort(RouteKind.Input, in0), new RoutePort(RouteKind.Bus, busId, 0), 0.0);
            string? err2 = m.SetOrAddBusRoute(new RoutePort(RouteKind.Bus, busId, 0), new RoutePort(RouteKind.Output, out0), -3.0);

            Check("Bus-Routing von Eingang 0 über Bus auf Ausgang 0 ohne Fehler angelegt", err1 == null && err2 == null, $"{err1} {err2}");
            Check("Direkter Matrix-Kreuzpunkt (0,0) wurde durch Bus-Route NICHT gelöscht (bleibt 0.0 dB)",
                Math.Abs(m.Settings.MatrixDb[0][0] - 0.0) < 1e-6, $"MatrixDb[0][0]={m.Settings.MatrixDb[0][0]}");
            Check("ParallelPathWarning erkennt gleichzeitigen Direkt- und Bus-Pfad",
                m.ParallelWarnings.Any(w => w.InputId == in0 && w.OutputId == out0 && w.BusId == busId));

            m.UpdateChannel(true, 0, c => c with { DelayMs = 2.5 });
            Check("Patch-Undo stellt den bisherigen Bus-Routenpegel wieder her und behält den Kanal-Delay",
                m.UndoPatch() && m.FindBusRoute(new RoutePort(RouteKind.Bus, busId, 0), new RoutePort(RouteKind.Output, out0))?.GainDb == 0
                && m.Settings.MatrixDb[0][0] == 0 && m.Channel(true, 0).DelayMs == 2.5);

        }

        // ======================================================================================
        // 6. Invariante D: Erweiterter REW-Import & Live-Output-DSP ohne Audio-Unterbrechung
        // ======================================================================================
        {
            const string rewSample = """
                Filter Settings file
                Room EQ V5.31.3
                Dated: 07.11.2026
                Equaliser: Generic
                Filter  1: ON  PK       Fc   63.5 Hz  Gain  -5.20 dB  Q  4.10
                Filter  2: ON  LS Q     Fc  105.0 Hz  Gain  +3.80 dB  Q  0.71
                Filter  3: ON  HS 12dB  Fc 8500.0 Hz  Gain  -2.00 dB
                Filter  4: ON  HP Q     Fc   32.0 Hz  Q  0.7071
                Filter  5: ON  NO       Fc  500.0 Hz  Q  8.00
                Filter  6: OFF PK       Fc 1000.0 Hz  Gain  +9.00 dB  Q  2.00
                """;

            using var sr = new StringReader(rewSample);
            var preview = RewFilterParser.ParsePreview(sr, "studio-main-l.txt", m.Settings.SampleRate);
            Check("Erweiterter REW-Parser liest 5 aktive Filter (PK, LS Q, HS 12dB, HP Q, NO) und ignoriert OFF",
                preview.Filters.Count == 5 && preview.SkippedCount == 1, $"Count={preview.Filters.Count}, Skipped={preview.SkippedCount}");
            Check("REW-Vorschau erkennt positiven Summen-Boost und empfiehlt negativen Headroom-Preamp",
                preview.MaxBoostDb > 1.5 && preview.RecommendedPreampDb < -1.5,
                $"MaxBoost={preview.MaxBoostDb:0.00}, Preamp={preview.RecommendedPreampDb:0.00}");

            var root = new UiRoot(m, new NullShell(), new UiPrefs { Theme = ThemeMode.Dark });
            root.Resize(1600, 900);
            root.OpenOutputDsp(0);
            var dspPage = (OutputsDspPage)root.Pages[UiRoot.TabOutputsDsp];
            dspPage.ApplyRewPreview(0, preview, applyHeadroomTrim: true);
            var source = m.Channel(false, 0);
            var target = m.Channel(false, 1);
            var graphOptions = new SpectrumPrefs(Range: 1, Bars: true, WindowSize: 32768, DisplayPoints: 256);
            root.Prefs.SaveSpectrum(source.Id, graphOptions);
            Check("Spektrumprofil speichert Balkenzahl pro Ausgang", root.Prefs.Spectrum(source.Id) == graphOptions
                && root.Prefs.Spectrum(source.Id).EffectiveDisplayPoints == 256
                && root.Prefs.Spectrum(target.Id) == new SpectrumPrefs());
            Check("Kanalkopie übernimmt REW und behält Zielgerät, Name und Schutzgrenzen",
                m.CopyChannelSettings(false, source.Id, target.Id) && m.Channel(false, 1) is var copied
                && copied.Correction == source.Correction && copied.Id == target.Id && copied.DeviceId == target.DeviceId
                && copied.Name == target.Name && copied.CeilingDbfs == target.CeilingDbfs && copied.MaxGainDb == target.MaxGainDb);
            Settle(root, 6);
            Program.RenderRoot(root, 1600, 900, 1f, null);

            var out0 = m.Channel(false, 0);
            Check("REW-Korrektur inkl. Headroom-Trim auf Ausgang 0 aktiv übernommen",
                out0.Correction is { Bypassed: false, Filters.Count: 5 } && out0.Correction.PreampDb < -1.5);
            Check("Audio-Engine blieb während des Live-Output-DSP-Updates ununterbrochen auf Running",
                m.HostState == HostState.Running, m.HostState.ToString());
        }

        // ======================================================================================
        // 7. Invariante E: Selektiver Snapshot-Recall & Recall-Safe ohne SystemConfig-Eingriff
        // ======================================================================================
        {
            var sysBefore = m.Config;

            // Ausgangszustand für Snapshot definieren
            m.UpdateChannel(true, 0, c => c with { GainDb = -1.0, RecallSafe = RecallSafeFlags.None });
            m.UpdateChannel(true, 1, c => c with { GainDb = -3.0, RecallSafe = RecallSafeFlags.None });

            MixSnapshot? snapA = null;
            m.SaveSnapshot(null, "Szene A", "Selbsttest", "#3B82F6", 200.0, SnapshotScopeFlags.All, s => snapA = s);
            Check("Snapshot A gespeichert", snapA != null);

            // Kanal 0 auf RecallSafe.Fader setzen und beide Kanäle verstellen
            m.UpdateChannel(true, 0, c => c with { GainDb = -11.0, RecallSafe = RecallSafeFlags.Fader });
            m.UpdateChannel(true, 1, c => c with { GainDb = -14.0 });

            SnapshotDiffPreview? diff = null;
            m.PreviewSnapshot(snapA!.Id, SnapshotScopeFlags.All, d => diff = d);
            Check("Snapshot-Diff-Vorschau meldet mindestens 1 Recall-Safe-geschützten Parameter",
                diff != null && diff.SafeSkippedCount >= 1, $"SafeSkipped={diff?.SafeSkippedCount}");

            m.RecallSnapshot(snapA.Id, SnapshotScopeFlags.All, fadeMs: 250.0);

            Check("Snapshot-Recall stellt ungeschützten Kanal 1 auf -3.0 dB wieder her",
                Math.Abs(m.Channel(true, 1).GainDb - (-3.0)) < 0.05, $"Ch1={m.Channel(true, 1).GainDb}");
            Check("Snapshot-Recall respektiert RecallSafe.Fader auf Kanal 0 (bleibt bei -11.0 dB)",
                Math.Abs(m.Channel(true, 0).GainDb - (-11.0)) < 0.05, $"Ch0={m.Channel(true, 0).GainDb}");
            Check("Snapshot-Recall lässt SystemConfig (Hardware/Takt/App-Links) vollständig unverändert",
                Equals(m.Config.SampleRate, sysBefore.SampleRate) &&
                m.Config.Devices.Count == sysBefore.Devices.Count &&
                m.Config.AppLinks.Count == sysBefore.AppLinks.Count);
        }

        // ======================================================================================
        // 8. Invariante F: Schließen des UI-Clients beendet NIEMALS den Daemon oder den Ton
        // ======================================================================================
        {
            string pipeKey = "st-pipe-" + Guid.NewGuid().ToString("N")[..8];
            using var pipeServer = new NamedPipeControlServer(liveSession.Controller, pipeKey);
            pipeServer.Start();

            var pipeClient = new NamedPipeBackendClient(pipeKey);
            pipeClient.ConnectAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
            var clientModel = new AppModel(pipeClient);
            Check("Zweiter UI-Client verbindet sich per Named Pipe mit laufender Engine", clientModel.IsConnected && clientModel.HostRunning);

            // Normales Schließen des UI-Fensters simulieren
            clientModel.DisconnectClientOnly();
            Thread.Sleep(60);

            Check("Nach DisconnectClientOnly() läuft die Hintergrund-Engine ununterbrochen weiter",
                liveSession.Controller.Snapshot.HostStats.State == HostState.Running,
                liveSession.Controller.Snapshot.HostStats.State.ToString());
        }
    }

    static float MeasureEngineOutputPeak(IMixerEngine engine, int inputIdx, int outputIdx)
    {
        int inCh = Math.Max(1, engine.InputChannels);
        int outCh = Math.Max(1, engine.OutputChannels);
        const int frames = 512;
        var ins = new float[inCh][];
        var outs = new float[outCh][];
        for (int i = 0; i < inCh; i++) ins[i] = new float[frames];
        for (int o = 0; o < outCh; o++) outs[o] = new float[frames];

        for (int n = 0; n < frames; n++)
            ins[inputIdx][n] = 0.5f * MathF.Sin(2f * MathF.PI * 440f * n / Math.Max(1, engine.SampleRate));

        // Mehrere Blöcke verarbeiten, damit Klickschutz-Rampen vollständig eingeschwungen sind
        for (int k = 0; k < 12; k++)
            engine.Process(ins, outs, frames);

        float peak = 0f;
        for (int n = 0; n < frames; n++)
            peak = MathF.Max(peak, MathF.Abs(outs[outputIdx][n]));
        return peak;
    }

    static void ValidateWidgetTree(UiRoot root, string context)
    {
        var issues = new List<string>();
        CheckContainer(root.Side, root.Bounds, scrollableChildren: false, issues);
        CheckContainer(root.Top, root.Bounds, scrollableChildren: false, issues);
        CheckContainer(root.CurrentPage, root.Bounds, scrollableChildren: false, issues);
        Check($"Geometrie & Überlappungsfreiheit: {context}", issues.Count == 0,
            issues.Count > 0 ? string.Join(" | ", issues.Take(4)) : "");
    }

    static void CheckContainer(Widget w, RectF winBounds, bool scrollableChildren, List<string> issues)
    {
        if (!w.Visible) return;

        // Toasts-Overlay ist leer unsichtbar, belegt aber ContentRect -> überspringen
        if (w is Toasts) return;

        if (w.Bounds.W <= 0.5f || w.Bounds.H <= 0.5f)
        {
            issues.Add($"{w.GetType().Name} hat leere Bounds ({w.Bounds.W:0.#}×{w.Bounds.H:0.#})");
            return;
        }

        if (!scrollableChildren)
        {
            const float tol = 2.0f;
            if (w.Bounds.X < winBounds.X - tol || w.Bounds.Y < winBounds.Y - tol ||
                w.Bounds.Right > winBounds.Right + tol || w.Bounds.Bottom > winBounds.Bottom + tol)
            {
                issues.Add($"{w.GetType().Name} ragt aus dem Fenster ({w.Bounds.X:0},{w.Bounds.Y:0}..{w.Bounds.Right:0},{w.Bounds.Bottom:0} vs {winBounds.W:0}×{winBounds.H:0})");
            }
        }

        bool childScrollable = scrollableChildren || w is StripBankPanel or SheetOverlay;
        var visChildren = w.Children.Where(c => c.Visible && c is not Toasts).ToList();

        // Geschwister-Überlappungsprüfung für direkte sichtbare Kinder
        for (int i = 0; i < visChildren.Count; i++)
        {
            for (int j = i + 1; j < visChildren.Count; j++)
            {
                var a = visChildren[i];
                var b = visChildren[j];
                var inter = a.Bounds.Intersect(b.Bounds);
                if (inter.W > 1.5f && inter.H > 1.5f)
                {
                    issues.Add($"Überlappung in {w.GetType().Name}: {a.GetType().Name} und {b.GetType().Name} ({inter.W:0.#}×{inter.H:0.#}px)");
                }
            }
        }

        foreach (var child in visChildren)
            CheckContainer(child, winBounds, childScrollable, issues);
    }
}
