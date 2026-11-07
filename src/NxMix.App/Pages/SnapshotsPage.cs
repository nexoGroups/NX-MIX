using System.Globalization;
using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Engine;
using NxMix.Ipc;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Tab „Snapshots“ (Strg+7):
/// Szenen-Verwaltung einer digitalen Live-Konsole mit Sample-genauer Überblendzeit (0 ms .. 10 s),
/// selektivem Recall-Umfang (Scope), Live-Diff-Vorschau und pro-Kanal/Bus/Master schaltbarer Recall-Safe-Matrix.
/// Snapshots verändern ausschließlich den Mischzustand und niemals SystemConfig (Hardware/Takt/App-Links).</summary>
internal sealed class SnapshotsPage : Page
{
    readonly AppModel _m;
    Guid? _selectedId;
    double _fadeMs = 150.0;
    SnapshotScopeFlags _scope = SnapshotScopeFlags.All;
    int _rightMode; // 0 = Diff-Vorschau, 1 = Recall-Safe-Matrix
    SnapshotDiffPreview? _diff;

    // Linke Karte: Snapshot-Liste
    readonly Button _newBtn = new("+ Neu", ButtonStyle.Filled) { Font = Theme.CaptionStrong, Tooltip = "Aktuellen Live-Mix als neuen Snapshot speichern" };
    readonly Button _updBtn = new("Aktualisieren", ButtonStyle.Gray) { Font = Theme.CaptionMedium, Tooltip = "Ausgewählten Snapshot mit aktuellem Live-Mix überschreiben" };
    readonly Button _dupBtn = new("Kopie", ButtonStyle.Gray, Icon.Copy) { Font = Theme.CaptionMedium, Tooltip = "Ausgewählten Snapshot duplizieren" };
    readonly Button _renBtn = new("Name", ButtonStyle.Gray, Icon.Edit) { Font = Theme.CaptionMedium, Tooltip = "Ausgewählten Snapshot umbenennen" };
    readonly Button _delBtn = new("Löschen", ButtonStyle.DestructiveTinted, Icon.Trash) { Font = Theme.CaptionMedium, Tooltip = "Snapshot löschen" };

    // Rechte Karte: Recall, Blende, Scope, Diff / Recall-Safe
    readonly Button _recallBtn = new("Snapshot abrufen", ButtonStyle.Filled, Icon.Play) { Font = Theme.CaptionStrong, Tooltip = "Gewählten Snapshot mit eingestellter Blende und Scope abrufen" };
    readonly Segmented _fadeSeg = new("0 ms", "100", "250", "500", "1 s", "3 s") { Font = Theme.MicroStrong };
    readonly ValueBox _fadeBox = new() { EditHint = "Doppelklick: Überblendzeit 0..10000 ms eingeben" };
    readonly Dropdown _scopeDd = new() { Tooltip = "Selektiver Recall-Umfang (welche Parameter-Gruppen abgerufen werden)" };
    readonly Segmented _viewSeg = new("Diff-Vorschau", "Recall-Safe") { Font = Theme.CaptionStrong };

    readonly VScroll _snapScroll = new(), _tableScroll = new();
    RectF _leftCard, _rightCard, _snapListRect, _tableRect, _infoBarRect;

    static readonly double[] s_fadePresets = { 0, 100, 250, 500, 1000, 3000 };

    readonly record struct SafeRow(int Kind, int Index, Guid Id, string Badge, string Name, RecallSafeFlags Flags, Color4 Color);
    List<SafeRow> _safeRows = new();

    int _hotSnap = -1, _hotSafeRow = -1, _hotSafeCol = -1;

    public override string Title => "Snapshots";
    public override Icon Icon => Icon.Snapshots;
    public override string? Subtitle =>
        $"{_m.Snapshots.Count} gespeicherte Mischpult-Snapshots  ·  Hardware & App-Links bleiben stets unberührt";

