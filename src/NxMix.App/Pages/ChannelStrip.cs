using NxMix.App.Model;
using NxMix.App.Ui;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>
/// Kopf eines Kanalstreifens (wie der "Scribble Strip" bei Mixing Station): Nummer + Name, farbige Marke oben.
/// Klick oeffnet das Kanal-Blatt mit allen Einstellungen.
/// </summary>
internal sealed class StripHeader : Widget
{
    readonly ChannelStrip _strip;
    public string Number = "";
    public string Name = "";
    public bool NamePlaceholder;
    public Color4? Mark;

    public StripHeader(ChannelStrip strip) { _strip = strip; Cursor = CursorKind.Hand; }

    public override bool AcceptsMouse => Visible;
    public override CursorKind CursorAt(float x, float y) => _strip.IsMaster ? CursorKind.Arrow : CursorKind.Hand;
    public override void OnMouseEnter() => Invalidate();
    public override void OnMouseLeave() => Invalidate();
    public override void OnMouseDown(in MouseEvent e) => Invalidate();
    public override void OnMouseUp(in MouseEvent e)
    {
        Invalidate();
        if (_strip.IsMaster || !Bounds.Contains(e.X, e.Y)) return;
        if (e.Button == 0 || e.Button == 1) _strip.OpenSheet();
    }

    public override string? TooltipAt(float x, float y) => _strip.IsMaster ? null : "Kanal-Einstellungen öffnen";

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        bool sel = _strip.Selected;
        bool hot = IsHovered && !_strip.IsMaster;
        var bg = sel ? Theme.AccentSoft : hot ? Theme.ControlHover : Theme.Control;
        g.FillRound(b, Theme.RadiusControl, bg);
        if (sel) g.StrokeRound(b, Theme.RadiusControl, Theme.Accent, 1.5f);
        if (Mark is Color4 mk) g.FillPill(new RectF(b.CX - b.W * 0.3f, b.Y + 4, b.W * 0.6f, 3), mk);
        float y = b.Y + 7;
        if (Number.Length > 0)
        {
            g.Text(Number, new RectF(b.X + 4, y, b.W - 8, 13), Theme.MicroStrong, sel ? Theme.AccentText : Theme.TextTertiary, HAlign.Center, VAlign.Middle, false);
            y += 13;
        }
        var nr = RectF.FromLTRB(b.X + 5, y, b.Right - 5, b.Bottom - 4);
        var font = _strip.IsMaster ? Theme.BodyStrong : (b.W < 80 ? Theme.CaptionStrong : Theme.CalloutStrong);
        g.Text(Name, nr, font, NamePlaceholder ? Theme.TextTertiary : Theme.Text, HAlign.Center, VAlign.Middle, true, true);
    }
}

/// <summary>Ein Kanalstreifen: Kopf (Klick = Einstellungen), Zustandssymbole, Mute/Solo, Fader + Meter, Wert. Oder der Master.</summary>
internal sealed class ChannelStrip : Widget
{
    readonly AppModel _m;
    public readonly bool IsInput;
    public readonly bool IsMaster;
    public int Index;

    public readonly StripHeader Header;
    public readonly Toggle Mute = new("M", () => Theme.Red);
    public readonly Toggle Solo = new("S", () => Theme.Yellow) { ActiveText = () => Theme.Rgb(0x1A1B1E) };
    public readonly Button Eq = new("EQ", ButtonStyle.Gray) { Font = Theme.CaptionStrong, Tooltip = "EQ und Live-Spektrum öffnen" };
    public readonly Fader Fader = new();
    public readonly Meter Meter = new();
    public readonly ValueBox Readout = new();

    // Zustandssymbole unter dem Kopf
    readonly List<(Icon Icon, Color4 Color, string? Text, string Tip)> _marks = new();
    RectF _marksRect;

