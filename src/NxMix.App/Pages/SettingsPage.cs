using NxMix.App.Model;
using NxMix.App.Ui;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Einstellungen: Projekt (neu/Export/Import), Engine-Info, Sicherheit, Tastenkuerzel, App-Links (Parkplatz). Scrollt senkrecht, wenn das Fenster zu niedrig ist.</summary>
internal sealed class SettingsPage : Page
{
    readonly AppModel _m;
    readonly TextButton _new = new("Neues leeres Projekt", ButtonStyle.Danger) { Font = Theme.SmallBold, Radius = 8 };
    readonly TextButton _export = new("Exportieren …", ButtonStyle.Secondary) { Font = Theme.SmallBold, Radius = 8 };
    readonly TextButton _import = new("Importieren …", ButtonStyle.Secondary) { Font = Theme.SmallBold, Radius = 8 };
    // App-Links
    readonly Dropdown _parking = new() { Tooltip = "Parkplatz-Ausgang: stilles virtuelles Gerät, auf das Programme umgeleitet werden" };
    readonly TextButton _reset = new("Alle Umleitungen zurücksetzen", ButtonStyle.Secondary) { Font = Theme.SmallBold, Radius = 8, Tooltip = "Setzt jedes umgeleitete Programm zurück auf „System-Standard“" };
    readonly ToggleButton _linksOn = new("Aktiv", Theme.Accent) { Font = Theme.SmallBold, Tooltip = "Aus: Programme laufen normal, die Links bleiben im Projekt gespeichert" };
    readonly VScroll _sc = new();
    RectF _cProject, _cEngine, _cSafety, _cKeys, _cLinks;
    RectF _rParkLabel, _rStatus, _rWin, _rOnLabel;
    float _leftIntroH, _leftWarnH;

    const string Intro = "Ein App-Link bindet ein Programm (z. B. Spotify oder Chrome) direkt an einen Eingangs-Fader: Sein Ton läuft dann nur noch über NX-MIX und nirgends sonst. Dafür leitet Windows die Ausgabe des Programms auf den Parkplatz um, und NX-MIX greift den Ton dort ab.";
    const string Warn = "Der Parkplatz muss ein virtuelles, stilles Ausgabegerät sein, an dem NICHTS weitergeleitet wird – nicht in einer VB-Matrix-, Voicemeeter- oder Cable-Kette und nicht auf Lautsprecher. Sonst wird der Ton dort hörbar. Das Standardgerät lässt sich nicht wählen.";

    public override string Title => "Einstellungen";
    public override bool AcceptsMouse => true;

    public SettingsPage(AppModel m)
    {
        _m = m;
        Add(_new); Add(_export); Add(_import);
        Add(_parking); Add(_reset); Add(_linksOn);
        _new.Clicked += () => Root?.NewProjectWithConfirm();
        _export.Clicked += () => Root?.ExportProject();
        _import.Clicked += () => Root?.ImportProject();
        _parking.Text = ParkingText;
        _parking.Items = ParkingItems;
        _reset.Clicked += ResetRoutes;
        _linksOn.Toggled += on => _m.SetAppLinksEnabled(on);
        m.StructureChanged += Invalidate;
        m.HostChanged += Invalidate;
    }

    public override void OnShow() { Layout(); Invalidate(); }

    // ---- Parkplatz-Auswahl -----------------------------------------------------------------------
    string ParkingText()
    {
        var d = _m.ParkingDevice;
        if (d != null) return d.Name;
        if (!string.IsNullOrEmpty(_m.ParkingDeviceId)) return $"{_m.ParkingDeviceName ?? _m.ParkingDeviceId} (nicht gefunden)";
        return "Nicht gewählt";
    }

    List<MenuItem> ParkingItems()
    {
        var items = new List<MenuItem> { new("Kein Parkplatz", null, () => _m.SetParking(null)) { Checked = string.IsNullOrEmpty(_m.ParkingDeviceId) } };
        var cands = _m.ParkingCandidates.ToList();
        foreach (var d in cands)
        {
            var dd = d;
            items.Add(new($"{d.Name}  ({d.Channels} Kanäle)", null, () => _m.SetParking(dd.Id)) { Checked = d.Id == _m.ParkingDeviceId });
        }
        if (cands.Count == 0) items.Add(new(_m.DevicesLoading ? "Lese Geräte ein …" : "Keine passenden Ausgabegeräte gefunden", null, null, false));
        items.Add(MenuItem.Sep);
        items.Add(new("Das Standardgerät ist nicht wählbar", null, null, false));
        return items;
    }

