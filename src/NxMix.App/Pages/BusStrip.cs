using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Engine;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Mix-Bus-Streifen mit echtem Summen-Pegelmeter, Mute, Solo, Routing-Inspektor und dB-Anzeige.</summary>
internal sealed class BusStrip : Widget
{
    readonly AppModel _m;
    public readonly Guid BusId;
    readonly Button _name = new("Bus", ButtonStyle.Tinted) { Font = Theme.CalloutStrong };
    readonly Button _patch = new("Routing", ButtonStyle.Gray) { Font = Theme.CaptionStrong };
    readonly Toggle _mute = new("M", () => Theme.Red) { Tooltip = "Bus stummschalten" };
    readonly Toggle _solo = new("S", () => Theme.Yellow) { ActiveText = () => Theme.Rgb(0x1A1B1E), Tooltip = "Bus Solo" };
    readonly Fader _fader = new();
    readonly Meter _meter = new() { ExtraTooltip = "Mix-Bus Summenpegel" };
    readonly ValueBox _value = new();

    public BusStrip(AppModel m, Guid id)
    {
        _m = m; BusId = id;
        Add(_name); Add(_patch); Add(_mute); Add(_solo); Add(_meter); Add(_fader); Add(_value);
        _name.Clicked += () => { if (Root is { } r) BusSheet.Show(r, m, id); };
        _patch.Clicked += () => { if (Root is { } r) BusSheet.Show(r, m, id); };
        _mute.Toggled += on => m.UpdateBus(id, b => b with { Mute = on });
        _solo.Toggled += on => m.UpdateBus(id, b => b with { Solo = on });
        _fader.Changed += value => m.UpdateBus(id, b => b with { GainDb = Db.IsOff(value) ? double.NegativeInfinity : Math.Clamp(value, -120, 12) });
        _value.Typed += text =>
        {
            if (Db.TryParse(text, out var v))
                m.UpdateBus(id, b => b with { GainDb = Db.IsOff(v) ? double.NegativeInfinity : Math.Clamp(v, -120, 12) });
        };
    }

    public void Sync()
    {
        var bus = _m.Settings.Buses.FirstOrDefault(b => b.Id == BusId);
        if (bus == null) return;
        _name.Text = bus.Name;
        _mute.SetChecked(bus.Mute);
        _solo.SetChecked(bus.Solo);
        _fader.SetValue(bus.GainDb);
        _value.Text = Db.Format(bus.GainDb);
        _value.TextColor = !Db.IsOff(bus.GainDb) && bus.GainDb > 0.05 ? Theme.Yellow : null;
    }

    public bool FeedMeter(float peak, float dt) => _meter.Feed(peak, dt);

    public override void Layout()
    {
        var b = Bounds;
        float pad = 6f;
        float x0 = b.X + pad, w = Math.Max(10, b.W - pad * 2);
        float y = b.Y + pad;

        _name.Bounds = new RectF(x0, y, w, Theme.StripHeaderH);
        y = _name.Bounds.Bottom + 24;

        float bh = Theme.MuteSoloH;
        float hw = (w - 4) / 2;
        _mute.Bounds = new RectF(x0, y, hw, bh);
        _solo.Bounds = new RectF(x0 + hw + 4, y, hw, bh);
        y += bh + 6;

        _patch.Bounds = new RectF(x0, y, w, Theme.ControlHSmall);
        y = _patch.Bounds.Bottom + 6;

        float rh = Theme.Touch ? 32 : 26;
        float by = b.Bottom - pad - rh;
        _value.Bounds = new RectF(x0, by, w, rh);

        float fy = y, fh = Math.Max(20, by - 8 - fy);
        float mw = Theme.MeterW;
        _meter.Bounds = new RectF(b.Right - pad - mw - 2, fy + 4, mw, Math.Max(10, fh - 8));
        _fader.ShowLabels = b.W >= 68;
        _fader.Bounds = new RectF(x0, fy, Math.Max(10, _meter.Bounds.X - x0 - 4), fh);
        foreach (var c in Children) c.Layout();
    }

    public override void Paint(Renderer g)
    {
        var bus = _m.Settings.Buses.FirstOrDefault(b => b.Id == BusId);
        if (bus == null) return;
        Sync();
        g.FillRound(Bounds, Theme.RadiusInner, Theme.Surface2);
        g.StrokeRound(Bounds, Theme.RadiusInner, Theme.Purple.With(0.4f), 1.1f);
        g.Text($"BUS · {bus.Channels} CH", new RectF(Bounds.X, Bounds.Y + 6 + Theme.StripHeaderH + 3, Bounds.W, 16),
            Theme.MicroStrong, Theme.Purple, HAlign.Center);
        PaintChildren(g);
    }
}

/// <summary>Seitenblatt zum Umbenennen und nicht-destruktiven Verbinden eines Mix-Busses.</summary>
internal sealed class BusSheet : SheetOverlay
{
    readonly AppModel _m;
    readonly Guid _id;
    readonly Button _name = new("Bus", ButtonStyle.Plain);
    readonly Button _close = new("Schließen");
    readonly Button _source = new("Quelle wählen …");
    readonly Button _target = new("Ziel wählen …");
    readonly Button _add = new("Verbinden (0 dB)", ButtonStyle.Tinted);
    readonly Button _toMatrix = new("In Patching Matrix öffnen", ButtonStyle.Gray, Icon.Matrix);
    readonly Button _delete = new("Bus löschen", ButtonStyle.DestructiveTinted, Icon.Trash);
    readonly List<Button> _routes = new();
    RoutePort? _from, _to;

