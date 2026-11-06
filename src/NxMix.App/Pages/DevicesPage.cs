using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Audio;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Liste der Audio-Geraete (aus IDeviceCatalog) mit Schnell-Zuordnung.</summary>
internal sealed class DeviceListView : Widget
{
    readonly AppModel _m;
    public AudioBackend Backend { get; set; } = AudioBackend.Windows;
    readonly VScroll _sc = new();
    readonly IconButton _refresh = new(Icon.Refresh, "Geräteliste neu einlesen") { IconSize = 17 };
    public readonly Segmented Filter = new("Alle", "Eingabe", "Ausgabe");
    (int Row, int Act) _hot = (-1, -1), _press = (-1, -1);
    static float RowH => Theme.Touch ? 70f : 60f;
    float HeadH => 52 + Theme.ControlHSmall + 14;

    public DeviceListView(AppModel m)
    {
        _m = m;
        Add(_refresh); Add(Filter);
        _refresh.Clicked += _ => _m.RefreshDevices();
        Filter.Changed += _ => { Layout(); Invalidate(); };
    }

    public override bool AcceptsMouse => true;

    List<AudioDeviceInfo> Filtered() =>
        _m.Devices.Where(d => d.Backend == Backend && (Filter.Selected == 0 || (Filter.Selected == 1 ? d.Flow == DeviceFlow.Capture : d.Flow == DeviceFlow.Render))).ToList();

    public override void Layout()
    {
        var b = Bounds;
        _refresh.Bounds = new RectF(b.Right - 12 - 34, b.Y + 10, 34, 34);
        Filter.Bounds = new RectF(b.X + Theme.CardPad, b.Y + 52, Math.Min(300, b.W - 2 * Theme.CardPad), Theme.ControlHSmall);
        _sc.Update(new RectF(b.X + 8, b.Y + HeadH, b.W - 16, Math.Max(0, b.H - HeadH - 8)), Filtered().Count * RowH);
    }

    RectF RowRect(int row) => new(_sc.View.X, _sc.View.Y + row * RowH - _sc.Offset, _sc.View.W - (_sc.Needed ? 10 : 0), RowH);

    // Aktionen je Zeile: 0 = Eingang/Ausgang zuordnen, 1 = Loopback
    static List<(int Act, RectF Rect, string Text)> Actions(RectF row, AudioDeviceInfo d)
    {
        var list = new List<(int, RectF, string)>();
        float x = row.Right - 10, h = Theme.ControlHSmall;
        if (d.Flow == DeviceFlow.Render && d.Backend == AudioBackend.Windows)
        {
            float w = Renderer.MeasureWidth("Loopback", Theme.CalloutMedium) + 24;
            x -= w; list.Add((1, new RectF(x, row.CY - h / 2, w, h), "Loopback")); x -= 6;
        }
        string t = d.Flow == DeviceFlow.Capture ? "Eingang" : "Ausgang";
        float w2 = Renderer.MeasureWidth(t, Theme.CalloutMedium) + 40;
        x -= w2; list.Add((0, new RectF(x, row.CY - h / 2, w2, h), t));
        return list;
    }

    (int Row, int Act) HitRow(float x, float y)
    {
        if (!_sc.View.Contains(x, y)) return (-1, -1);
        var list = Filtered();
        int row = (int)((y - _sc.View.Y + _sc.Offset) / RowH);
        if (row < 0 || row >= list.Count) return (-1, -1);
        var rr = RowRect(row);
        foreach (var a in Actions(rr, list[row])) if (a.Rect.Contains(x, y)) return (row, a.Act);
        return (row, -1);
    }

