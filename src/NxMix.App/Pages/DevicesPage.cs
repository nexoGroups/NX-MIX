using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Audio;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Liste der Audio-Geraete (aus IDeviceCatalog) mit Schnell-Zuordnung.</summary>
internal sealed class DeviceListView : Widget
{
    readonly AppModel _m;
    readonly VScroll _sc = new();
    readonly IconButton _refresh = new(IconKind.Refresh) { Tooltip = "Geräteliste neu einlesen" };
    int _filter;                       // 0 alle, 1 Aufnahme, 2 Wiedergabe
    (int Row, int Act) _hot = (-1, -1), _press = (-1, -1);
    int _hotSeg = -1, _pressSeg = -1;
    const float RowH = 62f, HeadH = 70f;
    static readonly string[] Segs = { "Alle", "Eingabe", "Ausgabe" };

    public DeviceListView(AppModel m)
    {
        _m = m;
        Add(_refresh);
        _refresh.Clicked += _ => _m.RefreshDevices();
    }

    public override bool AcceptsMouse => true;

    List<AudioDeviceInfo> Filtered() =>
        _m.Devices.Where(d => _filter == 0 || (_filter == 1 ? d.Flow == DeviceFlow.Capture : d.Flow == DeviceFlow.Render)).ToList();

    public override void Layout()
    {
        var b = Bounds;
        _refresh.Bounds = new RectF(b.Right - 16 - 30, b.Y + 14, 30, 30);
        _sc.Update(new RectF(b.X + 8, b.Y + HeadH, b.W - 16, Math.Max(0, b.H - HeadH - 8)), Filtered().Count * RowH);
    }

    RectF SegRect(int i)
    {
        float x = Bounds.X + 16;
        for (int k = 0; k < i; k++) x += Renderer.MeasureWidth(Segs[k], Theme.SmallBold) + 26 + 4;
        return new RectF(x, Bounds.Y + 40 - 2, Renderer.MeasureWidth(Segs[i], Theme.SmallBold) + 26, 22);
    }

    RectF RowRect(int row) => new(_sc.View.X, _sc.View.Y + row * RowH - _sc.Offset, _sc.View.W - (_sc.Needed ? 12 : 0), RowH - 4);

