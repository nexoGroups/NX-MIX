using System.Diagnostics;
using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Audio;

namespace NxMix.App.Pages;

internal sealed class DeviceOverview : Widget
{
    readonly AppModel _m;
    readonly Button _add = new("Hinzufügen", ButtonStyle.Tinted, Icon.Plus);
    readonly VScroll _scroll = new();
    readonly List<Button> _devices = new();
    string _signature = "";
    public DeviceOverview(AppModel m)
    {
        _m = m; Add(_add);
        _add.Clicked += () => { if (Root is { } r) DeviceAddSheet.Show(r, m); };
        m.BindingsChanged += Rebuild; m.HostChanged += Rebuild; m.StructureChanged += Rebuild; m.ValuesChanged += Rebuild;
        Rebuild();
    }
    void Rebuild()
    {
        string signature = string.Join('|', _m.Bindings.Select(b => b.DeviceId + _m.FindDevice(b.DeviceId)?.Name)) +
            string.Join('|', _m.AppLinks.Select(l => l.FirstChannel + l.Title));
        if (signature == _signature) { Layout(); Invalidate(); return; }
        _signature = signature;
        foreach (var b in _devices) Children.Remove(b);
        _devices.Clear();
        foreach (var id in _m.Bindings.Select(b => b.DeviceId).Distinct())
        {
            var d = _m.FindDevice(id);
            var b = Add(new Button($"{(AsioCatalog.IsAsio(id) ? "ASIO" : "Windows")} · {d?.Name ?? "Gerät offline"}", ButtonStyle.Gray) { Font = Theme.CalloutMedium });
            b.Clicked += () => { if (Root is { } r) DeviceSettingsSheet.Show(r, _m, id); };
            _devices.Add(b);
        }
        foreach (var link in _m.AppLinks)
        {
            var b = Add(new Button($"Software · {link.Title}", ButtonStyle.Gray));
            b.Clicked += () => { if (Root is { } r) ChannelSheet.Show(r, _m, true, link.FirstChannel).OpenTab(3); };
            _devices.Add(b);
        }
        Layout(); Invalidate();
    }
    public override void Layout()
    {
        var b = Bounds;
        _add.Bounds = new RectF(b.Right - _add.PreferredWidth - 12, b.Y + 12, _add.PreferredWidth, 32);
        _scroll.Update(new RectF(b.X + 12, b.Y + 60, b.W - 24, Math.Max(0, b.H - 72)), _devices.Count * 64);
        for (int i = 0; i < _devices.Count; i++) _devices[i].Bounds = new RectF(_scroll.View.X, _scroll.View.Y + i * 64 - _scroll.Offset, Math.Max(0, _scroll.View.W - 12), 54);
        _add.Enabled = !_m.HostRunning && !_m.HostBusy;
    }
    public override Widget? HitTest(float x, float y)
    {
        if (!Bounds.Contains(x, y)) return null;
        if (_add.HitTest(x, y) is { } hit) return hit;
        if (_scroll.View.Contains(x, y)) foreach (var d in _devices) if (d.HitTest(x, y) is { } child) return child;
        return this;
    }
    public override bool OnMouseWheel(in MouseEvent e) { if (!_scroll.Wheel(e.Wheel)) return false; Layout(); Invalidate(); return true; }
    public override void Paint(Renderer g)
    {
        Draw.Card(g, Bounds);
        Draw.CardTitle(g, new RectF(Bounds.X + 20, Bounds.Y + 6, 170, 40), "Hinzugefügt", _devices.Count);
        _add.Paint(g);
        g.PushClip(_scroll.View);
        if (_devices.Count == 0) g.Text("Füge Windows-Geräte, ASIO-Geräte oder Programme hinzu.", _scroll.View.Deflate(8), Theme.Body, Theme.TextSecondary, wrap: true);
        foreach (var d in _devices) d.Paint(g);
        g.PopClip(); _scroll.Paint(g);
    }
}

internal sealed class DeviceAddSheet : SheetOverlay
{
    readonly AppModel _m;
    readonly Segmented _tabs = new("Windows", "ASIO", "Software");
    readonly Button _close = new("Fertig");
    readonly Button _program = new("Programm hinzufügen …", ButtonStyle.Tinted, Icon.Plus);
    readonly Button _virtual = new("Virtuelles Windows-Gerät auswählen …", ButtonStyle.Gray);
    readonly DeviceListView _list;
    protected override bool FullPage => true;
    DeviceAddSheet(AppModel m)
    {
        _m = m; _list = Add(new DeviceListView(m)); Add(_tabs); Add(_close); Add(_program); Add(_virtual);
        _close.Clicked += Close;
        _tabs.Changed += i => { _list.Backend = i == 1 ? AudioBackend.Asio : AudioBackend.Windows; Layout(); Invalidate(); };
        _program.Clicked += () =>
        {
            var r = Root; if (r == null) return;
            int channel = m.InputCount;
            m.AddChannels(true, 2);
            Close();
            var sheet = ChannelSheet.Show(r, m, true, channel); sheet.OpenTab(3); sheet.OpenPicker();
        };
        _virtual.Clicked += () => { _tabs.SetSelected(0); _list.Backend = AudioBackend.Windows; Layout(); Invalidate(); };
        m.RefreshDevices();
    }
    public static void Show(UiRoot r, AppModel m) => r.ShowOverlay(new DeviceAddSheet(m));
    public override void Layout()
    {
        base.Layout();
        _close.Bounds = new RectF(Panel.Right - 130, Panel.Y + 20, 110, 34);
    }
    protected override float LayoutBody(float x, float y, float w)
    {
        _tabs.Bounds = new RectF(x, y + 8, w, 36);
        bool software = _tabs.Selected == 2;
        _program.Visible = _virtual.Visible = software; _list.Visible = !software;
        _program.Bounds = new RectF(x, y + 76, Math.Min(w, 560), 42);
        _virtual.Bounds = new RectF(x, y + 134, Math.Min(w, 560), 42);
        _list.Bounds = new RectF(x, y + 60, w, Math.Max(160, Body.H - 86)); _list.Layout();
        return Math.Max(240, Body.H - 28);
    }
    protected override void PaintHeader(Renderer g, RectF h) => g.Text("Gerät hinzufügen", new RectF(h.X + 20, h.Y + 20, h.W - 170, 34), Theme.Title, Theme.Text);
    protected override void PaintBody(Renderer g)
    {
        if (_tabs.Selected == 2) g.Text("Programme erhalten eigene Eingangskanäle. Virtuelle Ein- und Ausgänge müssen als Windows-Audiogeräte installiert sein.",
            new RectF(Body.X + 20, Body.Y + 200 - ScrollOffset, Body.W - 40, 60), Theme.Body, Theme.TextSecondary, wrap: true);
    }
}