    void ResetRoutes()
    {
        var root = Root; if (root == null) return;
        void Run()
        {
            _m.ResetAllRoutes(err =>
            {
                if (err == null) root.Status.Flash("Alle Umleitungen zurückgesetzt – Programme laufen wieder über ihr normales Gerät.");
                else root.Status.Flash("Zurücksetzen fehlgeschlagen: " + err, true);
            });
        }
        if (_m.HostRunning && _m.HasActiveLinks)
            root.Confirm("Umleitungen jetzt zurücksetzen?", "Audio läuft und Programme sind an Eingänge gebunden. Nach dem Zurücksetzen spielen sie wieder über ihr normales Gerät – ihr Ton kommt dann aus den normalen Lautsprechern, nicht mehr nur über NX-MIX.",
                "Zurücksetzen", Run, danger: true);
        else Run();
    }

    // ---- Layout ----------------------------------------------------------------------------------
    const float H1 = 232, H2 = 216, Gap = 12;

    string LinksLine()
    {
        if (_m.AppLinks.Count == 0) return "Noch kein Programm gebunden – am Eingangsstreifen auf den Ketten-Knopf klicken.";
        return "Gebunden: " + string.Join("  ·  ", _m.AppLinks.Take(4).Select(l => $"{l.Title} → Eingang {(l.Count == 2 ? $"{l.FirstChannel + 1:00}–{l.FirstChannel + 2:00}" : $"{l.FirstChannel + 1:00}")}"))
            + (_m.AppLinks.Count > 4 ? $"  ·  +{_m.AppLinks.Count - 4}" : "");
    }

    float LinksCardHeight(float cardW)
    {
        float lw = cardW / 2 - 30;
        _leftIntroH = Renderer.TextHeight(Intro, Theme.Small, lw);
        _leftWarnH = Renderer.TextHeight(Warn, Theme.Small, lw - 14);
        float left = 46 + _leftIntroH + 12 + _leftWarnH + 12 + 20 + 16;
        return Math.Max(256, left);
    }

    public override void Layout()
    {
        var full = new RectF(Bounds.X + Theme.Pad, Bounds.Y + 12, Math.Max(0, Bounds.W - 2 * Theme.Pad), 0);
        float colW = (full.W - 12) / 2;
        float linksH = LinksCardHeight(full.W);
        float contentH = 40 + H1 + Gap + H2 + Gap + linksH + 12 + 12;
        _sc.Update(Bounds, contentH);
        float oy = -_sc.Offset;
        float top = full.Y + 40 + oy;
        _cProject = new RectF(full.X, top, colW, H1);
        _cEngine = new RectF(full.X + colW + 12, top, colW, H1);
        _cSafety = new RectF(full.X, top + H1 + Gap, colW, H2);
        _cKeys = new RectF(full.X + colW + 12, top + H1 + Gap, colW, H2);
        _cLinks = new RectF(full.X, top + H1 + Gap + H2 + Gap, full.W, linksH);
        float bx = _cProject.X + 20, by = _cProject.Bottom - 58;
        _new.Bounds = new RectF(bx, by, 180, 34);
        _export.Bounds = new RectF(bx + 190, by, 120, 34);
        _import.Bounds = new RectF(bx + 320, by, 120, 34);

        // App-Links: rechte Spalte
        float rx = _cLinks.X + _cLinks.W / 2 + 10, rw = _cLinks.W / 2 - 30;
        _rParkLabel = new RectF(rx, _cLinks.Y + 46, rw, 14);
        _parking.Bounds = new RectF(rx, _cLinks.Y + 62, Math.Min(rw, 460), 34);
        _rStatus = new RectF(rx, _cLinks.Y + 104, rw, 40);
        _rWin = new RectF(rx, _cLinks.Y + 148, rw, 22);
        _reset.Bounds = new RectF(rx, _cLinks.Y + 186, 232, 34);
        _rOnLabel = new RectF(rx + 250, _cLinks.Y + 186, 70, 34);
        _linksOn.Bounds = new RectF(rx + 332, _cLinks.Y + 186, 86, 34);
    }

