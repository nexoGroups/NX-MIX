using NxMix.App.Model;
using NxMix.App.Native;
using Vortice.Mathematics;

namespace NxMix.App.Ui;

/// <summary>
/// Auswahl-Popup fuer App-Links: Liste der Programme mit Audio (aus IAppCatalog), Filterfeld, "Programm per Name hinzufuegen ...",
/// "Verknuepfung loesen", Mono/Stereo-Wahl. Verweigert (mit Erklaerung) alles, was mit einer Geraete-Zuordnung oder einem anderen Link kollidiert.
/// </summary>
internal sealed class AppLinkPicker : Overlay
{
    enum K { None, Row, Close, Refresh, Mono, Stereo, Manual, Unlink, Back, Ok, Field }
    readonly record struct Hit(K Kind, int Index = -1);

    const float BoxW = 404f, RowH = 46f, FootH = 34f, Pad = 14f;

    readonly AppModel _m;
    readonly RectF _anchor;
    readonly TextEditor _edit = new();
    readonly VScroll _sc = new();

    bool _manual;
    int _first, _count;
    readonly bool _namedPair;
    List<AppProgram> _all = new();
    List<AppProgram> _filtered = new();
    bool _loading = true;
    string? _loadError, _manualError, _actionError;
    int _sel = -1;
    Hit _hot, _press;

    // Geometrie (absolute Fensterkoordinaten, bei jedem Relayout neu)
    RectF _box, _rClose, _rRefresh, _rTarget, _rSegMono, _rSegStereo, _rNote, _rBanner, _rFieldLabel, _rField, _rList, _rManual, _rUnlink, _rHelp, _rErr, _rBack, _rOk;

    AppLinkPicker(AppModel m, int clickedInput, RectF anchor, bool startManual)
    {
        _m = m;
        _anchor = anchor;
        (_first, _count) = m.SuggestSpan(clickedInput);
        _namedPair = m.IsNamedPair(clickedInput);
        _manual = startManual;
    }

    /// <summary>Oeffnet das Popup fuer den Eingangsstreifen clickedInput (der Link sitzt am Anfang des Paares).</summary>
    public static AppLinkPicker? Show(UiRoot root, AppModel m, int clickedInput, RectF anchor, bool startManual = false, string? prefill = null)
    {
        var p = new AppLinkPicker(m, clickedInput, anchor, startManual);
        root.ShowOverlay(p);
        if (prefill != null) p.StartEditor(prefill);
        p.StartLoad();
        return p;
    }

    // ---- Daten -----------------------------------------------------------------------------------
    void StartLoad()
    {
        _loading = true;
        Changed();
        _m.LoadAppPrograms((list, err) =>
        {
            _all = list; _loadError = err; _loading = false;
            RefreshFilter(true);
            Changed();
        });
    }

    AppLink? Current => _m.LinkStartingAt(_first);

    void RefreshFilter(bool keepSel = false)
    {
        string f = _manual ? "" : _edit.Text.Trim();
        // freie Programme vor solchen, die schon an einem anderen Fader haengen (stabil: spielende zuerst, dann alphabetisch)
        _filtered = _all.Where(p => f.Length == 0 || p.DisplayName.Contains(f, StringComparison.CurrentCultureIgnoreCase)
            || p.ExeName.Contains(f, StringComparison.OrdinalIgnoreCase)).OrderBy(p => LinkedElsewhere(p) != null).ToList();
        if (!keepSel || _sel >= _filtered.Count) _sel = f.Length > 0 ? FirstEnabledRow() : -1;
        _sc.Offset = 0;
    }

    string? BlockMessage(out bool hard)
    {
        hard = true;
        var un = _m.AppLinksUnsupportedReason();
        if (un != null) return un;
        hard = false;
        return _m.LinkConflict(_first, _count, null, _first);
    }

    string? BannerText(out bool hard)
    {
        var b = BlockMessage(out hard);
        if (b != null) return b;
        if (_actionError != null) { hard = true; return _actionError; }
        hard = false;
        return null;
    }

    AppLink? LinkedElsewhere(AppProgram p) =>
        _m.AppLinks.FirstOrDefault(l => l.FirstChannel != _first && string.Equals(l.ExeName, p.ExeName, StringComparison.OrdinalIgnoreCase));

