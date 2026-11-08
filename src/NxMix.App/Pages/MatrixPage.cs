using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Engine;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>2D-Kreuzpunkt-Raster nach VB-Audio-Matrix-Vorbild:
/// Zeilen = Eingänge / VAIO In / Mix-Busse, Spalten = Mix-Busse / Ausgänge / VAIO Out.
/// Nicht-destruktives Parallel-Routing: Bus-Zuweisungen löschen niemals direkte Matrix-Kreuzpunkte; Parallelpfade werden markiert.</summary>
internal sealed class MatrixGridWidget : Widget
{
    readonly AppModel _m;
    public int ScopeFilter { get; set; } // 0=Komplett, 1=Nur Direkt In->Out, 2=Nur Bus-Routing
    public int ZoomMode { get; set; } = 1; // 0=Kompakt, 1=Standard, 2=Groß

    readonly record struct AxisPort(RoutePort Port, int Index, string GroupTag, string Label, Color4 Color);
    List<AxisPort> _rows = new();
    List<AxisPort> _cols = new();
    HashSet<(Guid InId, Guid OutId)> _parallelPairs = new();

    const float RowHeadW = 192f;
    const float ColHeadH = 74f;
    const float SbSize = 6f;

    float _cellW = 78f, _cellH = 44f;
    float _sx, _sy;
    int _hr = -1, _hc = -1;
    int _dr = -1, _dc = -1;
    float _downY;
    double _downDb;
    bool _moved, _recordedDrag;
    int _dragBar;
    float _barOff;

    public MatrixGridWidget(AppModel m) => _m = m;
    public override bool AcceptsMouse => true;

    RectF Inner => Bounds.Deflate(12);
    RectF CellArea => new(Inner.X + RowHeadW, Inner.Y + ColHeadH, Math.Max(0, Inner.W - RowHeadW), Math.Max(0, Inner.H - ColHeadH));
    float ContentW => _cols.Count * _cellW;
    float ContentH => _rows.Count * _cellH;
    bool NeedV => ContentH > CellArea.H + 0.5f;
    bool NeedH => ContentW > CellArea.W + 0.5f;

    public void RebuildAxes()
    {
        var rows = new List<AxisPort>();
        var cols = new List<AxisPort>();

        for (int i = 0; i < _m.InputCount; i++)
        {
            var ch = _m.Settings.Inputs[i];
            string grp = _m.ChannelOwnerTitle(ch);
            if (string.IsNullOrEmpty(grp)) grp = _m.LinkAt(i) != null ? "APP" : "IN";
            rows.Add(new AxisPort(new RoutePort(RouteKind.Input, ch.Id), i, grp, $"{i + 1:00} · {_m.DisplayName(true, i)}", Theme.Teal));
        }

        if (ScopeFilter != 1)
        {
            foreach (var bus in _m.Settings.Buses)
            {
                for (int ch = 0; ch < bus.Channels; ch++)
                {
                    string suffix = bus.Channels == 2 ? (ch == 0 ? "L" : "R") : $"Ch {ch + 1}";
                    var port = new AxisPort(new RoutePort(RouteKind.Bus, bus.Id, ch), ch, "BUS", $"{bus.Name} {suffix}", Theme.Purple);
                    rows.Add(port);
                    cols.Add(port);
                }
            }
        }

        for (int o = 0; o < _m.OutputCount; o++)
        {
            var ch = _m.Settings.Outputs[o];
            string grp = _m.ChannelOwnerTitle(ch);
            if (string.IsNullOrEmpty(grp)) grp = "OUT";
            cols.Add(new AxisPort(new RoutePort(RouteKind.Output, ch.Id), o, grp, $"{o + 1:00} · {_m.DisplayName(false, o)}", Theme.AccentText));
        }

        if (ScopeFilter == 2)
        {
            // Nur Zeilen/Spalten zeigen, bei denen mindestens ein Partner ein Bus ist
            if (_m.BusCount == 0) { rows.Clear(); cols.Clear(); }
        }

        _rows = rows;
        _cols = cols;

        _parallelPairs.Clear();
        foreach (var w in _m.ParallelWarnings)
            _parallelPairs.Add((w.InputId, w.OutputId));

        Layout();
    }