    public override void OnMouseMove(in MouseEvent e)
    {
        if (_sc.Dragging && IsPressed) { _sc.Drag(e.Y); Invalidate(); return; }
        var h = HitRow(e.X, e.Y);
        if (h != _hot) { _hot = h; Invalidate(); }
    }
    public override void OnMouseLeave() { _hot = (-1, -1); Invalidate(); }
    public override CursorKind CursorAt(float x, float y) => HitRow(x, y).Act >= 0 && !_m.HostRunning ? CursorKind.Hand : CursorKind.Arrow;

    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button != 0) return;
        if (_sc.Down(e.X, e.Y)) return;
        _press = HitRow(e.X, e.Y);
        Invalidate();
    }

    public override void OnMouseUp(in MouseEvent e)
    {
        if (_sc.Dragging) { _sc.Up(); Invalidate(); return; }
        var h = HitRow(e.X, e.Y);
        if (h.Act >= 0 && h == _press && !_m.HostRunning) Assign(Filtered()[h.Row], h.Act == 1);
        _press = (-1, -1);
        Invalidate();
    }

    public override bool OnMouseWheel(in MouseEvent e) { if (_sc.Wheel(e.Wheel)) { Invalidate(); return true; } return false; }

    void Assign(AudioDeviceInfo d, bool loopback)
    {
        if (d.Channels <= 0) { Root?.Notify.Flash(d.UnavailableReason ?? "Der Treiber konnte nicht geöffnet werden.", true); return; }
        var flow = d.Flow == DeviceFlow.Render && !loopback ? DeviceFlow.Render : DeviceFlow.Capture;
        if (_m.Bindings.Any(b => b.DeviceId == d.Id && b.Flow == flow && b.Loopback == loopback))
        {
            if (Root is { } r) DeviceSettingsSheet.Show(r, _m, d.Id);
            return;
        }
        int count = Math.Clamp(d.Channels, 1, 64);
        int devStart = 0;
        var refusal = _m.AddBinding(new DeviceBinding(d.Id, flow, devStart, _m.NextFreeEngineChannel(flow), count, loopback));
        if (refusal != null) Root?.Notify.Flash(refusal, true);
        else Root?.Notify.Flash($"Zuordnung angelegt: {d.Name}");
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        Draw.Card(g, b);
        Draw.CardTitle(g, new RectF(b.X + Theme.CardPad, b.Y + 2, 200, 52), "Geräte", _m.Devices.Count > 0 ? _m.Devices.Count : null);

        var list = Filtered();
        if (list.Count == 0)
        {
            string head, body;
            if (_m.DevicesLoading) { head = "Lese Geräte ein …"; body = ""; }
            else if (_m.DeviceError != null) { head = "Geräteliste nicht verfügbar"; body = _m.DeviceError; }
            else if (_m.Devices.Count > 0) { head = "Keine Geräte in dieser Ansicht"; body = "Wähle oben „Alle“."; }
            else { head = "Keine Geräte gefunden"; body = "Mit dem Aktualisieren-Knopf oben rechts erneut einlesen."; }
            var area = RectF.FromLTRB(b.X, b.Y + HeadH, b.Right, b.Bottom);
            Draw.Empty(g, area, area.Y + 20, Icon.Devices, head, body);
        }
        else
        {
            g.PushClip(_sc.View);
            for (int i = 0; i < list.Count; i++)
            {
                var r = RowRect(i);
                if (r.Bottom < _sc.View.Y || r.Y > _sc.View.Bottom) continue;
                PaintRow(g, r, list[i], i, i == list.Count - 1);
            }
            g.PopClip();
            _sc.Paint(g);
        }
        PaintChildren(g);
    }

    void PaintRow(Renderer g, RectF r, AudioDeviceInfo d, int idx, bool last)
    {
        bool rowHot = _hot.Row == idx;
        if (rowHot) g.FillRound(r.Deflate(0, 2), 10, Theme.ControlHover.With(Theme.IsDark ? 0.5f : 0.7f));
        else if (!last) g.Fill(new RectF(r.X + 58, r.Bottom - 0.5f, r.W - 66, 1), Theme.Separator);
        bool cap = d.Flow == DeviceFlow.Capture;
        var col = cap ? Theme.AccentText : Theme.Teal;
        var ic = new RectF(r.X + 10, r.CY - 18, 36, 36);
        g.FillRound(ic, 10, Theme.Soft(col));
        Icons.Draw(g, cap ? Icon.Mic : Icon.Speaker, ic.CX, ic.CY, 19, col);
        var actions = Actions(r, d);
        float textRight = actions.Min(a => a.Rect.X) - 10;
        float tw = Math.Max(10, textRight - r.X - 58);
        g.Text(d.Name, new RectF(r.X + 58, r.CY - 18, tw, 18), Theme.BodyMedium, Theme.Text, HAlign.Left, VAlign.Middle, true);
        string sub = $"{(cap ? "Aufnahme" : "Wiedergabe")}  ·  {d.Channels} {(d.Channels == 1 ? "Kanal" : "Kanäle")}  ·  {d.SampleRate / 1000.0:0.#} kHz" + (d.IsDefault ? "  ·  Standard" : "");
        g.Text(sub, new RectF(r.X + 58, r.CY + 1, tw, 17), Theme.Caption, Theme.TextTertiary, HAlign.Left, VAlign.Middle, true);
        foreach (var (act, rect, text) in actions)
        {
            bool hot = _hot.Row == idx && _hot.Act == act && !_m.HostRunning;
            bool down = hot && _press == (idx, act);
            Draw.ButtonLook(g, rect, text, act == 0 ? ButtonStyle.Tinted : ButtonStyle.Gray, !_m.HostRunning, hot, down, act == 0 ? Icon.Plus : Icon.None);
        }
    }

    public override string? TooltipAt(float x, float y)
    {
        var h = HitRow(x, y);
        if (h.Act < 0) return null;
        if (_m.HostRunning) return "Zum Ändern zuerst Audio stoppen";
        return h.Act == 1 ? "Nimmt auf, was dieses Wiedergabegerät abspielt (nur lesend)" : "Legt eine Zuordnung zu den nächsten freien Engine-Kanälen an";
    }
}

/// <summary>Tabelle der DeviceBindings: Richtung, Geraet, Geraetekanal-Start, Engine-Kanal-Start, Anzahl.</summary>
internal sealed class BindingTableView : Widget
{
    enum Reg { None, Chip, Device, DevMinus, DevVal, DevPlus, EngMinus, EngVal, EngPlus, CntMinus, CntVal, CntPlus, Status, Delete }

    readonly AppModel _m;
    readonly VScroll _sc = new();
    public readonly Button AddButton = new("Zuordnung", ButtonStyle.Tinted, Icon.Plus) { Font = Theme.CalloutMedium };
    (int Row, Reg R) _hot = (-1, Reg.None), _press = (-1, Reg.None);
    static float RowH => Theme.Touch ? 62f : 52f;
    const float HeadH = 74f;
    const float ColH = 26f;

    public BindingTableView(AppModel m)
    {
        _m = m;
        Add(AddButton);
        AddButton.Clicked += AddEmpty;
    }

    public override bool AcceptsMouse => true;

    void AddEmpty()
    {
        if (_m.HostRunning) return;
        var flow = DeviceFlow.Capture;
        var dev = _m.Devices.FirstOrDefault(d => d.Flow == DeviceFlow.Capture);
        var refusal = _m.AddBinding(new DeviceBinding(dev?.Id ?? "", flow, 0, _m.NextFreeEngineChannel(flow), 1));
        if (refusal != null) Root?.Notify.Flash(refusal, true);
    }