    void UpdateWidgets()
    {
        bool run = _m.HostRunning || _m.HostBusy;
        _parking.Enabled = !run;
        _linksOn.Enabled = !run;
        _linksOn.SetChecked(_m.AppLinksEnabled);
        _reset.Enabled = true;
        _parking.Tooltip = run ? "Zum Ändern zuerst Audio stoppen" : "Parkplatz-Ausgang: stilles virtuelles Gerät, auf das Programme umgeleitet werden";
    }

    // ---- Maus (Scrollen) -------------------------------------------------------------------------
    public override void OnMouseDown(in MouseEvent e) { if (e.Button == 0 && _sc.Down(e.X, e.Y)) { Layout(); Invalidate(); } }
    public override void OnMouseMove(in MouseEvent e) { if (_sc.Dragging && IsPressed) { _sc.Drag(e.Y); Layout(); Invalidate(); } }
    public override void OnMouseUp(in MouseEvent e) { if (_sc.Dragging) { _sc.Up(); Invalidate(); } }
    public override bool OnMouseWheel(in MouseEvent e)
    {
        if (!_sc.Wheel(e.Wheel)) return false;
        Layout(); Invalidate();
        return true;
    }

    // ---- Zeichnen --------------------------------------------------------------------------------
    void Card(Renderer g, RectF r, string title)
    {
        g.FillRound(r, Theme.RadiusPanel, Theme.Panel);
        g.StrokeRound(r, Theme.RadiusPanel, Theme.Border);
        g.Text(title, new RectF(r.X + 20, r.Y + 14, r.W - 40, 24), Theme.BodyBold, Theme.Text);
    }

    void Line(Renderer g, RectF card, int i, string label, string value, Color4? vc = null)
    {
        float y = card.Y + 48 + i * 26;
        g.Text(label, new RectF(card.X + 20, y, 150, 22), Theme.Small, Theme.TextFaint, HAlign.Left, VAlign.Middle, false);
        g.Text(value, new RectF(card.X + 170, y, card.W - 190, 22), Theme.Small, vc ?? Theme.Text, HAlign.Left, VAlign.Middle, true);
    }