    public SnapshotsPage(AppModel m)
    {
        _m = m;
        Add(_newBtn); Add(_updBtn); Add(_dupBtn); Add(_renBtn); Add(_delBtn);
        Add(_recallBtn); Add(_fadeSeg); Add(_fadeBox); Add(_scopeDd); Add(_viewSeg);

        _newBtn.Clicked += CreateNewSnapshot;
        _updBtn.Clicked += UpdateSelectedSnapshot;
        _dupBtn.Clicked += () =>
        {
            if (SelectedSummary is { } s) _m.DuplicateSnapshot(s.Id);
        };
        _renBtn.Clicked += RenameSelectedSnapshot;
        _delBtn.Clicked += () =>
        {
            if (SelectedSummary is { } s)
                Root?.Confirm($"Snapshot „{s.Name}“ löschen?", "Der aktuelle Live-Mix läuft unverändert weiter.",
                    "Snapshot löschen", () => { _m.DeleteSnapshot(s.Id); EnsureSelection(); RefreshPreview(); Layout(); Invalidate(); }, danger: true);
        };

        _recallBtn.Clicked += () =>
        {
            if (SelectedSummary is { } s)
            {
                _m.RecallSnapshot(s.Id, _scope, _fadeMs);
                Root?.Notify.Flash($"Snapshot „{s.Name}“ abgerufen ({_fadeMs:0} ms Blende).");
            }
        };

        _fadeSeg.Changed += idx =>
        {
            _fadeMs = s_fadePresets[idx];
            Layout();
            Invalidate();
        };
        _fadeBox.Typed += s =>
        {
            string clean = s.Trim().ToLowerInvariant().Replace(',', '.');
            double mult = 1.0;
            if (clean.EndsWith("ms")) clean = clean[..^2].Trim();
            else if (clean.EndsWith("s")) { mult = 1000.0; clean = clean[..^1].Trim(); }
            if (double.TryParse(clean, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v))
            {
                _fadeMs = Math.Clamp(v * mult, 0.0, 10000.0);
                Layout();
                Invalidate();
            }
        };

        _scopeDd.Text = DescribeScope;
        _scopeDd.Items = BuildScopeMenu;

        _viewSeg.Changed += idx =>
        {
            _rightMode = idx;
            _tableScroll.Offset = 0;
            RebuildSafeRows();
            RefreshPreview();
            Layout();
            Invalidate();
        };

        m.SnapshotsChanged += () => { EnsureSelection(); RefreshPreview(); Layout(); Invalidate(); };
        m.ValuesChanged += () => { RebuildSafeRows(); if (Visible && _rightMode == 0) RefreshPreview(); Layout(); Invalidate(); };
        m.StructureChanged += () => { RebuildSafeRows(); if (Visible && _rightMode == 0) RefreshPreview(); Layout(); Invalidate(); };

        EnsureSelection();
        RebuildSafeRows();
    }

    MixSnapshotSummary? SelectedSummary =>
        _selectedId is { } id ? _m.Snapshots.FirstOrDefault(s => s.Id == id) : null;

    void EnsureSelection()
    {
        if (_selectedId != null && _m.Snapshots.Any(s => s.Id == _selectedId)) return;
        var first = _m.Snapshots.FirstOrDefault(s => s.Id == _m.ActiveSnapshotId) ?? _m.Snapshots.FirstOrDefault();
        _selectedId = first?.Id;
        if (first != null)
        {
            _fadeMs = first.DefaultFadeMs;
            _scope = first.DefaultScope == SnapshotScopeFlags.None ? SnapshotScopeFlags.All : first.DefaultScope;
        }
    }

    public override void OnShow()
    {
        EnsureSelection();
        RebuildSafeRows();
        RefreshPreview();
        Layout();
    }

    void CreateNewSnapshot()
    {
        string defaultName = $"Szene {_m.Snapshots.Count + 1}";
        string[] palette = { "#3B82F6", "#10B981", "#F59E0B", "#A855F7", "#EC4899" };
        string color = palette[_m.Snapshots.Count % palette.Length];
        _m.SaveSnapshot(null, defaultName, "Aus Live-Mix erfasst", color, _fadeMs, _scope, saved =>
        {
            _selectedId = saved.Id;
            RefreshPreview();
            Layout();
            Invalidate();
            Root?.Notify.Flash($"Snapshot „{saved.Name}“ gespeichert.");
        });
    }