    /// <summary>Aendert eine Zuordnung; lehnt das Modell ab (z. B. Konflikt mit einem App-Link), erscheint der Grund als Meldung.</summary>
    void Upd(int row, Func<DeviceBinding, DeviceBinding> f)
    {
        var refusal = _m.UpdateBinding(row, f);
        if (refusal != null) Root?.Notify.Flash(refusal, true);
    }

    public override void Layout()
    {
        var b = Bounds;
        float bw = AddButton.PreferredWidth;
        AddButton.Bounds = new RectF(b.Right - Theme.CardPad - bw + 4, b.Y + 14, bw, Theme.ControlHSmall);
        _sc.Update(new RectF(b.X + 8, b.Y + HeadH + ColH, b.W - 16, Math.Max(0, b.H - HeadH - ColH - 8)), _m.Bindings.Count * RowH);
        AddButton.Enabled = !_m.HostRunning;
    }

    RectF RowRect(int row) => new(_sc.View.X, _sc.View.Y + row * RowH - _sc.Offset, _sc.View.W - (_sc.Needed ? 10 : 0), RowH);

    static float StepW => Theme.Touch ? 116f : 96f;

    (RectF Chip, RectF Dev, RectF DevStep, RectF EngStep, RectF CntStep, RectF Status, RectF Del) Cols(RectF row)
    {
        float cy = row.CY, h = Theme.ControlHSmall + 2;
        float x = row.X + 8;
        var chip = new RectF(x, cy - h / 2, 120, h); x += 128;
        float right = row.Right - 8;
        var del = new RectF(right - 32, cy - 16, 32, 32); right -= 36;
        var status = new RectF(right - 28, cy - 14, 28, 28); right -= 34;
        var cnt = new RectF(right - StepW, cy - h / 2, StepW, h); right -= StepW + 8;
        var eng = new RectF(right - StepW, cy - h / 2, StepW, h); right -= StepW + 8;
        var dev = new RectF(right - StepW, cy - h / 2, StepW, h); right -= StepW + 10;
        var device = new RectF(x, cy - h / 2, Math.Max(80, right - x), h);
        return (chip, device, dev, eng, cnt, status, del);
    }

    static (RectF Minus, RectF Val, RectF Plus) Step(RectF s) => (new RectF(s.X, s.Y, 28, s.H), new RectF(s.X + 28, s.Y, s.W - 56, s.H), new RectF(s.Right - 28, s.Y, 28, s.H));

    (int Row, Reg R) Hit(float x, float y)
    {
        if (!_sc.View.Contains(x, y)) return (-1, Reg.None);
        int row = (int)((y - _sc.View.Y + _sc.Offset) / RowH);
        if (row < 0 || row >= _m.Bindings.Count) return (-1, Reg.None);
        var c = Cols(RowRect(row));
        if (c.Chip.Contains(x, y)) return (row, Reg.Chip);
        if (c.Dev.Contains(x, y)) return (row, Reg.Device);
        if (c.Del.Contains(x, y)) return (row, Reg.Delete);
        if (c.Status.Contains(x, y)) return (row, Reg.Status);
        foreach (var (s, m, v, p) in new[] { (c.DevStep, Reg.DevMinus, Reg.DevVal, Reg.DevPlus), (c.EngStep, Reg.EngMinus, Reg.EngVal, Reg.EngPlus), (c.CntStep, Reg.CntMinus, Reg.CntVal, Reg.CntPlus) })
        {
            var st = Step(s);
            if (st.Minus.Contains(x, y)) return (row, m);
            if (st.Val.Contains(x, y)) return (row, v);
            if (st.Plus.Contains(x, y)) return (row, p);
        }
        return (row, Reg.None);
    }

