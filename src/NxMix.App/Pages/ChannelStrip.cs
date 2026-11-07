using NxMix.App.Model;
using NxMix.App.Ui;
using NxMix.Engine;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Scribble-Strip-Kopf eines Kanalstreifens: Nummer, Name, Gerätemarke. Klick öffnet das Kanal-Blatt.</summary>
internal sealed class StripHeader : Widget
{
    readonly ChannelStrip _strip;
    public string Number = "";
    public string Name = "";
    public bool NamePlaceholder;
    public Color4? Mark;
    Motion _selection;

    public StripHeader(ChannelStrip strip) { _strip = strip; Cursor = CursorKind.Hand; }

    public override bool AcceptsMouse => Visible;
    public override CursorKind CursorAt(float x, float y) => _strip.IsMaster ? CursorKind.Arrow : CursorKind.Hand;
    public override void OnMouseEnter() => Invalidate();
    public override void OnMouseLeave() => Invalidate();
    public override void OnMouseDown(in MouseEvent e) => Invalidate();
    public override void OnMouseUp(in MouseEvent e)
    {
        Invalidate();
        if (_strip.IsMaster || !HitOnRelease(e)) return;
        if (e.Button is 0 or 1) _strip.OpenSheet();
    }

    public override string? TooltipAt(float x, float y) => _strip.IsMaster ? null : "Kanal-Einstellungen öffnen";

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        float selected = _selection.At(Root, _strip.Selected ? 1 : 0, Motion.Selection);
        float hover = HoverAmount(IsHovered && !_strip.IsMaster);
        var bg = Theme.Mix(Theme.Mix(Theme.Control, Theme.ControlHover, hover), Theme.AccentSoft, selected);
        g.FillRound(b, Theme.RadiusControl, bg);
        if (selected > 0) g.StrokeRound(b, Theme.RadiusControl, Theme.Accent.With(selected), 1.5f);
        if (Mark is Color4 mk) g.FillPill(new RectF(b.CX - b.W * 0.3f, b.Y + 4, b.W * 0.6f, 3), mk);
        float y = b.Y + 7;
        if (Number.Length > 0)
        {
            g.Text(Number, new RectF(b.X + 3, y, b.W - 6, 13), Theme.MicroStrong,
                Theme.Mix(Theme.TextTertiary, Theme.AccentText, selected), HAlign.Center, VAlign.Middle, false);
            y += 13;
        }
        var nr = RectF.FromLTRB(b.X + 4, y, b.Right - 4, b.Bottom - 4);
        var font = _strip.IsMaster ? Theme.BodyStrong : (b.W < 76 ? Theme.CaptionStrong : Theme.CalloutStrong);
        g.Text(Name, nr, font, NamePlaceholder ? Theme.TextTertiary : Theme.Text, HAlign.Center, VAlign.Middle, true, true);
    }
}

/// <summary>Einzelkanal-Streifen (Eingang, Ausgang oder Global-Master).
/// Eingänge zeigen ehrlich „Kanal / HPF“ (kein leerer Input-EQ!), Ausgänge öffnen mit „DSP“ direkt den Tab Outputs / DSP.</summary>
internal sealed class ChannelStrip : Widget
{
    readonly AppModel _m;
    public readonly bool IsInput;
    public readonly bool IsMaster;
    public int Index;
    public string? CustomDeckLabel;

    public readonly StripHeader Header;
    public readonly Toggle Mute = new("M", () => Theme.Red);
    public readonly Toggle Solo = new("S", () => Theme.Yellow) { ActiveText = () => Theme.Rgb(0x1A1B1E) };
    public readonly Button ActionBtn;
    public readonly Fader Fader = new();
    public readonly Meter Meter = new();
    public readonly ValueBox Readout = new();

    readonly List<(Icon Icon, Color4 Color, string? Text, string Tip)> _marks = new();
    RectF _marksRect;