    void UpdateSelectedSnapshot()
    {
        if (SelectedSummary is not { } s) return;
        _m.SaveSnapshot(s.Id, s.Name, s.Notes, s.ColorHex, _fadeMs, _scope, saved =>
        {
            RefreshPreview();
            Layout();
            Invalidate();
            Root?.Notify.Flash($"Snapshot „{saved.Name}“ mit Live-Mix aktualisiert.");
        });
    }

    void RenameSelectedSnapshot()
    {
        if (SelectedSummary is not { } s || Root == null) return;
        Root.BeginEdit(_renBtn.Bounds, s.Name, Theme.BodyMedium, nextName =>
        {
            if (string.IsNullOrWhiteSpace(nextName)) return;
            _m.RenameSnapshot(s.Id, nextName.Trim(), _ =>
            {
                RefreshPreview();
                Layout();
                Invalidate();
            });
        }, 36);
    }

    void RefreshPreview()
    {
        if (SelectedSummary is not { } s)
        {
            _diff = null;
            return;
        }
        _m.PreviewSnapshot(s.Id, _scope, preview =>
        {
            _diff = preview;
            Layout();
            Invalidate();
        });
    }

    string DescribeScope()
    {
        if (_scope == SnapshotScopeFlags.All) return "Umfang: Komplett (Alle Mix-Parameter)";
        if (_scope == SnapshotScopeFlags.None) return "Umfang: Nichts gewählt";
        int n = 0;
        foreach (SnapshotScopeFlags f in new[]
        {
            SnapshotScopeFlags.InputFaders, SnapshotScopeFlags.InputMutes, SnapshotScopeFlags.InputPans,
            SnapshotScopeFlags.OutputFaders, SnapshotScopeFlags.OutputMutes, SnapshotScopeFlags.DeviceMasters,
            SnapshotScopeFlags.Buses, SnapshotScopeFlags.MatrixRouting, SnapshotScopeFlags.OutputDsp
        })
        {
            if ((_scope & f) != 0) n++;
        }
        return $"Umfang: Selektiv ({n}/9 Bereiche)";
    }

    List<MenuItem> BuildScopeMenu()
    {
        void ToggleFlag(SnapshotScopeFlags flag)
        {
            _scope = (_scope & flag) != 0 ? (_scope & ~flag) : (_scope | flag);
            RefreshPreview();
            Layout();
            Invalidate();
        }

        void SetPreset(SnapshotScopeFlags preset)
        {
            _scope = preset;
            RefreshPreview();
            Layout();
            Invalidate();
        }

        var items = new List<MenuItem>
        {
            MenuItem.Header("Schnellauswahl"),
            new("Alle Mix-Parameter", null, () => SetPreset(SnapshotScopeFlags.All)) { Checked = _scope == SnapshotScopeFlags.All },
            new("Nur Eingangs-Mix (Fader, Mute, Pan)", null, () => SetPreset(SnapshotScopeFlags.InputFaders | SnapshotScopeFlags.InputMutes | SnapshotScopeFlags.InputPans)),
            new("Nur Routing & Subgruppen (Matrix + Busse)", null, () => SetPreset(SnapshotScopeFlags.MatrixRouting | SnapshotScopeFlags.Buses)),
            new("Ohne Ausgangs-DSP (Weiche/EQ/REW schonen)", null, () => SetPreset(SnapshotScopeFlags.All & ~SnapshotScopeFlags.OutputDsp)),
            MenuItem.Sep,
            MenuItem.Header("Einzelne Teilbereiche ein-/ausschalten"),
        };

        (SnapshotScopeFlags Flag, string Label)[] flags =
        {
            (SnapshotScopeFlags.InputFaders, "Eingangs-Fader & Trim"),
            (SnapshotScopeFlags.InputMutes, "Eingangs-Mutes & Solo"),
            (SnapshotScopeFlags.InputPans, "Eingangs-Panorama"),
            (SnapshotScopeFlags.DeviceMasters, "Geräte-Master (Pegel/Mute/Dim)"),
            (SnapshotScopeFlags.Buses, "Mix-Busse (Pegel/Mute)"),
            (SnapshotScopeFlags.MatrixRouting, "Patching-Matrix & Bus-Routen"),
            (SnapshotScopeFlags.OutputFaders, "Ausgangs-Fader"),
            (SnapshotScopeFlags.OutputMutes, "Ausgangs-Mutes"),
            (SnapshotScopeFlags.OutputDsp, "Ausgangs-DSP (Weiche, PEQ, REW, Delay)"),
        };

        foreach (var (flag, label) in flags)
        {
            var fl = flag;
            items.Add(new MenuItem(label, null, () => ToggleFlag(fl)) { Checked = (_scope & fl) != 0 });
        }
        return items;
    }