    public override void OnMouseMove(in MouseEvent e)
    {
        if (_sc.Dragging && IsPressed) { _sc.Drag(e.Y); Invalidate(); return; }
        var h = Hit(e.X, e.Y);
        if (h != _hot) { _hot = h; Invalidate(); }
    }
    public override void OnMouseLeave() { _hot = (-1, Reg.None); Invalidate(); }
    public override CursorKind CursorAt(float x, float y) => Hit(x, y).R is not Reg.None and not Reg.Status && !_m.HostRunning ? CursorKind.Hand : CursorKind.Arrow;

    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button != 0) return;
        if (_sc.Down(e.X, e.Y)) return;
        _press = Hit(e.X, e.Y);
        if (e.Clicks >= 2 && !_m.HostRunning && _press.Row >= 0) EditValue(_press);
        Invalidate();
    }

    public override void OnMouseUp(in MouseEvent e)
    {
        if (_sc.Dragging) { _sc.Up(); Invalidate(); return; }
        var h = Hit(e.X, e.Y);
        if (h.Row >= 0 && h == _press && !_m.HostRunning && e.Clicks == 1) Act(h);
        _press = (-1, Reg.None);
        Invalidate();
    }

    public override bool OnMouseWheel(in MouseEvent e)
    {
        var h = Hit(e.X, e.Y);
        if (h.Row >= 0 && !_m.HostRunning && h.R is Reg.DevVal or Reg.EngVal or Reg.CntVal or Reg.DevMinus or Reg.DevPlus or Reg.EngMinus or Reg.EngPlus or Reg.CntMinus or Reg.CntPlus)
        {
            Nudge(h.Row, h.R, e.Wheel > 0 ? 1 : -1);
            return true;
        }
        if (_sc.Wheel(e.Wheel)) { Invalidate(); return true; }
        return false;
    }

    void Nudge(int row, Reg r, int d)
    {
        switch (r)
        {
            case Reg.DevMinus: case Reg.DevPlus: case Reg.DevVal: SetDevStart(row, _m.Bindings[row].DeviceFirstChannel + d); break;
            case Reg.EngMinus: case Reg.EngPlus: case Reg.EngVal: SetEngStart(row, _m.Bindings[row].EngineFirstChannel + d); break;
            case Reg.CntMinus: case Reg.CntPlus: case Reg.CntVal: SetCount(row, _m.Bindings[row].Count + d); break;
        }
    }

    void Act((int Row, Reg R) h)
    {
        int row = h.Row;
        switch (h.R)
        {
            case Reg.DevMinus: case Reg.EngMinus: case Reg.CntMinus: Nudge(row, h.R, -1); break;
            case Reg.DevPlus: case Reg.EngPlus: case Reg.CntPlus: Nudge(row, h.R, +1); break;
            case Reg.Delete: _m.RemoveBinding(row); break;
            case Reg.Device: OpenDeviceMenu(row, Cols(RowRect(row)).Dev); break;
            case Reg.Chip: OpenChipMenu(row, Cols(RowRect(row)).Chip); break;
        }
    }

    void SetDevStart(int row, int v)
    {
        var b = _m.Bindings[row];
        var d = _m.FindDevice(b.DeviceId);
        int max = d != null ? Math.Max(0, d.Channels - b.Count) : 63;
        _m.UpdateBinding(row, x => x with { DeviceFirstChannel = Math.Clamp(v, 0, max) });
    }

    void SetEngStart(int row, int v)
    {
        var b = _m.Bindings[row];
        int engCount = b.Flow == DeviceFlow.Capture ? _m.InputCount : _m.OutputCount;
        int max = Math.Max(0, Math.Max(engCount, b.EngineFirstChannel + b.Count) - b.Count);
        Upd(row, x => x with { EngineFirstChannel = Math.Clamp(v, 0, max) });
        GrowToFit(row);
    }

    void SetCount(int row, int v)
    {
        var b = _m.Bindings[row];
        var d = _m.FindDevice(b.DeviceId);
        int max = d != null ? Math.Max(1, d.Channels - b.DeviceFirstChannel) : 64;
        Upd(row, x => x with { Count = Math.Clamp(v, 1, max) });
        GrowToFit(row);
    }

    /// <summary>Fehlen Engine-Kanaele fuer die Zuordnung, werden sie (ohne Namen) am Ende angelegt.</summary>
    void GrowToFit(int row)
    {
        if (row >= _m.Bindings.Count) return;
        var b = _m.Bindings[row];
        bool input = b.Flow == DeviceFlow.Capture;
        int have = input ? _m.InputCount : _m.OutputCount;
        int need = b.EngineFirstChannel + b.Count;
        if (need > have) _m.AddChannels(input, need - have);
    }

    void EditValue((int Row, Reg R) h)
    {
        var root = Root; if (root == null) return;
        var c = Cols(RowRect(h.Row));
        var b = _m.Bindings[h.Row];
        (RectF s, int cur, Action<int> set)? target = h.R switch
        {
            Reg.DevVal => (c.DevStep, b.DeviceFirstChannel, v => SetDevStart(h.Row, v)),
            Reg.EngVal => (c.EngStep, b.EngineFirstChannel, v => SetEngStart(h.Row, v)),
            Reg.CntVal => (c.CntStep, b.Count - 1, v => SetCount(h.Row, v + 1)),
            _ => null,
        };
        if (target == null) return;
        var (s, cur, set) = target.Value;
        var vr = Step(s).Val;
        root.BeginEdit(vr.Inflate(4, 2), (cur + 1).ToString(), Theme.Value, txt =>
        {
            if (int.TryParse(txt.Trim(), out int n)) set(n - 1);
            else root.Notify.Flash("Ungültige Zahl: " + txt, true);
        }, 3, ch => char.IsDigit(ch), HAlign.Center);
    }

    void OpenDeviceMenu(int row, RectF anchor)
    {
        var root = Root; if (root == null) return;
        var b = _m.Bindings[row];
        var wantFlow = b.Loopback ? DeviceFlow.Render : b.Flow;
        var items = new List<MenuItem>();
        foreach (var d in _m.Devices.Where(d => d.Flow == wantFlow))
        {
            var dd = d;
            items.Add(new($"{d.Name}  ({d.Channels} Kanäle)", null, () => _m.UpdateBinding(row, x => x with
            {
                DeviceId = dd.Id,
                DeviceFirstChannel = Math.Min(x.DeviceFirstChannel, Math.Max(0, dd.Channels - x.Count)),
            })) { Checked = d.Id == b.DeviceId });
        }
        if (items.Count == 0) items.Add(new("Keine passenden Geräte gefunden", null, null, false));
        items.Add(MenuItem.Sep);
        items.Add(new("Geräteliste neu einlesen", null, _m.RefreshDevices) { Icon = Icon.Refresh });
        PopupMenu.Show(root, anchor, items, Math.Max(anchor.W, 300));
    }

    void OpenChipMenu(int row, RectF anchor)
    {
        var root = Root; if (root == null) return;
        var b = _m.Bindings[row];
        void Set(DeviceFlow flow, bool loop)
        {
            Upd(row, x =>
            {
                var d = _m.FindDevice(x.DeviceId);
                var want = loop ? DeviceFlow.Render : flow;
                bool keep = d != null && d.Flow == want;
                return x with { Flow = flow, Loopback = loop, DeviceId = keep ? x.DeviceId : "" };
            });
            GrowToFit(row);
        }
        var items = new List<MenuItem>
        {
            new("Eingang (Aufnahmegerät)", null, () => Set(DeviceFlow.Capture, false)) { Checked = b.Flow == DeviceFlow.Capture && !b.Loopback, Icon = Icon.Mic },
            new("Loopback (Wiedergabe abgreifen)", null, () => Set(DeviceFlow.Capture, true)) { Checked = b.Loopback, Icon = Icon.Loopback },
            new("Ausgang (Wiedergabegerät)", null, () => Set(DeviceFlow.Render, false)) { Checked = b.Flow == DeviceFlow.Render, Icon = Icon.Speaker },
        };
        PopupMenu.Show(root, anchor, items, 280);
    }

    public override string? TooltipAt(float x, float y)
    {
        var h = Hit(x, y);
        if (h.Row < 0) return null;
        if (_m.HostRunning && h.R != Reg.None && h.R != Reg.Status) return "Zum Ändern zuerst Audio stoppen";
        return h.R switch
        {
            Reg.Status => _m.ValidateBinding(_m.Bindings[h.Row]) ?? "In Ordnung",
            Reg.Chip => "Richtung ändern",
            Reg.Device => "Gerät wählen",
            Reg.DevVal => "Erster Gerätekanal (Doppelklick: tippen)",
            Reg.EngVal => "Erster Engine-Kanal (Doppelklick: tippen)",
            Reg.CntVal => "Anzahl Kanäle (Doppelklick: tippen)",
            Reg.Delete => "Zuordnung entfernen",
            _ => null,
        };
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        Draw.Card(g, b);
        Draw.CardTitle(g, new RectF(b.X + Theme.CardPad, b.Y + 6, 240, 30), "Zuordnungen", _m.Bindings.Count > 0 ? _m.Bindings.Count : null);
        g.Text("Welche Geräte-Kanäle an welchen Engine-Kanälen hängen", new RectF(b.X + Theme.CardPad, b.Y + 36, b.W - 200, 18), Theme.Callout, Theme.TextSecondary, HAlign.Left, VAlign.Middle, true);

        var hr = new RectF(_sc.View.X, b.Y + HeadH, _sc.View.W - (_sc.Needed ? 10 : 0), ColH);
        var c = Cols(hr);
        void H(string t, RectF r, HAlign a = HAlign.Center) => g.Text(t, new RectF(r.X, hr.Y, r.W, ColH), Theme.Overline, Theme.TextTertiary, a, VAlign.Middle, false);
        H("RICHTUNG", c.Chip.Offset(6, 0), HAlign.Left); H("GERÄT", c.Dev.Offset(4, 0), HAlign.Left); H("GERÄT AB", c.DevStep); H("ENGINE AB", c.EngStep); H("ANZAHL", c.CntStep);
        g.Fill(new RectF(b.X + 1, hr.Bottom - 1, b.W - 2, 1), Theme.Separator);

        if (_m.Bindings.Count == 0)
        {
            var area = RectF.FromLTRB(b.X, b.Y + HeadH + ColH, b.Right, b.Bottom);
            Draw.Empty(g, area, area.Y + 24, Icon.Link, "Noch keine Zuordnung",
                "Wähle links bei einem Gerät „Eingang“, „Ausgang“ oder „Loopback“ – oder lege oben eine leere Zuordnung an. Fehlende Engine-Kanäle werden automatisch angelegt. Audio startet erst, wenn du auf „Audio starten“ klickst.");
        }
        else
        {
            g.PushClip(_sc.View);
            for (int i = 0; i < _m.Bindings.Count; i++)
            {
                var r = RowRect(i);
                if (r.Bottom < _sc.View.Y || r.Y > _sc.View.Bottom) continue;
                PaintRow(g, r, i, i == _m.Bindings.Count - 1);
            }
            g.PopClip();
            _sc.Paint(g);
        }
        PaintChildren(g);
    }

    void PaintStepper(Renderer g, RectF s, int display, int row, Reg minus, Reg plus, bool dis)
    {
        var (mi, va, pl) = Step(s);
        g.FillRound(s, Theme.RadiusControl, dis ? Theme.Control.With(0.5f) : Theme.Control);
        bool hm = _hot.Row == row && _hot.R == minus && !dis, hp = _hot.Row == row && _hot.R == plus && !dis;
        if (hm) g.FillRound(mi.Deflate(2), 6, Theme.ControlPressed);
        if (hp) g.FillRound(pl.Deflate(2), 6, Theme.ControlPressed);
        var ic = dis ? Theme.TextTertiary : Theme.TextSecondary;
        Icons.Draw(g, Icon.Minus, mi.CX, mi.CY, 13, ic);
        Icons.Draw(g, Icon.Plus, pl.CX, pl.CY, 13, ic);
        g.Text(display.ToString(), va, Theme.Value, dis ? Theme.TextTertiary : Theme.Text, HAlign.Center, VAlign.Middle, false);
    }

    void PaintRow(Renderer g, RectF r, int i, bool last)
    {
        var bd = _m.Bindings[i];
        bool dis = _m.HostRunning;
        if (_hot.Row == i) g.FillRound(r.Deflate(0, 2), 10, Theme.ControlHover.With(Theme.IsDark ? 0.35f : 0.5f));
        else if (!last) g.Fill(new RectF(r.X + 8, r.Bottom - 0.5f, r.W - 16, 1), Theme.Separator);
        var c = Cols(r);
        bool loop = bd.Loopback; bool cap = bd.Flow == DeviceFlow.Capture;
        string chip = loop ? "Loopback" : cap ? "Eingang" : "Ausgang";
        var col = loop ? Theme.Purple : cap ? Theme.AccentText : Theme.Teal;
        bool chipHot = _hot.Row == i && _hot.R == Reg.Chip && !dis;
        g.FillPill(c.Chip, col.With((Theme.IsDark ? 0.18f : 0.12f) + (chipHot ? 0.1f : 0)));
        Icons.Draw(g, loop ? Icon.Loopback : cap ? Icon.Mic : Icon.Speaker, c.Chip.X + 16, c.Chip.CY, 14, col);
        g.Text(chip, new RectF(c.Chip.X + 28, c.Chip.Y, c.Chip.W - 44, c.Chip.H), Theme.CalloutStrong, col, HAlign.Left, VAlign.Middle, false);
        Icons.Draw(g, Icon.ChevronDown, c.Chip.Right - 13, c.Chip.CY, 11, col);
        var dev = _m.FindDevice(bd.DeviceId);
        bool devHot = _hot.Row == i && _hot.R == Reg.Device && !dis;
        g.FillRound(c.Dev, Theme.RadiusControl, dis ? Theme.Control.With(0.5f) : devHot ? Theme.ControlHover : Theme.Control);
        string dn = dev?.Name ?? (string.IsNullOrEmpty(bd.DeviceId) ? "Gerät wählen …" : "Gerät nicht gefunden");
        g.Text(dn, new RectF(c.Dev.X + 12, c.Dev.Y, c.Dev.W - 36, c.Dev.H), Theme.Callout, dev == null ? Theme.Orange : dis ? Theme.TextTertiary : Theme.Text, HAlign.Left, VAlign.Middle, true);
        Icons.Draw(g, Icon.UpDown, c.Dev.Right - 15, c.Dev.CY, 13, Theme.TextSecondary);
        PaintStepper(g, c.DevStep, bd.DeviceFirstChannel + 1, i, Reg.DevMinus, Reg.DevPlus, dis);
        PaintStepper(g, c.EngStep, bd.EngineFirstChannel + 1, i, Reg.EngMinus, Reg.EngPlus, dis);
        PaintStepper(g, c.CntStep, bd.Count, i, Reg.CntMinus, Reg.CntPlus, dis);
        var v = _m.ValidateBinding(bd);
        if (v == null)
        {
            g.FillCircle(c.Status.CX, c.Status.CY, 11, Theme.Soft(Theme.Green));
            Icons.Draw(g, Icon.Check, c.Status.CX, c.Status.CY, 14, Theme.Green);
        }
        else
        {
            g.FillCircle(c.Status.CX, c.Status.CY, 11, Theme.Soft(Theme.Orange));
            Icons.Draw(g, Icon.Warn, c.Status.CX, c.Status.CY, 14, Theme.Orange);
        }
        bool hd = _hot.Row == i && _hot.R == Reg.Delete && !dis;
        if (hd) g.FillPill(c.Del, Theme.Soft(Theme.Red));
        Icons.Draw(g, Icon.Trash, c.Del.CX, c.Del.CY, 17, hd ? Theme.Red : dis ? Theme.TextTertiary : Theme.TextSecondary);
    }
}