    public override void Paint(Renderer g)
    {
        UpdateWidgets();
        Layout();
        g.PushClip(Bounds);
        float oy = -_sc.Offset;
        var a = new RectF(Bounds.X + Theme.Pad, Bounds.Y + 12 + oy, Bounds.W - 2 * Theme.Pad, 30);
        g.Text("Einstellungen", new RectF(a.X + 2, a.Y, 300, 28), Theme.Heading, Theme.Text);

        Card(g, _cProject, "Projekt");
        g.Text("Alles wird automatisch gespeichert – beim Ändern (kurz verzögert) und beim Beenden.", new RectF(_cProject.X + 20, _cProject.Y + 44, _cProject.W - 40, 20), Theme.Small, Theme.TextDim, HAlign.Left, VAlign.Middle, true);
        string path = ProjectStore.Enabled ? ProjectStore.FilePath : "(Vorschau – es wird nichts gespeichert)";
        g.Text("Speicherort", new RectF(_cProject.X + 20, _cProject.Y + 76, 100, 20), Theme.Small, Theme.TextFaint, HAlign.Left, VAlign.Middle, false);
        g.Text(path, new RectF(_cProject.X + 20, _cProject.Y + 96, _cProject.W - 40, 22), Theme.Small, Theme.Text, HAlign.Left, VAlign.Middle, true);
        g.Text("Gain-Sperren, Namen, Matrix, Geräte-Zuordnungen, App-Links und Fenstergröße gehören zum Projekt.", new RectF(_cProject.X + 20, _cProject.Y + 118, _cProject.W - 40, 20), Theme.Small, Theme.TextFaint, HAlign.Left, VAlign.Middle, true);

        Card(g, _cEngine, "Engine");
        Line(g, _cEngine, 0, "Engine", _m.EngineName, _m.EngineIsDemo ? Theme.Solo : Theme.Text);
        Line(g, _cEngine, 1, "Samplerate", $"{_m.Settings.SampleRate / 1000.0:0.##} kHz  (änderbar auf der Seite „Geräte“)");
        Line(g, _cEngine, 2, "Kanäle", $"{Fmt.Inputs(_m.InputCount)} · {Fmt.Outputs(_m.OutputCount)}");
        Line(g, _cEngine, 3, "Limiter-Treffer", _m.Engine.LimiterHits.ToString());
        Line(g, _cEngine, 4, "Audio-Host", _m.HostAvailable ? (_m.HostRunning ? "läuft" : "bereit, gestoppt") : "nicht verfügbar", _m.HostAvailable ? Theme.Text : Theme.Solo);
        Line(g, _cEngine, 5, "Kanalzahl ändern", "erzeugt eine neue Engine (Audio stoppt kurz)", Theme.TextDim);

        Card(g, _cSafety, "Sicherheit");
        string[] lines =
        {
            "Gain-Sperre: pro Kanal über das Schloss-Symbol einstellbar (Fader lässt sich nicht darüber ziehen).",
            "Limiter-Grenze: pro Ausgang im Kanal-Menü (⋯) einstellbar, Marke am Meter.",
            "Matrix-Punkte auf Ausgänge mit starker Gain-Sperre bleiben auf höchstens 0 dB begrenzt.",
            "Audio startet nie automatisch; vor dem ersten Start je Sitzung fragt NX-MIX nach.",
            "App-Links: Programme werden auf den Parkplatz umgeleitet. NX-MIX stellt alle Umleitungen beim Start und beim Beenden wieder her.",
        };
        float y = _cSafety.Y + 46;
        foreach (var l in lines)
        {
            float h = Renderer.TextHeight(l, Theme.Small, _cSafety.W - 56);
            g.FillCircle(_cSafety.X + 26, y + 9, 2.2f, Theme.Protect);
            g.Text(l, new RectF(_cSafety.X + 38, y, _cSafety.W - 56, h + 4), Theme.Small, Theme.TextDim, HAlign.Left, VAlign.Top, false, true);
            y += h + 8;
        }

        Card(g, _cKeys, "Tastenkürzel");
        (string, string)[] keys =
        {
            ("Strg+1 … 4", "Seite wechseln"), ("Strg+N", "Neues leeres Projekt"), ("Strg+E", "Projekt exportieren"), ("Strg+O", "Projekt importieren"),
            ("Doppelklick", "Fader: 0 dB · Name: umbenennen · Wert: tippen"), ("Strg / Umschalt", "Feinjustierung beim Ziehen und Mausrad"),
        };
        for (int i = 0; i < keys.Length; i++)
        {
            float ky = _cKeys.Y + 46 + i * 26;
            var chip = new RectF(_cKeys.X + 20, ky, Renderer.MeasureWidth(keys[i].Item1, Theme.SmallBold) + 18, 22);
            g.FillRound(chip, 6, Theme.Raised);
            g.Text(keys[i].Item1, chip, Theme.SmallBold, Theme.Text, HAlign.Center, VAlign.Middle, false);
            g.Text(keys[i].Item2, new RectF(chip.Right + 12, ky, _cKeys.W - chip.W - 56, 22), Theme.Small, Theme.TextDim, HAlign.Left, VAlign.Middle, true);
        }

        PaintLinksCard(g);
        PaintChildren(g);
        g.PopClip();
        _sc.Paint(g);
    }