    public ChannelStrip(AppModel m, bool input, int index, bool master = false)
    {
        _m = m; IsInput = input; Index = index; IsMaster = master;
        Header = Add(new StripHeader(this));
        Add(Meter);
        Add(Fader);
        Add(Readout);

        ActionBtn = input
            ? new Button("Kanal", ButtonStyle.Gray) { Font = Theme.CaptionStrong, Tooltip = "Trim, Low-Cut (HPF), Pan, Laufzeit & App-Link" }
            : new Button("DSP", ButtonStyle.Tinted) { Font = Theme.CaptionStrong, Tooltip = "Ausgangs-DSP (Crossover, PEQ, REW & Limiter) öffnen" };

        if (master)
        {
            Header.Name = "Master";
            Fader.ScaleTop = Theme.MasterMaxDb;
            Fader.MaxDb = Theme.MasterMaxDb;
            Fader.Changed += v => _m.SetMaster(v);
            Readout.Typed += s => ApplyTyped(s, db => _m.SetMaster(Math.Min(db, Theme.MasterMaxDb)));
            Meter.ExtraTooltip = "Globaler Summen-Master";
            return;
        }

        Add(Mute); Add(Solo); Add(ActionBtn);
        ActionBtn.Clicked += () =>
        {
            if (IsInput) OpenSheet();
            else Root?.OpenOutputDsp(Index);
        };
        Mute.Tooltip = "Stumm"; Solo.Tooltip = "Solo";
        Mute.Toggled += on => _m.UpdateChannel(IsInput, Index, c => c with { Mute = on });
        Solo.Toggled += on => _m.UpdateChannel(IsInput, Index, c => c with { Solo = on });
        Fader.Changed += v => _m.UpdateChannel(IsInput, Index, c => c with { GainDb = v });
        Readout.Typed += s => ApplyTyped(s, db => _m.UpdateChannel(IsInput, Index, c => c with { GainDb = Math.Min(db, c.MaxGainDb) }));
    }

    void ApplyTyped(string text, Action<double> apply)
    {
        if (Db.TryParse(text, out double db)) apply(db);
        else Root?.Notify.Flash("Ungültiger Wert: " + text, true);
        Sync();
    }

    public bool Selected => !IsMaster && Root != null && Root.Overlays.Any(o => o is ChannelSheet s && s.IsInput == IsInput && s.Index == Index);

    public void OpenSheet()
    {
        var root = Root; if (root == null || IsMaster) return;
        ChannelSheet.Show(root, _m, IsInput, Index);
    }