internal sealed class DeviceSettingsSheet : SheetOverlay
{
    readonly AppModel _m;
    readonly string _id;
    readonly Dropdown _rate = new(), _buffer = new();
    readonly Button _panel = new("Treibereinstellungen öffnen …");
    readonly Button _remove = new("Gerät entfernen", ButtonStyle.DestructiveTinted);
    readonly Button _close = new("Fertig");
    AudioDeviceInfo? Device => _m.FindDevice(_id);
    DeviceBinding? Binding => _m.Bindings.FirstOrDefault(b => b.DeviceId == _id);
    bool Asio => AsioCatalog.IsAsio(_id);
    DeviceSettingsSheet(AppModel m, string id)
    {
        _m = m; _id = id; Add(_rate); Add(_buffer); Add(_panel); Add(_remove); Add(_close);
        _rate.Text = () => Binding?.SampleRate is { } sr ? $"{sr} Hz" : "Automatisch";
        _buffer.Text = () => Binding?.BufferFrames is { } size ? $"{size} Samples" : "Treibervorgabe";
        _rate.Items = () => new[] { new MenuItem("Automatisch", null, () => Set(null, Binding?.BufferFrames)) }
            .Concat((Device?.SupportedRates ?? Array.Empty<int>()).Select(sr => new MenuItem($"{sr} Hz", null, () => Set(sr, Binding?.BufferFrames)))).ToList();
        _buffer.Items = () => new[] { new MenuItem("Treibervorgabe", null, () => Set(Binding?.SampleRate, null)) }
            .Concat((Device?.SupportedBuffers ?? Array.Empty<int>()).Select(n => new MenuItem($"{n} Samples", null, () => Set(Binding?.SampleRate, n)))).ToList();
        _panel.Clicked += () =>
        {
            try
            {
                if (Asio) AsioCatalog.ShowControlPanel(id);
                else Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl") { UseShellExecute = true });
            }
            catch (Exception e) { Root?.Notify.Flash(e.Message, true); }
        };
        _remove.Clicked += () =>
        {
            for (int i = m.Bindings.Count - 1; i >= 0; i--) if (m.Bindings[i].DeviceId == id) m.RemoveBinding(i);
            Close();
        };
        _close.Clicked += Close;
    }
    void Set(int? rate, int? size) => _m.SetDeviceOptions(_id, rate, size, Binding?.Exclusive);
    public static void Show(UiRoot r, AppModel m, string id) => r.ShowOverlay(new DeviceSettingsSheet(m, id));
    public override void Layout()
    {
        base.Layout(); _close.Bounds = new RectF(Panel.Right - 100, Panel.Y + 22, 80, 32);
        bool editable = !_m.HostRunning && !_m.HostBusy;
        _rate.Enabled = editable && Asio && Device?.SupportedRates.Count > 0;
        _buffer.Enabled = editable && Asio && Device?.SupportedBuffers.Count > 0;
        _panel.Enabled = _remove.Enabled = editable;
    }
    protected override float LayoutBody(float x, float y, float w)
    {
        _rate.Bounds = new RectF(x, y + 116, w, 36);
        _buffer.Bounds = new RectF(x, y + 190, w, 36);
        _panel.Bounds = new RectF(x, y + 264, w, 36);
        _remove.Bounds = new RectF(x, y + 360, w, 36);
        return 420;
    }
    protected override void PaintHeader(Renderer g, RectF h) => g.Text("Gerät bearbeiten", new RectF(h.X + 20, h.Y + 22, h.W - 135, 30), Theme.Title, Theme.Text);
    protected override void PaintBody(Renderer g)
    {
        float x = Body.X + 20, y = Body.Y + 4 - ScrollOffset, w = Body.W - 40;
        g.Text(Device?.Name ?? "Gerät offline", new RectF(x, y + 8, w, 48), Theme.Headline, Theme.Text, wrap: true);
        g.Text($"{(Asio ? "ASIO" : "Windows")} · {Device?.Channels ?? 0} Kanäle · {Device?.SampleRate ?? 0} Hz", new RectF(x, y + 62, w, 24), Theme.Callout, Theme.TextSecondary);
        g.Text("Abtastrate", new RectF(x, y + 90, w, 24), Theme.Body, Theme.Text);
        g.Text("Puffergröße", new RectF(x, y + 164, w, 24), Theme.Body, Theme.Text);
        g.Text(Asio ? "Die Einstellungen gelten für Ein- und Ausgänge dieses ASIO-Geräts." : "Windows verwaltet das Geräteformat. Änderungen sind in den Windows-Soundeinstellungen möglich.",
            new RectF(x, y + 310, w, 48), Theme.Caption, Theme.TextSecondary, wrap: true);
    }
}