internal sealed class DevicesPage : Page
{
    readonly AppModel _m;
    readonly DeviceOverview _list;
    readonly BindingTableView _table;
    public readonly Button StartButton = new("Audio starten", ButtonStyle.Filled, Icon.Play) { Font = new TextStyle(14f, Vortice.DirectWrite.FontWeight.SemiBold) };
    readonly Dropdown _rate = new() { Tooltip = "Samplerate der Engine (nur im Stillstand änderbar)" };
    readonly Dropdown _block = new() { Tooltip = "Blockgröße des Audio-Hosts (kleiner = weniger Latenz, mehr CPU)" };
    readonly Switch _excl = new() { Tooltip = "Exklusiv-Modus (WASAPI): geringere Latenz, das Gerät gehört nur NX-MIX" };
    RectF _hostCard, _rRate, _rBlock, _rExcl;

    public override string Title => "Geräte";
    public override Icon Icon => Icon.Devices;
    public override string? Subtitle => $"Audio-Engine, Geräte und Zuordnungen  ·  {Fmt.Count(_m.Bindings.Count, "Zuordnung", "Zuordnungen")}";

    public DevicesPage(AppModel m)
    {
        _m = m;
        _list = Add(new DeviceOverview(m));
        _table = Add(new BindingTableView(m));
        Add(StartButton); Add(_rate); Add(_block); Add(_excl);
        StartButton.Clicked += OnStartClicked;

        _rate.Text = () => $"{_m.Settings.SampleRate / 1000.0:0.##} kHz";
        _rate.Items = () => AppModel.SampleRates.Select(sr => new MenuItem($"{sr / 1000.0:0.##} kHz", null, () => _m.SetSampleRate(sr)) { Checked = sr == _m.Settings.SampleRate }).ToList();
        _block.Text = () => $"{_m.BlockFrames} Samples · {_m.BlockFrames * 1000.0 / _m.Settings.SampleRate:0.0} ms";
        _block.Items = () => AppModel.BlockSizes.Select(f => new MenuItem($"{f} Samples · {f * 1000.0 / _m.Settings.SampleRate:0.0} ms", null, () => _m.SetBlockFrames(f)) { Checked = f == _m.BlockFrames }).ToList();
        _excl.Changed += on => _m.SetExclusive(on);

        m.BindingsChanged += Refresh;
        m.HostChanged += Refresh;
        m.StructureChanged += Refresh;
    }