    bool RowDisabled(AppProgram p) => BlockMessage(out _) != null || LinkedElsewhere(p) != null;

    int FirstEnabledRow()
    {
        for (int i = 0; i < _filtered.Count; i++) if (!RowDisabled(_filtered[i])) return i;
        return -1;
    }

    // ---- Aktionen --------------------------------------------------------------------------------
    void Changed() { Relayout(); Invalidate(); }

    void StartEditor(string text)
    {
        _edit.Parent = this;
        _edit.Begin(_rField, text, Theme.Body, _ => { }, 80, null);
        RefreshFilter();
        Changed();
    }

    void EnsureEditor()
    {
        if (_rField.IsEmpty) return;
        if (!_edit.Active || _edit.Bounds != _rField)
        {
            _edit.Parent = this;
            _edit.Begin(_rField, _edit.Text, Theme.Body, _ => { }, 80, null);
        }
    }

    void GoManual(string prefill)
    {
        _manual = true; _manualError = null; _actionError = null;
        Relayout();
        StartEditor(prefill);
    }

    void GoList()
    {
        _manual = false; _manualError = null; _actionError = null;
        Relayout();
        StartEditor("");
    }

    void SetCount(int count)
    {
        if (count == 2)
        {
            var why = _m.LinkConflict(_first, 2, null, _first);
            if (why != null) { _actionError = why; Changed(); return; }
        }
        _count = count; _actionError = null;
        RefreshFilter(true);
        Changed();
    }

    void Pick(string exe, string? display)
    {
        var root = Root;
        var why = _m.SetLink(_first, _count, exe, display);
        if (why != null) { _actionError = why; Changed(); return; }
        Close();
        root?.Status.Flash($"{display ?? AppLinkRules.PrettyName(exe)} ist an {(_count == 2 ? $"Eingang {_first + 1:00} + {_first + 2:00}" : $"Eingang {_first + 1:00}")} gebunden – die Verknüpfung wird beim Start von Audio aktiv.");
    }

    void ClickRow(int i)
    {
        if (i < 0 || i >= _filtered.Count) return;
        var p = _filtered[i];
        var other = LinkedElsewhere(p);
        if (BlockMessage(out _) != null) return; // Hinweis steht schon im Banner
        if (other != null)
        {
            _actionError = $"{p.ExeName} ist schon an Eingang {other.FirstChannel + 1:00} gebunden – ein Programm kann nur an einen Fader. Löse dort zuerst die Verknüpfung.";
            Changed();
            return;
        }
        Pick(p.ExeName, p.DisplayName);
    }

    void ConfirmManual()
    {
        var exe = AppLinkRules.NormalizeExe(_edit.Text, out var err);
        if (exe == null) { _manualError = err; Changed(); return; }
        var known = _all.FirstOrDefault(p => string.Equals(p.ExeName, exe, StringComparison.OrdinalIgnoreCase));
        _manualError = null;
        Pick(exe, known?.DisplayName);
    }

    void DoUnlink()
    {
        var root = Root;
        var cur = Current;
        if (cur == null) return;
        if (!_m.RemoveLink(_first)) return;
        Close();
        root?.Status.Flash($"Verknüpfung mit {cur.Title} gelöst.");
    }

    void Activate(Hit h)
    {
        switch (h.Kind)
        {
            case K.Close: Close(); break;
            case K.Refresh: StartLoad(); break;
            case K.Mono: SetCount(1); break;
            case K.Stereo: SetCount(2); break;
            case K.Row: ClickRow(h.Index); break;
            case K.Manual: if (BlockMessage(out _) == null) GoManual(_edit.Text.Trim()); break;
            case K.Unlink: DoUnlink(); break;
            case K.Back: GoList(); break;
            case K.Ok: ConfirmManual(); break;
        }
    }

    // ---- Geometrie -------------------------------------------------------------------------------
    public override void Layout() => Relayout();

    string TargetText()
    {
        if (_count == 2) return $"Ziel: Eingänge {_first + 1:00} + {_first + 2:00} · Stereo-Paar";
        string nm = _first < _m.InputCount ? _m.Settings.Inputs[_first].Name : "";
        return $"Ziel: Eingang {_first + 1:00}" + (string.IsNullOrWhiteSpace(nm) ? "" : " · " + nm) + " · mono";
    }