    void RebuildSafeRows()
    {
        var list = new List<SafeRow>();
        for (int i = 0; i < _m.Settings.DeviceMasters.Count; i++)
        {
            var dm = _m.Settings.DeviceMasters[i];
            list.Add(new SafeRow(0, i, dm.Id, dm.IsInput ? "DEV IN" : "DEV OUT",
                string.IsNullOrWhiteSpace(dm.Name) ? "Geräte-Master" : dm.Name, dm.RecallSafe, dm.IsInput ? Theme.Teal : Theme.Accent));
        }
        for (int i = 0; i < _m.InputCount; i++)
        {
            var ch = _m.Settings.Inputs[i];
            list.Add(new SafeRow(1, i, ch.Id, $"IN {i + 1:00}", _m.DisplayName(true, i), ch.RecallSafe, Theme.Green));
        }
        for (int o = 0; o < _m.OutputCount; o++)
        {
            var ch = _m.Settings.Outputs[o];
            list.Add(new SafeRow(2, o, ch.Id, $"OUT {o + 1:00}", _m.DisplayName(false, o), ch.RecallSafe, Theme.AccentText));
        }
        for (int b = 0; b < _m.Settings.Buses.Count; b++)
        {
            var bus = _m.Settings.Buses[b];
            list.Add(new SafeRow(3, b, bus.Id, "BUS", bus.Name, bus.RecallSafe, Theme.Purple));
        }
        _safeRows = list;
    }

    void ToggleSafeFlag(SafeRow row, RecallSafeFlags flag)
    {
        RecallSafeFlags Next(RecallSafeFlags cur) =>
            flag == RecallSafeFlags.All
                ? (cur == RecallSafeFlags.All ? RecallSafeFlags.None : RecallSafeFlags.All)
                : ((cur & flag) != 0 ? (cur & ~flag) : (cur | flag));

        switch (row.Kind)
        {
            case 0:
                _m.UpdateDeviceMaster(row.Id, dm => dm with { RecallSafe = Next(dm.RecallSafe) }, row.Name);
                break;
            case 1:
                _m.UpdateChannel(true, row.Index, c => c with { RecallSafe = Next(c.RecallSafe) });
                break;
            case 2:
                _m.UpdateChannel(false, row.Index, c => c with { RecallSafe = Next(c.RecallSafe) });
                break;
            case 3:
                _m.UpdateBus(row.Id, b => b with { RecallSafe = Next(b.RecallSafe) });
                break;
        }
    }

    static float SnapRowH => Theme.Touch ? 52f : 44f;
    static float TableRowH => Theme.Touch ? 40f : 34f;