    public override void Layout()
    {
        (float minW, float minH) = ZoomMode switch
        {
            0 => (58f, 34f),
            2 => (98f, 56f),
            _ => (74f, 44f),
        };
        if (Theme.Touch) { minW += 12f; minH += 8f; }
        var a = CellArea;
        _cellW = _cols.Count > 0 ? Math.Clamp(a.W / _cols.Count, minW, 132f) : minW;
        _cellH = _rows.Count > 0 ? Math.Clamp(a.H / _rows.Count, minH, 64f) : minH;
        _sx = Math.Clamp(_sx, 0, Math.Max(0, ContentW - a.W));
        _sy = Math.Clamp(_sy, 0, Math.Max(0, ContentH - a.H));
    }

    bool IsBlockedCell(AxisPort r, AxisPort c) =>
        (ScopeFilter == 2 && r.Port.Kind == RouteKind.Input && c.Port.Kind == RouteKind.Output)
        || (r.Port.Kind == RouteKind.Bus && c.Port.Kind == RouteKind.Bus && r.Port.Id == c.Port.Id);

    double GetCellDb(AxisPort r, AxisPort c)
    {
        if (IsBlockedCell(r, c)) return double.NegativeInfinity;
        if (r.Port.Kind == RouteKind.Input && c.Port.Kind == RouteKind.Output)
        {
            var m = _m.Settings.MatrixDb;
            return r.Index < m.Length && c.Index < m[r.Index].Length ? m[r.Index][c.Index] : double.NegativeInfinity;
        }
        var br = _m.FindBusRoute(r.Port, c.Port);
        return br?.GainDb ?? double.NegativeInfinity;
    }

    double MaxDbFor(AxisPort c) =>
        c.Port.Kind == RouteKind.Output && AppModel.IsProtected(_m.Settings.Outputs[c.Index])
            ? Theme.ProtectedMatrixMaxDb
            : Theme.MatrixMaxDb;

    void SetCellDb(AxisPort r, AxisPort c, double db, bool recordUndo = true)
    {
        if (IsBlockedCell(r, c)) return;
        if (r.Port.Kind == RouteKind.Input && c.Port.Kind == RouteKind.Output)
        {
            _m.SetMatrix(r.Index, c.Index, db, recordUndo);
            return;
        }
        string? err = _m.SetOrAddBusRoute(r.Port, c.Port, db, recordUndo);
        if (err != null) Root?.Notify.Flash(err, true);
    }

    RectF CellRect(int r, int c)
    {
        var a = CellArea;
        return new RectF(a.X + c * _cellW - _sx, a.Y + r * _cellH - _sy, _cellW, _cellH);
    }

    bool CellAt(float x, float y, out int r, out int c)
    {
        r = c = -1;
        var a = CellArea;
        if (!a.Contains(x, y)) return false;
        if (NeedV && VBar().Track.Inflate(5).Contains(x, y)) return false;
        if (NeedH && HBar().Track.Inflate(5).Contains(x, y)) return false;
        c = (int)((x - a.X + _sx) / _cellW);
        r = (int)((y - a.Y + _sy) / _cellH);
        return r >= 0 && r < _rows.Count && c >= 0 && c < _cols.Count;
    }

    (RectF Track, RectF Thumb) VBar()
    {
        var a = CellArea;
        var track = new RectF(a.Right - SbSize - 2, a.Y + 2, SbSize, Math.Max(10, a.H - 4 - (NeedH ? SbSize + 2 : 0)));
        float th = Math.Max(28, track.H * a.H / Math.Max(1, ContentH));
        float maxS = Math.Max(1, ContentH - a.H);
        return (track, new RectF(track.X, track.Y + (track.H - th) * (_sy / maxS), SbSize, th));
    }