    string? NoteText()
    {
        if (_namedPair) return null;
        if (_count == 2 && _first + 2 > _m.InputCount) return $"Eingang {_first + 2:00} wird dafür neu angelegt.";
        return null;
    }

    const string ManualHelp = "Gib den Dateinamen des Programms ein, z. B. spotify.exe oder chrome.exe. Es muss noch nicht laufen – NX-MIX bindet es, sobald es startet.";

    void Relayout()
    {
        var b = Bounds;
        if (b.W < 50 || b.H < 50) return;
        float w = Math.Min(BoxW, b.W - 16);
        float maxH = b.H - 16;
        float h = Build(0, 0, w, maxH);
        float x = Math.Clamp(_anchor.X - 4, 8, Math.Max(8, b.W - w - 8));
        float y = _anchor.Bottom + 6;
        if (y + h > b.H - 8) y = Math.Max(8, b.H - 8 - h);
        Build(x, y, w, maxH);
        _box = new RectF(x, y, w, h);
        EnsureEditor();
    }

    /// <summary>Verteilt alle Rechtecke ab (ox, oy) und liefert die Gesamthoehe des Popups.</summary>
    float Build(float ox, float oy, float w, float maxH)
    {
        float cw = w - 2 * Pad, x = ox + Pad, y = oy + Pad;
        _rClose = new RectF(ox + w - Pad - 22, y - 3, 24, 24);
        _rRefresh = _manual ? default : new RectF(_rClose.X - 28, y - 3, 24, 24);
        y += 24;
        _rTarget = new RectF(x, y, cw, 16); y += 22;
        _rSegMono = _rSegStereo = _rNote = default;
        if (!_manual && !_namedPair)
        {
            float sw = (cw - 4) / 2;
            _rSegMono = new RectF(x, y, sw, 26);
            _rSegStereo = new RectF(x + sw + 4, y, sw, 26);
            y += 30;
            if (NoteText() != null) { _rNote = new RectF(x, y, cw, 14); y += 18; } else y += 4;
        }
        var bt = BannerText(out _);
        if (bt != null)
        {
            float bh = Renderer.TextHeight(bt, Theme.Small, cw - 36) + 16;
            _rBanner = new RectF(x, y, cw, bh); y += bh + 8;
        }
        else _rBanner = default;

        _rFieldLabel = _rList = _rManual = _rUnlink = _rHelp = _rErr = _rBack = _rOk = default;
        if (_manual)
        {
            _rFieldLabel = new RectF(x, y, cw, 14); y += 17;
            _rField = new RectF(x, y, cw, 32); y += 40;
            float hh = Renderer.TextHeight(ManualHelp, Theme.Small, cw);
            _rHelp = new RectF(x, y, cw, hh + 2); y += hh + 10;
            if (_manualError != null && BannerText(out _) == null)
            {
                float eh = Renderer.TextHeight(_manualError, Theme.Small, cw);
                _rErr = new RectF(x, y, cw, eh + 2); y += eh + 10;
            }
            _rOk = new RectF(ox + w - Pad - 110, y, 110, 32);
            _rBack = new RectF(_rOk.X - 8 - 90, y, 90, 32);
            y += 32 + Pad;
        }
        else
        {
            _rField = new RectF(x, y, cw, 32); y += 40;
            float after = 8 + 1 + FootH * 2 + 6;                       // Abstand + Trennlinie + zwei Fusszeilen + Rand
            float want = Math.Clamp(Math.Max(_all.Count, 3), 3, 6) * RowH;
            float maxList = maxH - (y - oy) - after;
            float listH = Math.Max(RowH * 2, Math.Min(want, maxList));
            _rList = new RectF(x, y, cw, listH);
            y += listH + 8 + 1;
            _rManual = new RectF(x, y, cw, FootH); y += FootH;
            _rUnlink = new RectF(x, y, cw, FootH); y += FootH + 6;
            _sc.Update(_rList, _filtered.Count * RowH);
        }
        return y - oy;
    }

    RectF RowRect(int i) => new(_rList.X, _rList.Y + i * RowH - _sc.Offset, _rList.W - (_sc.Needed ? 12 : 0), RowH - 3);