    // Aktionen je Zeile: 0 = Eingang/Ausgang zuordnen, 1 = Loopback
    List<(int Act, RectF Rect, string Text)> Actions(RectF row, AudioDeviceInfo d)
    {
        var list = new List<(int, RectF, string)>();
        float x = row.Right - 10;
        if (d.Flow == DeviceFlow.Render)
        {
            float w = Renderer.MeasureWidth("Loopback", Theme.SmallBold) + 22;
            x -= w; list.Add((1, new RectF(x, row.CY - 14, w, 28), "Loopback")); x -= 6;
        }
        string t = d.Flow == DeviceFlow.Capture ? "+ Eingang" : "+ Ausgang";
        float w2 = Renderer.MeasureWidth(t, Theme.SmallBold) + 22;
        x -= w2; list.Add((0, new RectF(x, row.CY - 14, w2, 28), t));
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

    int HitSeg(float x, float y) { for (int i = 0; i < Segs.Length; i++) if (SegRect(i).Inflate(2).Contains(x, y)) return i; return -1; }

    public override void OnMouseMove(in MouseEvent e)
    {
        if (_sc.Dragging && IsPressed) { _sc.Drag(e.Y); Invalidate(); return; }
        var h = HitRow(e.X, e.Y); int s = HitSeg(e.X, e.Y);
        if (h != _hot || s != _hotSeg) { _hot = h; _hotSeg = s; Invalidate(); }
    }
    public override void OnMouseLeave() { _hot = (-1, -1); _hotSeg = -1; Invalidate(); }
    public override CursorKind CursorAt(float x, float y) => (HitRow(x, y).Act >= 0 && !_m.HostRunning) || HitSeg(x, y) >= 0 ? CursorKind.Hand : CursorKind.Arrow;

    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button != 0) return;
        if (_sc.Down(e.X, e.Y)) return;
        _press = HitRow(e.X, e.Y); _pressSeg = HitSeg(e.X, e.Y);
    }

    public override void OnMouseUp(in MouseEvent e)
    {
        if (_sc.Dragging) { _sc.Up(); return; }
        var h = HitRow(e.X, e.Y); int s = HitSeg(e.X, e.Y);
        if (s >= 0 && s == _pressSeg) { _filter = s; Layout(); Invalidate(); }
        else if (h.Act >= 0 && h == _press && !_m.HostRunning)
        {
            var d = Filtered()[h.Row];
            Assign(d, h.Act == 1);
        }
        _press = (-1, -1); _pressSeg = -1;
    }

    public override bool OnMouseWheel(in MouseEvent e) { if (_sc.Wheel(e.Wheel)) { Invalidate(); return true; } return false; }

    void Assign(AudioDeviceInfo d, bool loopback)
    {
        var flow = d.Flow == DeviceFlow.Render && !loopback ? DeviceFlow.Render : DeviceFlow.Capture;
        int count = Math.Clamp(Math.Min(d.Channels, 2), 1, 64);
        int devStart = _m.Bindings.Where(b => b.DeviceId == d.Id && b.Flow == flow && b.Loopback == loopback)
            .Select(b => b.DeviceFirstChannel + b.Count).DefaultIfEmpty(0).Max();
        if (devStart + count > Math.Max(1, d.Channels)) devStart = 0;
        var refusal = _m.AddBinding(new DeviceBinding(d.Id, flow, devStart, _m.NextFreeEngineChannel(flow), count, loopback));
        if (refusal != null) Root?.Status.Flash(refusal, true);
        else Root?.Status.Flash($"Zuordnung angelegt: {d.Name}");
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        g.FillRound(b, Theme.RadiusPanel, Theme.Panel);
        g.StrokeRound(b, Theme.RadiusPanel, Theme.Border);
        g.Text("Geräte", new RectF(b.X + 16, b.Y + 10, 160, 24), Theme.BodyBold, Theme.Text);
        // Filter
        for (int i = 0; i < Segs.Length; i++)
        {
            var r = SegRect(i);
            bool on = i == _filter, hot = i == _hotSeg;
            if (on) g.FillRound(r, 11, Theme.Accent.With(0.18f));
            else if (hot) g.FillRound(r, 11, Theme.CardHover);
            g.Text(Segs[i], r, Theme.SmallBold, on ? Theme.AccentHi : Theme.TextDim, HAlign.Center, VAlign.Middle, false);
        }
        g.Fill(new RectF(b.X + 1, b.Y + HeadH - 4, b.W - 2, 1), Theme.Border);

        var list = Filtered();
        if (list.Count == 0)
        {
            string head, body;
            if (_m.DevicesLoading) { head = "Lese Geräte ein …"; body = ""; }
            else if (_m.DeviceError != null) { head = "Geräteliste nicht verfügbar"; body = _m.DeviceError; }
            else if (_m.Devices.Count > 0) { head = "Keine Geräte in dieser Ansicht"; body = "Wähle oben „Alle“."; }
            else { head = "Keine Geräte gefunden"; body = "Mit dem Aktualisieren-Knopf oben rechts erneut einlesen."; }
            g.Text(head, new RectF(b.X + 20, b.Y + HeadH + 30, b.W - 40, 24), Theme.BodyBold, Theme.TextDim, HAlign.Center);
            g.Text(body, new RectF(b.X + 30, b.Y + HeadH + 58, b.W - 60, 60), Theme.Small, Theme.TextFaint, HAlign.Center, VAlign.Top, false, true);
        }
        else
        {
            g.PushClip(_sc.View);
            for (int i = 0; i < list.Count; i++)
            {
                var r = RowRect(i);
                if (r.Bottom < _sc.View.Y || r.Y > _sc.View.Bottom) continue;
                PaintRow(g, r, list[i], i);
            }
            g.PopClip();
            _sc.Paint(g);
        }
        PaintChildren(g);
    }

    void PaintRow(Renderer g, RectF r, AudioDeviceInfo d, int idx)
    {
        bool rowHot = _hot.Row == idx;
        g.FillRound(r, 8, rowHot ? Theme.CardHover : Theme.Card);
        g.StrokeRound(r, 8, Theme.Border);
        bool cap = d.Flow == DeviceFlow.Capture;
        var ic = new RectF(r.X + 10, r.CY - 15, 30, 30);
        g.FillRound(ic, 8, (cap ? Theme.Accent : Theme.Solo).With(0.14f));
        if (cap) Icons.InArrow(g, ic.CX, ic.CY, 14, Theme.Accent); else Icons.OutArrow(g, ic.CX, ic.CY, 14, Theme.Solo);
        var actions = Actions(r, d);
        float textRight = actions.Min(a => a.Rect.X) - 10;
        g.Text(d.Name, new RectF(r.X + 50, r.Y + 8, Math.Max(10, textRight - r.X - 50), 22), Theme.Body, Theme.Text, HAlign.Left, VAlign.Middle, true);
        string sub = $"{(cap ? "Aufnahme" : "Wiedergabe")}  ·  {d.Channels} Kanäle  ·  {d.SampleRate / 1000.0:0.##} kHz" + (d.IsDefault ? "  ·  Standard" : "");
        g.Text(sub, new RectF(r.X + 50, r.Y + 30, Math.Max(10, textRight - r.X - 50), 20), Theme.Small, Theme.TextFaint, HAlign.Left, VAlign.Middle, true);
        foreach (var (act, rect, text) in actions)
        {
            bool hot = _hot.Row == idx && _hot.Act == act && !_m.HostRunning;
            g.FillRound(rect, 7, _m.HostRunning ? Theme.Card : hot ? Theme.RaisedHi : Theme.Raised);
            g.StrokeRound(rect, 7, Theme.BorderStrong.With(_m.HostRunning ? 0.4f : 1f));
            g.Text(text, rect, Theme.SmallBold, _m.HostRunning ? Theme.TextFaint : Theme.Text, HAlign.Center, VAlign.Middle, false);
        }
    }

    public override string? TooltipAt(float x, float y)
    {
        var h = HitRow(x, y);
        if (h.Act < 0) return null;
        if (_m.HostRunning) return "Zum Ändern zuerst stoppen";
        return h.Act == 1 ? "Nimmt auf, was dieses Wiedergabegerät abspielt (nur lesend)" : "Legt eine Zuordnung zu den nächsten freien Engine-Kanälen an";
    }
}