    (RectF Track, RectF Thumb) HBar()
    {
        var a = CellArea;
        var track = new RectF(a.X + 2, a.Bottom - SbSize - 2, Math.Max(10, a.W - 4 - (NeedV ? SbSize + 2 : 0)), SbSize);
        float tw = Math.Max(28, track.W * a.W / Math.Max(1, ContentW));
        float maxS = Math.Max(1, ContentW - a.W);
        return (track, new RectF(track.X + (track.W - tw) * (_sx / maxS), track.Y, tw, SbSize));
    }

    public override void OnMouseMove(in MouseEvent e)
    {
        if (_dragBar != 0 && IsPressed)
        {
            var a = CellArea;
            if (_dragBar == 1)
            {
                var (t, th) = VBar();
                if (t.H > th.H) _sy = Math.Clamp((e.Y - _barOff - t.Y) / (t.H - th.H), 0, 1) * Math.Max(0, ContentH - a.H);
            }
            else
            {
                var (t, th) = HBar();
                if (t.W > th.W) _sx = Math.Clamp((e.X - _barOff - t.X) / (t.W - th.W), 0, 1) * Math.Max(0, ContentW - a.W);
            }
            Invalidate();
            return;
        }
        if (!IsPressed)
        {
            CellAt(e.X, e.Y, out int r, out int c);
            if (r != _hr || c != _hc) { _hr = r; _hc = c; Invalidate(); }
            return;
        }
        if (_dr < 0 || _dc < 0) return;
        float dy = _downY - e.Y;
        if (!_moved && Math.Abs(dy) < 4) return;
        if (!_moved)
        {
            _moved = true;
            if (Db.IsOff(_downDb)) _downDb = 0;
        }
        double perPx = e.Fine ? 0.03 : 0.3;
        double v = Math.Clamp(Math.Round((_downDb + dy * perPx) * 10) / 10, Theme.MatrixMinDb, MaxDbFor(_cols[_dc]));
        if (Math.Abs(v) < 0.2 && !e.Fine) v = 0;
        var row = _rows[_dr];
        var col = _cols[_dc];
        double before = GetCellDb(row, col);
        SetCellDb(row, col, v, !_recordedDrag);
        _recordedDrag |= GetCellDb(row, col) != before;
        Invalidate();
    }

    public override void OnMouseDown(in MouseEvent e)
    {
        _dr = _dc = -1; _moved = false; _recordedDrag = false; _dragBar = 0;
        if (e.Button == 0)
        {
            if (NeedV && VBar().Track.Inflate(5).Contains(e.X, e.Y))
            {
                var (_, th) = VBar(); _dragBar = 1; _barOff = th.Contains(e.X, e.Y) ? e.Y - th.Y : th.H / 2; return;
            }
            if (NeedH && HBar().Track.Inflate(5).Contains(e.X, e.Y))
            {
                var (_, th) = HBar(); _dragBar = 2; _barOff = th.Contains(e.X, e.Y) ? e.X - th.X : th.W / 2; return;
            }
        }
        if (!CellAt(e.X, e.Y, out int r, out int c)) return;
        if (IsBlockedCell(_rows[r], _cols[c])) return;

        if (e.Button == 1)
        {
            ShowSignalPath(_rows[r], _cols[c], new RectF(e.X, e.Y, 1, 1));
            return;
        }
        if (e.Button != 0) return;

        _dr = r; _dc = c;
        _downY = e.Y;
        _downDb = GetCellDb(_rows[r], _cols[c]);
    }

    public override void OnMouseUp(in MouseEvent e)
    {
        if (_dragBar != 0) { _dragBar = 0; Invalidate(); return; }
        if (e.Button == 0 && !_moved && _dr >= 0 && _dc >= 0 && CellAt(e.X, e.Y, out int r, out int c) && r == _dr && c == _dc)
        {
            double cur = GetCellDb(_rows[r], _cols[c]);
            double next = Db.IsOff(cur) ? Math.Min(0.0, MaxDbFor(_cols[c])) : double.NegativeInfinity;
            SetCellDb(_rows[r], _cols[c], next);
        }
        _dr = _dc = -1; _moved = false;
        Invalidate();
    }