    Hit HitAt(float x, float y)
    {
        if (!_box.Contains(x, y)) return default;
        if (_rClose.Contains(x, y)) return new Hit(K.Close);
        if (_manual)
        {
            if (_rBack.Contains(x, y)) return new Hit(K.Back);
            if (_rOk.Contains(x, y)) return new Hit(K.Ok);
            if (_rField.Contains(x, y)) return new Hit(K.Field);
            return default;
        }
        if (_rRefresh.Contains(x, y)) return new Hit(K.Refresh);
        if (_rSegMono.Contains(x, y)) return new Hit(K.Mono);
        if (_rSegStereo.Contains(x, y)) return new Hit(K.Stereo);
        if (_rField.Contains(x, y)) return new Hit(K.Field);
        if (_rManual.Contains(x, y)) return new Hit(K.Manual);
        if (_rUnlink.Contains(x, y)) return new Hit(K.Unlink);
        if (_rList.Contains(x, y) && !(_sc.Needed && x >= _rList.Right - 12))
        {
            int i = (int)((y - _rList.Y + _sc.Offset) / RowH);
            if (i >= 0 && i < _filtered.Count && RowRect(i).Contains(x, y)) return new Hit(K.Row, i);
        }
        return default;
    }

    bool HitEnabled(Hit h) => h.Kind switch
    {
        K.Row => h.Index >= 0 && h.Index < _filtered.Count && !RowDisabled(_filtered[h.Index]),
        K.Manual => BlockMessage(out _) == null,
        K.Unlink => Current != null,
        K.Stereo => true,
        K.None => false,
        _ => true,
    };

    // ---- Eingabe ---------------------------------------------------------------------------------
    public override CursorKind CursorAt(float x, float y)
    {
        var h = HitAt(x, y);
        if (h.Kind == K.Field) return CursorKind.IBeam;
        return HitEnabled(h) ? CursorKind.Hand : CursorKind.Arrow;
    }

    public override void OnMouseMove(in MouseEvent e)
    {
        if (_sc.Dragging) { _sc.Drag(e.Y); Invalidate(); return; }
        var h = HitAt(e.X, e.Y);
        if (h.Kind == K.Row && HitEnabled(h) && h.Index != _sel) { _sel = h.Index; Invalidate(); }
        if (h != _hot) { _hot = h; Invalidate(); }
    }

    public override void OnMouseDown(in MouseEvent e)
    {
        if (!_box.Contains(e.X, e.Y)) { Close(); return; }
        if (!_manual && _sc.Down(e.X, e.Y)) { Invalidate(); return; }
        var h = HitAt(e.X, e.Y);
        _press = h;
        if (h.Kind == K.Field) { _edit.OnMouseDown(e); Invalidate(); }
    }

    public override void OnMouseUp(in MouseEvent e)
    {
        if (_sc.Dragging) { _sc.Up(); Invalidate(); return; }
        var h = HitAt(e.X, e.Y);
        var p = _press; _press = default;
        if (h == p && h.Kind != K.None && h.Kind != K.Field)
        {
            if (h.Kind == K.Stereo || HitEnabled(h) || h.Kind == K.Row) Activate(h);
        }
    }

    public override bool OnMouseWheel(in MouseEvent e)
    {
        if (!_manual && _sc.Wheel(e.Wheel)) Invalidate();
        return true;
    }

    public override bool Tick(float dt) => _edit.Active && _edit.Tick(dt);

    void EnsureVisible(int i)
    {
        if (i < 0) return;
        float top = i * RowH, bot = top + RowH;
        if (top < _sc.Offset) _sc.Offset = top;
        else if (bot > _sc.Offset + _rList.H) _sc.Offset = Math.Min(bot - _rList.H, Math.Max(0, _sc.ContentH - _rList.H));
    }

    void StepSel(int d)
    {
        if (_filtered.Count == 0) return;
        int i = _sel;
        for (int n = 0; n < _filtered.Count; n++)
        {
            i = i < 0 ? (d > 0 ? 0 : _filtered.Count - 1) : (i + d + _filtered.Count) % _filtered.Count;
            if (!RowDisabled(_filtered[i])) { _sel = i; EnsureVisible(i); break; }
        }
        Invalidate();
    }