    void Refresh()
    {
        UpdateEnabled();
        _list.Layout(); _table.Layout();
        Invalidate();
    }

    void UpdateEnabled()
    {
        bool run = _m.HostRunning;
        _rate.Enabled = _block.Enabled = _excl.Enabled = !run && !_m.HostBusy;
        _excl.SetOn(_m.Exclusive);
        if (run)
        {
            StartButton.Text = _m.HostBusy && _m.HostStopping ? "Stoppt …" : "Audio stoppen";
            StartButton.Style = ButtonStyle.Destructive; StartButton.Icon = Icon.Stop; StartButton.Enabled = !_m.HostBusy;
        }
        else if (_m.HostBusy) { StartButton.Text = "Startet …"; StartButton.Style = ButtonStyle.Filled; StartButton.Icon = Icon.Play; StartButton.Enabled = false; }
        else { StartButton.Text = "Audio starten"; StartButton.Style = ButtonStyle.Filled; StartButton.Icon = Icon.Play; StartButton.Enabled = _m.CanStart(out _) || OnlyLinksBlock(); }
    }

    /// <summary>Der Start scheitert NUR an den App-Links (ohne sie ginge er): dann bleibt der Knopf klickbar und erklaert / bietet Alternativen an.</summary>
    bool OnlyLinksBlock() => !_m.CanStart(out _) && _m.HasActiveLinks && _m.CanStartCore(false, out _);