/// <summary>Tabelle der DeviceBindings: Richtung, Geraet, Geraetekanal-Start, Engine-Kanal-Start, Anzahl.</summary>
internal sealed class BindingTableView : Widget
{
    enum Reg { None, Chip, Device, DevMinus, DevVal, DevPlus, EngMinus, EngVal, EngPlus, CntMinus, CntVal, CntPlus, Status, Delete }

    readonly AppModel _m;
    readonly VScroll _sc = new();
    readonly TextButton _add = new("Zuordnung", ButtonStyle.Secondary, true) { Font = Theme.SmallBold, Radius = 7 };
    (int Row, Reg R) _hot = (-1, Reg.None), _press = (-1, Reg.None);
    const float RowH = 52f, HeadH = 70f, ColH = 26f;

    public BindingTableView(AppModel m)
    {
        _m = m;
        Add(_add);
        _add.Clicked += AddEmpty;
    }

    public override bool AcceptsMouse => true;

    void AddEmpty()
    {
        if (_m.HostRunning) return;
        var flow = DeviceFlow.Capture;
        var dev = _m.Devices.FirstOrDefault(d => d.Flow == DeviceFlow.Capture);
        var refusal = _m.AddBinding(new DeviceBinding(dev?.Id ?? "", flow, 0, _m.NextFreeEngineChannel(flow), 1));
        if (refusal != null) Root?.Status.Flash(refusal, true);
    }

    /// <summary>Aendert eine Zuordnung; lehnt das Modell ab (z. B. Konflikt mit einem App-Link), steht der Grund in der Statusleiste.</summary>
    void Upd(int row, Func<DeviceBinding, DeviceBinding> f)
    {
        var refusal = _m.UpdateBinding(row, f);
        if (refusal != null) Root?.Status.Flash(refusal, true);
    }

    public override void Layout()
    {
        var b = Bounds;
        _add.Bounds = new RectF(b.Right - 16 - 120, b.Y + 14, 120, 30);
        _sc.Update(new RectF(b.X + 8, b.Y + HeadH + ColH, b.W - 16, Math.Max(0, b.H - HeadH - ColH - 8)), _m.Bindings.Count * RowH);
        _add.Enabled = !_m.HostRunning;
    }

