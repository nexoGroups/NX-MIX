using NxMix.App.Model;
using NxMix.App.Ui;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>
/// Einstellungen als gruppierte Liste (wie die Apple-Systemeinstellungen): Darstellung, Projekt, App-Links, Engine,
/// Sicherheit, Tastenkuerzel. Eine Spalte, scrollt senkrecht.
/// </summary>
internal sealed class SettingsPage : Page
{
    readonly AppModel _m;
    public readonly Segmented ThemeSeg = new("System", "Hell", "Dunkel");
    public readonly Segmented DensitySeg = new("Maus", "Touch");
    public readonly Switch SidebarSw = new();
    readonly Button _export = new("Exportieren …", ButtonStyle.Gray, Icon.Export);
    readonly Button _import = new("Importieren …", ButtonStyle.Gray, Icon.Import);
    readonly Button _new = new("Neues leeres Projekt", ButtonStyle.DestructiveTinted, Icon.Document);
    public readonly Switch LinksSw = new() { Tooltip = "Aus: Programme laufen normal, die Links bleiben im Projekt gespeichert" };
    public readonly Dropdown Parking = new();
    readonly Button _reset = new("Alle Umleitungen zurücksetzen", ButtonStyle.Gray, Icon.Refresh) { Tooltip = "Setzt jedes umgeleitete Programm zurück auf „System-Standard“" };
    readonly VScroll _sc = new();
    readonly List<Action<Renderer>> _painters = new();
    readonly List<RectF> _groups = new();

    const string Intro = "Ein App-Link bindet ein Programm (z. B. Spotify oder Chrome) direkt an einen Eingangs-Fader: Sein Ton läuft dann nur noch über NX-MIX und nirgends sonst. Dafür leitet Windows die Ausgabe des Programms auf den Parkplatz um, und NX-MIX greift den Ton dort ab.";
    const string Warn = "Der Parkplatz muss ein virtuelles, stilles Ausgabegerät sein, an dem NICHTS weitergeleitet wird – nicht in einer VB-Matrix-, Voicemeeter- oder Cable-Kette und nicht auf Lautsprecher. Sonst wird der Ton dort hörbar. Das Standardgerät lässt sich nicht wählen.";

    public override string Title => "Einstellungen";
    public override Icon Icon => Icon.Settings;
    public override string? Subtitle => "Darstellung, Projekt, App-Links und Engine";
    public override bool AcceptsMouse => true;

    public SettingsPage(AppModel m)
    {
        _m = m;
        Add(ThemeSeg); Add(DensitySeg); Add(SidebarSw);
        Add(_export); Add(_import); Add(_new);
        Add(LinksSw); Add(Parking); Add(_reset);
        ThemeSeg.Changed += i => Root?.SetThemeMode(i switch { 1 => ThemeMode.Light, 2 => ThemeMode.Dark, _ => ThemeMode.System });
        DensitySeg.Changed += i => Root?.SetDensity(i == 1 ? Density.Touch : Density.Mouse);
        SidebarSw.Changed += on => Root?.SetSidebarExpanded(on);
        _new.Clicked += () => Root?.NewProjectWithConfirm();
        _export.Clicked += () => Root?.ExportProject();
        _import.Clicked += () => Root?.ImportProject();
        Parking.Text = ParkingText;
        Parking.TextColor = () => string.IsNullOrEmpty(_m.ParkingDeviceId) || _m.ParkingDevice == null ? Theme.TextSecondary : Theme.Text;
        Parking.Items = ParkingItems;
        _reset.Clicked += ResetRoutes;
        LinksSw.Changed += on => _m.SetAppLinksEnabled(on);
        m.StructureChanged += Invalidate;
        m.HostChanged += Invalidate;
    }

    public override void OnShow() { Layout(); Invalidate(); }

