using NxMix.App.Model;
using NxMix.App.Native;
using Vortice.Mathematics;

namespace NxMix.App.Ui;

/// <summary>
/// Auswahl-Popup fuer App-Links: Liste der Programme mit Audio (aus IAppCatalog), Suchfeld, "Programm per Name hinzufuegen ...",
/// "Verknuepfung loesen", Mono/Stereo-Wahl. Verweigert (mit Erklaerung) alles, was mit einer Geraete-Zuordnung oder einem anderen Link kollidiert.
/// </summary>
internal sealed class AppLinkPicker : Overlay
{
    enum K { None, Row, Close, Refresh, Mono, Stereo, Manual, Unlink, Back, Ok, Field }
    readonly record struct Hit(K Kind, int Index = -1);

    const float BoxW = 420f, Pad = 16f;
    static float RowH => Theme.Touch ? 58f : 50f;
    static float FootH => Theme.Touch ? 44f : 36f;

    readonly AppModel _m;
    readonly RectF _anchor;
    readonly TextEditor _edit = new() { Bare = true };
    readonly VScroll _sc = new();

    bool _manual;
    readonly int _first;
    int _count;
    readonly bool _namedPair;
    List<AppProgram> _all = new();
    List<AppProgram> _filtered = new();
    bool _loading = true;
    string? _loadError, _manualError, _actionError;
    int _sel = -1;
    Hit _hot, _press;

    RectF _box, _rClose, _rRefresh, _rTarget, _rSegMono, _rSegStereo, _rNote, _rBanner, _rFieldLabel, _rField, _rList, _rManual, _rUnlink, _rHelp, _rErr, _rBack, _rOk;

    AppLinkPicker(AppModel m, int clickedInput, RectF anchor, bool startManual)
    {
        _m = m;
        _anchor = anchor;
        (_first, _count) = m.SuggestSpan(clickedInput);
        _namedPair = m.IsNamedPair(clickedInput);
        _manual = startManual;
    }

