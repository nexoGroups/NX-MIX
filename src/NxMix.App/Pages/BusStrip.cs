using NxMix.App.Model;
using NxMix.App.Native;
using NxMix.App.Ui;
using NxMix.Engine;

namespace NxMix.App.Pages;

internal sealed class BusStrip : Widget
{
    readonly AppModel _m;
    readonly Guid _id;
    readonly Button _name = new("Bus", ButtonStyle.Tinted);
    readonly Button _patch = new("Routing", ButtonStyle.Gray) { Font = Theme.CaptionStrong };
    readonly Toggle _mute = new("M", () => Theme.Red);
    readonly Fader _fader = new();
    readonly ValueBox _value = new();
    public BusStrip(AppModel m, Guid id)
    {
        _m = m; _id = id;
        Add(_name); Add(_patch); Add(_mute); Add(_fader); Add(_value);
        _name.Clicked += () => { if (Root is { } r) BusSheet.Show(r, m, id); };
        _patch.Clicked += () => { if (Root is { } r) BusSheet.Show(r, m, id); };
        _mute.Toggled += on => m.UpdateBus(id, b => b with { Mute = on });
        _fader.Changed += value => m.UpdateBus(id, b => b with { GainDb = Math.Clamp(double.IsFinite(value) ? value : -120, -120, 12) });
        _value.Typed += text => { if (Db.TryParse(text, out var v)) m.UpdateBus(id, b => b with { GainDb = Math.Clamp(double.IsFinite(v) ? v : -120, -120, 12) }); };
    }
    public override void Layout()
    {
        var b = Bounds;
        _name.Bounds = new RectF(b.X + 6, b.Y + 8, b.W - 12, 56);
        _mute.Bounds = new RectF(b.X + 8, b.Y + 88, b.W - 16, 28);
        _patch.Bounds = new RectF(b.X + 8, b.Y + 124, b.W - 16, 28);
        _fader.Bounds = new RectF(b.X + 10, b.Y + 172, b.W - 20, Math.Max(80, b.H - 218));
        _value.Bounds = new RectF(b.X + 8, b.Bottom - 32, b.W - 16, 26);
        foreach (var c in Children) c.Layout();
    }
    public override void Paint(Renderer g)
    {
        var bus = _m.Settings.Buses.FirstOrDefault(b => b.Id == _id);
        if (bus == null) return;
        _name.Text = bus.Name; _mute.SetChecked(bus.Mute); _fader.SetValue(bus.GainDb); _value.Text = Db.Format(bus.GainDb);
        g.FillRound(Bounds, Theme.RadiusInner, Theme.Surface2);
        g.Text($"BUS · {bus.Channels} CH", new RectF(Bounds.X, Bounds.Y + 65, Bounds.W, 18), Theme.MicroStrong, Theme.AccentText, HAlign.Center);
        PaintChildren(g);
    }
}

internal sealed class BusSheet : SheetOverlay
{
    readonly AppModel _m;
    readonly Guid _id;
    readonly Button _name = new("Bus", ButtonStyle.Plain);
    readonly Button _close = new("Schließen");
    readonly Button _source = new("Quelle wählen …");
    readonly Button _target = new("Ziel wählen …");
    readonly Button _add = new("Verbinden", ButtonStyle.Tinted);
    readonly Button _delete = new("Bus löschen", ButtonStyle.DestructiveTinted);
    readonly List<Button> _routes = new();
    RoutePort? _from, _to;
    protected override bool FullPage => true;
    BusSheet(AppModel m, Guid id)
    {
        _m = m; _id = id;
        Add(_name); Add(_close); Add(_source); Add(_target); Add(_add); Add(_delete);
        _close.Clicked += Close;
        _name.Clicked += () => Root?.BeginEdit(_name.Bounds, Bus.Name, Theme.Title, s => m.UpdateBus(id, b => b with { Name = s.Trim() }), 40);
        _source.Clicked += () => Pick(true);
        _target.Clicked += () => Pick(false);
        _add.Clicked += () =>
        {
            if (_from == null || _to == null) return;
            string? error = m.AddBusRoute(new BusRoute(_from, _to));
            if (error != null) Root?.Notify.Flash(error, true);
            else Rebuild();
        };
        _delete.Clicked += () => Root?.Confirm("Bus löschen?", "Die Verbindungen dieses Busses werden entfernt.", "Löschen", () => { Close(); m.RemoveBus(id); }, true);
        Rebuild();
    }
    MixBus Bus => _m.Settings.Buses.First(b => b.Id == _id);
    public static void Show(UiRoot root, AppModel m, Guid id) => root.ShowOverlay(new BusSheet(m, id));
    string PortName(RoutePort p) => p.Kind == RouteKind.Bus
        ? $"{_m.Settings.Buses.First(b => b.Id == p.Id).Name} / {p.Channel + 1}"
        : _m.FaderDescription(new FaderSlot(p.Kind, p.Id));
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
        PopupMenu.Show(root, source ? _source.Bounds : _target.Bounds, entries, 280);
    }
    void Rebuild()
    {
        foreach (var b in _routes) Children.Remove(b);
        _routes.Clear();
        foreach (var route in _m.Settings.BusRoutes.Where(r => r.Source.Id == _id || r.Target.Id == _id))
        {
            var button = Add(new Button($"{PortName(route.Source)}  →  {PortName(route.Target)}     ·     Entfernen", ButtonStyle.Gray));
            button.Clicked += () => { _m.RemoveBusRoute(route); Rebuild(); };
            _routes.Add(button);
        }
        if (Root != null) { Layout(); Invalidate(); }
    }
    public override void Layout()
    {
        base.Layout();
        _name.Text = $"{Bus.Name} · {Bus.Channels} Kanäle";
        _name.Bounds = new RectF(Panel.X + 20, Panel.Y + 18, Math.Min(500, Panel.W - 180), 40);
        _close.Bounds = new RectF(Panel.Right - 130, Panel.Y + 22, 110, 32);
    }
    protected override float LayoutBody(float x, float y, float w)
    {
        float start = y;
        y += 60;
        float half = (w - 12) / 2;
        _source.Bounds = new RectF(x, y, half, 38);
        _target.Bounds = new RectF(x + half + 12, y, half, 38);
        _add.Bounds = new RectF(x, y + 48, 160, 36);
        _add.Enabled = _from != null && _to != null && (_from.Id == _id || _to.Id == _id);
        y += 110;
        foreach (var b in _routes) { b.Bounds = new RectF(x, y, w, 38); y += 46; }
        _delete.Bounds = new RectF(x, y + 20, 170, 36);
        return y + 64 - start;
    }
    protected override void PaintHeader(Renderer g, RectF h) { }
    protected override void PaintBody(Renderer g) => g.Text("Quellen einem Bus-Kanal zuweisen und Bus-Kanäle auf Ausgänge oder weitere Busse patchen.\nEin Eingang läuft nach der Zuweisung über seine Busse; vorhandene direkte Matrix-Verbindungen werden dabei gelöst.",
        new RectF(Body.X + 20, Body.Y + 10 - ScrollOffset, Body.W - 40, 44), Theme.Callout, Theme.TextSecondary, wrap: true);
}