    public override bool OnMouseWheel(in MouseEvent e)
    {
        if (CellAt(e.X, e.Y, out int r, out int c) && !IsBlockedCell(_rows[r], _cols[c]))
        {
            double cur = GetCellDb(_rows[r], _cols[c]);
            if (!Db.IsOff(cur))
            {
                double step = e.Fine ? 0.1 : 1.0;
                double next = Math.Clamp(Math.Round((cur + Math.Sign(e.Wheel) * step) * 10) / 10, Theme.MatrixMinDb, MaxDbFor(_cols[c]));
                SetCellDb(_rows[r], _cols[c], next);
                Invalidate();
                return true;
            }
        }
        if (e.Shift && NeedH)
        {
            _sx = Math.Clamp(_sx - e.Wheel * 60, 0, Math.Max(0, ContentW - CellArea.W));
            Invalidate();
            return true;
        }
        if (NeedV)
        {
            _sy = Math.Clamp(_sy - e.Wheel * 60, 0, Math.Max(0, ContentH - CellArea.H));
            Invalidate();
            return true;
        }
        return false;
    }

    public override CursorKind CursorAt(float x, float y) =>
        CellAt(x, y, out int r, out int c) && !IsBlockedCell(_rows[r], _cols[c]) ? CursorKind.Hand : CursorKind.Arrow;

    public override string? TooltipAt(float x, float y)
    {
        if (!CellAt(x, y, out int r, out int c)) return null;
        var rp = _rows[r]; var cp = _cols[c];
        if (IsBlockedCell(rp, cp)) return "Nicht verfügbar (Eigenrückkopplung / Filtermodus)";
        double db = GetCellDb(rp, cp);
        bool par = rp.Port.Kind == RouteKind.Input && cp.Port.Kind == RouteKind.Output && _parallelPairs.Contains((rp.Port.Id, cp.Port.Id));
        string status = Db.IsOff(db) ? "Getrennt (Klick = 0.0 dB)" : $"{Db.Format(db)} dB (Klick = trennen · Ziehen/Mausrad = Pegel)";
        if (par) status += "\n⚡ Parallelpfad aktiv: Dieser Eingang speist denselben Ausgang zusätzlich über einen Mix-Bus!";
        return $"{rp.Label}  →  {cp.Label}\n{status}\nRechtsklick · Signalweg prüfen";
    }