    public override void OnKeyDown(int vk, bool ctrl, bool shift)
    {
        switch (vk)
        {
            case Win32.VK_ESCAPE:
                if (_manual) GoList(); else Close();
                return;
            case Win32.VK_TAB:
                return;
            case Win32.VK_RETURN:
                if (_manual) { ConfirmManual(); return; }
                if (_sel >= 0) { ClickRow(_sel); return; }
                if (_filtered.Count == 0 && _edit.Text.Trim().Length > 0 && BlockMessage(out _) == null) GoManual(_edit.Text.Trim());
                return;
            case Win32.VK_DOWN when !_manual: StepSel(1); return;
            case Win32.VK_UP when !_manual: StepSel(-1); return;
        }
        string before = _edit.Text;
        _edit.OnKeyDown(vk, ctrl, shift);
        AfterEdit(before);
    }

    public override void OnChar(char c)
    {
        string before = _edit.Text;
        _edit.OnChar(c);
        AfterEdit(before);
    }

    void AfterEdit(string before)
    {
        EnsureEditor();
        if (_edit.Text != before)
        {
            _actionError = null; _manualError = null;
            if (!_manual) RefreshFilter();
            Changed();
        }
        else Invalidate();
    }

    // ---- Zeichnen --------------------------------------------------------------------------------
    static void Btn(Renderer g, RectF r, bool hot, bool enabled = true)
    {
        if (hot && enabled) g.Fill(r, Theme.Raised);
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        g.Fill(b, new Color4(0, 0, 0, 0.4f));
        if (_box.IsEmpty) return;
        g.Fill(_box, Theme.Popup);
        g.StrokeRect(_box, Theme.BorderStrong);
        float x = _box.X + Pad, cw = _box.W - 2 * Pad;

        // Kopf
        g.Text(_manual ? "PROGRAMM PER NAME HINZUFÜGEN" : "PROGRAMM AN EINGANG BINDEN", new RectF(x, _box.Y + Pad - 2, cw - 60, 20), Theme.Caps, Theme.TextDim, HAlign.Left, VAlign.Middle, true);
        Btn(g, _rClose, _hot.Kind == K.Close);
        Icons.Cross(g, _rClose.CX, _rClose.CY, 11, _hot.Kind == K.Close ? Theme.Text : Theme.TextDim, 1.4f);
        if (!_rRefresh.IsEmpty)
        {
            Btn(g, _rRefresh, _hot.Kind == K.Refresh);
            Icons.Refresh(g, _rRefresh.CX, _rRefresh.CY, 14, _hot.Kind == K.Refresh ? Theme.Text : Theme.TextDim);
        }
        g.Text(TargetText(), _rTarget, Theme.Small, Theme.TextDim, HAlign.Left, VAlign.Middle, true);

        // Mono / Stereo
        if (!_rSegMono.IsEmpty)
        {
            bool stereoOk = _m.LinkConflict(_first, 2, null, _first) == null;
            PaintSeg(g, _rSegMono, "Mono · 1 Kanal", _count == 1, true, _hot.Kind == K.Mono);
            PaintSeg(g, _rSegStereo, "Stereo · 2 Kanäle", _count == 2, stereoOk, _hot.Kind == K.Stereo);
            var note = NoteText();
            if (note != null) g.Text(note, _rNote, Theme.Tiny, Theme.TextFaint, HAlign.Left, VAlign.Middle, true);
        }

        // Hinweis / Konflikt
        var bt = BannerText(out bool hard);
        if (bt != null)
        {
            var col = hard ? Theme.Mute : Theme.Solo;
            g.Fill(_rBanner, col.With(0.12f));
            g.StrokeRect(_rBanner, col.With(0.55f));
            Icons.Warn(g, _rBanner.X + 15, _rBanner.Y + 15, 13, col);
            g.Text(bt, new RectF(_rBanner.X + 30, _rBanner.Y + 8, _rBanner.W - 38, _rBanner.H - 12), Theme.Small, Theme.Text, HAlign.Left, VAlign.Top, false, true);
        }

        if (_manual) PaintManual(g, x, cw); else PaintList(g, x, cw);

        // Texteditor ganz oben (zeichnet nur, wenn aktiv)
        _edit.Paint(g);
        if (_edit.Active && _edit.Text.Length == 0)
            g.Text(_manual ? "z. B. spotify.exe" : "Programm suchen …", new RectF(_rField.X + 10, _rField.Y, _rField.W - 20, _rField.H), Theme.Body, Theme.TextFaint, HAlign.Left, VAlign.Middle, false);
    }