    // ---- Parkplatz -----------------------------------------------------------------------------
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
        if (cands.Count > 0) items.Add(MenuItem.Header("WIEDERGABEGERÄTE"));
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
                if (err == null) root.Notify.Flash("Alle Umleitungen zurückgesetzt – Programme laufen wieder über ihr normales Gerät.");
                else root.Notify.Flash("Zurücksetzen fehlgeschlagen: " + err, true);
            });
        }
        if (_m.HostRunning && _m.HasActiveLinks)
            root.Confirm("Umleitungen jetzt zurücksetzen?", "Audio läuft und Programme sind an Eingänge gebunden. Nach dem Zurücksetzen spielen sie wieder über ihr normales Gerät – ihr Ton kommt dann aus den normalen Lautsprechern, nicht mehr nur über NX-MIX.",
                "Zurücksetzen", Run, danger: true);
        else Run();
    }

    string LinksLine()
    {
        if (_m.AppLinks.Count == 0) return "Noch kein Programm gebunden – im Mixer auf einen Eingangsnamen klicken und „Programm binden“ wählen.";
        return string.Join("  ·  ", _m.AppLinks.Take(4).Select(l => $"{l.Title} → {LinkLook.Target(l)}")) + (_m.AppLinks.Count > 4 ? $"  ·  +{_m.AppLinks.Count - 4}" : "");
    }

    // ---- Werte -----------------------------------------------------------------------------------
    void UpdateWidgets()
    {
        var root = Root;
        if (root != null)
        {
            ThemeSeg.SetSelected(root.Prefs.Theme switch { ThemeMode.Light => 1, ThemeMode.Dark => 2, _ => 0 });
            DensitySeg.SetSelected(root.Prefs.Density == Density.Touch ? 1 : 0);
            SidebarSw.SetOn(root.Prefs.SidebarExpanded);
        }
        bool run = _m.HostRunning || _m.HostBusy;
        Parking.Enabled = !run;
        LinksSw.Enabled = !run;
        LinksSw.SetOn(_m.AppLinksEnabled);
        Parking.Tooltip = run ? "Zum Ändern zuerst Audio stoppen" : "Parkplatz-Ausgang: stilles virtuelles Gerät, auf das Programme umgeleitet werden";
    }

    // ---- Layout ----------------------------------------------------------------------------------
    float _contentH;

    public override void Layout()
    {
        if (!Visible) return; // erst beim Anzeigen (fragt u. a. die Umleitungs-Klasse ab)
        UpdateWidgets();
        _painters.Clear();
        _groups.Clear();
        float colW = Math.Min(780, Bounds.W - 2 * Theme.Pad - 20);
        float x = Bounds.X + (Bounds.W - colW) / 2;
        float y0 = Bounds.Y + 4 - _sc.Offset;
        float y = y0;
        float rowH = Theme.Touch ? 60 : 52;
        float ip = 16;

        // ---- Darstellung ----
        y = Section(x, y, colW, "DARSTELLUNG");
        float g0 = y;
        y = Row(x, y, colW, rowH, Icon.Palette, Theme.Purple, "Farbmodus", "System folgt der Windows-Einstellung", ThemeSeg, 270);
        y = Row(x, y, colW, rowH, Icon.Hand, Theme.Orange, "Bedienung", "Touch: größere Tasten, Fader und Zeilen", DensitySeg, 190, sep: true);
        y = Row(x, y, colW, rowH, Icon.Sidebar, Theme.Teal, "Seitenleiste ausgeklappt", null, SidebarSw, 60, sep: true);
        Group(x, g0, colW, y - g0);
        y += 28;

        // ---- Projekt ----
        y = Section(x, y, colW, "PROJEKT");
        g0 = y;
        string path = ProjectStore.Enabled ? ProjectStore.FilePath : "(Vorschau – es wird nichts gespeichert)";
        y = Row(x, y, colW, rowH, Icon.Document, Theme.Accent, "Automatisch gespeichert", path, null, 0);
        _painters.Add(g => g.Fill(new RectF(x + 56, y, colW - 56, 1), Theme.Separator));
        float bh = Theme.ControlHSmall + 4;
        float bx = x + ip;
        foreach (var b in new[] { _export, _import, _new })
        {
            float w = b.PreferredWidth;
            b.Bounds = new RectF(bx, y + 12, w, bh);
            bx += w + 8;
        }
        y += 12 + bh + 12;
        Group(x, g0, colW, y - g0);
        y = Footer(x, y, colW, "Gain-Sperren, Namen, Matrix, Geräte-Zuordnungen, App-Links und Fenstergröße gehören zum Projekt. Farbmodus und Bedienung gelten nur für dieses Gerät.");
        y += 22;

        // ---- App-Links ----
        y = Section(x, y, colW, "APP-LINKS", AppLinksChip());
        g0 = y;
        y = Row(x, y, colW, rowH, Icon.Link, Theme.Accent, "App-Links aktiv", "Aus: Programme laufen normal, die Links bleiben gespeichert", LinksSw, 60);
        y = Row(x, y, colW, rowH, Icon.Speaker, Theme.Teal, "Parkplatz-Ausgang", null, Parking, Math.Min(320, colW - 260), sep: true);
        // Status des Parkplatzes
        var (msg, col, good) = ParkingStatus();
        float sy = y;
        _painters.Add(g => g.Fill(new RectF(x + 56, sy, colW - 56, 1), Theme.Separator));
        float mh = Math.Max(22, Renderer.TextHeight(msg, Theme.Callout, colW - 56 - ip));
        _painters.Add(g =>
        {
            Icons.Draw(g, good ? Icon.Check : Icon.Warn, x + 30, sy + 12 + 11, 16, col);
            g.Text(msg, new RectF(x + 56, sy + 12, colW - 56 - ip, mh + 2), Theme.Callout, good ? Theme.Text : col, HAlign.Left, VAlign.Middle, false, true);
        });
        y += 12 + mh + 12;
        // Windows-Unterstuetzung
        string? un = _m.AppLinksUnsupportedReason();
        bool unavailable = _m.Restorer == null;
        string win = unavailable ? "Umleitung nicht verfügbar (NxMix.Audio.AppRouteRestorer fehlt)." : un ?? "Diese Windows-Version kann Programm-Ausgaben einzeln umleiten.";
        var wc = unavailable ? Theme.Orange : un != null ? Theme.Red : Theme.TextSecondary;
        float wy = y, wh = Math.Max(20, Renderer.TextHeight(win, Theme.Callout, colW - 56 - ip));
        _painters.Add(g =>
        {
            g.Fill(new RectF(x + 56, wy, colW - 56, 1), Theme.Separator);
            Icons.Draw(g, Icon.Info, x + 30, wy + 12 + 10, 16, wc);
            g.Text(win, new RectF(x + 56, wy + 12, colW - 56 - ip, wh + 2), Theme.Callout, wc, HAlign.Left, VAlign.Middle, false, true);
        });
        y += 12 + wh + 12;
        // Gebundene Programme
        string ll = LinksLine();
        float ly = y, lh = Math.Max(20, Renderer.TextHeight(ll, Theme.Callout, colW - 56 - ip));
        _painters.Add(g =>
        {
            g.Fill(new RectF(x + 56, ly, colW - 56, 1), Theme.Separator);
            Icons.Draw(g, Icon.Bars, x + 30, ly + 12 + 10, 14, Theme.TextSecondary);
            g.Text(ll, new RectF(x + 56, ly + 12, colW - 56 - ip, lh + 2), Theme.Callout, _m.AppLinks.Count == 0 ? Theme.TextTertiary : Theme.TextSecondary, HAlign.Left, VAlign.Middle, false, true);
        });
        y += 12 + lh + 12;
        _painters.Add(g => g.Fill(new RectF(x + 56, y, colW - 56, 1), Theme.Separator));
        float rw = _reset.PreferredWidth;
        _reset.Bounds = new RectF(x + ip, y + 12, rw, bh);
        y += 12 + bh + 12;
        Group(x, g0, colW, y - g0);
        y = Footer(x, y, colW, Intro);
        // Warnkasten
        float wh2 = Renderer.TextHeight(Warn, Theme.Callout, colW - 60);
        float wy2 = y + 10;
        _painters.Add(g =>
        {
            var r = new RectF(x, wy2, colW, wh2 + 24);
            g.FillRound(r, 12, Theme.Soft(Theme.Orange));
            Icons.Draw(g, Icon.Warn, x + 22, wy2 + 20, 16, Theme.Orange);
            g.Text(Warn, new RectF(x + 42, wy2 + 12, colW - 58, wh2 + 2), Theme.Callout, Theme.Text, HAlign.Left, VAlign.Top, false, true);
        });
        y = wy2 + wh2 + 24 + 28;

        // ---- Engine ----
        y = Section(x, y, colW, "ENGINE");
        g0 = y;
        var kv = new (string, string, Color4?)[]
        {
            ("Engine", _m.EngineName, _m.EngineIsDemo ? Theme.Orange : null),
            ("Samplerate", $"{_m.Settings.SampleRate / 1000.0:0.##} kHz  (änderbar auf der Seite „Geräte“)", null),
            ("Kanäle", $"{Fmt.Inputs(_m.InputCount)} · {Fmt.Outputs(_m.OutputCount)}", null),
            ("Limiter-Treffer", _m.Engine.LimiterHits.ToString(), _m.Engine.LimiterHits > 0 ? Theme.Orange : null),
            ("Audio-Host", _m.HostAvailable ? (_m.HostRunning ? "läuft" : "bereit, gestoppt") : "nicht verfügbar", _m.HostAvailable ? null : Theme.Orange),
            ("Schrift", Renderer.FontName, null),
        };
        float kh = Theme.Touch ? 46 : 40;
        for (int i = 0; i < kv.Length; i++)
        {
            var (k, v, c) = kv[i];
            float ry = y;
            bool sep = i > 0;
            _painters.Add(g =>
            {
                if (sep) g.Fill(new RectF(x + ip, ry, colW - ip, 1), Theme.Separator);
                g.Text(k, new RectF(x + ip, ry, 180, kh), Theme.Body, Theme.Text, HAlign.Left, VAlign.Middle, false);
                g.Text(v, new RectF(x + 200, ry, colW - 200 - ip, kh), Theme.Body, c ?? Theme.TextSecondary, HAlign.Right, VAlign.Middle, true);
            });
            y += kh;
        }
        Group(x, g0, colW, y - g0);
        y = Footer(x, y, colW, "Eine andere Kanalzahl erzeugt eine neue Engine – laufendes Audio stoppt dabei kurz.");
        y += 22;

        // ---- Sicherheit ----
        y = Section(x, y, colW, "SICHERHEIT");
        g0 = y;
        string[] lines =
        {
            "Gain-Sperre: pro Kanal im Kanal-Blatt einstellbar – der Fader lässt sich nicht darüber ziehen.",
            "Limiter-Grenze: pro Ausgang im Kanal-Blatt, als orange Marke am Meter sichtbar.",
            "Matrix-Punkte auf Ausgänge mit starker Gain-Sperre bleiben auf höchstens 0 dB begrenzt.",
            "Audio startet nie automatisch; vor dem ersten Start je Sitzung fragt NX-MIX nach.",
            "App-Links: Programme werden auf den Parkplatz umgeleitet. NX-MIX stellt alle Umleitungen beim Start und beim Beenden wieder her.",
        };
        for (int i = 0; i < lines.Length; i++)
        {
            string l = lines[i];
            float h = Renderer.TextHeight(l, Theme.Callout, colW - 56 - ip);
            float ry = y;
            bool sep = i > 0;
            _painters.Add(g =>
            {
                if (sep) g.Fill(new RectF(x + 48, ry, colW - 48, 1), Theme.Separator);
                Icons.Draw(g, Icon.Shield, x + 26, ry + 12 + 9, 15, Theme.Green);
                g.Text(l, new RectF(x + 48, ry + 12, colW - 48 - ip, h + 2), Theme.Callout, Theme.TextSecondary, HAlign.Left, VAlign.Top, false, true);
            });
            y += 24 + h;
        }
        Group(x, g0, colW, y - g0);
        y += 28;

        // ---- Tastenkuerzel ----
        y = Section(x, y, colW, "TASTENKÜRZEL");
        g0 = y;
        (string, string)[] keys =
        {
            ("Strg+1 … 4", "Seite wechseln"), ("Strg+N", "Neues leeres Projekt"), ("Strg+E", "Projekt exportieren"), ("Strg+O", "Projekt importieren"),
            ("Doppelklick", "Fader: 0 dB · Wert: eintippen · Matrix: 0 dB"), ("Strg / Umschalt", "Feinjustierung beim Ziehen und Mausrad"),
            ("← / →", "Im Kanal-Blatt: vorheriger / nächster Kanal"), ("Esc", "Menü, Blatt oder Dialog schließen"),
        };
        for (int i = 0; i < keys.Length; i++)
        {
            var (k, v) = keys[i];
            float ry = y;
            bool sep = i > 0;
            _painters.Add(g =>
            {
                if (sep) g.Fill(new RectF(x + ip, ry, colW - ip, 1), Theme.Separator);
                g.Text(v, new RectF(x + ip, ry, colW - 200, kh), Theme.Body, Theme.Text, HAlign.Left, VAlign.Middle, true);
                float cw = Renderer.MeasureWidth(k, Theme.CalloutStrong) + 18;
                var chip = new RectF(x + colW - ip - cw, ry + (kh - 24) / 2, cw, 24);
                g.FillRound(chip, 6, Theme.Control);
                g.Text(k, chip, Theme.CalloutStrong, Theme.TextSecondary, HAlign.Center, VAlign.Middle, false);
            });
            y += kh;
        }
        Group(x, g0, colW, y - g0);
        y += 32;

        _contentH = y - y0;
        _sc.Update(Bounds, _contentH);
    }

    (string Text, Color4 Color) AppLinksChip()
    {
        string? un = _m.AppLinksUnsupportedReason();
        if (_m.Restorer == null) return ("Nicht verfügbar", Theme.Orange);
        if (un != null) return ("Windows: nicht unterstützt", Theme.Red);
        return ("Windows: unterstützt", Theme.Green);
    }

    (string Msg, Color4 Col, bool Good) ParkingStatus()
    {
        var park = _m.ParkingDevice;
        if (string.IsNullOrEmpty(_m.ParkingDeviceId)) return ("App-Links bleiben aus, bis ein Parkplatz gewählt ist.", Theme.Orange, false);
        if (park == null)
            return (_m.Devices.Count == 0 ? "Geräteliste noch leer – Parkplatz lässt sich nicht prüfen." : $"Das Gerät „{_m.ParkingDeviceName ?? _m.ParkingDeviceId}“ wurde nicht gefunden (nicht angeschlossen?). App-Links bleiben aus.", Theme.Orange, false);
        if (!AppModel.IsValidParking(park)) return ("Das Standardgerät darf nicht Parkplatz sein. App-Links bleiben aus.", Theme.Red, false);
        if (_m.Bindings.Any(b => b.Flow == NxMix.Audio.DeviceFlow.Render && b.DeviceId == park.Id))
            return ("Dieses Gerät ist zugleich ein Ausgang deiner Geräte-Zuordnung – dort würde der Ton hörbar. Wähle ein anderes.", Theme.Red, false);
        return ($"Parkplatz bereit: {park.Name}", Theme.Green, true);
    }

    float Section(float x, float y, float w, string title, (string Text, Color4 Color)? chip = null)
    {
        _painters.Add(g =>
        {
            g.Text(title, new RectF(x + 16, y, w - 32, 22), Theme.Overline, Theme.TextTertiary, HAlign.Left, VAlign.Middle, false);
            if (chip is { } c) Draw.Chip(g, x + w - 4, y + 11, c.Text, c.Color, true, null, alignRight: true);
        });
        return y + 30;
    }

    float Footer(float x, float y, float w, string text)
    {
        float h = Renderer.TextHeight(text, Theme.Caption, w - 32);
        _painters.Add(g => g.Text(text, new RectF(x + 16, y + 8, w - 32, h + 2), Theme.Caption, Theme.TextTertiary, HAlign.Left, VAlign.Top, false, true));
        return y + 8 + h + 2;
    }

    /// <summary>Gruppenflaeche (wird vor allen Zeilen gezeichnet).</summary>
    void Group(float x, float y, float w, float h) => _groups.Add(new RectF(x, y, w, h));

    /// <summary>Eine Zeile: Symbol-Kachel, Titel, optional Untertitel, rechts ein Bedienelement.</summary>
    float Row(float x, float y, float w, float h, Icon icon, Color4 tint, string title, string? sub, Widget? control, float controlW, bool sep = false)
    {
        float ip = 16;
        float ctrlH = control is Switch ? h : control is Segmented ? Theme.ControlHSmall + 2 : Theme.ControlH;
        if (control != null) control.Bounds = new RectF(x + w - ip - controlW, y + (h - ctrlH) / 2, controlW, ctrlH);
        float textW = w - 56 - ip - (control != null ? controlW + 12 : 0);
        _painters.Add(g =>
        {
            if (sep) g.Fill(new RectF(x + 56, y, w - 56, 1), Theme.Separator);
            var tile = new RectF(x + 16, y + (h - 28) / 2, 28, 28);
            g.FillRound(tile, 8, tint);
            Icons.Draw(g, icon, tile.CX, tile.CY, 17, Theme.White);
            if (sub != null)
            {
                g.Text(title, new RectF(x + 56, y + h / 2 - 18, textW, 18), Theme.Body, Theme.Text, HAlign.Left, VAlign.Middle, true);
                g.Text(sub, new RectF(x + 56, y + h / 2 + 1, textW, 17), Theme.Caption, Theme.TextSecondary, HAlign.Left, VAlign.Middle, true);
            }
            else g.Text(title, new RectF(x + 56, y, textW, h), Theme.Body, Theme.Text, HAlign.Left, VAlign.Middle, true);
        });
        return y + h;
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

    public override Widget? HitTest(float x, float y)
    {
        if (!Visible || !Bounds.Contains(x, y)) return null;
        for (int i = Children.Count - 1; i >= 0; i--)
        {
            var h = Children[i].HitTest(x, y);
            if (h != null) return h;
        }
        return this;
    }

    // ---- Zeichnen --------------------------------------------------------------------------------
    public override void Paint(Renderer g)
    {
        Layout();
        g.PushClip(Bounds);
        foreach (var r in _groups) Draw.Card(g, r, 12);
        foreach (var p in _painters) p(g);
        PaintChildren(g);
        g.PopClip();
        _sc.Paint(g);
    }
}