    /// <summary>Oeffnet das Popup fuer den Eingang clickedInput (der Link sitzt am Anfang des Paares).</summary>
    public static AppLinkPicker Show(UiRoot root, AppModel m, int clickedInput, RectF anchor, bool startManual = false, string? prefill = null)
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
        _edit.Begin(FieldTextRect, text, Theme.Body, _ => { }, 80, null);
        RefreshFilter();
        Changed();
    }

    RectF FieldTextRect => _manual ? _rField.Deflate(12, 0) : RectF.FromLTRB(_rField.X + 34, _rField.Y, _rField.Right - 10, _rField.Bottom);

    void EnsureEditor()
    {
        if (_rField.IsEmpty) return;
        if (!_edit.Active || _edit.Bounds != FieldTextRect)
        {
            _edit.Parent = this;
            _edit.Begin(FieldTextRect, _edit.Text, Theme.Body, _ => { }, 80, null);
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
        root?.Notify.Flash($"{display ?? AppLinkRules.PrettyName(exe)} ist an {(_count == 2 ? $"Eingang {_first + 1:00} + {_first + 2:00}" : $"Eingang {_first + 1:00}")} gebunden.");
    }

    void ClickRow(int i)
    {
        if (i < 0 || i >= _filtered.Count) return;
        var p = _filtered[i];
        var other = LinkedElsewhere(p);
        if (BlockMessage(out _) != null) return;
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
        root?.Notify.Flash($"Verknüpfung mit {cur.Title} gelöst.");
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
        if (_count == 2) return $"Eingänge {_first + 1:00} + {_first + 2:00} · Stereo-Paar";
        string nm = _first < _m.InputCount ? _m.Settings.Inputs[_first].Name : "";
        return $"Eingang {_first + 1:00}" + (string.IsNullOrWhiteSpace(nm) ? "" : " · " + nm) + " · mono";
    }

    string? NoteText()
    {
        if (_namedPair) return null;
        if (_count == 2 && _first + 2 > _m.InputCount) return $"Eingang {_first + 2:00} wird dafür neu angelegt.";
        return null;
    }

    const string ManualHelp = "Dateiname des Programms, z. B. spotify.exe oder chrome.exe. Es muss noch nicht laufen – NX-MIX bindet es, sobald es startet.";

    void Relayout()
    {
        var b = Bounds;
        if (b.W < 50 || b.H < 50) return;
        float w = Math.Min(BoxW, b.W - 16);
        float maxH = b.H - 16;
        float h = Build(0, 0, w, maxH);
        float x = Math.Clamp(_anchor.X - 4, 8, Math.Max(8, b.W - w - 8));
        // links neben einem rechten Seitenblatt bleiben, wenn der Anker darin liegt
        if (_anchor.Right > b.W - Theme.SheetW - 20) x = Math.Max(8, Math.Min(x, b.W - Theme.SheetW - 24 - w));
        float y = _anchor.Bottom + 8;
        if (y + h > b.H - 8) y = Math.Max(8, b.H - 8 - h);
        Build(x, y, w, maxH);
        _box = new RectF(x, y, w, h);
        EnsureEditor();
    }

    /// <summary>Verteilt alle Rechtecke ab (ox, oy) und liefert die Gesamthoehe des Popups.</summary>
    float Build(float ox, float oy, float w, float maxH)
    {
        float cw = w - 2 * Pad, x = ox + Pad, y = oy + Pad;
        _rClose = new RectF(ox + w - Pad - 26, y - 4, 28, 28);
        _rRefresh = _manual ? default : new RectF(_rClose.X - 32, y - 4, 28, 28);
        y += 26;
        _rTarget = new RectF(x, y, cw, 18); y += 26;
        _rSegMono = _rSegStereo = _rNote = default;
        float segH = Theme.ControlHSmall + 2;
        if (!_manual && !_namedPair)
        {
            _rSegMono = new RectF(x, y, cw / 2, segH);
            _rSegStereo = new RectF(x + cw / 2, y, cw / 2, segH);
            y += segH + 6;
            if (NoteText() != null) { _rNote = new RectF(x, y, cw, 16); y += 20; } else y += 4;
        }
        var bt = BannerText(out _);
        if (bt != null)
        {
            float bh = Renderer.TextHeight(bt, Theme.Callout, cw - 40) + 20;
            _rBanner = new RectF(x, y, cw, bh); y += bh + 10;
        }
        else _rBanner = default;

        _rFieldLabel = _rList = _rManual = _rUnlink = _rHelp = _rErr = _rBack = _rOk = default;
        float fh = Theme.ControlH + 2;
        if (_manual)
        {
            _rFieldLabel = new RectF(x, y, cw, 16); y += 20;
            _rField = new RectF(x, y, cw, fh); y += fh + 10;
            float hh = Renderer.TextHeight(ManualHelp, Theme.Callout, cw);
            _rHelp = new RectF(x, y, cw, hh + 2); y += hh + 12;
            if (_manualError != null && BannerText(out _) == null)
            {
                float eh = Renderer.TextHeight(_manualError, Theme.Callout, cw);
                _rErr = new RectF(x, y, cw, eh + 2); y += eh + 12;
            }
            float bh = Theme.ControlH;
            _rOk = new RectF(ox + w - Pad - 110, y, 110, bh);
            _rBack = new RectF(_rOk.X - 8 - 96, y, 96, bh);
            y += bh + Pad;
        }
        else
        {
            _rField = new RectF(x, y, cw, fh); y += fh + 10;
            float after = 8 + 1 + FootH * 2 + 8;
            float want = Math.Clamp(Math.Max(_all.Count, 3), 3, 6) * RowH;
            float maxList = maxH - (y - oy) - after;
            float listH = Math.Max(RowH * 2, Math.Min(want, maxList));
            _rList = new RectF(x - 6, y, cw + 12, listH);
            y += listH + 8 + 1;
            _rManual = new RectF(x - 6, y, cw + 12, FootH); y += FootH;
            _rUnlink = new RectF(x - 6, y, cw + 12, FootH); y += FootH + 8;
            _sc.Update(_rList, _filtered.Count * RowH);
        }
        return y - oy;
    }

    RectF RowRect(int i) => new(_rList.X, _rList.Y + i * RowH - _sc.Offset, _rList.W - (_sc.Needed ? 12 : 0), RowH - 2);

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
        K.None => false,
        _ => true,
    };

    // ---- Eingabe ---------------------------------------------------------------------------------
    public override Widget? HitTest(float x, float y) => this;

    public override CursorKind CursorAt(float x, float y)
    {
        var h = HitAt(x, y);
        if (h.Kind == K.Field) return CursorKind.IBeam;
        return HitEnabled(h) ? CursorKind.Hand : CursorKind.Arrow;
    }

    public override void OnMouseMove(in MouseEvent e)
    {
        if (_sc.Dragging) { _sc.Drag(e.Y); Invalidate(); return; }
        if (IsPressed && _press.Kind == K.Field) { _edit.DragTo(e.X); Invalidate(); return; }
        var h = HitAt(e.X, e.Y);
        if (h.Kind == K.Row && HitEnabled(h) && h.Index != _sel) { _sel = h.Index; Invalidate(); }
        if (h != _hot) { _hot = h; Invalidate(); }
    }

    public override void OnMouseDown(in MouseEvent e)
    {
        if (!_box.Contains(e.X, e.Y)) { Close(); return; }
        if (e.Button != 0) return;
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
        if (e.Button == 0 && h == p && h.Kind != K.None && h.Kind != K.Field)
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
    public override void Paint(Renderer g)
    {
        var b = Bounds;
        g.Fill(b, Theme.Scrim.With(Theme.Scrim.A * 0.5f));
        if (_box.IsEmpty) return;
        g.Shadow(_box, 16, 22, 1.5f, 8);
        g.FillRound(_box, Theme.RadiusCard, Theme.Elevated);
        g.StrokeRound(_box, Theme.RadiusCard, Theme.IsDark ? Theme.BorderStrong : Theme.Border);
        float x = _box.X + Pad, cw = _box.W - 2 * Pad;

        // Kopf
        g.Text(_manual ? "Programm per Name hinzufügen" : "Programm an Eingang binden", new RectF(x, _box.Y + Pad - 4, cw - 70, 26), Theme.Headline, Theme.Text, HAlign.Left, VAlign.Middle, true);
        IconBtn(g, _rClose, Icon.Close, _hot.Kind == K.Close);
        if (!_rRefresh.IsEmpty) IconBtn(g, _rRefresh, Icon.Refresh, _hot.Kind == K.Refresh);
        g.Text(TargetText(), _rTarget, Theme.Callout, Theme.TextSecondary, HAlign.Left, VAlign.Middle, true);

        // Mono / Stereo als Segment-Schalter
        if (!_rSegMono.IsEmpty)
        {
            bool stereoOk = _m.LinkConflict(_first, 2, null, _first) == null;
            var track = RectF.FromLTRB(_rSegMono.X, _rSegMono.Y, _rSegStereo.Right, _rSegMono.Bottom);
            g.FillRound(track, Theme.RadiusControl + 1, Theme.Control);
            PaintSeg(g, _rSegMono.Deflate(2), "Mono · 1 Kanal", _count == 1, true, _hot.Kind == K.Mono);
            PaintSeg(g, _rSegStereo.Deflate(2), "Stereo · 2 Kanäle", _count == 2, stereoOk, _hot.Kind == K.Stereo);
            var note = NoteText();
            if (note != null) g.Text(note, _rNote, Theme.Caption, Theme.TextTertiary, HAlign.Left, VAlign.Middle, true);
        }

        // Hinweis / Konflikt
        var bt = BannerText(out bool hard);
        if (bt != null)
        {
            var col = hard ? Theme.Red : Theme.Orange;
            g.FillRound(_rBanner, 10, Theme.Soft(col));
            Icons.Draw(g, Icon.Warn, _rBanner.X + 18, _rBanner.Y + 18, 16, col);
            g.Text(bt, new RectF(_rBanner.X + 34, _rBanner.Y + 10, _rBanner.W - 44, _rBanner.H - 16), Theme.Callout, Theme.Text, HAlign.Left, VAlign.Top, false, true);
        }

        if (_manual) PaintManual(g); else PaintList(g, x, cw);

        _edit.Paint(g);
        if (_edit.Active && _edit.Text.Length == 0)
            g.Text(_manual ? "z. B. spotify.exe" : "Programm suchen …", FieldTextRect, Theme.Body, Theme.TextTertiary, HAlign.Left, VAlign.Middle, false);
    }

    static void IconBtn(Renderer g, RectF r, Icon icon, bool hot)
    {
        if (hot) g.FillPill(r, Theme.ControlHover);
        Icons.Draw(g, icon, r.CX, r.CY, 16, hot ? Theme.Text : Theme.TextSecondary);
    }

    static void PaintSeg(Renderer g, RectF r, string text, bool on, bool enabled, bool hot)
    {
        if (on)
        {
            if (!Theme.IsDark) g.Shadow(r, Theme.RadiusControl - 1, 2.5f, 0.7f, 1);
            g.FillRound(r, Theme.RadiusControl - 1, Theme.SegmentThumb);
        }
        else if (hot && enabled) g.FillRound(r, Theme.RadiusControl - 1, Theme.ControlHover);
        g.Text(text, r, on ? Theme.CalloutStrong : Theme.CalloutMedium, on ? Theme.Text : enabled ? Theme.TextSecondary : Theme.TextTertiary, HAlign.Center, VAlign.Middle, false);
    }

    void PaintField(Renderer g)
    {
        bool focus = _edit.Active;
        if (focus) g.FillRound(_rField.Inflate(3), Theme.RadiusControl + 3, Theme.Accent.With(0.22f));
        g.FillRound(_rField, Theme.RadiusControl, Theme.IsDark ? Theme.Well : Theme.Surface2);
        g.StrokeRound(_rField, Theme.RadiusControl, focus ? Theme.Accent : Theme.Border, focus ? 1.5f : 1f);
        if (!_manual) Icons.Draw(g, Icon.Search, _rField.X + 18, _rField.CY, 15, Theme.TextTertiary);
    }

    void PaintManual(Renderer g)
    {
        g.Text("EXE-NAME", _rFieldLabel, Theme.Overline, Theme.TextTertiary, HAlign.Left, VAlign.Middle, false);
        PaintField(g);
        g.Text(ManualHelp, _rHelp, Theme.Callout, Theme.TextSecondary, HAlign.Left, VAlign.Top, false, true);
        if (_manualError != null && _rBanner.IsEmpty) g.Text(_manualError, _rErr, Theme.Callout, Theme.Red, HAlign.Left, VAlign.Top, false, true);
        Draw.ButtonLook(g, _rBack, "Zurück", ButtonStyle.Gray, true, _hot.Kind == K.Back, font: Theme.BodyMedium);
        Draw.ButtonLook(g, _rOk, "Binden", ButtonStyle.Filled, true, _hot.Kind == K.Ok, font: Theme.BodyMedium);
    }

    void PaintList(Renderer g, float x, float cw)
    {
        PaintField(g);
        g.PushClip(_rList);
        if (_loading && _all.Count == 0)
            g.Text("Lese Programme ein …", _rList, Theme.Body, Theme.TextSecondary, HAlign.Center);
        else if (_filtered.Count == 0)
        {
            string head, body = "";
            string f = _edit.Text.Trim();
            if (_loadError != null && _all.Count == 0) { head = "Programmliste nicht verfügbar"; body = _loadError; }
            else if (_all.Count == 0) { head = "Gerade nutzt kein Programm Audio"; body = "Starte das Programm und klicke oben auf Aktualisieren – oder füge es per Name hinzu."; }
            else { head = $"Kein Treffer für „{f}“"; body = "Enter: als Programmname hinzufügen."; }
            g.Text(head, new RectF(_rList.X + 10, _rList.Y + 16, _rList.W - 20, 22), Theme.BodyStrong, Theme.TextSecondary, HAlign.Center, VAlign.Middle, true);
            g.Text(body, new RectF(_rList.X + 20, _rList.Y + 42, _rList.W - 40, _rList.H - 48), Theme.Callout, _loadError != null && _all.Count == 0 ? Theme.Orange : Theme.TextTertiary, HAlign.Center, VAlign.Top, false, true);
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
        g.Fill(new RectF(x, _rManual.Y - 5, cw, 1), Theme.Separator);
        bool blocked = BlockMessage(out _) != null;
        bool hm = _hot.Kind == K.Manual && !blocked;
        if (hm) g.FillRound(_rManual, 8, Theme.ControlHover);
        Icons.Draw(g, Icon.Plus, _rManual.X + 20, _rManual.CY, 16, blocked ? Theme.TextTertiary : Theme.AccentText);
        g.Text("Programm per Name hinzufügen …", new RectF(_rManual.X + 40, _rManual.Y, _rManual.W - 48, _rManual.H), Theme.Body, blocked ? Theme.TextTertiary : Theme.AccentText, HAlign.Left, VAlign.Middle, true);

        var cur = Current;
        bool hu = _hot.Kind == K.Unlink && cur != null;
        if (hu) g.FillRound(_rUnlink, 8, Theme.Soft(Theme.Red));
        Color4 uc = cur != null ? Theme.Red : Theme.TextTertiary;
        Icons.Draw(g, Icon.Close, _rUnlink.X + 20, _rUnlink.CY, 15, uc);
        float rw = 0;
        if (cur != null)
        {
            string t = cur.Title + " · " + (cur.Count == 2 ? "Stereo" : "mono");
            rw = Math.Min(190, Renderer.MeasureWidth(t, Theme.Callout) + 8);
            g.Text(t, new RectF(_rUnlink.Right - rw - 10, _rUnlink.Y, rw, _rUnlink.H), Theme.Callout, Theme.TextTertiary, HAlign.Right, VAlign.Middle, true);
        }
        g.Text("Verknüpfung lösen", new RectF(_rUnlink.X + 40, _rUnlink.Y, _rUnlink.W - 48 - (rw > 0 ? rw + 8 : 0), _rUnlink.H), Theme.Body, uc, HAlign.Left, VAlign.Middle, true);
    }

    void PaintRow(Renderer g, RectF r, AppProgram p, bool sel)
    {
        var cur = Current;
        bool here = cur != null && string.Equals(cur.ExeName, p.ExeName, StringComparison.OrdinalIgnoreCase);
        var other = LinkedElsewhere(p);
        bool dis = BlockMessage(out _) != null || other != null;
        bool hot = sel && !dis;
        if (here) g.FillRound(r, 10, Theme.AccentSoft);
        else if (hot) g.FillRound(r, 10, Theme.ControlHover);

        var tile = new RectF(r.X + 10, r.CY - 16, 32, 32);
        var tc = dis ? Theme.TextTertiary : p.IsPlaying ? Theme.AccentText : Theme.TextSecondary;
        g.FillRound(tile, 9, dis ? Theme.Control.With(0.6f) : p.IsPlaying ? Theme.AccentSoft : Theme.Control);
        string letter = p.DisplayName.Length > 0 ? p.DisplayName[..1].ToUpperInvariant() : "?";
        g.Text(letter, tile, Theme.BodyStrong, tc, HAlign.Center, VAlign.Middle, false);

        float rightW = 92;
        float tx = r.X + 54, tw = Math.Max(10, r.W - 54 - rightW - 10);
        g.Text(p.DisplayName, new RectF(tx, r.CY - 17, tw, 18), Theme.BodyMedium, dis ? Theme.TextTertiary : Theme.Text, HAlign.Left, VAlign.Middle, true);
        string sub = p.ExeName + (p.Instances > 1 ? $" · {p.Instances} Sitzungen" : "");
        g.Text(sub, new RectF(tx, r.CY + 1, tw, 16), Theme.Caption, Theme.TextTertiary, HAlign.Left, VAlign.Middle, true);

        var rr = new RectF(r.Right - rightW - 10, r.CY - 17, rightW, 17);
        if (p.IsPlaying)
        {
            Icons.Draw(g, Icon.Bars, rr.Right - Renderer.MeasureWidth("spielt", Theme.CaptionMedium) - 12, rr.CY, 12, dis ? Theme.TextTertiary : Theme.Green);
            g.Text("spielt", rr, Theme.CaptionMedium, dis ? Theme.TextTertiary : Theme.Green, HAlign.Right, VAlign.Middle, false);
        }
        else g.Text("offen", rr, Theme.CaptionMedium, Theme.TextTertiary, HAlign.Right, VAlign.Middle, false);
        string? tag = here ? "gebunden" : other != null ? $"an Eingang {other.FirstChannel + 1:00}" : null;
        if (tag != null) g.Text(tag, new RectF(r.Right - rightW - 10, r.CY + 1, rightW, 16), Theme.Caption, here ? Theme.AccentText : Theme.TextSecondary, HAlign.Right, VAlign.Middle, false);
    }
}