    void PaintSeg(Renderer g, RectF r, string text, bool on, bool enabled, bool hot)
    {
        if (on) { g.Fill(r, Theme.AccentDim); g.StrokeRect(r, Theme.Accent.With(0.8f)); }
        else { g.Fill(r, enabled ? (hot ? Theme.RaisedHi : Theme.Raised) : Theme.Card); g.StrokeRect(r, Theme.BorderStrong.With(enabled ? 0.8f : 0.4f)); }
        g.Text(text, r, Theme.SmallBold, on ? Theme.AccentHi : enabled ? Theme.Text : Theme.TextFaint, HAlign.Center, VAlign.Middle, false);
    }

    void PaintManual(Renderer g, float x, float cw)
    {
        g.Text("EXE-NAME", _rFieldLabel, Theme.Tiny, Theme.TextFaint, HAlign.Left, VAlign.Middle, false);
        g.Text(ManualHelp, _rHelp, Theme.Small, Theme.TextDim, HAlign.Left, VAlign.Top, false, true);
        if (_manualError != null && _rBanner.IsEmpty) g.Text(_manualError, _rErr, Theme.Small, Theme.Mute, HAlign.Left, VAlign.Top, false, true);
        PaintButton(g, _rBack, "Zurück", false, _hot.Kind == K.Back);
        PaintButton(g, _rOk, "Binden", true, _hot.Kind == K.Ok);
    }

    static void PaintButton(Renderer g, RectF r, string text, bool primary, bool hot)
    {
        Color4 bg = primary ? (hot ? Theme.AccentHi : Theme.Accent) : (hot ? Theme.RaisedHi : Theme.Raised);
        g.Fill(r, bg);
        if (!primary) g.StrokeRect(r, Theme.BorderStrong);
        g.Text(text, r, Theme.BodyBold, primary ? Theme.OnAccent : Theme.Text, HAlign.Center, VAlign.Middle, false);
    }

    void PaintList(Renderer g, float x, float cw)
    {
        // Liste
        g.PushClip(_rList);
        if (_loading && _all.Count == 0)
            g.Text("Lese Programme ein …", _rList, Theme.Body, Theme.TextDim, HAlign.Center);
        else if (_filtered.Count == 0)
        {
            string head, body = "";
            string f = _edit.Text.Trim();
            if (_loadError != null && _all.Count == 0) { head = "Programmliste nicht verfügbar"; body = _loadError; }
            else if (_all.Count == 0) { head = "Gerade nutzt kein Programm Audio"; body = "Starte das Programm und klicke oben auf Aktualisieren – oder füge es per Name hinzu."; }
            else { head = $"Kein Treffer für „{f}“"; body = "Enter: als Programmname hinzufügen."; }
            g.Text(head, new RectF(_rList.X + 10, _rList.Y + 18, _rList.W - 20, 22), Theme.BodyBold, Theme.TextDim, HAlign.Center, VAlign.Middle, true);
            g.Text(body, new RectF(_rList.X + 20, _rList.Y + 44, _rList.W - 40, _rList.H - 50), Theme.Small, _loadError != null && _all.Count == 0 ? Theme.Solo : Theme.TextFaint, HAlign.Center, VAlign.Top, false, true);
        }
        else
        {
            for (int i = 0; i < _filtered.Count; i++)
            {
                var r = RowRect(i);
                if (r.Bottom < _rList.Y || r.Y > _rList.Bottom) continue;
                PaintRow(g, r, _filtered[i], i == _sel);
            }
        }
        g.PopClip();
        _sc.Paint(g);

        // Fusszeilen
        g.Fill(new RectF(x, _rManual.Y - 1, cw, 1), Theme.Border);
        bool blocked = BlockMessage(out _) != null;
        bool hm = _hot.Kind == K.Manual && !blocked;
        if (hm) g.Fill(_rManual, Theme.Raised);
        Icons.Plus(g, _rManual.X + 18, _rManual.CY, 11, blocked ? Theme.TextFaint : Theme.Accent, 1.6f);
        g.Text("Programm per Name hinzufügen …", new RectF(_rManual.X + 36, _rManual.Y, _rManual.W - 44, _rManual.H), Theme.Body, blocked ? Theme.TextFaint : Theme.Text, HAlign.Left, VAlign.Middle, true);

        var cur = Current;
        bool hu = _hot.Kind == K.Unlink && cur != null;
        if (hu) g.Fill(_rUnlink, Theme.Mute.With(0.18f));
        Color4 uc = cur != null ? Theme.Mute : Theme.TextFaint;
        Icons.Cross(g, _rUnlink.X + 18, _rUnlink.CY, 9, uc, 1.6f);
        float rw = 0;
        if (cur != null)
        {
            string t = cur.Title + " · " + (cur.Count == 2 ? "Stereo" : "mono");
            rw = Math.Min(190, Renderer.MeasureWidth(t, Theme.Small) + 8);
            g.Text(t, new RectF(_rUnlink.Right - rw - 8, _rUnlink.Y, rw, _rUnlink.H), Theme.Small, Theme.TextFaint, HAlign.Right, VAlign.Middle, true);
        }
        g.Text("Verknüpfung lösen", new RectF(_rUnlink.X + 36, _rUnlink.Y, _rUnlink.W - 44 - (rw > 0 ? rw + 8 : 0), _rUnlink.H), Theme.Body, uc, HAlign.Left, VAlign.Middle, true);
    }