    void ShowSignalPath(AxisPort source, AxisPort target, RectF anchor)
    {
        if (Root is not { } root) return;
        string Peak(float value) => value > 0.00001f ? $"{20 * Math.Log10(value):0.0} dBFS" : "kein Pegel";
        var items = new List<MenuItem> { MenuItem.Header($"{source.Label} → {target.Label}") };
        if (!_m.HostRunning) items.Add(MenuItem.Header("Audio-Engine gestoppt"));
        if (_m.PanicMuted) items.Add(MenuItem.Header("Panik-Stumm aktiv"));
        if (Db.IsOff(_m.Settings.MasterGainDb)) items.Add(MenuItem.Header("Master auf −∞"));

        if (source.Port.Kind == RouteKind.Input)
        {
            var channel = _m.Channel(true, source.Index);
            items.Add(MenuItem.Header($"Eingang nach Fader · {Peak(_m.GetInputPeak(source.Index))}"));
            if (channel.Mute || Db.IsOff(channel.GainDb)) items.Add(MenuItem.Header("Eingang stumm"));
            if (_m.Settings.Inputs.Any(c => c.Solo) && !channel.Solo) items.Add(MenuItem.Header("Ein anderer Eingang ist auf Solo"));
            if (_m.LinkAt(source.Index) is { } link) items.Add(MenuItem.Header(LinkLook.Text(_m.ViewOf(link).State)));
        }

        double db = GetCellDb(source, target);
        items.Add(MenuItem.Header(Db.IsOff(db) ? "Kreuzpunkt getrennt" : $"Kreuzpunkt · {Db.Format(db)} dB"));
        if (target.Port.Kind == RouteKind.Output)
        {
            var channel = _m.Channel(false, target.Index);
            items.Add(MenuItem.Header($"Ausgang gesamt · {Peak(_m.GetOutputPeak(target.Index))}"));
            if (channel.Mute || Db.IsOff(channel.GainDb)) items.Add(MenuItem.Header("Ausgang stumm"));
            if (_m.Settings.Outputs.Any(c => c.Solo) && !channel.Solo) items.Add(MenuItem.Header("Ein anderer Ausgang ist auf Solo"));
            string? endpoint = _m.Config.Devices.FirstOrDefault(device => device.Id == channel.DeviceId)?.EndpointId
                ?? _m.Config.Vaios.FirstOrDefault(slot => slot.Id == channel.DeviceId)?.EndpointOrExe;
            if (_m.Runtime.HostStats.Devices.Any(d => d.IsOffline && SameDevice(d.DeviceId, endpoint)))
                items.Add(MenuItem.Header("Ausgabegerät offline"));
        }
        for (int i = 0; i < _m.Settings.Buses.Count; i++)
        {
            var bus = _m.Settings.Buses[i];
            if (bus.Id != source.Port.Id && bus.Id != target.Port.Id) continue;
            items.Add(MenuItem.Header($"Bus „{bus.Name}“ gesamt · {Peak(_m.GetBusPeak(i))}"));
            if (bus.Mute || Db.IsOff(bus.GainDb)) items.Add(MenuItem.Header($"Bus „{bus.Name}“ stumm"));
            else if (!bus.Solo && _m.Settings.Buses.Any(b => b.Solo)) items.Add(MenuItem.Header("Ein anderer Bus ist auf Solo"));
        }
        if (_parallelPairs.Contains((source.Port.Id, target.Port.Id)))
            items.Add(MenuItem.Header("Direkt- und Buspfad parallel"));
        if (!Db.IsOff(db))
            items.Add(new MenuItem("Kreuzpunkt trennen", null, () =>
            {
                int row = _rows.FindIndex(p => p.Port == source.Port);
                int col = _cols.FindIndex(p => p.Port == target.Port);
                if (row >= 0 && col >= 0) SetCellDb(_rows[row], _cols[col], double.NegativeInfinity);
            }));
        PopupMenu.Show(root, anchor, items);
    }

    static bool SameDevice(string a, string? b) => a == b ||
        (b != null && NxMix.Audio.AsioCatalog.IsAsio(a) && NxMix.Audio.AsioCatalog.IsAsio(b) &&
            NxMix.Audio.AsioCatalog.DriverName(a) == NxMix.Audio.AsioCatalog.DriverName(b));