    public ChannelStrip(AppModel m, bool input, int index, bool master = false)
    {
        _m = m; IsInput = input; Index = index; IsMaster = master;
        Header = new StripHeader(this);
        Add(Header);
        Add(Meter);
        Add(Fader);
        Add(Readout);
        if (master)
        {
            Header.Name = "Master";
            Fader.ScaleTop = Theme.MasterMaxDb;
            Fader.MaxDb = Theme.MasterMaxDb;
            Fader.Changed += v => _m.SetMaster(v);
            Readout.Typed += s => ApplyTyped(s, db => _m.SetMaster(Math.Min(db, Theme.MasterMaxDb)));
            Meter.ExtraTooltip = "Master: größter Ausgangspegel";
            return;
        }
        Add(Mute); Add(Solo); Add(Eq);
        Eq.Clicked += () => OpenSheet(eq: true);
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

    /// <summary>Das Kanal-Blatt dieses Streifens ist offen.</summary>
    public bool Selected => !IsMaster && Root != null && Root.Overlays.Any(o => o is ChannelSheet s && s.IsInput == IsInput && s.Index == Index);

    public void OpenSheet(bool eq = false)
    {
        var root = Root; if (root == null || IsMaster) return;
        ChannelSheet.Show(root, _m, IsInput, Index, eq);
    }

    // ---- Zustand aus dem Modell ----------------------------------------------------------------
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
        if (!string.IsNullOrWhiteSpace(c.Name)) { Header.Name = c.Name; Header.NamePlaceholder = false; }
        else if (link != null) { Header.Name = link.Count == 2 ? $"{link.Title} {(Index == link.FirstChannel ? "L" : "R")}" : link.Title; Header.NamePlaceholder = false; }
        else { Header.Name = AppModel.DefaultName(IsInput, Index); Header.NamePlaceholder = true; }

        bool locked = c.MaxGainDb < Theme.FaderMaxDb - 0.05;
        Header.Mark = locked ? Theme.Orange : link != null ? LinkLook.Color(_m.ViewOf(link).State) : null;

        _marks.Clear();
        if (link != null)
        {
            var v = _m.ViewOf(link);
            _marks.Add((Icon.Link, LinkLook.Color(v.State), null, "App-Link: " + LinkLook.Describe(link, v)));
        }
        if (locked) _marks.Add((Icon.Lock, Theme.Orange, null, $"Gain-Sperre: max. {Db.Format(c.MaxGainDb)} dB"));
        if (c.DelayMs > 0.004) _marks.Add((Icon.Clock, Theme.AccentText, $"{c.DelayMs:0.##}", $"Laufzeit: {c.DelayMs:0.##} ms"));
        if (c.InvertPhase) _marks.Add((Icon.Phase, Theme.AccentText, null, "Phase invertiert (180°)"));
        if (!IsInput && Math.Abs(c.CeilingDbfs + 1.0) > 0.05) _marks.Add((Icon.Shield, Theme.TextSecondary, null, $"Limiter-Grenze {Db.Format(c.CeilingDbfs)} dBFS"));

        Mute.SetChecked(c.Mute); Solo.SetChecked(c.Solo);
        Fader.MaxDb = c.MaxGainDb;
        Fader.SetValue(c.GainDb);
        Readout.Text = Db.Format(c.GainDb);
        Readout.TextColor = !Db.IsOff(c.GainDb) && c.GainDb > 0.05 ? Theme.Yellow : locked && c.GainDb >= c.MaxGainDb - 0.05 ? Theme.Orange : null;
        Meter.CeilingDb = IsInput ? double.NaN : c.CeilingDbfs;
        Meter.ExtraTooltip = IsInput ? "Eingangspegel" : $"Ausgangspegel · Limiter-Grenze {Db.Format(c.CeilingDbfs)} dBFS (Marke)";
    }

    public bool FeedMeter(float peak, float dt) => Meter.Feed(peak, dt);
    public void ResetMeter() => Meter.Reset();

    public override bool AcceptsMouse => true;
    public override string? TooltipAt(float x, float y)
    {
        if (_marksRect.Contains(x, y) && _marks.Count > 0) return string.Join("\n", _marks.Select(m => m.Tip));
        return null;
    }

    // ---- Layout --------------------------------------------------------------------------------
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
            _marksRect = new RectF(x0, y, w, 18);
            y += 22;
            float bh = Theme.MuteSoloH;
            if (w >= 2 * 28 + 4)
            {
                float hw = (w - 4) / 2;
                Mute.Bounds = new RectF(x0, y, hw, bh);
                Solo.Bounds = new RectF(x0 + hw + 4, y, hw, bh);
                y += bh + 8;
            }
            else
            {
                Mute.Bounds = new RectF(x0, y, w, bh); y += bh + 4;
                Solo.Bounds = new RectF(x0, y, w, bh); y += bh + 8;
            }
            Eq.Bounds = new RectF(x0, y, w, Theme.ControlHSmall);
            y = Eq.Bounds.Bottom + 6;
        }
        else y += 8;

        float rh = Theme.Touch ? 32 : 26;
        float by = b.Bottom - pad - rh;
        Readout.Bounds = new RectF(x0, by, w, rh);

        float fy = y, fh = Math.Max(20, by - 8 - fy);
        float mw = Theme.MeterW;
        Meter.Bounds = new RectF(b.Right - pad - mw - 2, fy + 4, mw, Math.Max(10, fh - 8));
        Fader.ShowLabels = b.W >= 66;
        Fader.Bounds = new RectF(x0, fy, Math.Max(10, Meter.Bounds.X - x0 - 4), fh);
        foreach (var c in Children) c.Layout();
    }

    // ---- Zeichnen ------------------------------------------------------------------------------
    public override void Paint(Renderer g)
    {
        var b = Bounds;
        g.FillRound(b, Theme.RadiusInner, IsMaster ? Theme.Surface2 : Theme.Surface2);
        if (Selected) g.StrokeRound(b, Theme.RadiusInner, Theme.Accent.With(0.6f), 1.5f);
        if (!IsMaster && _marks.Count > 0) PaintMarks(g);
        PaintChildren(g);
    }

    void PaintMarks(Renderer g)
    {
        var r = _marksRect;
        const float iconS = 13f, gap = 6f;
        float total = 0;
        foreach (var m in _marks) total += iconS + (m.Text != null ? Renderer.MeasureWidth(m.Text, Theme.Micro) + 2 : 0) + gap;
        total -= gap;
        bool texts = total <= r.W;
        if (!texts) { total = _marks.Count * (iconS + gap) - gap; }
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