    public void Sync()
    {
        if (IsMaster)
        {
            double mg = _m.Settings.MasterGainDb;
            Fader.SetValue(mg);
            Readout.Text = Db.Format(mg);
            Readout.TextColor = mg > 0.05 ? Theme.Yellow : null;
            return;
        }
        if (Index < 0 || Index >= (IsInput ? _m.InputCount : _m.OutputCount)) return;
        var c = _m.Channel(IsInput, Index);
        var link = IsInput ? _m.LinkAt(Index) : null;
        Header.Number = (IsInput ? "IN " : "OUT ") + (Index + 1).ToString("00");

        if (!string.IsNullOrWhiteSpace(CustomDeckLabel)) { Header.Name = CustomDeckLabel!; Header.NamePlaceholder = false; }
        else if (!string.IsNullOrWhiteSpace(c.Name)) { Header.Name = c.Name; Header.NamePlaceholder = false; }
        else if (link != null) { Header.Name = link.Count == 2 ? $"{link.Title} {(Index == link.FirstChannel ? "L" : "R")}" : link.Title; Header.NamePlaceholder = false; }
        else { Header.Name = AppModel.DefaultName(IsInput, Index); Header.NamePlaceholder = true; }

        bool locked = c.MaxGainDb < Theme.FaderMaxDb - 0.05;
        bool hasDsp = (c.Filters.Count > 0 && c.Filters.Any(f => f.Enabled)) || c.Correction is { Bypassed: false };
        Header.Mark = locked ? Theme.Orange : link != null ? LinkLook.Color(_m.ViewOf(link).State) : c.LinkGroupId.HasValue ? Theme.Teal : null;

        if (IsInput)
            ActionBtn.Text = hasDsp ? "HPF an" : "Kanal";
        else
            ActionBtn.Style = hasDsp ? ButtonStyle.Tinted : ButtonStyle.Gray;

        _marks.Clear();
        if (link != null)
        {
            var v = _m.ViewOf(link);
            _marks.Add((Icon.Link, LinkLook.Color(v.State), null, "App-Link: " + LinkLook.Describe(link, v)));
        }
        else if (c.LinkGroupId.HasValue)
        {
            _marks.Add((Icon.Link, Theme.Teal, null, "Stereo-/Gruppen-Kopplung aktiv (relativer Pegelabstand bleibt erhalten)"));
        }
        if (locked) _marks.Add((Icon.Lock, Theme.Orange, null, $"Gain-Sperre: max. {Db.Format(c.MaxGainDb)} dB"));
        if (c.RecallSafe != RecallSafeFlags.None) _marks.Add((Icon.Shield, Theme.Green, null, $"Recall-Safe geschützt ({c.RecallSafe})"));
        if (c.DelayMs > 0.004) _marks.Add((Icon.Clock, Theme.AccentText, $"{c.DelayMs:0.#}", $"Laufzeit: {c.DelayMs:0.##} ms"));
        if (c.InvertPhase) _marks.Add((Icon.Phase, Theme.AccentText, null, "Phase invertiert (180°)"));

        Mute.SetChecked(c.Mute); Solo.SetChecked(c.Solo);
        Fader.MaxDb = c.MaxGainDb;
        Fader.SetValue(c.GainDb);
        Readout.Text = Db.Format(c.GainDb);
        Readout.TextColor = !Db.IsOff(c.GainDb) && c.GainDb > 0.05 ? Theme.Yellow : locked && c.GainDb >= c.MaxGainDb - 0.05 ? Theme.Orange : null;
        Meter.CeilingDb = IsInput ? double.NaN : c.CeilingDbfs;
        Meter.ExtraTooltip = IsInput ? "Eingangspegel" : $"Ausgangspegel · Limiter {Db.Format(c.CeilingDbfs)} dBFS";
    }

    public bool FeedMeter(float peak, float dt) => Meter.Feed(peak, dt);
    public void ResetMeter() => Meter.Reset();

    public override bool AcceptsMouse => true;
    public override string? TooltipAt(float x, float y) =>
        _marksRect.Contains(x, y) && _marks.Count > 0 ? string.Join("\n", _marks.Select(m => m.Tip)) : null;

    public override void Layout()
    {
        var b = Bounds;
        float pad = 6f;
        float x0 = b.X + pad, w = Math.Max(10, b.W - pad * 2);
        float y = b.Y + pad;
        Header.Bounds = new RectF(x0, y, w, IsMaster ? 40 : Theme.StripHeaderH);
        y = Header.Bounds.Bottom + 4;
        if (!IsMaster)
        {
            _marksRect = new RectF(x0, y, w, 16);
            y += 20;
            float bh = Theme.MuteSoloH;
            if (w >= 2 * 24 + 4)
            {
                float hw = (w - 4) / 2;
                Mute.Bounds = new RectF(x0, y, hw, bh);
                Solo.Bounds = new RectF(x0 + hw + 4, y, hw, bh);
                y += bh + 6;
            }
            else
            {
                Mute.Bounds = new RectF(x0, y, w, bh); y += bh + 4;
                Solo.Bounds = new RectF(x0, y, w, bh); y += bh + 6;
            }
            ActionBtn.Bounds = new RectF(x0, y, w, Theme.ControlHSmall);
            y = ActionBtn.Bounds.Bottom + 6;
        }
        else y += 8;

        float rh = Theme.Touch ? 32 : 26;
        float by = b.Bottom - pad - rh;
        Readout.Bounds = new RectF(x0, by, w, rh);

        float fy = y, fh = Math.Max(20, by - 8 - fy);
        float mw = Theme.MeterW;
        Meter.Bounds = new RectF(b.Right - pad - mw - 2, fy + 4, mw, Math.Max(10, fh - 8));
        Fader.ShowLabels = b.W >= 68;
        Fader.Bounds = new RectF(x0, fy, Math.Max(10, Meter.Bounds.X - x0 - 4), fh);
        foreach (var c in Children) c.Layout();
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        g.FillRound(b, Theme.RadiusInner, Theme.Surface2);
        if (Selected) g.StrokeRound(b, Theme.RadiusInner, Theme.Accent.With(0.6f), 1.5f);
        if (!IsMaster && _marks.Count > 0) PaintMarks(g);
        PaintChildren(g);
    }