    RectF RowRect(int row) => new(_sc.View.X, _sc.View.Y + row * RowH - _sc.Offset, _sc.View.W - (_sc.Needed ? 12 : 0), RowH - 4);

    const float StepW = 104f;

    // Spaltenaufteilung (gleich fuer Kopfzeile und Zeilen)
    (RectF Chip, RectF Dev, RectF DevStep, RectF EngStep, RectF CntStep, RectF Status, RectF Del) Cols(RectF row)
    {
        float cy = row.CY;
        float x = row.X + 12;
        var chip = new RectF(x, cy - 13, 86, 26); x += 94;
        float right = row.Right - 10;
        var del = new RectF(right - 28, cy - 14, 28, 28); right -= 32;
        var status = new RectF(right - 26, cy - 14, 26, 28); right -= 30;
        var cnt = new RectF(right - StepW, cy - 14, StepW, 28); right -= StepW + 8;
        var eng = new RectF(right - StepW, cy - 14, StepW, 28); right -= StepW + 8;
        var dev = new RectF(right - StepW, cy - 14, StepW, 28); right -= StepW + 10;
        var device = new RectF(x, cy - 14, Math.Max(80, right - x), 28);
        return (chip, device, dev, eng, cnt, status, del);
    }

    static (RectF Minus, RectF Val, RectF Plus) Step(RectF s) => (new RectF(s.X, s.Y, 26, s.H), new RectF(s.X + 26, s.Y, s.W - 52, s.H), new RectF(s.Right - 26, s.Y, 26, s.H));

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
    public override CursorKind CursorAt(float x, float y) => Hit(x, y).R != Reg.None && !_m.HostRunning ? CursorKind.Hand : CursorKind.Arrow;

    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button != 0) return;
        if (_sc.Down(e.X, e.Y)) return;
        _press = Hit(e.X, e.Y);
        if (e.Clicks >= 2 && !_m.HostRunning && _press.Row >= 0) EditValue(_press);
    }

    public override void OnMouseUp(in MouseEvent e)
    {
        if (_sc.Dragging) { _sc.Up(); return; }
        var h = Hit(e.X, e.Y);
        if (h.Row >= 0 && h == _press && !_m.HostRunning && e.Clicks == 1) Act(h);
        _press = (-1, Reg.None);
    }

    public override bool OnMouseWheel(in MouseEvent e)
    {
        var h = Hit(e.X, e.Y);
        if (h.Row >= 0 && !_m.HostRunning && h.R is Reg.DevVal or Reg.EngVal or Reg.CntVal or Reg.DevMinus or Reg.DevPlus or Reg.EngMinus or Reg.EngPlus or Reg.CntMinus or Reg.CntPlus)
        {
            int d = e.Wheel > 0 ? 1 : -1;
            Nudge(h.Row, h.R, d);
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

    void Act(( int Row, Reg R) h)
    {
        int row = h.Row;
        var b = _m.Bindings[row];
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
        root.BeginEdit(vr.Inflate(2), (cur + 1).ToString(), Theme.Value, txt =>
        {
            if (int.TryParse(txt.Trim(), out int n)) set(n - 1);
            else root.Status.Flash("Ungültige Zahl: " + txt, true);
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
        items.Add(new("Geräteliste neu einlesen", null, _m.RefreshDevices));
        PopupMenu.Show(root, anchor, items, Math.Max(anchor.W, 280));
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
            new("Eingang (Aufnahmegerät)", null, () => Set(DeviceFlow.Capture, false)) { Checked = b.Flow == DeviceFlow.Capture && !b.Loopback },
            new("Loopback (Wiedergabe abgreifen)", null, () => Set(DeviceFlow.Capture, true)) { Checked = b.Loopback },
            new("Ausgang (Wiedergabegerät)", null, () => Set(DeviceFlow.Render, false)) { Checked = b.Flow == DeviceFlow.Render },
        };
        PopupMenu.Show(root, anchor, items, 260);
    }

    public override string? TooltipAt(float x, float y)
    {
        var h = Hit(x, y);
        if (h.Row < 0) return null;
        if (_m.HostRunning && h.R != Reg.None && h.R != Reg.Status) return "Zum Ändern zuerst stoppen";
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
        g.FillRound(b, Theme.RadiusPanel, Theme.Panel);
        g.StrokeRound(b, Theme.RadiusPanel, Theme.Border);
        g.Text("Zuordnung", new RectF(b.X + 16, b.Y + 10, 200, 24), Theme.BodyBold, Theme.Text);
        g.Text("Welche Geräte-Kanäle an welchen Engine-Kanälen hängen", new RectF(b.X + 16, b.Y + 33, b.W - 170, 20), Theme.Small, Theme.TextFaint, HAlign.Left, VAlign.Middle, true);
        g.Fill(new RectF(b.X + 1, b.Y + HeadH - 4, b.W - 2, 1), Theme.Border);

        // Spaltenkoepfe
        var hr = new RectF(_sc.View.X, b.Y + HeadH - 2, _sc.View.W - (_sc.Needed ? 12 : 0), ColH);
        var c = Cols(hr);
        void H(string t, RectF r, HAlign a = HAlign.Center) => g.Text(t, new RectF(r.X, hr.Y, r.W, ColH), Theme.Tiny, Theme.TextFaint, a, VAlign.Middle, false);
        H("RICHTUNG", c.Chip, HAlign.Left); H("GERÄT", c.Dev, HAlign.Left); H("GERÄT AB KANAL", c.DevStep); H("ENGINE AB KANAL", c.EngStep); H("ANZAHL", c.CntStep);

        if (_m.Bindings.Count == 0)
        {
            g.Text("Noch keine Zuordnung", new RectF(b.X + 20, b.Y + HeadH + 50, b.W - 40, 24), Theme.BodyBold, Theme.TextDim, HAlign.Center);
            g.Text("Wähle links bei einem Gerät „+ Eingang“, „+ Ausgang“ oder „Loopback“ – oder lege hier oben eine leere Zuordnung an. Fehlende Engine-Kanäle werden dabei automatisch angelegt. Audio startet erst, wenn du auf „Audio starten“ klickst.",
                new RectF(b.X + 50, b.Y + HeadH + 80, b.W - 100, 90), Theme.Small, Theme.TextFaint, HAlign.Center, VAlign.Top, false, true);
        }
        else
        {
            g.PushClip(_sc.View);
            for (int i = 0; i < _m.Bindings.Count; i++)
            {
                var r = RowRect(i);
                if (r.Bottom < _sc.View.Y || r.Y > _sc.View.Bottom) continue;
                PaintRow(g, r, i);
            }
            g.PopClip();
            _sc.Paint(g);
        }
        PaintChildren(g);
    }

    void PaintStepper(Renderer g, RectF s, int display, int row, Reg minus, Reg val, Reg plus, bool dis)
    {
        var (mi, va, pl) = Step(s);
        g.FillRound(s, 7, dis ? Theme.Card : Theme.Well);
        g.StrokeRound(s, 7, Theme.Border);
        bool hm = _hot.Row == row && _hot.R == minus && !dis, hp = _hot.Row == row && _hot.R == plus && !dis;
        if (hm) g.FillRound(mi.Deflate(2), 5, Theme.RaisedHi);
        if (hp) g.FillRound(pl.Deflate(2), 5, Theme.RaisedHi);
        var ic = dis ? Theme.TextFaint : Theme.TextDim;
        Icons.Minus(g, mi.CX, mi.CY, 9, ic, 1.6f);
        Icons.Plus(g, pl.CX, pl.CY, 9, ic, 1.6f);
        g.Text(display.ToString(), va, Theme.Value, dis ? Theme.TextFaint : Theme.Text, HAlign.Center, VAlign.Middle, false);
    }

    void PaintRow(Renderer g, RectF r, int i)
    {
        var bd = _m.Bindings[i];
        bool dis = _m.HostRunning;
        g.FillRound(r, 8, _hot.Row == i ? Theme.CardHover : Theme.Card);
        g.StrokeRound(r, 8, Theme.Border);
        var c = Cols(r);
        // Richtung
        bool loop = bd.Loopback; bool cap = bd.Flow == DeviceFlow.Capture;
        string chip = loop ? "Loopback" : cap ? "Eingang" : "Ausgang";
        var col = loop ? Theme.Solo : cap ? Theme.Accent : Theme.Rgb(0x8FA3FF);
        g.FillRound(c.Chip, 13, col.With(_hot.Row == i && _hot.R == Reg.Chip && !dis ? 0.28f : 0.16f));
        g.Text(chip, new RectF(c.Chip.X, c.Chip.Y, c.Chip.W - 12, c.Chip.H), Theme.SmallBold, col, HAlign.Center, VAlign.Middle, false);
        Icons.Chevron(g, c.Chip.Right - 11, c.Chip.CY, 6, col, 0, 1.3f);
        // Geraet
        var dev = _m.FindDevice(bd.DeviceId);
        g.FillRound(c.Dev, 7, dis ? Theme.Card : (_hot.Row == i && _hot.R == Reg.Device) ? Theme.RaisedHi : Theme.Raised);
        g.StrokeRound(c.Dev, 7, Theme.BorderStrong.With(dis ? 0.4f : 1f));
        string dn = dev?.Name ?? (string.IsNullOrEmpty(bd.DeviceId) ? "Gerät wählen …" : "Gerät nicht gefunden");
        g.Text(dn, new RectF(c.Dev.X + 10, c.Dev.Y, c.Dev.W - 30, c.Dev.H), Theme.Small, dev == null ? Theme.Solo : dis ? Theme.TextFaint : Theme.Text, HAlign.Left, VAlign.Middle, true);
        Icons.Chevron(g, c.Dev.Right - 13, c.Dev.CY, 7, Theme.TextDim, 0);
        // Steppers
        PaintStepper(g, c.DevStep, bd.DeviceFirstChannel + 1, i, Reg.DevMinus, Reg.DevVal, Reg.DevPlus, dis);
        PaintStepper(g, c.EngStep, bd.EngineFirstChannel + 1, i, Reg.EngMinus, Reg.EngVal, Reg.EngPlus, dis);
        PaintStepper(g, c.CntStep, bd.Count, i, Reg.CntMinus, Reg.CntVal, Reg.CntPlus, dis);
        // Pruefung
        var v = _m.ValidateBinding(bd);
        if (v == null)
        {
            float cx = c.Status.CX, cy = c.Status.CY;
            var ok = Theme.Rgb(0x3FD08A);
            g.Line(cx - 5, cy, cx - 1.5f, cy + 3.5f, ok, 1.8f, true);
            g.Line(cx - 1.5f, cy + 3.5f, cx + 5, cy - 4, ok, 1.8f, true);
        }
        else Icons.Warn(g, c.Status.CX, c.Status.CY, 13, Theme.Solo);
        // Loeschen
        bool hd = _hot.Row == i && _hot.R == Reg.Delete && !dis;
        if (hd) g.FillRound(c.Del, 6, Theme.Mute.With(0.18f));
        Icons.Trash(g, c.Del.CX, c.Del.CY, 13, hd ? Theme.Mute : dis ? Theme.TextFaint : Theme.TextDim);
    }
}

internal sealed class DevicesPage : Page
{
    readonly AppModel _m;
    readonly DeviceListView _list;
    readonly BindingTableView _table;
    readonly TextButton _start = new("Audio starten", ButtonStyle.Primary) { Font = new TextStyle(14.5f, Vortice.DirectWrite.FontWeight.SemiBold), Radius = 12 };
    readonly Dropdown _rate = new() { Tooltip = "Samplerate der Engine (nur im Stillstand änderbar)" };
    readonly Dropdown _block = new() { Tooltip = "Blockgröße des Audio-Hosts (kleiner = weniger Latenz, mehr CPU)" };
    readonly ToggleButton _excl = new("Exklusiv", Theme.Accent) { Font = Theme.SmallBold, Tooltip = "Exklusiv-Modus (WASAPI): geringere Latenz, Gerät gehört nur NX-MIX" };
    RectF _hostCard;

    public override string Title => "Geräte";

    public DevicesPage(AppModel m)
    {
        _m = m;
        _list = Add(new DeviceListView(m));
        _table = Add(new BindingTableView(m));
        Add(_start); Add(_rate); Add(_block); Add(_excl);
        _start.Clicked += OnStartClicked;

        _rate.Text = () => $"{_m.Settings.SampleRate / 1000.0:0.##} kHz";
        _rate.Items = () => AppModel.SampleRates.Select(sr => new MenuItem($"{sr / 1000.0:0.##} kHz", null, () => _m.SetSampleRate(sr)) { Checked = sr == _m.Settings.SampleRate }).ToList();
        _block.Text = () => $"{_m.BlockFrames} Samples · {_m.BlockFrames * 1000.0 / _m.Settings.SampleRate:0.0} ms";
        _block.Items = () => AppModel.BlockSizes.Select(f => new MenuItem($"{f} Samples · {f * 1000.0 / _m.Settings.SampleRate:0.0} ms", null, () => _m.SetBlockFrames(f)) { Checked = f == _m.BlockFrames }).ToList();
        _excl.Toggled += on => _m.SetExclusive(on);

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
        _excl.SetChecked(_m.Exclusive);
        var st = _m.HostState;
        if (run) { _start.Text = _m.HostBusy && _m.HostStopping ? "Stoppt …" : "Audio stoppen"; _start.Style = ButtonStyle.Danger; _start.Enabled = !_m.HostBusy; }
        else if (_m.HostBusy) { _start.Text = "Startet …"; _start.Style = ButtonStyle.Primary; _start.Enabled = false; }
        else { _start.Text = "Audio starten"; _start.Style = ButtonStyle.Primary; _start.Enabled = _m.CanStart(out _) || OnlyLinksBlock(); }
    }

    /// <summary>Der Start scheitert NUR an den App-Links (ohne sie ginge er): dann bleibt der Knopf klickbar und erklaert / bietet Alternativen an.</summary>
    bool OnlyLinksBlock() => !_m.CanStart(out _) && _m.HasActiveLinks && _m.CanStartCore(false, out _);

    public override void OnShow()
    {
        if (_m.Devices.Count == 0 && !_m.DevicesLoading && _m.DeviceError == null) _m.RefreshDevices();
        Refresh();
    }

    void OnStartClicked()
    {
        var root = Root; if (root == null) return;
        if (_m.HostRunning) { _m.StopHost(); return; }
        if (!_m.CanStart(out var why))
        {
            if (OnlyLinksBlock()) ShowLinkProblemDialog(why);
            else root.Status.Flash(why, true);
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
                else if (i == 2) { _m.SetAppLinksEnabled(false); root.Status.Flash("App-Links ausgeschaltet – Programme laufen normal."); OnStartClicked(); }
            });
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
            });
    }

    public override void Layout()
    {
        var area = new RectF(Bounds.X + Theme.Pad, Bounds.Y + 12, Math.Max(0, Bounds.W - 2 * Theme.Pad), Math.Max(0, Bounds.H - 24));
        _hostCard = new RectF(area.X, area.Y, area.W, 138);
        float y = _hostCard.Bottom + 12;
        float h = Math.Max(0, area.Bottom - y);
        float lw = Math.Clamp(area.W * 0.40f, 380, 580);
        _list.Bounds = new RectF(area.X, y, lw, h);
        _table.Bounds = new RectF(area.X + lw + 12, y, Math.Max(0, area.W - lw - 12), h);

        var c = _hostCard;
        _start.Bounds = new RectF(c.X + 20, c.Y + 22, 200, 56);
        float rx = c.Right - 20;
        _excl.Bounds = new RectF(rx - 110, c.Y + 84, 110, 30);
        _block.Bounds = new RectF(rx - 220, c.Y + 36, 220, 34);
        _rate.Bounds = new RectF(rx - 220 - 12 - 120, c.Y + 36, 120, 34);
        _list.Layout(); _table.Layout();
        UpdateEnabled();
    }

    public override void Tick(float dt) { }

    public override void Paint(Renderer g)
    {
        UpdateEnabled();
        var c = _hostCard;
        g.FillRound(c, Theme.RadiusPanel, Theme.Panel);
        g.StrokeRound(c, Theme.RadiusPanel, Theme.Border);

        var st = _m.HostState;
        var stats = _m.Stats;
        // Status-Chip
        (string label, Color4 col) = st switch
        {
            HostState.Running => ("Läuft", Theme.Rgb(0x3FD08A)),
            HostState.Starting => ("Startet …", Theme.Solo),
            HostState.Faulted => ("Fehler", Theme.Mute),
            _ => (_m.HostBusy ? "Stoppt …" : "Gestoppt", Theme.TextDim),
        };
        float sx = c.X + 244;
        float cw = Renderer.MeasureWidth(label, Theme.SmallBold) + 36;
        var chip = new RectF(sx, c.Y + 18, cw, 24);
        g.FillRound(chip, 12, col.With(0.14f));
        g.FillCircle(chip.X + 13, chip.CY, 4, col);
        g.Text(label, new RectF(chip.X + 24, chip.Y, cw - 28, chip.H), Theme.SmallBold, col, HAlign.Left, VAlign.Middle, false);
        g.Text("Audio-Host", new RectF(sx + cw + 12, c.Y + 18, 200, 24), Theme.Small, Theme.TextFaint, HAlign.Left, VAlign.Middle, false);

        // Kennzahlen
        bool have = stats != null && (st == HostState.Running || st == HostState.Faulted);
        string V(Func<HostStats, string> f) => have ? f(stats!) : "–";
        var tiles = new (string, string)[]
        {
            ("LATENZ", V(s => $"{s.OutputLatencyMs:0.0} ms")),
            ("BLOCK", V(s => $"{s.BlockFrames}")),
            ("CPU", V(s => $"{s.CpuLoad * 100:0.#} %")),
            ("UNDERRUNS", V(s => s.Underruns.ToString())),
            ("OVERRUNS", V(s => s.Overruns.ToString())),
            ("GERÄTE", V(s => s.Devices.Count.ToString())),
        };
        float tx = sx, ty = c.Y + 56, tw = 92;
        for (int i = 0; i < tiles.Length; i++)
        {
            int col2 = i % 3, row = i / 3;
            var r = new RectF(tx + col2 * (tw + 8), ty + row * 40, tw, 36);
            g.Text(tiles[i].Item1, new RectF(r.X, r.Y, r.W, 12), Theme.Tiny, Theme.TextFaint, HAlign.Left, VAlign.Middle, false);
            bool warn = have && ((i == 3 && stats!.Underruns > 0) || (i == 4 && stats!.Overruns > 0));
            g.Text(tiles[i].Item2, new RectF(r.X, r.Y + 12, r.W, 22), new TextStyle(15f, Vortice.DirectWrite.FontWeight.SemiBold), warn ? Theme.Solo : have ? Theme.Text : Theme.TextFaint, HAlign.Left, VAlign.Middle, false);
        }

        // Hinweis unter dem Knopf
        string note; Color4 nc = Theme.TextFaint;
        string? err = _m.HostError ?? (stats?.LastError);
        if (!_m.HostAvailable) { note = _m.HostUnavailableReason ?? "Audio-Host nicht verfügbar."; nc = Theme.Solo; }
        else if (err != null && !_m.HostRunning) { note = err; nc = Theme.Mute; }
        else if (err != null) { note = err; nc = Theme.Mute; }
        else if (!_m.HostRunning && !_start.Enabled && !_m.HostBusy) { _m.CanStart(out note!); nc = Theme.TextDim; }
        else if (!_m.HostRunning && !_m.HostBusy && OnlyLinksBlock()) { note = "App-Links nicht startklar – ein Klick auf „Audio starten“ zeigt, warum."; nc = Theme.Solo; }
        else note = _m.HostRunning ? "Zum Ändern von Geräten oder Samplerate zuerst stoppen." : "Audio startet nie automatisch – nur auf Klick.";
        g.Text(note, new RectF(c.X + 20, c.Y + 84, 214, 44), Theme.Small, nc, HAlign.Left, VAlign.Top, false, true);

        // Beschriftungen der Auswahlfelder
        g.Text("SAMPLERATE", new RectF(_rate.Bounds.X, c.Y + 18, 120, 14), Theme.Tiny, Theme.TextFaint, HAlign.Left, VAlign.Middle, false);
        g.Text("BLOCKGRÖSSE", new RectF(_block.Bounds.X, c.Y + 18, 140, 14), Theme.Tiny, Theme.TextFaint, HAlign.Left, VAlign.Middle, false);
        PaintChildren(g);
    }
}