    public override void Paint(Renderer g)
    {
        Draw.Card(g, Bounds);
        if (_rows.Count == 0 || _cols.Count == 0)
        {
            string msg = ScopeFilter == 2 && _m.BusCount == 0
                ? "Lege oben mit „+ Mix-Bus“ einen Stereo- oder Subgruppen-Bus an, um Bus-Routings zu patchen."
                : "Sobald mindestens eine Quelle und ein Ziel vorhanden sind, lassen sich hier alle direkten und Bus-Kreuzpunkte patchen.";
            float ey = Bounds.Y + Math.Max(20, (Bounds.H - Draw.EmptyHeight(Bounds.W, msg)) / 2);
            Draw.Empty(g, Bounds, ey, Icon.Matrix, "Keine Kreuzpunkte in dieser Ansicht", msg);
            return;
        }

        var inn = Inner;
        var ca = CellArea;

        // Ecke oben links: Legende
        g.Text("QUELLE  ↓   \\   ZIEL  →", new RectF(inn.X + 8, inn.Y + 8, RowHeadW - 14, ColHeadH - 12), Theme.MicroStrong, Theme.TextTertiary);

        // Spaltenköpfe
        g.PushClip(new RectF(ca.X, inn.Y, ca.W, ColHeadH));
        for (int c = 0; c < _cols.Count; c++)
        {
            var cp = _cols[c];
            var hr = new RectF(ca.X + c * _cellW - _sx + 2, inn.Y + 2, _cellW - 4, ColHeadH - 6);
            if (hr.Right < ca.X || hr.X > ca.Right) continue;
            g.FillRound(hr, Theme.RadiusSmall, c == _hc ? Theme.ControlHover : Theme.Surface2);
            g.FillPill(new RectF(hr.X + 6, hr.Y + 4, hr.W - 12, 3), cp.Color);
            g.Text(cp.GroupTag, new RectF(hr.X + 4, hr.Y + 9, hr.W - 8, 13), Theme.MicroStrong, cp.Color, HAlign.Center);
            g.Text(cp.Label, new RectF(hr.X + 4, hr.Y + 22, hr.W - 8, hr.H - 25), Theme.CaptionStrong, Theme.Text, HAlign.Center, VAlign.Middle, true, true);
        }
        g.PopClip();

        // Zeilenköpfe
        g.PushClip(new RectF(inn.X, ca.Y, RowHeadW, ca.H));
        for (int r = 0; r < _rows.Count; r++)
        {
            var rp = _rows[r];
            var hr = new RectF(inn.X + 2, ca.Y + r * _cellH - _sy + 2, RowHeadW - 6, _cellH - 4);
            if (hr.Bottom < ca.Y || hr.Y > ca.Bottom) continue;
            g.FillRound(hr, Theme.RadiusSmall, r == _hr ? Theme.ControlHover : Theme.Surface2);
            g.FillPill(new RectF(hr.X + 4, hr.Y + 6, 3, hr.H - 12), rp.Color);
            g.Text(rp.GroupTag, new RectF(hr.X + 12, hr.Y + 3, hr.W - 16, 13), Theme.MicroStrong, rp.Color);
            g.Text(rp.Label, new RectF(hr.X + 12, hr.Y + 15, hr.W - 16, hr.H - 17), Theme.CalloutMedium, Theme.Text);
        }
        g.PopClip();

        // Kreuzpunkt-Zellen
        g.PushClip(ca);
        for (int r = 0; r < _rows.Count; r++)
        {
            float cy = ca.Y + r * _cellH - _sy;
            if (cy + _cellH < ca.Y || cy > ca.Bottom) continue;
            var rp = _rows[r];

            for (int c = 0; c < _cols.Count; c++)
            {
                var cr = CellRect(r, c).Deflate(2);
                if (cr.Right < ca.X || cr.X > ca.Right) continue;
                var cp = _cols[c];

                if (IsBlockedCell(rp, cp))
                {
                    g.FillRound(cr, Theme.RadiusSmall, Theme.Well.With(0.45f));
                    continue;
                }

                double db = GetCellDb(rp, cp);
                bool active = !Db.IsOff(db);
                bool hot = r == _hr && c == _hc;
                bool isBusCell = rp.Port.Kind == RouteKind.Bus || cp.Port.Kind == RouteKind.Bus;
                var activeTint = isBusCell ? Theme.Purple : Theme.Accent;

                var bg = active
                    ? activeTint.With(Theme.IsDark ? 0.28f : 0.18f)
                    : (hot ? Theme.ControlHover : Theme.Well);
                g.FillRound(cr, Theme.RadiusSmall, bg);
                if (active || hot)
                    g.StrokeRound(cr, Theme.RadiusSmall, active ? activeTint.With(0.85f) : Theme.BorderStrong);

                if (active)
                {
                    g.Text(Db.Format(db), cr, _cellW < 64 ? Theme.CaptionStrong : Theme.Value,
                        db > 0.05 ? Theme.Yellow : Theme.Text, HAlign.Center);
                }
                else if (hot)
                {
                    g.Text("—", cr, Theme.Caption, Theme.TextTertiary, HAlign.Center);
                }

                // Parallel-Pfad-Warnpunkt (Direkt + Bus aktiv)
                if (rp.Port.Kind == RouteKind.Input && cp.Port.Kind == RouteKind.Output && _parallelPairs.Contains((rp.Port.Id, cp.Port.Id)))
                {
                    g.FillCircle(cr.Right - 6, cr.Y + 6, 3.5f, Theme.Orange);
                }
            }
        }
        if (NeedV) g.FillPill(VBar().Thumb, Theme.TextTertiary.With(0.6f));
        if (NeedH) g.FillPill(HBar().Thumb, Theme.TextTertiary.With(0.6f));
        g.PopClip();
    }
}

