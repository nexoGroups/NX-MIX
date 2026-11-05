using System.Text;
using NxMix.App.Model;
using NxMix.App.Native;
using NxMix.App.Pages;
using NxMix.App.Ui;
using NxMix.Audio;
using NxMix.Engine;

namespace NxMix.App;

/// <summary>
/// Selbsttest ohne Fenster und ohne Audio: spielt synthetische Mausereignisse gegen die Oberflaeche und prueft das Modell.
/// Aufruf: NX-MIX.exe --selftest ergebnis.txt   (schreibt nichts anderes als diese Datei und eine temporaere JSON daneben)
/// </summary>
internal static class SelfTest
{
    static readonly StringBuilder Log = new();
    static int _fail;

    static void Check(string name, bool ok, string detail = "")
    {
        if (!ok) _fail++;
        Log.AppendLine((ok ? "OK   " : "FAIL ") + name + (ok || detail.Length == 0 ? "" : "  -> " + detail));
    }

    static IEnumerable<Widget> Walk(Widget w)
    {
        yield return w;
        foreach (var c in w.Children.ToArray()) foreach (var d in Walk(c)) yield return d;
    }

    static T First<T>(Widget w, Func<T, bool>? pred = null) where T : Widget => Walk(w).OfType<T>().First(x => pred == null || pred(x));

    static (float X, float Y) Mid(Widget w) => (w.Bounds.CX, w.Bounds.CY);

    static void Click(UiRoot r, float x, float y, int clicks = 1, int button = 0, bool ctrl = false)
    {
        r.MouseMove(x, y, ctrl, false, false);
        r.MouseDown(x, y, button, clicks, ctrl, false, false);
        r.MouseUp(x, y, button, ctrl, false, false);
    }

    static void Click(UiRoot r, Widget w, int clicks = 1, int button = 0, bool ctrl = false) { var (x, y) = Mid(w); Click(r, x, y, clicks, button, ctrl); }

    static void Drag(UiRoot r, float x, float y, float dx, float dy, bool ctrl = false)
    {
        r.MouseMove(x, y, ctrl, false, false);
        r.MouseDown(x, y, 0, 1, ctrl, false, false);
        for (int i = 1; i <= 10; i++) r.MouseMove(x + dx * i / 10, y + dy * i / 10, ctrl, false, false);
        r.MouseUp(x + dx, y + dy, 0, ctrl, false, false);
    }

    static UiRoot NewRoot(out AppModel model, float w = 1600, float h = 900)
    {
        EngineFactory.ForceDemo = true;
        model = new AppModel(AppModel.EmptySettings(), new List<DeviceBinding>(), EngineFactory.Create, () => new DemoCatalog(), () => new DemoHost(false), true, null)
        { Post = a => a() };
        model.SetDevicesNow(new DemoCatalog().List());
        model.RunInline = true;
        model.AppCatalogFactory = () => new DemoAppCatalog();
        model.RestorerFactory = () => new DemoRestorer();
        var r = new UiRoot(model, new NullShell());
        r.Resize(w, h);
        return r;
    }

    public static int Run(string outFile)
    {
        ProjectStore.Enabled = false;
        try { RunAll(outFile); }
        catch (Exception ex) { _fail++; Log.AppendLine("FAIL Ausnahme: " + ex); }
        Log.AppendLine(_fail == 0 ? "ALLE TESTS BESTANDEN" : $"{_fail} FEHLER");
        File.WriteAllText(outFile, Log.ToString());
        return _fail == 0 ? 0 : 2;
    }