    void PaintMarks(Renderer g)
    {
        var r = _marksRect;
        const float iconS = 12.5f, gap = 5f;
        float total = 0;
        foreach (var m in _marks) total += iconS + (m.Text != null ? Renderer.MeasureWidth(m.Text, Theme.Micro) + 2 : 0) + gap;
        total -= gap;
        bool texts = total <= r.W;
        if (!texts) total = _marks.Count * (iconS + gap) - gap;
        float x = r.CX - Math.Min(total, r.W) / 2;
        foreach (var m in _marks)
        {
            if (x + iconS > r.Right + 1) break;
            Icons.Draw(g, m.Icon, x + iconS / 2, r.CY, iconS, m.Color);
            x += iconS;
            if (texts && m.Text != null)
            {
                float tw = Renderer.MeasureWidth(m.Text, Theme.Micro);
                g.Text(m.Text, new RectF(x + 1, r.Y, tw + 2, r.H), Theme.Micro, m.Color, HAlign.Left, VAlign.Middle, false);
                x += tw + 2;
            }
            x += gap;
        }
    }
}

/// <summary>Geräte-Summenstreifen (DeviceMasterStrip): steuert die eigenständige Master-Gain-Stufe eines mehrkanaligen
/// Hardware-Geräts oder VAIO-Slots, OHNE die gespeicherten Einzelkanal-Faderwerte (ChannelDsp.GainDb) zu überschreiben!
/// Über den Ausklapp-Knopf lassen sich die zugeordneten Einzelkanäle inline ein- und ausblenden.</summary>
internal sealed class DeviceMasterStrip : Widget
{
    readonly AppModel _m;
    public readonly Guid DeviceId;
    public readonly bool IsInput;
    readonly string _fallbackTitle;
    readonly int _memberCount;
    readonly Action _onToggleExpand;
    readonly Func<bool> _isExpanded;

    public readonly Toggle Mute = new("M", () => Theme.Red) { Tooltip = "Gerät stummschalten" };
    public readonly Toggle Dim = new("DIM", () => Theme.Orange) { Font = Theme.MicroStrong, Tooltip = "Dim (−20 dB Absenkung)" };
    public readonly Button ExpandBtn;
    public readonly Fader Fader = new();
    public readonly Meter Meter = new();
    public readonly ValueBox Readout = new();

    public DeviceMasterStrip(AppModel m, Guid deviceId, string title, bool isInput, int memberCount, Func<bool> isExpanded, Action onToggleExpand)
    {
        _m = m;
        DeviceId = deviceId;
        IsInput = isInput;
        _fallbackTitle = title;
        _memberCount = memberCount;
        _isExpanded = isExpanded;
        _onToggleExpand = onToggleExpand;

        ExpandBtn = new Button("", ButtonStyle.Tinted)
        {
            Font = Theme.MicroStrong,
            Tooltip = "Einzelkanäle dieses Geräts aus-/einklappen (relative Einzelkanal-Pegel bleiben erhalten)",
        };
        ExpandBtn.Clicked += () => _onToggleExpand();

        Add(Mute); Add(Dim); Add(ExpandBtn); Add(Meter); Add(Fader); Add(Readout);

        Fader.ScaleTop = Theme.FaderMaxDb;
        Fader.MaxDb = Theme.FaderMaxDb;
        Mute.Toggled += on => _m.UpdateDeviceMaster(DeviceId, d => d with { Mute = on }, _fallbackTitle, IsInput);
        Dim.Toggled += on => _m.UpdateDeviceMaster(DeviceId, d => d with { Dim = on }, _fallbackTitle, IsInput);
        Fader.Changed += v => _m.UpdateDeviceMaster(DeviceId, d => d with { GainDb = v }, _fallbackTitle, IsInput);
        Readout.Typed += text =>
        {
            if (Db.TryParse(text, out double db))
                _m.UpdateDeviceMaster(DeviceId, d => d with { GainDb = Math.Clamp(db, -120, Theme.FaderMaxDb) }, _fallbackTitle, IsInput);
            Sync();
        };
    }