/// <summary>Tab „Patching Matrix“ (Strg+4): hierarchische 2D-Kreuzschiene für Eingänge, VAIOs, Mix-Busse und Ausgänge.</summary>
internal sealed class MatrixPage : Page
{
    readonly AppModel _m;
    readonly Segmented _scopeSeg = new("Komplett", "Direkt In→Out", "Bus-Routing") { Font = Theme.CaptionMedium };
    readonly Segmented _zoomSeg = new("Kompakt", "Standard", "Groß") { Font = Theme.CaptionMedium };
    readonly Button _diagBtn = new("1:1 Diagonal", ButtonStyle.Gray) { Font = Theme.CalloutMedium, Tooltip = "Eingang 1→Ausgang 1, 2→2 usw. mit 0 dB verbinden" };
    readonly Button _stereoBtn = new("Stereo L/R", ButtonStyle.Gray) { Font = Theme.CalloutMedium, Tooltip = "Ungerade Eingänge auf Ausgang 1 (L), gerade auf Ausgang 2 (R) patchen" };
    readonly Button _clearBtn = new("Direkt trennen", ButtonStyle.DestructiveTinted) { Font = Theme.CalloutMedium };
    readonly Button _addBusBtn = new("+ Mix-Bus", ButtonStyle.Filled, Icon.Plus) { Font = Theme.CalloutMedium };
    readonly MatrixGridWidget _grid;

    public override string Title => "Patching Matrix";
    public override Icon Icon => Icon.Matrix;
    public override string? Subtitle
    {
        get
        {
            int direct = _m.Settings.MatrixDb.Sum(r => r.Count(v => !Db.IsOff(v)));
            int busRoutes = _m.Settings.BusRoutes.Count;
            int par = _m.ParallelWarnings.Count;
            return $"{direct} Direkt-Routen  ·  {busRoutes} Bus-Routen" + (par > 0 ? $"  ·  ⚡ {par} Parallelpfade" : "");
        }
    }

    internal MatrixGridWidget Grid => _grid;

    public MatrixPage(AppModel m)
    {
        _m = m;
        _grid = Add(new MatrixGridWidget(m));
        Add(_scopeSeg); Add(_zoomSeg); Add(_diagBtn); Add(_stereoBtn); Add(_clearBtn); Add(_addBusBtn);

        _zoomSeg.SetSelected(1);
        _scopeSeg.Changed += i => { _grid.ScopeFilter = i; _grid.RebuildAxes(); Invalidate(); };
        _zoomSeg.Changed += i => { _grid.ZoomMode = i; _grid.Layout(); Invalidate(); };
        _diagBtn.Clicked += () => PreviewPatch(stereo: false);
        _stereoBtn.Clicked += () => PreviewPatch(stereo: true);
        _clearBtn.Clicked += () => Root?.Confirm("Alle direkten Kreuzpunkte trennen?",
            $"{_m.Settings.MatrixDb.Sum(r => r.Count(v => !Db.IsOff(v)))} direkte In→Out-Verbindungen werden getrennt. Bus-Routen bleiben erhalten.",
            "Trennen", () => _m.ClearAllDirectMatrix(), danger: true);
        _addBusBtn.Clicked += ShowAddBusMenu;

        m.StructureChanged += () => { _grid.RebuildAxes(); Invalidate(); };
        m.ValuesChanged += () => { _grid.RebuildAxes(); Invalidate(); };
        _grid.RebuildAxes();
    }