    public override void OnShow()
    {
        if (_m.Devices.Count == 0 && !_m.DevicesLoading && _m.DeviceError == null) _m.RefreshDevices();
        Layout();
        Refresh();
    }

    void OnStartClicked()
    {
        var root = Root; if (root == null) return;
        if (_m.HostRunning) { _m.StopHost(); return; }
        if (!_m.CanStart(out var why))
        {
            if (OnlyLinksBlock()) ShowLinkProblemDialog(why);
            else root.Notify.Flash(why, true);
            return;
        }
        if (_m.SessionConfirmed && _m.LinksConfirmed) { _m.StartHost(); return; }
        ShowStartDialog();
    }

    /// <summary>App-Links werden nie still ignoriert: erklaeren, was fehlt, und anbieten, zu den Einstellungen zu gehen oder die Links auszuschalten.</summary>
    public void ShowLinkProblemDialog(string why)
    {
        var root = Root; if (root == null) return;
        ModalDialog.Show(root, "App-Links können nicht starten",
            why + "\n\nApp-Links werden nie einfach ignoriert. Wähle einen Parkplatz in den Einstellungen – oder schalte die App-Links aus: Die Programme laufen dann ganz normal über Windows, die Links bleiben im Projekt gespeichert.",
            new[] { "Abbrechen", "Einstellungen", "Links ausschalten" }, 1, -1, i =>
            {
                if (i == 1) root.SwitchPage(3);
                else if (i == 2) { _m.SetAppLinksEnabled(false); root.Notify.Flash("App-Links ausgeschaltet – Programme laufen normal."); OnStartClicked(); }
            }, Icon.Warn);
    }

    /// <summary>Vor dem ersten Start je Sitzung: "Lautstaerke und Gain-Sperren geprueft?"</summary>
    public void ShowStartDialog()
    {
        var root = Root; if (root == null) return;
        var locks = new List<string>();
        for (int i = 0; i < _m.OutputCount; i++)
        {
            var c = _m.Settings.Outputs[i];
            if (c.MaxGainDb < Theme.FaderMaxDb - 0.05) locks.Add($"{_m.DisplayName(false, i)}: max. {Db.Format(c.MaxGainDb)} dB");
        }
        string lockText = locks.Count == 0
            ? "Aktuell ist KEINE Gain-Sperre auf einem Ausgang gesetzt."
            : "Gain-Sperren an Ausgängen:\n" + string.Join("\n", locks.Take(8)) + (locks.Count > 8 ? $"\n… und {locks.Count - 8} weitere" : "");
        string linkText = "";
        if (_m.HasActiveLinks)
        {
            string progs = string.Join(", ", _m.AppLinks.Take(5).Select(l => l.Title)) + (_m.AppLinks.Count > 5 ? $" und {_m.AppLinks.Count - 5} weitere" : "");
            linkText = $"\n\nProgramme werden auf den Parkplatz „{_m.ParkingDevice?.Name ?? _m.ParkingDeviceName ?? "?"}“ umgeleitet: {progs}. Ihr Ton läuft dann nur noch über NX-MIX – kommt dort nichts an, ist das Programm stumm. Beim Beenden stellt NX-MIX die Umleitungen wieder her.";
        }
        ModalDialog.Show(root, "Lautstärke und Gain-Sperren geprüft?",
            "Audio beginnt immer mit Stille und blendet innerhalb von etwa 50 ms ein. Bitte vorher prüfen: Wiedergabe-Lautstärke der Geräte, Verstärker, Lautsprecher – und bei empfindlichen Hochtönern die Gain-Sperre des Kanals.\n\n" + lockText + linkText,
            new[] { "Abbrechen", "Ja, starten" }, 1, -1, i =>
            {
                if (i == 1) { _m.SessionConfirmed = true; _m.ConfirmLinks(); _m.StartHost(); }
            }, Icon.Shield);
    }

