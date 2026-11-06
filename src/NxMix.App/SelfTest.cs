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
/// Aufruf: NX-MIX.exe --selftest ergebnis.txt (schreibt den Bericht und temporaere Testdateien daneben).
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

    static (float X, float Y) Mid(Widget w) => (w.Bounds.CX, w.Bounds.CY);

    static void Click(UiRoot r, float x, float y, int clicks = 1, int button = 0, bool ctrl = false)
    {
        r.MouseMove(x, y, ctrl, false, false);
        r.MouseDown(x, y, button, clicks, ctrl, false, false);
        r.MouseUp(x, y, button, ctrl, false, false);
    }

    static void Click(UiRoot r, Widget w, int clicks = 1, int button = 0, bool ctrl = false) { var (x, y) = Mid(w); Click(r, x, y, clicks, button, ctrl); }

    static void ClickSeg(UiRoot r, Segmented s, int i) { var b = s.SegRect(i); Click(r, b.CX, b.CY); }

    static void Drag(UiRoot r, float x, float y, float dx, float dy, bool ctrl = false)
    {
        r.MouseMove(x, y, ctrl, false, false);
        r.MouseDown(x, y, 0, 1, ctrl, false, false);
        for (int i = 1; i <= 10; i++) r.MouseMove(x + dx * i / 10, y + dy * i / 10, ctrl, false, false);
        r.MouseUp(x + dx, y + dy, 0, ctrl, false, false);
    }

    static void Type(UiRoot r, string s) { foreach (char c in s) r.Char(c); }
    static void Key(UiRoot r, int vk) => r.KeyDown(vk, false, false, false);
    static void Settle(UiRoot r, int frames = 20) { for (int i = 0; i < frames; i++) r.Tick(1f / 60f); }
    static void CloseAll(UiRoot r) { while (r.Modal != null) r.CloseOverlay(r.Modal); }

    static UiRoot NewRoot(out AppModel model, float w = 1600, float h = 900, UiPrefs? prefs = null, IUpdateBackend? updates = null)
    {
        EngineFactory.ForceDemo = true;
        model = new AppModel(AppModel.EmptySettings(), new List<DeviceBinding>(), EngineFactory.Create, () => new DemoCatalog(), () => new DemoHost(false), true, null)
        { Post = a => a() };
        model.SetDevicesNow(new DemoCatalog().List());
        model.RunInline = true;
        model.AppCatalogFactory = () => new DemoAppCatalog();
        model.RestorerFactory = () => new DemoRestorer();
        var r = new UiRoot(model, new NullShell(), prefs ?? new UiPrefs { Theme = ThemeMode.Dark }, updates);
        r.Updater.RunInline = true;
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
        Check("Schrift Inter wird aus dem App-Ordner geladen", Renderer.FontName == "Inter", Renderer.FontName);

        // ---- Leerer Start --------------------------------------------------------------------
        var root = NewRoot(out var m);
        Check("Start ist leer: 0 Eingaenge, 0 Ausgaenge", m.InputCount == 0 && m.OutputCount == 0);
        Check("Start: keine Matrix-Verbindungen und keine Zuordnungen", m.Settings.MatrixDb.Length == 0 && m.Bindings.Count == 0);
        var mixer = (MixerPage)root.Pages[0];

        // ---- Kanaele per Klick anlegen --------------------------------------------------------
        Check("Leerzustand zeigt die Anlegen-Tasten", mixer.Inputs.EmptyMono.Visible && !mixer.Inputs.AddButton.Visible);
        Click(root, mixer.Inputs.EmptyMono);
        Check("Klick auf 'Eingang' legt einen Eingang an", m.InputCount == 1, "InputCount=" + m.InputCount);
        Check("neuer Kanal hat keinen vorbelegten Namen", m.Settings.Inputs[0].Name == "");
        var eng0 = m.Engine;
        Click(root, mixer.Outputs.EmptyPair);
        Check("Stereo-Paar legt zwei Ausgaenge an", m.OutputCount == 2 && m.Settings.Outputs[0].Name == "Stereo 1 L" && m.Settings.Outputs[1].Name == "Stereo 1 R");
        Check("Kanalzahl-Aenderung erzeugt neue Engine", !ReferenceEquals(eng0, m.Engine));
        Click(root, mixer.Outputs.AddButton);
        Check("'Hinzufuegen' oeffnet ein Menue", root.Modal is PopupMenu);
        if (root.Modal is PopupMenu pm)
        {
            var r0 = pm.RectOf(pm.IndexOf("Ausgang (mono)"));
            Click(root, r0.CX, r0.CY);
        }
        Check("Menue 'Ausgang (mono)' legt einen Ausgang an und schliesst", m.OutputCount == 3 && root.Modal == null, "OutputCount=" + m.OutputCount);
        m.AddChannels(false, 1); // Ausgang 4
        m.AddChannels(true, 2);  // Eingang 2 + 3
        Check("Anlegen: 3 Eingaenge, 4 Ausgaenge", m.InputCount == 3 && m.OutputCount == 4);
        Check("Matrix passt sich an (3x4)", m.Settings.MatrixDb.Length == 3 && m.Settings.MatrixDb.All(r => r.Length == 4));

        // ---- Kanal-Blatt: Umbenennen ----------------------------------------------------------
        var strip0 = mixer.Inputs.Strips[0];
        Check("Streifen ohne Namen zeigt den Platzhalter", strip0.Header.Name == "Eingang 1" && strip0.Header.NamePlaceholder);
        Click(root, strip0.Header);
        var sheet = root.Modal as ChannelSheet;
        Check("Klick auf den Kanalkopf oeffnet das Kanal-Blatt", sheet != null && sheet.IsInput && sheet.Index == 0);
        Settle(root);
        Click(root, sheet!.Name);
        Check("Klick auf den Namen oeffnet den Editor", root.Editor.Active);
        Type(root, "Front L");
        Key(root, Win32.VK_RETURN);
        Check("Name per Editor gesetzt", m.Settings.Inputs[0].Name == "Front L", m.Settings.Inputs[0].Name);
        Check("Editor danach geschlossen, Blatt bleibt offen", !root.Editor.Active && root.Modal == sheet);
        Click(root, sheet.Name);
        Type(root, "XYZ");
        Key(root, Win32.VK_ESCAPE);
        Check("Esc verwirft die Eingabe", m.Settings.Inputs[0].Name == "Front L");
        Check("Streifen zeigt den neuen Namen", strip0.Header.Name == "Front L" && !strip0.Header.NamePlaceholder);

        // Schalter im Blatt
        Click(root, sheet.MuteSw);
        Check("Blatt: Stumm-Schalter schaltet den Kanal stumm", m.Settings.Inputs[0].Mute);
        Click(root, sheet.MuteSw);
        Check("Blatt: Stumm-Schalter wieder aus", !m.Settings.Inputs[0].Mute);
        Click(root, sheet.PhaseSw);
        Check("Blatt: Phase invertieren", m.Settings.Inputs[0].InvertPhase);
        Click(root, sheet.PhaseSw);
        Click(root, sheet.DelayBox, 2);
        Type(root, "1,5");
        Key(root, Win32.VK_RETURN);
        Check("Blatt: Laufzeit per Eingabe (1,5 ms)", Math.Abs(m.Settings.Inputs[0].DelayMs - 1.5) < 1e-9, "Delay=" + m.Settings.Inputs[0].DelayMs);
        Click(root, sheet.Delay, 2);
        Check("Blatt: Doppelklick auf den Laufzeit-Regler = 0", m.Settings.Inputs[0].DelayMs == 0, "Delay=" + m.Settings.Inputs[0].DelayMs);
        Key(root, Win32.VK_RIGHT);
        Check("Pfeil rechts wechselt zum naechsten Kanal", sheet.Index == 1);
        Key(root, Win32.VK_LEFT);
        Key(root, Win32.VK_ESCAPE);
        Check("Esc schliesst das Blatt", root.Modal == null);

        // ---- Fader ----------------------------------------------------------------------------
        var fader = strip0.Fader;
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

        // ---- Mute / Solo ----------------------------------------------------------------------
        Click(root, strip0.Mute);
        Check("Mute schaltet", m.Settings.Inputs[0].Mute);
        Click(root, strip0.Mute);
        Check("Mute schaltet zurueck", !m.Settings.Inputs[0].Mute);
        Click(root, strip0.Solo);
        Check("Solo schaltet", m.Settings.Inputs[0].Solo);
        Click(root, strip0.Solo);

        // ---- Gain-Sperre (Hochtoener-Schutz) ----------------------------------------------------
        m.UpdateChannel(false, 2, c => c with { GainDb = -13, MaxGainDb = -13, Name = "Hi Links" });
        var hiStrip = mixer.Outputs.Strips[2];
        var hi = hiStrip.Fader;
        Check("Gain-Sperre: Fader zeigt Sperrwert", hi.Value == -13 && hi.MaxDb == -13);
        Check("Gain-Sperre: Streifen markiert den Kanal orange", hiStrip.Header.Mark is { } mk && mk.Equals(Theme.Orange));
        var (hx, hy) = Mid(hi);
        Drag(root, hx + 4, FaderCapY(hi, -13), 0, -300);
        Check("Gesperrter Fader laesst sich nicht ueber -13 dB ziehen", m.Settings.Outputs[2].GainDb <= -13 + 1e-9, "Gain=" + m.Settings.Outputs[2].GainDb);
        Click(root, hx + 4, hi.Bounds.Y + 50, 2);
        Check("Doppelklick (0 dB) wird auf die Sperre geklemmt", m.Settings.Outputs[2].GainDb <= -13 + 1e-9, "Gain=" + m.Settings.Outputs[2].GainDb);
        root.MouseWheel(hx + 4, hy, 5f, false, false, false);
        Check("Mausrad kann die Sperre nicht uebersteigen", m.Settings.Outputs[2].GainDb <= -13 + 1e-9, "Gain=" + m.Settings.Outputs[2].GainDb);
        Click(root, hiStrip.Readout, 2);
        Type(root, "5");
        Key(root, Win32.VK_RETURN);
        Check("Getippter Wert +5 wird auf die Sperre geklemmt", m.Settings.Outputs[2].GainDb <= -13 + 1e-9, "Gain=" + m.Settings.Outputs[2].GainDb);
        m.UpdateChannel(false, 2, c => c with { GainDb = 3 });
        Check("Modell klemmt GainDb auf MaxGainDb", m.Settings.Outputs[2].GainDb == -13);

        // Gain-Sperre im Kanal-Blatt
        Click(root, hiStrip.Header);
        var hs = root.Modal as ChannelSheet;
        Check("Kanal-Blatt des Hochtoeners offen", hs != null && !hs.IsInput && hs.Index == 2);
        Settle(root);
        Check("Blatt: Segment zeigt keine Standardsperre bei -13 dB", hs!.Lock.Selected == -1);
        ClickSeg(root, hs.Lock, 0); // "Aus" = lockern
        Check("Sperre lockern fragt nach", root.Modal is ModalDialog);
        Key(root, Win32.VK_ESCAPE);
        Check("Abbrechen laesst die Sperre bei -13 dB", m.Settings.Outputs[2].MaxGainDb == -13 && root.Modal == hs);
        ClickSeg(root, hs.Lock, 4); // -20 dB = strenger: ohne Rueckfrage
        Check("Strengere Sperre gilt sofort (-20 dB)", m.Settings.Outputs[2].MaxGainDb == -20 && m.Settings.Outputs[2].GainDb <= -20 + 1e-9);
        ClickSeg(root, hs.Ceiling, 2);
        Check("Blatt: Limiter-Grenze -6 dBFS", m.Settings.Outputs[2].CeilingDbfs == -6);
        Click(root, hs.GainBox, 2);
        Type(root, "0");
        Key(root, Win32.VK_RETURN);
        Check("Blatt: getippter Gain wird auf die Sperre geklemmt", m.Settings.Outputs[2].GainDb <= -20 + 1e-9);
        Key(root, Win32.VK_ESCAPE);
        m.UpdateChannel(false, 2, c => c with { MaxGainDb = -13, GainDb = -13, CeilingDbfs = -1 });

        // ---- Matrix ---------------------------------------------------------------------------
        root.SwitchPage(1);
        var mp = (MatrixPage)root.Pages[1];
        var view = mp.View;
        Check("Matrix-Ansicht sichtbar bei vorhandenen Kanaelen", view.Visible && view.Bounds.W > 100);
        var cell = CellCenter(view, 0, 0);
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
        var pc = CellCenter(view, 1, 2); // auf den geschuetzten Hochtoener-Ausgang
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
        var ds = Program.Sheet(root, true, 1);
        Settle(root);
        root.MouseWheel(ds!.Delete.Bounds.CX, root.H - 120, -30f, false, false, false); // Blatt nach unten scrollen
        Check("Blatt scrollt bis 'Kanal loeschen'", ds.Delete.Bounds.Bottom < root.H - 10, "Delete.Y=" + ds.Delete.Bounds.Y);
        Click(root, ds.Delete);
        Check("Kanal loeschen fragt nach", root.Modal is ModalDialog);
        Key(root, Win32.VK_ESCAPE);
        Check("Abbrechen behaelt den Kanal", m.InputCount == 3 && root.Modal == ds);
        Click(root, ds.Delete);
        Key(root, Win32.VK_RETURN);
        Check("Bestaetigen loescht den Kanal und schliesst das Blatt", m.InputCount == 2 && m.Settings.Inputs[1].Name == "B" && root.Modal == null);
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

        // ---- Navigation + Menues --------------------------------------------------------------
        var nav = root2.Side.ItemRect(2);
        Click(root2, nav.CX, nav.CY);
        Check("Seitenleiste: Klick auf 'Geraete' wechselt die Seite", root2.PageIndex == 2);
        root2.KeyDown('2', true, false, false);
        Check("Strg+2 wechselt zur Matrix", root2.PageIndex == 1);
        root2.SwitchPage(2);
        Click(root2, root2.Top.MenuRect.CX, root2.Top.MenuRect.CY);
        Check("Projekt-Menue oeffnet", root2.Modal is PopupMenu);
        Key(root2, Win32.VK_ESCAPE);
        Check("Esc schliesst das Menue", root2.Modal == null);
        Click(root2, root2.Top.AudioRect.CX, root2.Top.AudioRect.CY);
        Check("Audio-Pille fuehrt zur Seite Geraete", root2.PageIndex == 2);
        root2.NewProjectWithConfirm();
        Check("Neues Projekt fragt nach", root2.Modal is ModalDialog);
        Key(root2, Win32.VK_ESCAPE);
        Check("Abbrechen laesst das Projekt unveraendert", m2.InputCount == m.InputCount);
        root2.NewProjectWithConfirm();
        Key(root2, Win32.VK_RETURN);
        Check("Bestaetigen leert das Projekt", m2.InputCount == 0 && m2.OutputCount == 0);

        // ---- Darstellung: Farbmodus, Touch, Seitenleiste ------------------------------------------
        DisplayTests();
        UpdateTests();
        RewImportTests(outFile);
        SpectrumTests();

        AppLinkTests(outFile);
        try
        {
            // Nur Information (kein Pass/Fail): die echten Klassen der Audio-Schicht per Reflection; es wird nichts umgeleitet oder zurueckgesetzt.
            var rr = AudioFactory.CreateRestorer();
            var cc = AudioFactory.CreateAppCatalog();
            Log.AppendLine($"INFO App-Links echt: Restorer={(rr == null ? "fehlt" : rr.IsSupported ? "unterstuetzt" : "nicht unterstuetzt: " + rr.UnsupportedReason)} | Katalog={(cc == null ? "fehlt" : cc.List().Count + " Sitzung(en)")} | Monitor={(typeof(IAppLinkMonitor).IsAssignableFrom(AudioFactory.HostType) ? "AudioHost ja" : "AudioHost nein")}");
        }
        catch (Exception ex) { Log.AppendLine("INFO App-Links echt: Ausnahme " + ex.Message); }

        // ---- Layout bei vielen Groessen, beiden Farbmodi und Touch (nur Zeichnen, keine Ausnahme) ----
        int drawn = 0;
        foreach (var (w, h, sc) in new[] { (1100, 680, 1f), (1280, 720, 1f), (1600, 900, 1f), (1920, 1080, 1f), (2560, 1440, 1f), (2000, 1200, 1.5f), (3840, 2160, 2f) })
        {
            foreach (var (demo, light, touch, collapsed) in new[] { (false, false, false, false), (true, false, false, false), (true, true, true, true) })
            {
                var prefs = new UiPrefs { Theme = light ? ThemeMode.Light : ThemeMode.Dark, Density = touch ? Density.Touch : Density.Mouse, SidebarExpanded = !collapsed };
                var r = NewRoot(out var mm, w / sc, h / sc, prefs);
                if (demo) { var (dset, dbind) = DemoProject.Create(); mm.LoadProject(dset, dbind, 480, false, DemoProject.AppLinks()); mm.LinkStatusSource = DemoProject.LinkStatuses; }
                else { mm.AddChannels(true, 30); mm.AddChannels(false, 30); } // viele Kanaele: Scrollen
                for (int p = 0; p < 4; p++)
                {
                    r.SwitchPage(p);
                    r.Resize(w / sc, h / sc);
                    r.Tick(0.016f);
                    Program.RenderRoot(r, w, h, sc, null);
                    drawn++;
                }
                // Kanal-Blaetter und Auswahl-Popup (Liste, Handeingabe, Konflikt) in jeder Groesse zeichnen
                Program.Sheet(r, false, 2); Settle(r, 15); Program.RenderRoot(r, w, h, sc, null); drawn++; CloseAll(r);
                if (demo)
                {
                    foreach (var (strip, manual) in new[] { (10, false), (10, true), (2, false), (8, false) })
                    {
                        Program.Picker(r, strip, manual);
                        Settle(r, 15);
                        Program.RenderRoot(r, w, h, sc, null);
                        drawn++;
                        CloseAll(r);
                    }
                }
            }
        }
        Theme.Apply(true, Density.Mouse);
        Check($"Zeichnen in {drawn} Groessen/Seiten/Modi ohne Ausnahme", true);
    }

    static void SpectrumTests()
    {
        var root = NewRoot(out var model);
        model.AddChannels(true, 1);
        model.AddChannels(false, 1);
        var mixer = (MixerPage)root.Pages[0];
        Click(root, mixer.Inputs.Strips[0].Eq);
        Check("Spektrum: EQ am Eingangsfader öffnet die EQ-Ansicht", root.Modal is ChannelSheet { EqView: true, IsInput: true });
        CloseAll(root);
        Click(root, mixer.Outputs.Strips[0].Eq);
        Check("Spektrum: EQ am Ausgangsfader öffnet die EQ-Ansicht", root.Modal is ChannelSheet { EqView: true, IsInput: false });
        Settle(root);
        Program.RenderRoot(root, 1600, 900, 1f, null);
        CloseAll(root);

        const int rate = 48000;
        const int toneBin = 170;
        double hz = rate * (double)toneBin / 8192;
        var settings = MixerSettings.Create(rate, 1, 1);
        settings.MatrixDb[0][0] = 0;
        var filter = new FilterSpec(FilterType.Peak, hz, 1, -12);
        settings = settings with { Outputs = new[] { new ChannelDsp { GainDb = -20,
            Correction = new OutputCorrection("Test", new[] { filter }) } } };
        var engine = new MixerEngine(settings);
        var capture = engine.StartSpectrum(false, 0);
        var before = new float[capture.WindowSize];
        var after = new float[capture.WindowSize];
        var signal = new[] { new float[capture.WindowSize] };
        var output = new[] { new float[capture.WindowSize] };
        for (int i = 0; i < signal[0].Length; i++) signal[0][i] = (float)(0.1 * Math.Sin(2 * Math.PI * i * hz / rate));
        engine.Process(signal, output, before.Length);
        long allocated = GC.GetAllocatedBytesForCurrentThread();
        engine.Process(signal, output, before.Length);
        Check("Spektrum: Audio-Abgriffe allokieren im Echtzeitpfad nichts", GC.GetAllocatedBytesForCurrentThread() == allocated);

        const int hop = 480;
        engine.Process(signal, output, hop);
        long frame = 0;
        Check("Spektrum: synchrones Fenster über die Ringgrenze", capture.TryRead(before, after, ref frame) &&
            frame == 2 * capture.WindowSize + hop && before.AsSpan(0, before.Length - hop).SequenceEqual(signal[0].AsSpan(hop)) &&
            before.AsSpan(before.Length - hop).SequenceEqual(signal[0].AsSpan(0, hop)));
        var analyzer = new SpectrumAnalyzer(capture.WindowSize, rate);
        var pre = new float[SpectrumAnalyzer.Points];
        var post = new float[SpectrumAnalyzer.Points];
        analyzer.Analyze(before, pre);
        analyzer.Analyze(after, post);
        int peak = Array.IndexOf(pre, pre.Max());
        Check("Spektrum: FFT zeigt den Testton bei −20 dBFS", Math.Abs(pre[peak] + 20) < 0.1 &&
            Math.Abs(SpectrumAnalyzer.Frequency(peak / (double)(pre.Length - 1), rate) / hz - 1) < 0.03);
        Check("Spektrum: Abgriffe zeigen −12 dB EQ vor dem Ausgangsfader", Math.Abs(post[peak] - pre[peak] + 12) < 0.1);

        engine.StopSpectrum(capture);
        engine.Process(signal, output, before.Length);
        Check("Spektrum: Stoppen beendet den Abgriff", !capture.TryRead(before, after, ref frame));

        settings.MatrixDb[0][0] = double.NegativeInfinity;
        engine = new MixerEngine(settings with { Inputs = new[] { new ChannelDsp { Filters = new[] { filter } } } });
        capture = engine.StartSpectrum(true, 0);
        engine.Process(signal, output, before.Length);
        engine.Process(signal, output, before.Length);
        frame = 0;
        bool readInput = capture.TryRead(before, after, ref frame);
        analyzer.Analyze(after, post);
        Check("Spektrum: Eingangs-EQ auch ohne Matrix-Verbindung sichtbar", readInput && Math.Abs(post[peak] - pre[peak] + 12) < 0.1);
    }

    static void RewImportTests(string outFile)
    {
        var root = NewRoot(out var model, h: 1200);
        model.AddChannels(true, 1);
        model.AddChannels(false, 2);
        model.SetSampleRate(48000);
        model.SetMatrix(0, 0, 0);
        model.SetMatrix(0, 1, 0);
        var protection = new FilterSpec(FilterType.HighPass, 100);
        model.UpdateChannel(false, 0, c => c with { Filters = new[] { protection }, CeilingDbfs = -6 });

        var sheet = ChannelSheet.Show(root, model, false, 0);
        var dir = Path.GetDirectoryName(Path.GetFullPath(outFile))!;
        string filtersFile = Path.Combine(dir, "selftest-rew.txt");
        string projectFile = Path.Combine(dir, "selftest-rew-project.json");
        ((NullShell)root.Shell).OpenResult = filtersFile;
        const string header = "Filter Settings file\n\nEqualiser: Generic\nMessung\n";
        string fullSet = header + string.Join('\n', Enumerable.Range(1, 20).Select(n => $"Filter {n}: ON PK Fc 22000 Hz Gain -120 dB Q 50"));
        using var fullSetReader = new StringReader(fullSet);
        Check("REW: 20 aktive Bänder an den Parametergrenzen von Generic", RewFilterImport.Parse(fullSetReader, 44100).Count == 20);
        try
        {
            File.WriteAllText(filtersFile, header + "Filter 1: ON PK Fc 1000 Hz Gain -60 dB Q 1\n" +
                "Filter 2: OFF PK Fc 150 Hz Gain 6 dB Q 3\nFilter 1: ON None\nFilter 2: ON None\n");
            Click(root, sheet.RewImport);
            Key(root, Win32.VK_ESCAPE);
            Check("REW: Abbrechen übernimmt keine Filter", model.Channel(false, 0).Correction == null);
            Click(root, sheet.RewImport);
            Key(root, Win32.VK_RETURN);
            var channel = model.Channel(false, 0);
            var eq = channel.Correction;
            Check("REW: nur aktive Peak-Filter auf dem gewählten Ausgang", eq?.Filters is [var filter] &&
                filter == new FilterSpec(FilterType.Peak, 1000, 1, -60) && model.Channel(false, 1).Correction == null);
            Check("REW: Weiche und Limiter bleiben getrennt", channel.Filters.SequenceEqual(new[] { protection }) && channel.CeilingDbfs == -6);

            var input = new[] { new float[480] };
            var output = new[] { new float[480], new float[480] };
            for (int i = 0; i < 480; i++) input[0][i] = (float)(0.1 * Math.Sin(2 * Math.PI * i / 48));
            double OutputRatio()
            {
                var engine = new MixerEngine(model.Settings);
                for (int i = 0; i < 100; i++) engine.Process(input, output, 480);
                return Math.Sqrt(output[0].Sum(x => x * x) / output[1].Sum(x => x * x));
            }
            Check("REW: echter Audioausgang erreicht −60 dB", Math.Abs(OutputRatio() - 0.001) < 0.00002);
            Click(root, sheet.RewBypass);
            Check("REW: Bypass umgeht nur die Korrektur", model.Channel(false, 0).Correction?.Bypassed == true && Math.Abs(OutputRatio() - 1) < 0.001);

            ProjectStore.WriteFile(projectFile, ProjectStore.ToDto(model, null));
            var dto = ProjectStore.ReadFile(projectFile);
            var (restored, _) = ProjectStore.FromDto(dto);
            Check("REW: Filter, Herkunft und Bypass werden im Projekt gespeichert", dto.Version == 2 &&
                restored.Outputs[0].Correction is { Bypassed: true, SourceName: "selftest-rew.txt", Filters: [var saved] } &&
                eq != null && saved == eq.Filters[0]);

            File.WriteAllText(filtersFile, "Include: andere-datei.txt\n");
            Click(root, sheet.RewImport);
            Check("REW: APO-Konfiguration wird abgelehnt und bestehender EQ bleibt erhalten", root.Modal is ModalDialog d &&
                d.TitleText == "REW-Import fehlgeschlagen" && model.Channel(false, 0).Correction?.Bypassed == true);
            Key(root, Win32.VK_RETURN);
        }
        finally
        {
            try { File.Delete(filtersFile); } catch { }
            try { File.Delete(projectFile); } catch { }
        }
    }

    // ================================================================================================
    // Updates (Attrappe statt GitHub): finden, laden, nie von selbst installieren, sicher neu starten
    // ================================================================================================
    static void UpdateTests()
    {
        var r0 = NewRoot(out _);
        r0.StartUpdateCheck();
        Check("Update: Build aus dem Quellcode meldet 'nicht installiert'", r0.Updater.Stage == UpdateStage.NotInstalled && r0.Top.UpdateRect.IsEmpty);

        var fake = new FakeUpdateBackend { Offer = new UpdateOffer("9.9.0", "## NX-MIX 9.9.0: Test\n\n### Neu\n\n- **Etwas:** neu") };
        var root = NewRoot(out _, updates: fake);
        var shell = (NullShell)root.Shell;
        root.StartUpdateCheck();
        Check("Update: Suche beim Start findet 9.9.0", root.Updater.Stage == UpdateStage.Available && root.Updater.Offer?.Version == "9.9.0");
        Check("Update: Kopfleiste zeigt die Update-Pille", !root.Top.UpdateRect.IsEmpty);
        Check("Update: nichts wird von selbst geladen oder installiert", fake.Restarts == 0 && fake.ExitInstalls == 0 && root.Modal == null);
        var ur = root.Top.UpdateRect;
        Click(root, ur.CX, ur.CY);
        Check("Update: Klick auf die Pille zeigt Neuigkeiten und fragt", root.Modal is ModalDialog d && d.TitleText.Contains("9.9.0"));
        Key(root, Win32.VK_ESCAPE);
        Check("Update: 'Spaeter' laedt nichts", root.Updater.Stage == UpdateStage.Available && root.Modal == null);
        root.ShowUpdate();
        Key(root, Win32.VK_RETURN);
        Check("Update: 'Herunterladen' laedt das Update (bereit, 100 %)", root.Updater.Stage == UpdateStage.Ready && root.Updater.Progress == 100);
        Check("Update: geladen, aber noch nicht installiert", fake.Restarts == 0 && fake.ExitInstalls == 0);
        root.ShowUpdate();
        Check("Update: bereit-Dialog bietet Neustart oder 'beim Beenden'", root.Modal is ModalDialog d2 && d2.TitleText == "Update bereit");
        Key(root, Win32.VK_RETURN);
        Check("Update: 'Jetzt neu starten' speichert/stoppt zuerst und installiert dann", shell.ExitPrepared == 1 && fake.Restarts == 1);

        var fake2 = new FakeUpdateBackend { Offer = new UpdateOffer("9.9.0", null) };
        var root2 = NewRoot(out _, updates: fake2);
        root2.Updater.Check(); root2.Updater.Download(); root2.Updater.InstallOnExit();
        Check("Update: 'Beim Beenden installieren' meldet sich beim Updater an, ohne Neustart", fake2.ExitInstalls == 1 && fake2.Restarts == 0 && root2.Updater.InstallOnExitPending);

        var root3 = NewRoot(out _, prefs: new UiPrefs { AutoUpdateCheck = false }, updates: new FakeUpdateBackend { Offer = new UpdateOffer("9.9.0", null) });
        root3.StartUpdateCheck();
        Check("Update: Suche beim Start abschaltbar", root3.Updater.Stage == UpdateStage.Idle);
        var root4 = NewRoot(out _, updates: new FakeUpdateBackend { FailWith = "offline" });
        root4.Updater.Check();
        Check("Update: Netzwerkfehler wird gemeldet, nicht geworfen", root4.Updater.Stage == UpdateStage.Failed && root4.Updater.Error == "offline");
        var root5 = NewRoot(out _, updates: new FakeUpdateBackend());
        root5.Updater.Check();
        Check("Update: aktuelle Version = 'neueste Version'", root5.Updater.Stage == UpdateStage.UpToDate && root5.Top.UpdateRect.IsEmpty);
        root.SwitchPage(3);
        Program.RenderRoot(root, 1600, 900, 1f, null);
        Check("Update: Einstellungen zeigen Version und Update-Knopf", ((SettingsPage)root.Pages[3]).UpdateButton.Visible);
    }

    // ================================================================================================
    // Darstellung: Farbmodus, Maus/Touch, Seitenleiste
    // ================================================================================================
    static void DisplayTests()
    {
        var shell = new NullShell { SystemDark = false };
        EngineFactory.ForceDemo = true;
        var m = new AppModel(AppModel.EmptySettings(), new List<DeviceBinding>(), EngineFactory.Create, () => new DemoCatalog(), () => new DemoHost(false), true, null) { Post = a => a() };
        m.SetDevicesNow(new DemoCatalog().List());
        var root = new UiRoot(m, shell, new UiPrefs { Theme = ThemeMode.System, SidebarExpanded = true });
        root.Resize(1600, 900);
        Check("Farbmodus 'System' folgt Windows (hell)", !Theme.IsDark);
        shell.SystemDark = true;
        root.SystemThemeChanged();
        Check("Windows wechselt auf dunkel: Oberflaeche folgt", Theme.IsDark);

        root.SwitchPage(3);
        var sp = (SettingsPage)root.Pages[3];
        Program.RenderRoot(root, 1600, 900, 1f, null); // Layout der Einstellungen
        Check("Einstellungen: Gruppen nutzen die ganze Inhaltsbreite",
            sp.ContentArea.W > 1200 && Math.Abs(sp.ContentArea.X - root.ContentRect.X - Theme.Pad) < 1 &&
            Math.Abs(sp.ContentArea.Right - (root.ContentRect.Right - Theme.Pad - 20)) < 1);
        float sliderX = sp.UiScaleSlider.Bounds.X + 10 + (sp.UiScaleSlider.Bounds.W - 20) * 2 / 3;
        Click(root, sliderX, sp.UiScaleSlider.Bounds.CY);
        Check("UI-Skalierung: Regler stellt 125 % sofort ein",
            root.Prefs.UiScalePercent == 125 && ((NullShell)root.Shell).ScaleChanges == 1);
        root.Resize(1600 / 1.25f, 900 / 1.25f);
        Program.RenderRoot(root, 1600, 900, 1.25f, null);
        Check("UI-Skalierung: Regler bleibt nach Neu-Layout synchron", sp.UiScaleSlider.Value == 125);
        root.SetUiScale(500);
        root.Resize(900 / 1.5f, 600 / 1.5f);
        Program.RenderRoot(root, 900, 600, 1.5f, null);
        Check("UI-Skalierung: 150 % bleibt in schmalem Fenster bedienbar",
            root.Prefs.UiScalePercent == 150 && sp.UiScaleSlider.Bounds.X >= root.ContentRect.X &&
            sp.UiScaleSlider.Bounds.Right <= root.ContentRect.Right);
        Click(root, sp.UiScaleSlider, clicks: 2);
        root.Resize(1600, 900);
        Check("UI-Skalierung: Doppelklick setzt 100 % wieder her",
            root.Prefs.UiScalePercent == 100 && sp.UiScaleSlider.Value == 100 && ((NullShell)root.Shell).ScaleChanges == 3);
        ClickSeg(root, sp.ThemeSeg, 1);
        Check("Einstellungen: 'Hell' schaltet auf hell und merkt es", !Theme.IsDark && root.Prefs.Theme == ThemeMode.Light);
        Program.RenderRoot(root, 1600, 900, 1f, null);
        ClickSeg(root, sp.ThemeSeg, 2);
        Check("Einstellungen: 'Dunkel' schaltet auf dunkel", Theme.IsDark && root.Prefs.Theme == ThemeMode.Dark);
        shell.SystemDark = false;
        root.SystemThemeChanged();
        Check("Fester Farbmodus ignoriert den Windows-Wechsel", Theme.IsDark);

        float mouseCap = Theme.FaderCapH, mouseStrip = Theme.StripMinW;
        Program.RenderRoot(root, 1600, 900, 1f, null);
        ClickSeg(root, sp.DensitySeg, 1);
        Check("Einstellungen: 'Touch' vergroessert Fader-Kappen und Streifen", Theme.Touch && Theme.FaderCapH > mouseCap && Theme.StripMinW > mouseStrip && root.Prefs.Density == Density.Touch);
        Program.RenderRoot(root, 1600, 900, 1f, null);
        ClickSeg(root, sp.DensitySeg, 0);
        Check("Einstellungen: 'Maus' wieder kompakt", !Theme.Touch && Theme.FaderCapH == mouseCap);

        float wide = root.Side.Width;
        Program.RenderRoot(root, 1600, 900, 1f, null);
        Click(root, sp.SidebarSw.Track.CX, sp.SidebarSw.Track.CY);
        Check("Seitenleiste einklappen macht den Inhalt breiter", !root.Side.Expanded && root.Side.Width < wide && root.ContentRect.X < wide);
        var col = root.Side.CollapseRect;
        Click(root, col.CX, col.CY);
        Check("Knopf unten klappt die Seitenleiste wieder aus", root.Side.Expanded && root.Prefs.SidebarExpanded);
        Theme.Apply(true, Density.Mouse);
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

        Program.Picker(root, 2);
        Check("Programm binden an einem belegten Eingang oeffnet das Popup", root.Modal is AppLinkPicker);
        Key(root, Win32.VK_DOWN);
        Key(root, Win32.VK_RETURN);
        Check("Popup an belegtem Eingang bindet nichts", m.AppLinks.Count == 0);
        Key(root, Win32.VK_ESCAPE);
        Check("Esc schliesst das Popup, das Kanal-Blatt bleibt", root.Modal is ChannelSheet);
        Key(root, Win32.VK_ESCAPE);
        Check("Zweites Esc schliesst das Blatt", root.Modal == null);

        // ---- Bedienung: Kanalkopf -> Blatt -> "Programm binden" -> Filter tippen -> Enter -------------
        Click(root, mixer.Inputs.Strips[4].Header);
        var sheet = root.Modal as ChannelSheet;
        Settle(root);
        Check("Blatt eines freien Eingangs bietet 'Programm binden'", sheet != null && sheet.LinkBind.Visible && sheet.LinkBind.Enabled && !sheet.LinkRemove.Visible);
        Click(root, sheet!.LinkBind);
        Check("Klick auf 'Programm binden' oeffnet das Auswahl-Popup", root.Modal is AppLinkPicker);
        Type(root, "vlc");
        Key(root, Win32.VK_RETURN);
        var vlc = m.LinkAt(4);
        Check("Filter 'vlc' + Enter bindet VLC (mono) an Eingang 5", vlc != null && vlc.ExeName == "vlc.exe" && vlc.Count == 1 && vlc.DisplayName == "VLC media player", vlc?.ToString() ?? "null");
        Check("Popup schliesst nach der Auswahl, Blatt bleibt", root.Modal == sheet);
        Settle(root);
        Program.RenderRoot(root, 1600, 900, 1f, null);
        Check("Blatt bietet danach 'Loesen' an", sheet.LinkRemove.Visible);
        Check("Gebundener Streifen zeigt den Programmnamen", mixer.Inputs.Strips[4].Header.Name == "VLC media player");
        CloseAll(root);

        // ---- Handeingabe ------------------------------------------------------------------------------
        Program.Picker(root, 5, true);
        Type(root, "C:\\Programme\\Chrome");
        Key(root, Win32.VK_RETURN);
        var chrome = m.LinkAt(5);
        Check("Handeingabe normalisiert Pfad/Endung: 'Chrome.exe'", chrome != null && chrome.ExeName == "Chrome.exe" && chrome.Count == 1, chrome?.ExeName ?? "null");
        Check("Handeingabe kennt Programme der Liste (Anzeigename)", chrome?.DisplayName == "Google Chrome", chrome?.DisplayName ?? "null");
        CloseAll(root);

        // ---- Stereo-Paar, Duplikate, ungueltige Namen -------------------------------------------------
        Check("Stereo-App-Link auf das benannte Paar 1+2", m.SetLink(0, 2, "Spotify.exe", "Spotify") == null && m.LinkAt(1)?.FirstChannel == 0 && m.LinkAt(1)?.Count == 2);
        m.UpdateChannel(true, 0, c => c with { Name = "" });
        m.UpdateChannel(true, 1, c => c with { Name = "" });
        Check("Ohne eigenen Namen: Streifen heissen 'Spotify L' / 'Spotify R'", mixer.Inputs.Strips[1].Header.Name == "Spotify R" && mixer.Inputs.Strips[0].Header.Name == "Spotify L",
            mixer.Inputs.Strips[0].Header.Name + " / " + mixer.Inputs.Strips[1].Header.Name);
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
        mixer.Inputs.SyncAll();
        Check("Streifen mit Fehler-Link bekommt eine rote Marke", mixer.Inputs.Strips[4].Header.Mark is { } fm && fm.Equals(Theme.Red));
        m.LinkStatusSource = null;
        m.Tick(0.3f);
        Check("Ohne Host-Status: bereit (Audio gestoppt)", m.ViewOf(m.LinkAt(0)!).State == LinkUi.Idle);

        // ---- Umleitungen zuruecksetzen ---------------------------------------------------------------------------
        string? resetErr = "nicht aufgerufen";
        int before = restorer.Calls;
        m.ResetAllRoutes(e => resetErr = e);
        Check("'Alle Umleitungen zuruecksetzen' ruft RestoreAllKnown", restorer.Calls == before + 1 && resetErr == null);
        root.SwitchPage(3); Program.RenderRoot(root, 1600, 900, 1f, null); root.Resize(1100, 680);
        m.SetParking(null); Program.RenderRoot(root, 1100, 680, 1f, null);
        Check("Einstellungsseite mit App-Link-Abschnitt zeichnet (mit/ohne Parkplatz, niedriges Fenster scrollt)", true);
        root.MouseWheel(600, 400, -5f, false, false, false);
        Program.RenderRoot(root, 1100, 680, 1f, null);
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
        Check("Geladener Streifen zeigt den Link", ((MixerPage)root2.Pages[0]).Inputs.Strips[1].Header.Name == "Spotify R");

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
        _ = root3; _ = root4; _ = root5; _ = root6;
    }

    static float FaderCapY(Fader f, double db)
    {
        // gleiche Geometrie wie Fader: Kappenmitte fuer den Wert
        float capH = Theme.FaderCapH;
        float yTop = f.Bounds.Y + capH / 2 + 1, yBot = f.Bounds.Bottom - capH / 2 - 1;
        double t = FaderScale.ToT(db) / FaderScale.ToT(f.ScaleTop);
        return (float)(yBot - Math.Clamp(t, 0, 1) * (yBot - yTop));
    }

    static (float X, float Y) CellCenter(MatrixView v, int i, int o)
    {
        var r = v.CellRect(i, o);
        return (r.CX, r.CY);
    }
}