    public void UndoPatch()
    {
        if (_m.UndoPatch()) Root?.Notify.Flash("Letzte Patch-Änderung rückgängig gemacht.");
    }

    void PreviewPatch(bool stereo)
    {
        int count = stereo ? (_m.OutputCount < 2 ? 0 : _m.InputCount) : Math.Min(_m.InputCount, _m.OutputCount);
        var rows = Enumerable.Range(0, count)
            .Select(i => (Input: i, Output: stereo ? i % 2 : i))
            .Where(pair => _m.Settings.MatrixDb[pair.Input][pair.Output] != 0).ToArray();
        if (rows.Length == 0) { Root?.Notify.Flash("Keine Patch-Änderungen erforderlich."); return; }
        string preview = string.Join("\n", rows.Take(5).Select(pair => $"IN {pair.Input + 1:00} → OUT {pair.Output + 1:00}"));
        if (rows.Length > 5) preview += $"\n… und {rows.Length - 5} weitere";
        Root?.Confirm(stereo ? "Stereo L/R patchen?" : "1:1 Diagonal patchen?",
            $"{rows.Length} Kreuzpunkte auf 0 dB setzen. Andere Routen bleiben erhalten.\n\n{preview}", "Patchen", () =>
            {
                if (stereo) _m.PatchStereoPairs();
                else _m.PatchDiagonal1To1();
            });
    }

    void ShowAddBusMenu()
    {
        if (Root is not { } root) return;
        PopupMenu.Show(root, _addBusBtn.Bounds, new List<MenuItem>
        {
            new("Stereo-Bus anlegen (2 Kanäle)", null, () => _m.AddBus(2)) { Icon = Icon.Plus },
            new("Mono-Bus anlegen (1 Kanal)", null, () => _m.AddBus(1)) { Icon = Icon.Plus },
            new("Surround-/Mehrkanal-Bus (6 Kanäle)", null, () => _m.AddBus(6)) { Icon = Icon.Plus },
        }, 250);
    }

    public override void OnShow() { _grid.RebuildAxes(); Layout(); }

    public override void Layout()
    {
        if (Bounds.W < 20 || Bounds.H < 20) return;
        var area = Bounds.Deflate(Theme.Pad, 10, Theme.Pad, Theme.Pad);
        float barH = Theme.ControlH;

        float rx = area.Right;
        float wBus = _addBusBtn.PreferredWidth;
        _addBusBtn.Bounds = new RectF(rx - wBus, area.Y, wBus, barH); rx -= wBus + 8;

        float x = area.X;
        float scopeW = Math.Min(270f, Math.Max(140f, rx - x - 6f));
        _scopeSeg.Bounds = new RectF(x, area.Y, scopeW, barH); x += scopeW + 8;

        _zoomSeg.Bounds = new RectF(x, area.Y, 200f, barH);
        _zoomSeg.Visible = _zoomSeg.Bounds.Right + 6 <= rx;
        if (_zoomSeg.Visible) x += 208f;

        float wClr = _clearBtn.PreferredWidth;
        _clearBtn.Bounds = new RectF(rx - wClr, area.Y, wClr, barH);
        _clearBtn.Visible = _clearBtn.Bounds.X > x;
        if (_clearBtn.Visible) rx -= wClr + 6;

        float wSt = _stereoBtn.PreferredWidth;
        _stereoBtn.Bounds = new RectF(rx - wSt, area.Y, wSt, barH);
        _stereoBtn.Visible = _stereoBtn.Bounds.X > x;
        if (_stereoBtn.Visible) rx -= wSt + 6;

        float wDg = _diagBtn.PreferredWidth;
        _diagBtn.Bounds = new RectF(rx - wDg, area.Y, wDg, barH);
        _diagBtn.Visible = _diagBtn.Bounds.X > x;

        _grid.Bounds = new RectF(area.X, area.Y + barH + 10, area.W, Math.Max(0, area.H - barH - 10));
        _grid.Layout();
    }
}