    static void RunAll(string outFile)
    {
        // ---- Leerer Start --------------------------------------------------------------------
        var root = NewRoot(out var m);
        Check("Start ist leer: 0 Eingaenge, 0 Ausgaenge", m.InputCount == 0 && m.OutputCount == 0);
        Check("Start: keine Matrix-Verbindungen und keine Zuordnungen", m.Settings.MatrixDb.Length == 0 && m.Bindings.Count == 0);
        var mixer = (MixerPage)root.Pages[0];

        // ---- Kanaele per Klick anlegen --------------------------------------------------------
        var addIn = First<TextButton>(mixer.Inputs, b => b.Visible && b.Text == "Eingang");
        Click(root, addIn);
        Check("Klick auf '+ Eingang' legt einen Eingang an", m.InputCount == 1, "InputCount=" + m.InputCount);
        Check("neuer Kanal hat keinen vorbelegten Namen", m.Settings.Inputs[0].Name == "");
        var eng0 = m.Engine;
        var addPair = First<TextButton>(mixer.Outputs, b => b.Visible && b.Text == "Stereo-Paar");
        Click(root, addPair);
        Check("Stereo-Paar legt zwei Ausgaenge an", m.OutputCount == 2 && m.Settings.Outputs[0].Name == "Stereo 1 L" && m.Settings.Outputs[1].Name == "Stereo 1 R");
        Check("Kanalzahl-Aenderung erzeugt neue Engine", !ReferenceEquals(eng0, m.Engine));
        m.AddChannels(false, 2); // Ausgang 3 + 4
        m.AddChannels(true, 2);  // Eingang 2 + 3
        Check("Anlegen: 3 Eingaenge, 4 Ausgaenge", m.InputCount == 3 && m.OutputCount == 4);
        Check("Matrix passt sich an (3x4)", m.Settings.MatrixDb.Length == 3 && m.Settings.MatrixDb.All(r => r.Length == 4));

        // ---- Umbenennen mit eigenem Editor ----------------------------------------------------
        var strip0 = mixer.Inputs.Strips[0];
        var name0 = First<NameLabel>(strip0);
        Click(root, name0, 2);
        Check("Doppelklick auf den Namen oeffnet den Editor", root.Editor.Active);
        foreach (char c in "Front L") root.Char(c);
        root.KeyDown(Win32.VK_RETURN, false, false, false);
        Check("Name per Editor gesetzt", m.Settings.Inputs[0].Name == "Front L", m.Settings.Inputs[0].Name);
        Check("Editor danach geschlossen", !root.Editor.Active);
        // Esc bricht ab
        Click(root, First<NameLabel>(mixer.Inputs.Strips[0]), 2);
        foreach (char c in "XYZ") root.Char(c);
        root.KeyDown(Win32.VK_ESCAPE, false, false, false);
        Check("Esc verwirft die Eingabe", m.Settings.Inputs[0].Name == "Front L");

        // ---- Fader ----------------------------------------------------------------------------
        var fader = First<Fader>(mixer.Inputs.Strips[0]);
        Check("Fader startet bei 0 dB", fader.Value == 0);
        var (fx, fy) = Mid(fader);
        Drag(root, fx + 4, FaderCapY(fader, 0), 0, -60);
        Check("Fader nach oben ziehen erhoeht den Pegel", m.Settings.Inputs[0].GainDb > 0.5, "Gain=" + m.Settings.Inputs[0].GainDb);
        Check("Fader begrenzt auf MaxGainDb (+12)", m.Settings.Inputs[0].GainDb <= 12.0 + 1e-9);
        Drag(root, fx + 4, FaderCapY(fader, m.Settings.Inputs[0].GainDb), 0, -2000);
        Check("Ziehen weit ueber das Ende bleibt bei +12", Math.Abs(m.Settings.Inputs[0].GainDb - 12.0) < 1e-9, "Gain=" + m.Settings.Inputs[0].GainDb);
        Click(root, fx + 4, fader.Bounds.Y + 100, 2);
        Check("Doppelklick auf den Fader = 0 dB", m.Settings.Inputs[0].GainDb == 0, "Gain=" + m.Settings.Inputs[0].GainDb);
        root.MouseWheel(fx + 4, fy, 1f, false, false, false);
        Check("Mausrad: +1 dB", Math.Abs(m.Settings.Inputs[0].GainDb - 1.0) < 1e-9, "Gain=" + m.Settings.Inputs[0].GainDb);
        root.MouseWheel(fx + 4, fy, -1f, true, false, false);
        Check("Strg+Mausrad: fein (0,1 dB)", Math.Abs(m.Settings.Inputs[0].GainDb - 0.9) < 1e-9, "Gain=" + m.Settings.Inputs[0].GainDb);
        root.MouseWheel(fx + 4, fy, -80f, false, false, false);
        Check("Mausrad bis unten ergibt -unendlich", double.IsNegativeInfinity(m.Settings.Inputs[0].GainDb), "Gain=" + m.Settings.Inputs[0].GainDb);
        Click(root, fx + 4, fader.Bounds.Y + 100, 2);

        // ---- Mute / Solo / Phase --------------------------------------------------------------
        var mute = First<ToggleButton>(mixer.Inputs.Strips[0], b => b.Text == "M");
        Click(root, mute);
        Check("Mute schaltet", m.Settings.Inputs[0].Mute);
        Click(root, mute);
        Check("Mute schaltet zurueck", !m.Settings.Inputs[0].Mute);

        // ---- Gain-Sperre (Hochtoener-Schutz) ----------------------------------------------------
        m.UpdateChannel(false, 2, c => c with { GainDb = -13, MaxGainDb = -13, Name = "Hi Links" });
        var hi = First<Fader>(mixer.Outputs.Strips[2]);
        Check("Gain-Sperre: Fader zeigt Sperrwert", hi.Value == -13 && hi.MaxDb == -13);
        var (hx, hy) = Mid(hi);
        Drag(root, hx + 4, FaderCapY(hi, -13), 0, -300);
        Check("Gesperrter Fader laesst sich nicht ueber -13 dB ziehen", m.Settings.Outputs[2].GainDb <= -13 + 1e-9, "Gain=" + m.Settings.Outputs[2].GainDb);
        Click(root, hx + 4, hi.Bounds.Y + 50, 2);
        Check("Doppelklick (0 dB) wird auf die Sperre geklemmt", m.Settings.Outputs[2].GainDb <= -13 + 1e-9, "Gain=" + m.Settings.Outputs[2].GainDb);
        root.MouseWheel(hx + 4, hy, 5f, false, false, false);
        Check("Mausrad kann die Sperre nicht uebersteigen", m.Settings.Outputs[2].GainDb <= -13 + 1e-9, "Gain=" + m.Settings.Outputs[2].GainDb);
        var readout = First<ReadoutBox>(mixer.Outputs.Strips[2]);
        Click(root, readout, 2);
        foreach (char c in "5") root.Char(c);
        root.KeyDown(Win32.VK_RETURN, false, false, false);
        Check("Getippter Wert +5 wird auf die Sperre geklemmt", m.Settings.Outputs[2].GainDb <= -13 + 1e-9, "Gain=" + m.Settings.Outputs[2].GainDb);
        m.UpdateChannel(false, 2, c => c with { GainDb = 3 });
        Check("Modell klemmt GainDb auf MaxGainDb", m.Settings.Outputs[2].GainDb == -13);

        // Schloss-Menue
        var lockBtn = First<IconButton>(mixer.Outputs.Strips[2], b => b.Icon == IconKind.Lock);
        Click(root, lockBtn);
        Check("Klick auf das Schloss oeffnet ein Menue", root.Modal is PopupMenu);
        root.CloseOverlay(root.Modal);

        // ---- Matrix ---------------------------------------------------------------------------
        root.SwitchPage(1);
        var mp = (MatrixPage)root.Pages[1];
        var view = First<MatrixView>(mp);
        Check("Matrix-Ansicht sichtbar bei vorhandenen Kanaelen", view.Visible && view.Bounds.W > 100);
        var cell = CellCenter(view, m, 0, 0);
        Click(root, cell.X, cell.Y);
        Check("Matrix: Klick verbindet bei 0 dB", m.Settings.MatrixDb[0][0] == 0, "v=" + m.Settings.MatrixDb[0][0]);
        Drag(root, cell.X, cell.Y, 0, -30);
        Check("Matrix: Ziehen aendert den Pegel", m.Settings.MatrixDb[0][0] > 1, "v=" + m.Settings.MatrixDb[0][0]);
        Click(root, cell.X, cell.Y, 2);
        Check("Matrix: Doppelklick = 0 dB", m.Settings.MatrixDb[0][0] == 0, "v=" + m.Settings.MatrixDb[0][0]);
        Click(root, cell.X, cell.Y);
        Check("Matrix: erneuter Klick trennt", double.IsNegativeInfinity(m.Settings.MatrixDb[0][0]));
        root.MouseWheel(cell.X, cell.Y, 3f, false, false, false);
        Check("Matrix: Mausrad auf getrennter Zelle aendert nichts", double.IsNegativeInfinity(m.Settings.MatrixDb[0][0]));
        Click(root, cell.X, cell.Y);
        root.MouseWheel(cell.X, cell.Y, 2f, false, false, false);
        Check("Matrix: Mausrad +2 dB", Math.Abs(m.Settings.MatrixDb[0][0] - 2) < 1e-9, "v=" + m.Settings.MatrixDb[0][0]);
        Click(root, cell.X, cell.Y, 1, 1);
        Check("Matrix: Rechtsklick trennt", double.IsNegativeInfinity(m.Settings.MatrixDb[0][0]));
        var pc = CellCenter(view, m, 1, 2); // auf den geschuetzten Hochtoener-Ausgang
        Click(root, pc.X, pc.Y);
        Drag(root, pc.X, pc.Y, 0, -400);
        Check("Matrix: geschuetzter Ausgang bleibt <= 0 dB", m.Settings.MatrixDb[1][2] <= 0 + 1e-9, "v=" + m.Settings.MatrixDb[1][2]);
        root.MouseWheel(pc.X, pc.Y, 30f, false, false, false);
        Check("Matrix: Mausrad auf geschuetztem Ausgang bleibt <= 0 dB", m.Settings.MatrixDb[1][2] <= 0 + 1e-9, "v=" + m.Settings.MatrixDb[1][2]);
        m.SetMatrix(2, 3, -6);

        // ---- Verschieben / Loeschen ----------------------------------------------------------
        root.SwitchPage(0);
        m.UpdateChannel(true, 1, c => c with { Name = "B" });
        m.UpdateChannel(true, 2, c => c with { Name = "C" });
        m.MoveChannel(true, 2, -1);
        Check("Verschieben tauscht die Reihenfolge", m.Settings.Inputs[1].Name == "C" && m.Settings.Inputs[2].Name == "B");
        Check("Verschieben tauscht die Matrix-Zeilen mit", m.Settings.MatrixDb[1][3] == -6 && double.IsNegativeInfinity(m.Settings.MatrixDb[2][3]), $"{m.Settings.MatrixDb[1][3]} / {m.Settings.MatrixDb[2][3]}");
        mixer.Inputs.Strips[1].DebugOpen("channel");
        Check("Kanal-Menue oeffnet", root.Modal is PopupMenu);
        root.CloseOverlay(root.Modal);
        int eIn = m.InputCount;
        m.RemoveChannel(true, 0);
        Check("Loeschen entfernt den Kanal", m.InputCount == eIn - 1 && m.Settings.MatrixDb.Length == eIn - 1);
        Check("Loeschen laesst die Matrix passend", m.Settings.MatrixDb.All(r => r.Length == m.OutputCount));
        m.RemoveChannel(false, 3);
        Check("Ausgang loeschen entfernt die Matrix-Spalte", m.OutputCount == 3 && m.Settings.MatrixDb.All(r => r.Length == 3));

        // ---- Zuordnungen ---------------------------------------------------------------------
        m.NewProject();
        Check("Neues Projekt: wieder leer", m.InputCount == 0 && m.OutputCount == 0 && m.Bindings.Count == 0);
        m.AddBinding(new DeviceBinding("demo-in-1", DeviceFlow.Capture, 0, 0, 4));
        Check("Zuordnung legt fehlende Engine-Eingaenge an", m.InputCount == 4, "InputCount=" + m.InputCount);
        m.AddBinding(new DeviceBinding("demo-out-1", DeviceFlow.Render, 0, 0, 2));
        Check("Zuordnung legt fehlende Engine-Ausgaenge an", m.OutputCount == 2);
        Check("Gueltige Zuordnung wird akzeptiert", m.ValidateBinding(m.Bindings[0]) == null && m.ValidateBinding(m.Bindings[1]) == null);
        Check("Zu viele Geraetekanaele werden gemeldet", m.ValidateBinding(new DeviceBinding("demo-in-3", DeviceFlow.Capture, 0, 0, 2)) != null);
        Check("Unbekanntes Geraet wird gemeldet", m.ValidateBinding(new DeviceBinding("gibt-es-nicht", DeviceFlow.Capture, 0, 0, 1)) != null);
        string why;
        Check("Start mit Demo-Engine ist nicht moeglich", !m.CanStart(out why) && why.Length > 0, why);
        m.RemoveChannel(true, 1);
        Check("Loeschen eines Eingangs schrumpft die Zuordnung", m.Bindings[0].Count == 3, "Count=" + m.Bindings[0].Count);
        Log.AppendLine("INFO Audio-Host verfuegbar: " + AudioFactory.HostAvailable + " | Echte Engine geladen: " + !(EngineFactory.Create(m.Settings) is DemoEngine));

        // ---- Speichern / Laden (JSON, -unendlich als null) ------------------------------------
        m.UpdateChannel(true, 0, c => c with { Name = "Ä Ö ü", GainDb = double.NegativeInfinity });
        m.UpdateChannel(false, 0, c => c with { MaxGainDb = -13, GainDb = -13, CeilingDbfs = -6 });
        m.SetMatrix(0, 0, -3.5);
        string tmp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outFile))!, "selftest-project.json");
        ProjectStore.WriteFile(tmp, ProjectStore.ToDto(m, new WindowDto { Width = 1500, Height = 800 }));
        string json = File.ReadAllText(tmp);
        Check("JSON enthaelt null fuer -unendlich", json.Contains("null"));
        Check("JSON enthaelt kein Infinity/NaN", !json.Contains("Infinity") && !json.Contains("NaN"));
        var dto = ProjectStore.ReadFile(tmp);
        var (s2, b2) = ProjectStore.FromDto(dto);
        Check("Laden: Kanalzahlen", s2.Inputs.Count == m.InputCount && s2.Outputs.Count == m.OutputCount);
        Check("Laden: -unendlich bleibt -unendlich", double.IsNegativeInfinity(s2.Inputs[0].GainDb) && double.IsNegativeInfinity(s2.MatrixDb[0][1]));
        Check("Laden: Matrixwert", Math.Abs(s2.MatrixDb[0][0] + 3.5) < 1e-9);
        Check("Laden: Umlaute im Namen", s2.Inputs[0].Name == "Ä Ö ü");
        Check("Laden: Gain-Sperre und Limiter", s2.Outputs[0].MaxGainDb == -13 && s2.Outputs[0].CeilingDbfs == -6);
        Check("Laden: Zuordnungen und Fenstergroesse", b2.Count == m.Bindings.Count && dto.Window?.Width == 1500);
        var root2 = NewRoot(out var m2);
        m2.LoadProject(s2, b2, dto.BlockFrames, dto.Exclusive);
        Check("Projekt laden ersetzt den Zustand", m2.InputCount == m.InputCount && m2.Settings.Outputs[0].MaxGainDb == -13);
        try { File.Delete(tmp); } catch { }

        // ---- Seitenwechsel + Menues -----------------------------------------------------------
        root2.SwitchPage(2);
        Check("Geraete-Seite wechselt", root2.PageIndex == 2);
        root2.ShowProjectMenu(root2.Title.MenuRect);
        Check("Projekt-Menue oeffnet", root2.Modal is PopupMenu);
        root2.KeyDown(Win32.VK_ESCAPE, false, false, false);
        Check("Esc schliesst das Menue", root2.Modal == null);
        root2.NewProjectWithConfirm();
        Check("Neues Projekt fragt nach", root2.Modal is ModalDialog);
        root2.KeyDown(Win32.VK_ESCAPE, false, false, false);
        Check("Abbrechen laesst das Projekt unveraendert", m2.InputCount == m.InputCount);
        root2.NewProjectWithConfirm();
        root2.KeyDown(Win32.VK_RETURN, false, false, false);
        Check("Bestaetigen leert das Projekt", m2.InputCount == 0 && m2.OutputCount == 0);

        AppLinkTests(outFile);
        try
        {
            // Nur Information (kein Pass/Fail): die echten Klassen der Audio-Schicht per Reflection; es wird nichts umgeleitet oder zurueckgesetzt.
            var rr = AudioFactory.CreateRestorer();
            var cc = AudioFactory.CreateAppCatalog();
            Log.AppendLine($"INFO App-Links echt: Restorer={(rr == null ? "fehlt" : rr.IsSupported ? "unterstuetzt" : "nicht unterstuetzt: " + rr.UnsupportedReason)} | Katalog={(cc == null ? "fehlt" : cc.List().Count + " Sitzung(en)")} | Monitor={(typeof(IAppLinkMonitor).IsAssignableFrom(AudioFactory.HostType) ? "AudioHost ja" : "AudioHost nein")}");
        }
        catch (Exception ex) { Log.AppendLine("INFO App-Links echt: Ausnahme " + ex.Message); }

        // ---- Layout bei vielen Groessen (nur Zeichnen, keine Ausnahme) -----------------------------
        int drawn = 0;
        foreach (var (w, h, sc) in new[] { (1180, 680, 1f), (1280, 720, 1f), (1600, 900, 1f), (1920, 1080, 1f), (2560, 1440, 1f), (2000, 1200, 1.5f), (3840, 2160, 2f) })
        {
            foreach (bool demo in new[] { false, true })
            {
                var r = NewRoot(out var mm, w / sc, h / sc);
                if (demo) { var (ds, db) = DemoProject.Create(); mm.LoadProject(ds, db, 480, false, DemoProject.AppLinks()); mm.LinkStatusSource = DemoProject.LinkStatuses; }
                else { mm.AddChannels(true, 30); mm.AddChannels(false, 30); } // viele Kanaele: Scrollen
                for (int p = 0; p < 4; p++)
                {
                    r.SwitchPage(p);
                    r.Resize(w / sc, h / sc);
                    r.Tick(0.016f);
                    Program.RenderRoot(r, w, h, sc, null);
                    drawn++;
                }
                if (demo)
                {
                    // Auswahl-Popup (Liste, Handeingabe, Konflikt) in jeder Groesse zeichnen
                    r.SwitchPage(0);
                    foreach (var (strip, how) in new[] { (10, "applink"), (10, "applink-manual"), (2, "applink"), (8, "applink") })
                    {
                        ((MixerPage)r.Pages[0]).DebugOpen(true, strip, how);
                        r.Tick(0.016f);
                        Program.RenderRoot(r, w, h, sc, null);
                        drawn++;
                        r.CloseOverlay(r.Modal);
                    }
                }
            }
        }
        Check($"Zeichnen in {drawn} Groessen/Seiten ohne Ausnahme", true);
    }

    // ================================================================================================
    // App-Links: Modell (Konflikte, Speichern/Laden, Host-Konfiguration) und Bedienung (Popup)
    // ================================================================================================
    static void AppLinkTests(string outFile)
    {
        var root = NewRoot(out var m);
        var restorer = new DemoRestorer();
        m.RestorerFactory = () => restorer;
        var mixer = (MixerPage)root.Pages[0];
        m.AddChannels(true, 7); // Eingang 7 bleibt frei
        m.UpdateChannel(true, 0, c => c with { Name = "Stereo 1 L" });
        m.UpdateChannel(true, 1, c => c with { Name = "Stereo 1 R" });
        Check("App-Links: Stereo-Paar anhand der Namen erkannt", m.IsNamedPair(0) && m.IsNamedPair(1) && !m.IsNamedPair(3) && m.SuggestSpan(1) == (0, 2) && m.SuggestSpan(3) == (3, 1));
        Check("App-Links: anfangs keine Links, kein Problem", m.AppLinks.Count == 0 && !m.HasActiveLinks && m.AppLinkProblem() == null);
        var grouped = AppLinkRules.Group(new DemoAppCatalog().List());
        Check("App-Links: Programmliste fasst Sitzungen je Exe zusammen, spielende zuerst", grouped.Count == 6 && grouped[0].IsPlaying && grouped.First(p => p.ExeName == "chrome.exe").Instances == 2);

        // ---- Konflikt mit Geraete-Zuordnung (beide Richtungen) ------------------------------------
        Check("Geraete-Zuordnung auf Eingang 3+4 angelegt", m.AddBinding(new DeviceBinding("demo-in-1", DeviceFlow.Capture, 0, 2, 2)) == null);
        var why = m.SetLink(2, 1, "spotify.exe", null);
        Check("App-Link auf einen Eingang mit Geraete-Zuordnung wird abgelehnt und erklaert", why != null && why.Contains("Geräte-Zuordnung") && m.AppLinks.Count == 0, why ?? "null");
        Check("Auch ein Stereo-Link, der in die Zuordnung ragt, wird abgelehnt", m.SetLink(1, 2, "spotify.exe", null) != null && m.AppLinks.Count == 0);

        mixer.DebugOpen(true, 2, "applink");
        Check("Klick auf einen belegten Eingang oeffnet das Popup", root.Modal is AppLinkPicker);
        root.KeyDown(Win32.VK_DOWN, false, false, false);
        root.KeyDown(Win32.VK_RETURN, false, false, false);
        Check("Popup an belegtem Eingang bindet nichts", m.AppLinks.Count == 0);
        root.KeyDown(Win32.VK_ESCAPE, false, false, false);
        Check("Esc schliesst das Popup", root.Modal == null);

        // ---- Bedienung: Link-Knopf -> Filter tippen -> Enter -----------------------------------------
        var box = First<AppLinkBox>(mixer.Inputs.Strips[4]);
        Click(root, box.DrawRect.CX, box.DrawRect.CY);
        Check("Klick auf den Link-Knopf oeffnet das Auswahl-Popup", root.Modal is AppLinkPicker);
        foreach (char c in "vlc") root.Char(c);
        root.KeyDown(Win32.VK_RETURN, false, false, false);
        var vlc = m.LinkAt(4);
        Check("Filter 'vlc' + Enter bindet VLC (mono) an Eingang 5", vlc != null && vlc.ExeName == "vlc.exe" && vlc.Count == 1 && vlc.DisplayName == "VLC media player", vlc?.ToString() ?? "null");
        Check("Popup schliesst nach der Auswahl", root.Modal == null);
        Check("Gebundener Streifen zeigt den Programmnamen", First<NameLabel>(mixer.Inputs.Strips[4]).Placeholder == "VLC media player");

        // ---- Handeingabe ------------------------------------------------------------------------------
        mixer.DebugOpen(true, 5, "applink-manual");
        foreach (char c in "C:\\Programme\\Chrome") root.Char(c);
        root.KeyDown(Win32.VK_RETURN, false, false, false);
        var chrome = m.LinkAt(5);
        Check("Handeingabe normalisiert Pfad/Endung: 'Chrome.exe'", chrome != null && chrome.ExeName == "Chrome.exe" && chrome.Count == 1, chrome?.ExeName ?? "null");
        Check("Handeingabe kennt Programme der Liste (Anzeigename)", chrome?.DisplayName == "Google Chrome", chrome?.DisplayName ?? "null");

        // ---- Stereo-Paar, Duplikate, ungueltige Namen -------------------------------------------------
        Check("Stereo-App-Link auf das benannte Paar 1+2", m.SetLink(0, 2, "Spotify.exe", "Spotify") == null && m.LinkAt(1)?.FirstChannel == 0 && m.LinkAt(1)?.Count == 2);
        Check("Zweite Streifenhaelfte heisst 'Spotify R'", First<NameLabel>(mixer.Inputs.Strips[1]).Placeholder == "Spotify R" && First<NameLabel>(mixer.Inputs.Strips[0]).Placeholder == "Spotify L");
        Check("Dasselbe Programm an zwei Faders wird abgelehnt (ohne Beachtung der Gross-/Kleinschreibung)", m.SetLink(6, 1, "SPOTIFY.EXE", null) != null && m.LinkAt(6) == null);
        Check("Link ueber einen schon gebundenen Eingang wird abgelehnt", m.SetLink(1, 1, "other.exe", null) != null);
        Check("Ungueltige Programmnamen werden abgelehnt", m.SetLink(6, 1, "", null) != null && m.SetLink(6, 1, "readme.txt", null) != null && m.SetLink(6, 1, "a|b.exe", null) != null && m.AppLinks.Count == 3);
        Check("Links bleiben nach Eingang sortiert", m.AppLinks.Select(l => l.FirstChannel).SequenceEqual(new[] { 0, 4, 5 }));
        Check("Stereo-Link-Eingaenge lassen sich nicht verschieben", !m.CanMoveChannel(true, 0, +1) && !m.CanMoveChannel(true, 1, -1) && m.CanMoveChannel(true, 4, +1));

        // ---- Konflikt in der Gegenrichtung (neue Zuordnung auf einen Link-Eingang) -----------------------
        var r1 = m.AddBinding(new DeviceBinding("demo-in-1", DeviceFlow.Capture, 2, 4, 1));
        Check("Neue Geraete-Zuordnung auf einen App-Link-Eingang wird abgelehnt", r1 != null && r1.Contains("App-Link") && m.Bindings.Count == 1, r1 ?? "null");
        var r2 = m.UpdateBinding(0, b => b with { EngineFirstChannel = 1 });
        Check("Zuordnung in einen App-Link verschieben wird abgelehnt und aendert nichts", r2 != null && m.Bindings[0].EngineFirstChannel == 2, r2 ?? "null");
        Check("Neue Zuordnung wird hinter die Links einsortiert", m.NextFreeEngineChannel(DeviceFlow.Capture) >= 6, m.NextFreeEngineChannel(DeviceFlow.Capture).ToString());

        // ---- Parkplatz ------------------------------------------------------------------------------------
        Check("Ohne Parkplatz: Start-Problem benennt den Parkplatz", (m.AppLinkProblem() ?? "").Contains("Parkplatz"));
        m.SetParking("demo-out-3"); // Standardgeraet
        Check("Das Standardgeraet ist als Parkplatz nicht waehlbar", m.ParkingDeviceId == null);
        m.SetParking("demo-in-1");   // Aufnahmegeraet
        Check("Ein Aufnahmegeraet ist als Parkplatz nicht waehlbar", m.ParkingDeviceId == null);
        Check("Parkplatz-Kandidaten: nur Render-Geraete ohne Standardgeraet", m.ParkingCandidates.All(d => d.Flow == DeviceFlow.Render && !d.IsDefault) && m.ParkingCandidates.Any(d => d.Id == DemoProject.ParkingId));
        m.SetParking(DemoProject.ParkingId);
        Check("Parkplatz gesetzt, Problem verschwindet", m.ParkingDeviceId == DemoProject.ParkingId && m.AppLinkProblem() == null, m.AppLinkProblem() ?? "");
        m.AddBinding(new DeviceBinding(DemoProject.ParkingId, DeviceFlow.Render, 0, 0, 2));
        Check("Parkplatz zugleich als Ausgang zugeordnet: Problem", (m.AppLinkProblem() ?? "").Contains("hörbar"));
        m.RemoveBinding(m.Bindings.Count - 1);
        Check("Zuordnung entfernt, Problem weg", m.AppLinkProblem() == null);

        // ---- Host-Konfiguration -----------------------------------------------------------------------------
        var cfg = m.BuildHostConfig();
        Check("Host-Konfiguration: AppBindings (EngineFirstChannel = Eingang) und Parkplatz",
            cfg.AppBindings is { Count: 3 } && cfg.AppBindings.Any(b => b.ExeName == "Spotify.exe" && b.EngineFirstChannel == 0 && b.Count == 2)
            && cfg.AppBindings.Any(b => b.ExeName == "vlc.exe" && b.EngineFirstChannel == 4 && b.Count == 1) && cfg.ParkingDeviceId == DemoProject.ParkingId);
        m.SetAppLinksEnabled(false);
        var cfg2 = m.BuildHostConfig();
        Check("Links ausgeschaltet: keine AppBindings, kein Parkplatz, kein Start-Problem", cfg2.AppBindings == null && cfg2.ParkingDeviceId == null && m.AppLinkProblem() == null && m.ViewOf(m.AppLinks[0]).State == LinkUi.Off);
        m.SetAppLinksEnabled(true);

        // ---- Status aus dem Host -----------------------------------------------------------------------------
        m.LinkStatusSource = () => new[]
        {
            new AppLinkStatus("Spotify.exe", AppLinkState.Linked, 2, null, 0.5),
            new AppLinkStatus("vlc.exe", AppLinkState.Failed, 0, "Zugriff verweigert", 0),
            new AppLinkStatus("Chrome.exe", AppLinkState.WaitingForApp, 0, null, 0),
        };
        Check("Tick meldet geaenderten Link-Status", m.Tick(0.3f));
        var vs = m.ViewOf(m.LinkAt(0)!); var vv = m.ViewOf(m.LinkAt(4)!); var vc = m.ViewOf(m.LinkAt(5)!);
        Check("Status: verbunden / Fehler mit Meldung / wartet", vs.State == LinkUi.Linked && vs.ProcessCount == 2 && vv.State == LinkUi.Failed && vv.Message == "Zugriff verweigert" && vc.State == LinkUi.Waiting);
        Check("Tooltip nennt Programm und Fehlermeldung", LinkLook.Describe(m.LinkAt(4)!, vv).Contains("vlc.exe") && LinkLook.Describe(m.LinkAt(4)!, vv).Contains("Zugriff verweigert"));
        m.LinkStatusSource = null;
        m.Tick(0.3f);
        Check("Ohne Host-Status: bereit (Audio gestoppt)", m.ViewOf(m.LinkAt(0)!).State == LinkUi.Idle);

        // ---- Umleitungen zuruecksetzen ---------------------------------------------------------------------------
        string? resetErr = "nicht aufgerufen";
        int before = restorer.Calls;
        m.ResetAllRoutes(e => resetErr = e);
        Check("'Alle Umleitungen zuruecksetzen' ruft RestoreAllKnown", restorer.Calls == before + 1 && resetErr == null);
        // Seite Einstellungen zeichnet sich mit und ohne Parkplatz
        root.SwitchPage(3); Program.RenderRoot(root, 1600, 900, 1f, null); root.Resize(1180, 680);
        m.SetParking(null); Program.RenderRoot(root, 1180, 680, 1f, null);
        Check("Einstellungsseite mit App-Link-Abschnitt zeichnet (mit/ohne Parkplatz, niedriges Fenster scrollt)", true);
        root.MouseWheel(600, 400, -5f, false, false, false);
        Program.RenderRoot(root, 1180, 680, 1f, null);
        m.SetParking(DemoProject.ParkingId);
        root.SwitchPage(0);

        // ---- Speichern / Laden --------------------------------------------------------------------------------------
        string tmp = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outFile))!, "selftest-applinks.json");
        ProjectStore.WriteFile(tmp, ProjectStore.ToDto(m, null));
        string json = File.ReadAllText(tmp);
        Check("JSON enthaelt AppLinks und Parkplatz", json.Contains("\"AppLinks\"") && json.Contains("Spotify.exe") && json.Contains(DemoProject.ParkingId));
        var dto = ProjectStore.ReadFile(tmp);
        var (s2, b2) = ProjectStore.FromDto(dto);
        var setup = ProjectStore.AppLinksFromDto(dto);
        var root2 = NewRoot(out var m2);
        m2.LoadProject(s2, b2, dto.BlockFrames, dto.Exclusive, setup);
        Check("Laden: App-Links, Schalter und Parkplatz kommen zurueck", m2.AppLinks.SequenceEqual(m.AppLinks) && m2.AppLinksEnabled && m2.ParkingDeviceId == DemoProject.ParkingId && m2.ParkingDeviceName == m.ParkingDeviceName);
        Check("Geladener Streifen zeigt den Link", First<NameLabel>(((MixerPage)root2.Pages[0]).Inputs.Strips[1]).Placeholder == "Spotify R");

        // Rueckwaertskompatibel: Datei ohne die neuen Felder = keine Links
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        node.Remove("AppLinks"); node.Remove("AppLinksEnabled"); node.Remove("ParkingDeviceId"); node.Remove("ParkingDeviceName");
        File.WriteAllText(tmp, node.ToJsonString());
        var old = ProjectStore.AppLinksFromDto(ProjectStore.ReadFile(tmp));
        Check("Alte Projektdatei (ohne App-Link-Felder): keine Links, Schalter an, kein Parkplatz", old.Links.Count == 0 && old.Enabled && old.ParkingDeviceId == null);
        var root3 = NewRoot(out var m3);
        m3.LoadProject(s2, b2, dto.BlockFrames, dto.Exclusive, old);
        Check("Alte Datei geladen: Eingaenge da, keine Links", m3.InputCount == m.InputCount && m3.AppLinks.Count == 0);
        try { File.Delete(tmp); } catch { }

        // ---- Konflikt aus einer geladenen Datei wird erkannt ------------------------------------------------------------
        var root4 = NewRoot(out var m4);
        var conflicted = new AppLinkSetup(new[] { new AppLink("x.exe", 2, 1) }, true, DemoProject.ParkingId, "P");
        m4.LoadProject(s2, new List<DeviceBinding> { new("demo-in-1", DeviceFlow.Capture, 0, 2, 2) }, 480, false, conflicted);
        Check("Geladener Konflikt: Link zeigt 'Konflikt'", m4.ViewOf(m4.AppLinks[0]).State == LinkUi.Conflict);
        Check("Geladener Konflikt: Zuordnung meldet den App-Link", (m4.ValidateBinding(m4.Bindings[0]) ?? "").Contains("App-Link"), m4.ValidateBinding(m4.Bindings[0]) ?? "null");
        Check("Geladener Konflikt: Start-Problem benennt ihn", m4.AppLinkProblem() != null);

        // ---- Kanaele loeschen / verschieben mit Links -----------------------------------------------------------------------
        m.RemoveChannel(true, 0);
        Check("Eingang loeschen: Stereo-Link schrumpft auf mono, folgende Links ruecken auf",
            m.InputCount == 6 && m.LinkAt(0)?.Count == 1 && m.LinkAt(0)?.ExeName == "Spotify.exe" && m.LinkAt(3)?.ExeName == "vlc.exe" && m.LinkAt(4)?.ExeName == "Chrome.exe");
        m.MoveChannel(true, 3, +1);
        Check("Eingang verschieben: Mono-Link wandert mit", m.LinkAt(4)?.ExeName == "vlc.exe" && m.LinkAt(3)?.ExeName == "Chrome.exe");
        m.RemoveChannel(true, 3);
        Check("Eingang mit Mono-Link loeschen entfernt den Link", m.AppLinks.All(l => l.ExeName != "Chrome.exe") && m.AppLinks.Count == 2);
        m.NewProject();
        Check("Neues Projekt: keine Links, Parkplatz bleibt (Geraeteeinstellung)", m.AppLinks.Count == 0 && m.AppLinksEnabled && m.ParkingDeviceId == DemoProject.ParkingId);

        // ---- Windows unterstuetzt es nicht ---------------------------------------------------------------------------
        var root5 = NewRoot(out var m5);
        m5.RestorerFactory = () => new DemoRestorer(false, "zu alt");
        m5.AddChannels(true, 1);
        m5.SetLink(0, 1, "a.exe", null);
        m5.SetParking(DemoProject.ParkingId);
        Check("Nicht unterstuetztes Windows: Start-Problem nennt den Grund", (m5.AppLinkProblem() ?? "").Contains("zu alt") && m5.AppLinksUnsupportedReason() != null);
        var root6 = NewRoot(out var m6);
        m6.RestorerFactory = () => null;
        m6.AddChannels(true, 1); m6.SetLink(0, 1, "a.exe", null); m6.SetParking(DemoProject.ParkingId);
        Check("Ohne Umleitungs-Klasse: Start mit Links wird verweigert", m6.AppLinkProblem() != null);
    }

    static float FaderCapY(Fader f, double db)
    {
        // gleiche Geometrie wie Fader: Kappenmitte fuer den Wert
        float capH = Theme.FaderCapHeight;
        float yTop = f.Bounds.Y + capH / 2 + 1, yBot = f.Bounds.Bottom - capH / 2 - 1;
        double t = FaderScale.ToT(db) / FaderScale.ToT(f.ScaleTop);
        return (float)(yBot - Math.Clamp(t, 0, 1) * (yBot - yTop));
    }

    static (float X, float Y) CellCenter(MatrixView v, AppModel m, int i, int o)
    {
        // gleiche Geometrie wie MatrixView (Kopfbreite 168, Kopfhoehe 76)
        float cw = Math.Clamp((v.Bounds.W - 168) / m.OutputCount, 62f, 132f);
        float ch = Math.Clamp((v.Bounds.H - 76) / m.InputCount, 42f, 72f);
        return (v.Bounds.X + 168 + o * cw + cw / 2, v.Bounds.Y + 76 + i * ch + ch / 2);
    }
}