    void PaintRow(Renderer g, RectF r, AppProgram p, bool sel)
    {
        var cur = Current;
        bool here = cur != null && string.Equals(cur.ExeName, p.ExeName, StringComparison.OrdinalIgnoreCase);
        var other = LinkedElsewhere(p);
        bool dis = BlockMessage(out _) != null || other != null;
        bool hot = sel && !dis;
        g.Fill(r, hot ? Theme.Raised : Theme.Card);
        g.StrokeRect(r, here ? Theme.Accent.With(0.8f) : hot ? Theme.BorderStrong : Theme.Border);
        if (here) g.Fill(new RectF(r.X, r.Y, 3, r.H), Theme.Accent);

        var tile = new RectF(r.X + 12, r.CY - 14, 28, 28);
        g.Fill(tile, p.IsPlaying && !dis ? Theme.AccentDim : Theme.Well);
        g.StrokeRect(tile, p.IsPlaying && !dis ? Theme.Accent.With(0.6f) : Theme.Border);
        string letter = p.DisplayName.Length > 0 ? p.DisplayName[..1].ToUpperInvariant() : "?";
        g.Text(letter, tile, Theme.BodyBold, dis ? Theme.TextFaint : p.IsPlaying ? Theme.AccentHi : Theme.TextDim, HAlign.Center, VAlign.Middle, false);

        float rightW = 84;
        float tx = r.X + 50, tw = Math.Max(10, r.W - 50 - rightW - 10);
        g.Text(p.DisplayName, new RectF(tx, r.Y + 6, tw, 18), Theme.Body, dis ? Theme.TextFaint : Theme.Text, HAlign.Left, VAlign.Middle, true);
        string sub = p.ExeName + (p.Instances > 1 ? $" · {p.Instances} Sitzungen" : "");
        g.Text(sub, new RectF(tx, r.Y + 25, tw, 16), Theme.Small, Theme.TextFaint, HAlign.Left, VAlign.Middle, true);

        // rechts: spielt / offen, darunter Zuordnung
        var rr = new RectF(r.Right - rightW - 10, r.Y + 7, rightW, 17);
        if (p.IsPlaying)
        {
            Icons.EqBars(g, rr.Right - Renderer.MeasureWidth("spielt", Theme.Small) - 14, rr.CY, 9, dis ? Theme.TextFaint : Theme.Accent);
            g.Text("spielt", rr, Theme.Small, dis ? Theme.TextFaint : Theme.AccentHi, HAlign.Right, VAlign.Middle, false);
        }
        else g.Text("offen", rr, Theme.Small, Theme.TextFaint, HAlign.Right, VAlign.Middle, false);
        string? tag = here ? "gebunden" : other != null ? $"an Eingang {other.FirstChannel + 1:00}" : null;
        if (tag != null) g.Text(tag, new RectF(r.Right - rightW - 10, r.Y + 26, rightW, 14), Theme.Tiny, here ? Theme.Accent : Theme.TextDim, HAlign.Right, VAlign.Middle, false);
    }
}