    public override void Layout()
    {
        var area = new RectF(Bounds.X + Theme.Pad, Bounds.Y + 2, Math.Max(0, Bounds.W - 2 * Theme.Pad), Math.Max(0, Bounds.H - Theme.Pad - 2));
        float rowH = Theme.ControlH;
        float cardH = 24 + 3 * rowH + 2 * 10 + 24;
        _hostCard = new RectF(area.X, area.Y, area.W, cardH);
        float y = _hostCard.Bottom + Theme.Gap;
        float h = Math.Max(0, area.Bottom - y);
        if (area.W >= 1060)
        {
            float lw = Math.Clamp(area.W * 0.38f, 380, 540);
            _list.Bounds = new RectF(area.X, y, lw, h);
            _table.Bounds = new RectF(area.X + lw + Theme.Gap, y, Math.Max(0, area.W - lw - Theme.Gap), h);
        }
        else
        {
            // schmales Fenster: untereinander, Zuordnungen bekommen mehr Platz
            float lh = Math.Max(200, h * 0.42f);
            _list.Bounds = new RectF(area.X, y, area.W, lh);
            _table.Bounds = new RectF(area.X, y + lh + Theme.Gap, area.W, Math.Max(0, h - lh - Theme.Gap));
        }

        var c = _hostCard;
        StartButton.Bounds = new RectF(c.X + 24, c.Y + 24, 210, Theme.Touch ? 50 : 44);
        float rx = c.Right - 24, colW = 300;
        float ry = c.Y + 24;
        _rRate = new RectF(rx - colW, ry, colW, rowH);
        _rate.Bounds = new RectF(rx - 170, ry, 170, rowH);
        ry += rowH + 10;
        _rBlock = new RectF(rx - colW, ry, colW, rowH);
        _block.Bounds = new RectF(rx - 200, ry, 200, rowH);
        ry += rowH + 10;
        _rExcl = new RectF(rx - colW, ry, colW, rowH);
        _excl.Bounds = new RectF(rx - 80, ry, 80, rowH);
        _list.Layout(); _table.Layout();
        UpdateEnabled();
    }

    public override void Paint(Renderer g)
    {
        UpdateEnabled();
        var c = _hostCard;
        Draw.Card(g, c);

        // Hinweis unter dem Start-Knopf
        string note; Color4 nc = Theme.TextTertiary;
        var stats = _m.Stats;
        string? err = _m.HostError ?? stats?.LastError;
        if (!_m.HostAvailable) { note = _m.HostUnavailableReason ?? "Audio-Host nicht verfügbar."; nc = Theme.Orange; }
        else if (err != null) { note = err; nc = Theme.Red; }
        else if (!_m.HostRunning && !StartButton.Enabled && !_m.HostBusy) { _m.CanStart(out note!); nc = Theme.TextSecondary; }
        else if (!_m.HostRunning && !_m.HostBusy && OnlyLinksBlock()) { note = "App-Links nicht startklar – ein Klick auf „Audio starten“ zeigt, warum."; nc = Theme.Orange; }
        else note = _m.HostRunning ? "Zum Ändern von Geräten oder Samplerate zuerst stoppen." : "Audio startet nie automatisch – nur auf Klick.";
        var nr = new RectF(c.X + 24, StartButton.Bounds.Bottom + 10, 210, c.Bottom - StartButton.Bounds.Bottom - 18);
        g.Text(note, nr, Theme.Caption, nc, HAlign.Left, VAlign.Top, true, true);

        // Mitte: Status + Kennzahlen
        float mx = c.X + 24 + 210 + 32, mr = _rRate.X - 32;
        var st = _m.HostState;
        (string label, Color4 col) = st switch
        {
            HostState.Running => ("Läuft", Theme.Green),
            HostState.Starting => ("Startet …", Theme.Orange),
            HostState.Faulted => ("Fehler", Theme.Red),
            _ => (_m.HostBusy ? "Stoppt …" : "Gestoppt", Theme.TextTertiary),
        };
        g.Text("Audio-Engine", new RectF(mx, c.Y + 18, 160, 26), Theme.Headline, Theme.Text, HAlign.Left, VAlign.Middle, false);
        Draw.Chip(g, mx + Renderer.MeasureWidth("Audio-Engine", Theme.Headline) + 12, c.Y + 31, label, col);
        bool have = stats != null && (st == HostState.Running || st == HostState.Faulted);
        string V(Func<HostStats, string> f) => have ? f(stats!) : "–";
        var tiles = new (string, string, bool)[]
        {
            ("LATENZ", V(s => $"{s.OutputLatencyMs:0.0} ms"), false),
            ("BLOCK", V(s => $"{s.BlockFrames}"), false),
            ("CPU", V(s => $"{s.CpuLoad * 100:0.#} %"), false),
            ("UNDERRUNS", V(s => s.Underruns.ToString()), have && stats!.Underruns > 0),
            ("OVERRUNS", V(s => s.Overruns.ToString()), have && stats!.Overruns > 0),
            ("GERÄTE", V(s => s.Devices.Count.ToString()), false),
        };
        float avail = Math.Max(0, mr - mx);
        int cols = avail >= 6 * 96 ? 6 : 3;
        float tw = cols == 0 ? 0 : avail / cols;
        float ty = c.Y + 56;
        for (int i = 0; i < tiles.Length; i++)
        {
            int col2 = i % cols, row = i / cols;
            var r = new RectF(mx + col2 * tw, ty + row * (cols == 6 ? 0 : 44), tw - 8, 40);
            if (cols == 6) r = new RectF(mx + col2 * tw, ty + 6, tw - 8, 52);
            g.Text(tiles[i].Item1, new RectF(r.X, r.Y, r.W, 14), Theme.Overline, Theme.TextTertiary, HAlign.Left, VAlign.Middle, false);
            g.Text(tiles[i].Item2, new RectF(r.X, r.Y + 15, r.W, 26), Theme.Stat, tiles[i].Item3 ? Theme.Orange : have ? Theme.Text : Theme.TextTertiary, HAlign.Left, VAlign.Middle, true);
        }

        // Rechts: Beschriftungen der Einstellungen
        g.Fill(new RectF(_rRate.X - 16, c.Y + 20, 1, c.H - 40), Theme.Separator);
        g.Text("Samplerate", _rRate, Theme.Body, Theme.Text, HAlign.Left, VAlign.Middle, false);
        g.Text("Blockgröße", _rBlock, Theme.Body, Theme.Text, HAlign.Left, VAlign.Middle, false);
        g.Text("Exklusiv-Modus", _rExcl, Theme.Body, Theme.Text, HAlign.Left, VAlign.Middle, false);
        PaintChildren(g);
    }
}