    public override void Layout()
    {
        var area = Bounds.Deflate(Theme.Pad, 10, Theme.Pad, Theme.Pad);
        float gap = Theme.Gap;
        float leftW = Math.Clamp(area.W * 0.30f, 198f, 290f);
        float rightW = Math.Max(240f, area.W - leftW - gap);

        _leftCard = new RectF(area.X, area.Y, leftW, area.H);
        _rightCard = new RectF(_leftCard.Right + gap, area.Y, rightW, area.H);

        float pad = Theme.CardPad;
        float ch = Theme.ControlHSmall;

        // Linke Karte
        _newBtn.Bounds = new RectF(_leftCard.Right - pad - 74, _leftCard.Y + 11, 74, ch);
        float botY = _leftCard.Bottom - pad;
        float halfL = MathF.Floor((_leftCard.W - 2 * pad - 6) / 2f);
        _renBtn.Bounds = new RectF(_leftCard.X + pad, botY - ch, halfL, ch);
        _delBtn.Bounds = new RectF(_renBtn.Bounds.Right + 6, botY - ch, _leftCard.W - 2 * pad - halfL - 6, ch);
        botY -= ch + 6;
        _updBtn.Bounds = new RectF(_leftCard.X + pad, botY - ch, halfL + 10, ch);
        _dupBtn.Bounds = new RectF(_updBtn.Bounds.Right + 6, botY - ch, _leftCard.W - 2 * pad - (halfL + 10) - 6, ch);
        botY -= ch + 10;

        bool hasSel = SelectedSummary != null;
        _updBtn.Enabled = _dupBtn.Enabled = _renBtn.Enabled = _delBtn.Enabled = _recallBtn.Enabled = hasSel && _scope != SnapshotScopeFlags.None;

        _snapListRect = RectF.FromLTRB(_leftCard.X + pad, _leftCard.Y + 48, _leftCard.Right - pad, Math.Max(_leftCard.Y + 64, botY));
        _snapScroll.Update(_snapListRect, _m.Snapshots.Count * (SnapRowH + 5));

        // Rechte Karte
        float ry1 = _rightCard.Y + 11;
        _recallBtn.Bounds = new RectF(_rightCard.Right - pad - 156, ry1, 156, ch);

        float ry2 = ry1 + ch + 8;
        int presetIdx = Array.FindIndex(s_fadePresets, p => Math.Abs(p - _fadeMs) < 1.0);
        if (presetIdx >= 0) _fadeSeg.SetSelected(presetIdx);
        _fadeBox.Text = _fadeMs >= 1000 ? $"{_fadeMs / 1000.0:0.##} s" : $"{_fadeMs:0} ms";
        _fadeBox.Bounds = new RectF(_rightCard.Right - pad - 72, ry2, 72, ch);
        _fadeSeg.Bounds = new RectF(_rightCard.X + pad + 52, ry2, Math.Max(100, _fadeBox.Bounds.X - (_rightCard.X + pad + 52) - 8), ch);

        float ry3 = ry2 + ch + 8;
        _viewSeg.SetSelected(_rightMode);
        float viewW = Math.Clamp(_rightCard.W * 0.42f, 168f, 236f);
        _viewSeg.Bounds = new RectF(_rightCard.Right - pad - viewW, ry3, viewW, ch);
        _scopeDd.Bounds = new RectF(_rightCard.X + pad, ry3, Math.Max(100, _viewSeg.Bounds.X - (_rightCard.X + pad) - 8), ch);

        _infoBarRect = new RectF(_rightCard.X + pad, ry3 + ch + 8, _rightCard.W - 2 * pad, 22);
        _tableRect = RectF.FromLTRB(_rightCard.X + pad, _infoBarRect.Bottom + 4, _rightCard.Right - pad, _rightCard.Bottom - pad);

        int rowCount = _rightMode == 0 ? (_diff?.Changes.Count ?? 0) : _safeRows.Count;
        _tableScroll.Update(_tableRect, rowCount * (TableRowH + 4));
    }

    public override bool AcceptsMouse => true;

    RectF SnapRowRect(int i) =>
        new(_snapListRect.X, _snapListRect.Y + i * (SnapRowH + 5) - _snapScroll.Offset,
            _snapListRect.W - (_snapScroll.Needed ? 12 : 0), SnapRowH);

    RectF TableRowRect(int i) =>
        new(_tableRect.X, _tableRect.Y + i * (TableRowH + 4) - _tableScroll.Offset,
            _tableRect.W - (_tableScroll.Needed ? 12 : 0), TableRowH);

    static RectF SafePillRect(RectF row, int colIdx)
    {
        // 5 Pillen rechtsbündig: Fader, Mute, Routing, DSP, Alle
        float w = colIdx == 4 ? 46f : 52f;
        float right = row.Right - 6;
        for (int c = 4; c > colIdx; c--)
            right -= (c == 4 ? 46f : 52f) + 4f;
        return new RectF(right - w, row.CY - 12, w, 24);
    }