    BusSheet(AppModel m, Guid id)
    {
        _m = m; _id = id;
        AddHeader(_name); AddHeader(_close);
        Add(_source); Add(_target); Add(_add); Add(_toMatrix); Add(_delete);
        _close.Clicked += Close;
        _name.Clicked += () =>
        {
            if (Bus is { } b)
                Root?.BeginEdit(_name.Bounds, b.Name, Theme.Title, s => { if (!string.IsNullOrWhiteSpace(s)) m.UpdateBus(id, x => x with { Name = s.Trim() }); }, 40);
        };
        _source.Clicked += () => Pick(true);
        _target.Clicked += () => Pick(false);
        _add.Clicked += () =>
        {
            if (_from == null || _to == null) return;
            string? error = m.AddBusRoute(new BusRoute(_from, _to));
            if (error != null) Root?.Notify.Flash(error, true);
            else Rebuild();
        };
        _toMatrix.Clicked += () => { var r = Root; Close(); r?.SwitchPage(UiRoot.TabMatrix); };
        _delete.Clicked += () => Root?.Confirm("Mix-Bus löschen?", "Der Bus und seine Bus-Routen werden entfernt. Direkte Matrix-Verbindungen bleiben unberührt.",
            "Löschen", () => { Close(); m.RemoveBus(id); }, true);
        Rebuild();
    }

    MixBus? Bus => _m.Settings.Buses.FirstOrDefault(b => b.Id == _id);

    public static void Show(UiRoot root, AppModel m, Guid id) => root.ShowOverlay(new BusSheet(m, id));

    string PortName(RoutePort p) => p.Kind switch
    {
        RouteKind.Bus => $"{(_m.Settings.Buses.FirstOrDefault(b => b.Id == p.Id)?.Name ?? "Bus")} · Ch {p.Channel + 1}",
        RouteKind.Input => _m.Settings.Inputs.Select((c, idx) => (c, idx)).Where(x => x.c.Id == p.Id).Select(x => _m.DisplayName(true, x.idx)).FirstOrDefault() ?? "Eingang",
        RouteKind.Output => _m.Settings.Outputs.Select((c, idx) => (c, idx)).Where(x => x.c.Id == p.Id).Select(x => _m.DisplayName(false, x.idx)).FirstOrDefault() ?? "Ausgang",
        _ => "Kanal",
    };

    void Pick(bool source)
    {
        if (Root is not { } root) return;
        var entries = new List<MenuItem>();
        void Port(RoutePort p)
        {
            entries.Add(new MenuItem(PortName(p), null, () =>
            {
                if (source) { _from = p; _source.Text = PortName(p); }
                else { _to = p; _target.Text = PortName(p); }
                Invalidate();
            }));
        }
        if (source) foreach (var ch in _m.Settings.Inputs) Port(new RoutePort(RouteKind.Input, ch.Id));
        foreach (var bus in _m.Settings.Buses)
            for (int ch = 0; ch < bus.Channels; ch++) Port(new RoutePort(RouteKind.Bus, bus.Id, ch));
        if (!source) foreach (var ch in _m.Settings.Outputs) Port(new RoutePort(RouteKind.Output, ch.Id));
        PopupMenu.Show(root, source ? _source.Bounds : _target.Bounds, entries, 260);
    }

    void Rebuild()
    {
        foreach (var b in _routes) { Children.Remove(b); b.Parent = null; }
        _routes.Clear();
        foreach (var route in _m.Settings.BusRoutes.Where(r => r.Source.Id == _id || r.Target.Id == _id))
        {
            var rt = route;
            var button = Add(new Button($"{PortName(rt.Source)}  →  {PortName(rt.Target)} ({Db.Format(rt.GainDb)} dB)   ·   Trennen", ButtonStyle.Gray));
            button.Clicked += () => { _m.RemoveBusRoute(rt); Rebuild(); };
            _routes.Add(button);
        }
        if (Root != null) { Layout(); Invalidate(); }
    }

    public override void Layout()
    {
        base.Layout();
        var bus = Bus;
        if (bus == null) return;
        _name.Text = $"{bus.Name} ({bus.Channels} Ch)";
        _name.Bounds = new RectF(Panel.X + 20, Panel.Y + 18, Math.Max(80, Panel.W - 140), 38);
        _close.Bounds = new RectF(Panel.Right - 110, Panel.Y + 21, 90, 32);
    }

    protected override float LayoutBody(float x, float y, float w)
    {
        float start = y;
        y += 52;
        _source.Bounds = new RectF(x, y, w, Theme.ControlH); y += Theme.ControlH + 8;
        _target.Bounds = new RectF(x, y, w, Theme.ControlH); y += Theme.ControlH + 10;
        _add.Bounds = new RectF(x, y, (w - 8) / 2, Theme.ControlH);
        _toMatrix.Bounds = new RectF(x + (w - 8) / 2 + 8, y, (w - 8) / 2, Theme.ControlH);
        _add.Enabled = _from != null && _to != null && (_from.Id == _id || _to.Id == _id);
        y += Theme.ControlH + 18;
        foreach (var b in _routes) { b.Bounds = new RectF(x, y, w, Theme.ControlH); y += Theme.ControlH + 6; }
        _delete.Bounds = new RectF(x, y + 14, 160, Theme.ControlH);
        return y + 58 - start;
    }

    protected override void PaintHeader(Renderer g, RectF h) { }
    protected override void PaintBody(Renderer g) =>
        g.Text("Nicht-destruktives Bus-Routing: Bestehende direkte Matrix-Kreuzpunkte bleiben erhalten (Parallelpfade werden in der Matrix angezeigt).",
            new RectF(Body.X + 20, Body.Y + 8 - ScrollOffset, Body.W - 40, 44), Theme.Caption, Theme.TextSecondary, wrap: true);
}