    public void Sync()
    {
        var dm = _m.GetDeviceMaster(DeviceId);
        double gain = dm?.GainDb ?? 0.0;
        Mute.SetChecked(dm?.Mute ?? false);
        Dim.SetChecked(dm?.Dim ?? false);
        Fader.SetValue(gain);
        Readout.Text = Db.Format(gain);
        Readout.TextColor = dm?.Dim == true ? Theme.Orange : gain > 0.05 ? Theme.Yellow : null;
        bool exp = _isExpanded();
        ExpandBtn.Text = _memberCount > 0 ? (exp ? $"▾ {_memberCount} CH" : $"▸ {_memberCount} CH") : "Gerät";
        ExpandBtn.Style = exp ? ButtonStyle.Filled : ButtonStyle.Tinted;
        ExpandBtn.Enabled = _memberCount > 0;
        Meter.ExtraTooltip = $"Geräte-Summenpegel ({_fallbackTitle})";
    }

    public bool FeedMeter(float peak, float dt) => Meter.Feed(peak, dt);

    public override void Layout()
    {
        var b = Bounds;
        float pad = 6f;
        float x0 = b.X + pad, w = Math.Max(10, b.W - pad * 2);
        float y = b.Y + pad + Theme.StripHeaderH + 24;

        float bh = Theme.MuteSoloH;
        float hw = (w - 4) / 2;
        Mute.Bounds = new RectF(x0, y, hw, bh);
        Dim.Bounds = new RectF(x0 + hw + 4, y, hw, bh);
        y += bh + 6;

        ExpandBtn.Bounds = new RectF(x0, y, w, Theme.ControlHSmall);
        y = ExpandBtn.Bounds.Bottom + 6;

        float rh = Theme.Touch ? 32 : 26;
        float by = b.Bottom - pad - rh;
        Readout.Bounds = new RectF(x0, by, w, rh);

        float fy = y, fh = Math.Max(20, by - 8 - fy);
        float mw = Theme.MeterW;
        Meter.Bounds = new RectF(b.Right - pad - mw - 2, fy + 4, mw, Math.Max(10, fh - 8));
        Fader.ShowLabels = b.W >= 68;
        Fader.Bounds = new RectF(x0, fy, Math.Max(10, Meter.Bounds.X - x0 - 4), fh);
        foreach (var c in Children) c.Layout();
    }

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        var accent = IsInput ? Theme.Teal : Theme.Accent;
        g.FillRound(b, Theme.RadiusInner, Theme.Surface2);
        g.StrokeRound(b, Theme.RadiusInner, accent.With(0.45f), 1.25f);

        var head = new RectF(b.X + 6, b.Y + 6, b.W - 12, Theme.StripHeaderH);
        g.FillRound(head, Theme.RadiusControl, Theme.Soft(accent));
        g.FillPill(new RectF(head.CX - head.W * 0.32f, head.Y + 4, head.W * 0.64f, 3), accent);
        g.Text(IsInput ? "DEV IN" : "DEV OUT", new RectF(head.X + 4, head.Y + 7, head.W - 8, 13), Theme.MicroStrong, accent, HAlign.Center);

        var dm = _m.GetDeviceMaster(DeviceId);
        string title = !string.IsNullOrWhiteSpace(dm?.Name) ? dm!.Name : _fallbackTitle;
        g.Text(title, new RectF(head.X + 4, head.Y + 20, head.W - 8, head.H - 23),
            b.W < 76 ? Theme.CaptionStrong : Theme.CalloutStrong, Theme.Text, HAlign.Center, VAlign.Middle, true, true);

        g.Text("MASTER", new RectF(b.X + 6, head.Bottom + 3, b.W - 12, 16), Theme.MicroStrong, Theme.TextTertiary, HAlign.Center);
        PaintChildren(g);
    }
}