    public override void OnMouseMove(in MouseEvent e)
    {
        if (_snapScroll.Dragging) { _snapScroll.Drag(e.Y); Invalidate(); return; }
        if (_tableScroll.Dragging) { _tableScroll.Drag(e.Y); Invalidate(); return; }

        int hs = -1;
        if (_snapListRect.Contains(e.X, e.Y) && !(_snapScroll.Needed && e.X >= _snapListRect.Right - 12))
        {
            int idx = (int)((e.Y - _snapListRect.Y + _snapScroll.Offset) / (SnapRowH + 5));
            if (idx >= 0 && idx < _m.Snapshots.Count && SnapRowRect(idx).Contains(e.X, e.Y)) hs = idx;
        }

        int hsr = -1, hsc = -1;
        if (_rightMode == 1 && _tableRect.Contains(e.X, e.Y) && !(_tableScroll.Needed && e.X >= _tableRect.Right - 12))
        {
            int idx = (int)((e.Y - _tableRect.Y + _tableScroll.Offset) / (TableRowH + 4));
            if (idx >= 0 && idx < _safeRows.Count && TableRowRect(idx).Contains(e.X, e.Y))
            {
                hsr = idx;
                var rr = TableRowRect(idx);
                for (int c = 0; c < 5; c++)
                    if (SafePillRect(rr, c).Contains(e.X, e.Y)) { hsc = c; break; }
            }
        }

        if (hs != _hotSnap || hsr != _hotSafeRow || hsc != _hotSafeCol)
        {
            _hotSnap = hs; _hotSafeRow = hsr; _hotSafeCol = hsc;
            Invalidate();
        }
    }

    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button != 0) return;
        if (_snapScroll.Down(e.X, e.Y) || _tableScroll.Down(e.X, e.Y)) { Invalidate(); return; }

        if (_hotSnap >= 0 && _hotSnap < _m.Snapshots.Count)
        {
            var picked = _m.Snapshots[_hotSnap];
            _selectedId = picked.Id;
            _fadeMs = picked.DefaultFadeMs;
            if (e.Clicks >= 2)
            {
                _m.RecallSnapshot(picked.Id, _scope, _fadeMs);
                Root?.Notify.Flash($"Snapshot „{picked.Name}“ abgerufen.");
            }
            else
            {
                RefreshPreview();
                Layout();
                Invalidate();
            }
            return;
        }

        if (_rightMode == 1 && _hotSafeRow >= 0 && _hotSafeRow < _safeRows.Count && _hotSafeCol >= 0)
        {
            RecallSafeFlags flag = _hotSafeCol switch
            {
                0 => RecallSafeFlags.Fader,
                1 => RecallSafeFlags.Mute,
                2 => RecallSafeFlags.Routing,
                3 => RecallSafeFlags.Dsp,
                _ => RecallSafeFlags.All,
            };
            ToggleSafeFlag(_safeRows[_hotSafeRow], flag);
        }
    }

    public override void OnMouseUp(in MouseEvent e)
    {
        _snapScroll.Up();
        _tableScroll.Up();
    }

    public override bool OnMouseWheel(in MouseEvent e)
    {
        if (_snapListRect.Contains(e.X, e.Y) && _snapScroll.Wheel(e.Wheel)) { Invalidate(); return true; }
        if (_tableRect.Contains(e.X, e.Y) && _tableScroll.Wheel(e.Wheel)) { Invalidate(); return true; }
        return false;
    }

    static Color4 ParseHex(string hex)
    {
        if (!string.IsNullOrWhiteSpace(hex) && hex.StartsWith('#') && hex.Length == 7 &&
            uint.TryParse(hex[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint rgb))
            return Theme.Rgb(rgb);
        return Theme.Accent;
    }

    public override void Paint(Renderer g)
    {
        float pad = Theme.CardPad;

        // ---- Linke Karte: Snapshot-Liste ----
        Draw.Card(g, _leftCard);
        Draw.CardTitle(g, new RectF(_leftCard.X + pad, _leftCard.Y + 11, Math.Max(40, _newBtn.Bounds.X - (_leftCard.X + pad) - 6), Theme.ControlHSmall),
            "Szenen", _m.Snapshots.Count);

        g.PushClip(_snapListRect);
        if (_m.Snapshots.Count == 0)
        {
            Draw.Empty(g, _snapListRect, _snapListRect.Y + 12, Icon.Snapshots,
                "Noch keine Szene",
                "Speichere deinen aktuellen Live-Mix mit „+ Neu“ oder Strg+S als abrufbaren Snapshot.");
        }
        else
        {
            for (int i = 0; i < _m.Snapshots.Count; i++)
            {
                var r = SnapRowRect(i);
                if (r.Bottom < _snapListRect.Y || r.Y > _snapListRect.Bottom) continue;
                var s = _m.Snapshots[i];
                bool sel = s.Id == _selectedId;
                bool active = s.Id == _m.ActiveSnapshotId;
                var accent = ParseHex(s.ColorHex);

                g.FillRound(r, 9, sel ? Theme.AccentSoft : (i == _hotSnap ? Theme.ControlHover : Theme.Well));
                g.StrokeRound(r, 9, sel ? Theme.Accent : Theme.Border);
                g.FillRound(new RectF(r.X + 4, r.Y + 7, 4, r.H - 14), 2, accent);

                float rightUsed = 8f;
                if (active)
                    rightUsed += Draw.Chip(g, r.Right - 8, r.CY, "LIVE", Theme.Green, dot: true, Theme.MicroStrong, alignRight: true) + 6f;

                g.Text(s.Name, new RectF(r.X + 14, r.Y + 5, Math.Max(30, r.W - 22 - rightUsed), 19), Theme.BodyMedium, Theme.Text);
                g.Text($"Blende {s.DefaultFadeMs:0} ms  ·  {s.UpdatedUtc.ToLocalTime():dd.MM. HH:mm}",
                    new RectF(r.X + 14, r.Bottom - 19, Math.Max(30, r.W - 22 - rightUsed), 15), Theme.Caption, Theme.TextSecondary);
            }
        }
        g.PopClip();
        _snapScroll.Paint(g);

        // ---- Rechte Karte: Selektiver Recall, Diff & Recall-Safe ----
        Draw.Card(g, _rightCard);
        Draw.CardTitle(g, new RectF(_rightCard.X + pad, _rightCard.Y + 11, Math.Max(60, _recallBtn.Bounds.X - (_rightCard.X + pad) - 8), Theme.ControlHSmall),
            "Selektiver Recall");
        g.Text("Blende", new RectF(_rightCard.X + pad, _fadeSeg.Bounds.Y, 48, _fadeSeg.Bounds.H), Theme.CaptionStrong, Theme.TextSecondary);

        if (_rightMode == 0)
            PaintDiffView(g);
        else
            PaintSafeMatrix(g);

        PaintChildren(g);
    }

    void PaintDiffView(Renderer g)
    {
        if (SelectedSummary is not { } s)
        {
            g.Text("Wähle links einen Snapshot aus, um alle Parameter-Änderungen vor dem Abruf zu prüfen.", _infoBarRect, Theme.Caption, Theme.TextSecondary);
            g.PushClip(_tableRect);
            Draw.Empty(g, _tableRect, _tableRect.Y + 14, Icon.Snapshots, "Kein Snapshot ausgewählt",
                "Die Diff-Vorschau zeigt vor jedem Recall transparent, welche Fader, Routings oder Filter sich ändern.");
            g.PopClip();
            return;
        }

        int changes = _diff?.Changes.Count ?? 0;
        int safeCount = _diff?.SafeSkippedCount ?? 0;
        g.Text($"Vorschau für „{s.Name}“: {changes} Abweichung(en) zum Live-Mix" +
               (safeCount > 0 ? $"  ·  {safeCount} durch Recall-Safe geschützt" : "  ·  SystemConfig bleibt unberührt"),
               _infoBarRect, Theme.CaptionStrong, safeCount > 0 ? Theme.Orange : Theme.TextSecondary);

        g.PushClip(_tableRect);
        if (changes == 0)
        {
            Draw.Empty(g, _tableRect, _tableRect.Y + 14, Icon.Check,
                "Live-Mix entspricht dem Snapshot",
                "Im gewählten Recall-Umfang gibt es derzeit keine Abweichungen zwischen Live-Mix und diesem Snapshot.",
                Theme.Green);
        }
        else if (_diff != null)
        {
            for (int i = 0; i < _diff.Changes.Count; i++)
            {
                var r = TableRowRect(i);
                if (r.Bottom < _tableRect.Y || r.Y > _tableRect.Bottom) continue;
                var item = _diff.Changes[i];

                g.FillRound(r, 7, item.BlockedByRecallSafe ? Theme.Soft(Theme.Orange) : Theme.Well);
                float x = r.X + 8;
                float catW = Draw.Chip(g, x, r.CY, item.Category, item.BlockedByRecallSafe ? Theme.Orange : Theme.AccentText, dot: false, Theme.MicroStrong);
                x += catW + 8;

                float rightLimit = r.Right - 8;
                if (item.BlockedByRecallSafe)
                {
                    float sw = Draw.Chip(g, rightLimit, r.CY, "RECALL-SAFE", Theme.Orange, dot: true, Theme.MicroStrong, alignRight: true);
                    rightLimit -= sw + 8;
                }

                string valText = $"{item.CurrentValue} → {item.TargetValue}";
                float valW = Math.Min(190f, Renderer.MeasureWidth(valText, Theme.Value) + 12f);
                g.Text(valText, new RectF(rightLimit - valW, r.Y, valW, r.H), Theme.Value,
                    item.BlockedByRecallSafe ? Theme.TextTertiary : Theme.Text, HAlign.Right);

                float midW = Math.Max(30f, (rightLimit - valW - 8) - x);
                g.Text($"{item.ObjectName} · {item.Parameter}", new RectF(x, r.Y, midW, r.H), Theme.BodyMedium,
                    item.BlockedByRecallSafe ? Theme.TextSecondary : Theme.Text);
            }
        }
        g.PopClip();
        _tableScroll.Paint(g);
    }

    void PaintSafeMatrix(Renderer g)
    {
        g.Text("Geschützte Parameter (Recall-Safe) werden beim Abruf eines Snapshots niemals überschrieben:",
            _infoBarRect, Theme.CaptionStrong, Theme.TextSecondary);

        g.PushClip(_tableRect);
        string[] labels = { "Fader", "Mute", "Route", "DSP", "Alle" };
        RecallSafeFlags[] masks = { RecallSafeFlags.Fader, RecallSafeFlags.Mute, RecallSafeFlags.Routing, RecallSafeFlags.Dsp, RecallSafeFlags.All };

        for (int i = 0; i < _safeRows.Count; i++)
        {
            var r = TableRowRect(i);
            if (r.Bottom < _tableRect.Y || r.Y > _tableRect.Bottom) continue;
            var row = _safeRows[i];

            g.FillRound(r, 7, i == _hotSafeRow ? Theme.ControlHover : Theme.Well);
            float bw = Draw.Chip(g, r.X + 8, r.CY, row.Badge, row.Color, dot: false, Theme.MicroStrong);
            var firstPill = SafePillRect(r, 0);
            g.Text(row.Name, new RectF(r.X + 14 + bw, r.Y, Math.Max(30, firstPill.X - (r.X + 18 + bw)), r.H), Theme.BodyMedium, Theme.Text);

            for (int c = 0; c < 5; c++)
            {
                var pr = SafePillRect(r, c);
                bool on = c == 4 ? row.Flags == RecallSafeFlags.All : (row.Flags & masks[c]) != 0;
                bool hot = i == _hotSafeRow && c == _hotSafeCol;
                Draw.ButtonLook(g, pr, labels[c], on ? ButtonStyle.Filled : ButtonStyle.Gray, true, hot, font: Theme.MicroStrong);
            }
        }
        g.PopClip();
        _tableScroll.Paint(g);
    }
}