    void PaintLinksCard(Renderer g)
    {
        var c = _cLinks;
        Card(g, c, "App-Links");
        float lx = c.X + 20, lw = c.W / 2 - 30;

        // Kopf: Zustand der Windows-Unterstuetzung als Chip
        string? un = _m.AppLinksUnsupportedReason();
        bool unavailable = _m.Restorer == null;
        (string chipText, Color4 chipCol) = unavailable ? ("NICHT VERFÜGBAR", Theme.Solo) : un != null ? ("WINDOWS: NICHT UNTERSTÜTZT", Theme.MuteText) : ("WINDOWS: UNTERSTÜTZT", Theme.Ok);
        float cw = Renderer.MeasureWidth(chipText, Theme.TinyBold) + 26;
        var chip = new RectF(c.X + 20 + Renderer.MeasureWidth("App-Links", Theme.BodyBold) + 14, c.Y + 16, cw, 20);
        g.Fill(chip, chipCol.With(0.12f));
        g.StrokeRect(chip, chipCol.With(0.5f));
        g.FillCircle(chip.X + 10, chip.CY, 3f, chipCol);
        g.Text(chipText, new RectF(chip.X + 19, chip.Y, cw - 22, chip.H), Theme.TinyBold, chipCol, HAlign.Left, VAlign.Middle, false);

        // linke Spalte: Erklaerung, Warnung, Liste der Links
        float y = c.Y + 46;
        g.Text(Intro, new RectF(lx, y, lw, _leftIntroH + 4), Theme.Small, Theme.TextDim, HAlign.Left, VAlign.Top, false, true);
        y += _leftIntroH + 12;
        var wr = new RectF(lx, y, lw, _leftWarnH + 12);
        g.Fill(wr, Theme.Solo.With(0.10f));
        g.StrokeRect(wr, Theme.Solo.With(0.45f));
        g.Fill(new RectF(wr.X, wr.Y, 3, wr.H), Theme.Solo);
        g.Text(Warn, new RectF(wr.X + 12, wr.Y + 6, wr.W - 20, _leftWarnH + 4), Theme.Small, Theme.Text, HAlign.Left, VAlign.Top, false, true);
        y = wr.Bottom + 12;
        g.Text(LinksLine(), new RectF(lx, y, lw, 20), Theme.Small, _m.AppLinks.Count == 0 ? Theme.TextFaint : Theme.TextDim, HAlign.Left, VAlign.Middle, true);

        // rechte Spalte
        g.Text("PARKPLATZ-AUSGANG", _rParkLabel, Theme.Tiny, Theme.TextFaint, HAlign.Left, VAlign.Middle, false);

        // Status des Parkplatzes
        string msg; Color4 col; bool good = false;
        var park = _m.ParkingDevice;
        if (string.IsNullOrEmpty(_m.ParkingDeviceId)) { msg = "App-Links bleiben aus, bis ein Parkplatz gewählt ist."; col = Theme.Solo; }
        else if (park == null)
        {
            msg = _m.Devices.Count == 0 ? "Geräteliste noch leer – Parkplatz lässt sich nicht prüfen." : $"Das Gerät „{_m.ParkingDeviceName ?? _m.ParkingDeviceId}“ wurde nicht gefunden (nicht angeschlossen?). App-Links bleiben aus.";
            col = Theme.Solo;
        }
        else if (!AppModel.IsValidParking(park)) { msg = "Das Standardgerät darf nicht Parkplatz sein. App-Links bleiben aus."; col = Theme.MuteText; }
        else if (_m.Bindings.Any(b => b.Flow == NxMix.Audio.DeviceFlow.Render && b.DeviceId == park.Id)) { msg = "Dieses Gerät ist zugleich ein Ausgang deiner Geräte-Zuordnung – dort würde der Ton hörbar. Wähle ein anderes."; col = Theme.MuteText; }
        else { msg = $"Parkplatz bereit: {park.Name}"; col = Theme.Ok; good = true; }
        if (good) { g.Line(_rStatus.X + 2, _rStatus.Y + 10, _rStatus.X + 5, _rStatus.Y + 13.5f, col, 1.8f, true); g.Line(_rStatus.X + 5, _rStatus.Y + 13.5f, _rStatus.X + 11, _rStatus.Y + 6, col, 1.8f, true); }
        else Icons.Warn(g, _rStatus.X + 7, _rStatus.Y + 10, 12, col);
        g.Text(msg, new RectF(_rStatus.X + 20, _rStatus.Y, _rStatus.W - 20, _rStatus.H), Theme.Small, good ? Theme.Text : col, HAlign.Left, VAlign.Top, false, true);

        // Windows-Unterstuetzung
        string win; Color4 wc;
        if (unavailable) { win = "Umleitung nicht verfügbar (NxMix.Audio.AppRouteRestorer fehlt)."; wc = Theme.Solo; }
        else if (un != null) { win = un; wc = Theme.MuteText; }
        else { win = "Diese Windows-Version kann Programm-Ausgaben einzeln umleiten."; wc = Theme.TextDim; }
        g.Text(win, _rWin, Theme.Small, wc, HAlign.Left, VAlign.Middle, true);

        g.Text("App-Links", _rOnLabel, Theme.Small, Theme.TextDim, HAlign.Right, VAlign.Middle, false);
    }
}
