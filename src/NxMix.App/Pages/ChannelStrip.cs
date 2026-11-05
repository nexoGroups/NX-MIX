using NxMix.App.Model;
using NxMix.App.Ui;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Ein Kanalstreifen: Name, Laufzeit-Knopf, Mute/Solo/Phase, Fader + Meter, Gain-Sperre, Wert. (Oder der Master.)</summary>
internal sealed class ChannelStrip : Widget
{
    readonly AppModel _m;
    public readonly bool IsInput;
    public readonly bool IsMaster;
    public int Index;

    readonly NameLabel _name = new();
    readonly IconButton _more = new(IconKind.More);
    readonly IconButton _lock = new(IconKind.LockOpen);
    readonly Knob _delay = new() { Min = 0, Max = Theme.DelayMaxMs, Default = 0 };
    readonly ToggleButton _mute = new("M", Theme.Mute);
    readonly ToggleButton _solo = new("S", Theme.Solo);
    readonly ToggleButton _phase = new("Ø", Theme.Accent);
    readonly Fader _fader = new();
    readonly Meter _meter = new();
    readonly ReadoutBox _readout = new();
    readonly AppLinkBox? _link;
    bool _protected;
    double _delayMs;

    public ChannelStrip(AppModel m, bool input, int index, bool master = false)
    {
        _m = m; IsInput = input; Index = index; IsMaster = master;

        Add(_meter);
        Add(_fader);
        Add(_readout);
        if (master)
        {
            _name.Text = "Master"; _name.Editable = false; _name.Font = Theme.BodyBold;
            Add(_name);
            _fader.ScaleTop = Theme.MasterMaxDb;
            _fader.MaxDb = Theme.MasterMaxDb;
            _fader.Changed += v => _m.SetMaster(v);
            _readout.Typed += s => ApplyTyped(s, db => _m.SetMaster(Math.Min(db, Theme.MasterMaxDb)));
            _meter.ExtraTooltip = "Master: größter Ausgangspegel";
            return;
        }

        Add(_name); Add(_more); Add(_lock); Add(_delay); Add(_mute); Add(_solo); Add(_phase);
        if (input) _link = Add(new AppLinkBox(m, this));
        _name.Renamed += s => _m.UpdateChannel(IsInput, Index, c => c with { Name = s });
        _more.Tooltip = "Kanal-Menü: verschieben, löschen, Limiter";
        _more.Clicked += ShowChannelMenu;
        _lock.Clicked += ShowLockMenu;
        _lock.IconSize = 15;
        _delay.Changed += v => _m.UpdateChannel(IsInput, Index, c => c with { DelayMs = v });
        _delay.Tooltip = "Laufzeit (Delay) in ms: Ziehen, Strg/Umschalt = fein, Doppelklick = 0";
        _mute.Toggled += on => _m.UpdateChannel(IsInput, Index, c => c with { Mute = on });
        _solo.Toggled += on => _m.UpdateChannel(IsInput, Index, c => c with { Solo = on });
        _phase.Toggled += on => _m.UpdateChannel(IsInput, Index, c => c with { InvertPhase = on });
        _mute.Tooltip = "Stumm"; _solo.Tooltip = "Solo"; _phase.Tooltip = "Phase invertieren (180°)";
        _fader.Changed += v => _m.UpdateChannel(IsInput, Index, c => c with { GainDb = v });
        _readout.Typed += s => ApplyTyped(s, db => _m.UpdateChannel(IsInput, Index, c => c with { GainDb = Math.Min(db, c.MaxGainDb) }));
    }

    void ApplyTyped(string text, Action<double> apply)
    {
        if (Db.TryParse(text, out double db)) apply(db);
        else Root?.Status.Flash("Ungültiger Wert: " + text, true);
        Sync();
    }

    // ---- Zustand aus dem Modell ----------------------------------------------------------------
    public void Sync()
    {
        if (IsMaster)
        {
            double mg = _m.Settings.MasterGainDb;
            _fader.SetValue(mg);
            _readout.Text = Db.Format(mg);
            _readout.TextColor = mg > 0.05 ? Theme.Solo : Theme.Text;
            return;
        }
        if (Index < 0 || Index >= (IsInput ? _m.InputCount : _m.OutputCount)) return;
        var c = _m.Channel(IsInput, Index);
        _name.Text = c.Name;
        var link = IsInput ? _m.LinkAt(Index) : null;
        if (link != null)
        {
            // App-Link: ohne eigenen Namen zeigt der Streifen den Programmnamen (bei Stereo mit L/R)
            _name.Placeholder = link.Count == 2 ? $"{link.Title} {(Index == link.FirstChannel ? "L" : "R")}" : link.Title;
            _name.PlaceholderColor = Theme.Text;
        }
        else
        {
            _name.Placeholder = AppModel.DefaultName(IsInput, Index);
            _name.PlaceholderColor = Theme.TextFaint;
        }
        _delayMs = c.DelayMs;
        _delay.SetValue(c.DelayMs);
        _mute.SetChecked(c.Mute); _solo.SetChecked(c.Solo); _phase.SetChecked(c.InvertPhase);
        _fader.MaxDb = c.MaxGainDb;
        _fader.SetValue(c.GainDb);
        bool locked = c.MaxGainDb < Theme.FaderMaxDb - 0.05;
        _protected = AppModel.IsProtected(c);
        _lock.Icon = locked ? IconKind.Lock : IconKind.LockOpen;
        _lock.Active = locked;
        _lock.ActiveColor = Theme.Protect;
        _lock.Tooltip = locked ? $"Gain-Sperre aktiv: max. {Db.Format(c.MaxGainDb)} dB (Klick: ändern)" : "Gain-Sperre setzen (Klick)";
        _readout.Text = Db.Format(c.GainDb);
        _readout.TextColor = !Db.IsOff(c.GainDb) && c.GainDb > 0.05 ? Theme.Solo : locked && c.GainDb >= c.MaxGainDb - 0.05 ? Theme.Protect : Theme.Text;
        _meter.CeilingDb = IsInput ? double.NaN : c.CeilingDbfs;
        _meter.ExtraTooltip = IsInput ? "Eingangspegel" : $"Ausgangspegel · Limiter-Grenze {Db.Format(c.CeilingDbfs)} dBFS (Marke)";
    }

    public bool FeedMeter(float peak, float dt) => _meter.Feed(peak, dt);

    /// <summary>Nur fuer Screenshots: oeffnet Menue/Editor dieses Streifens.</summary>
    public void DebugOpen(string what)
    {
        switch (what)
        {
            case "lock": ShowLockMenu(_lock.Bounds); break;
            case "channel": ShowChannelMenu(_more.Bounds); break;
            case "rename": _name.BeginRename(); break;
            case "applink": _link?.Open(); break;
            case "applink-manual": _link?.Open(true); break;
        }
    }

    /// <summary>Bereich des App-Link-Felds (nur Eingangsstreifen) - fuer die Bruecke zwischen Stereo-Paaren.</summary>
    public RectF LinkRect => _link?.DrawRect ?? default;
    public void ResetMeter() => _meter.Reset();

    // ---- Menues --------------------------------------------------------------------------------
    void ShowChannelMenu(RectF anchor)
    {
        var root = Root; if (root == null) return;
        int count = IsInput ? _m.InputCount : _m.OutputCount;
        var c = _m.Channel(IsInput, Index);
        var items = new List<MenuItem>
        {
            new("Umbenennen", "Doppelklick", _name.BeginRename),
            new("Nach links verschieben", null, () => _m.MoveChannel(IsInput, Index, -1), Index > 0 && _m.CanMoveChannel(IsInput, Index, -1)),
            new("Nach rechts verschieben", null, () => _m.MoveChannel(IsInput, Index, +1), Index < count - 1 && _m.CanMoveChannel(IsInput, Index, +1)),
        };
        if (IsInput)
        {
            bool linked = _m.LinkAt(Index) != null;
            items.Add(MenuItem.Sep);
            items.Add(new(linked ? "App-Link ändern …" : "Programm binden (App-Link) …", null, () => _link?.Open(), !_m.HostRunning));
            if (linked) items.Add(new("Verknüpfung lösen", null, () => _m.RemoveLink(_m.LinkAt(Index)?.FirstChannel ?? -1), !_m.HostRunning));
        }
        if (!IsInput)
        {
            items.Add(MenuItem.Sep);
            items.Add(new($"Limiter-Grenze (aktuell {Db.Format(c.CeilingDbfs)} dBFS)", null, null, false));
            foreach (double v in new[] { -1.0, -3.0, -6.0, -12.0 })
            {
                double vv = v;
                items.Add(new($"   {Db.Format(vv)} dBFS", null, () => _m.UpdateChannel(false, Index, ch => ch with { CeilingDbfs = vv })) { Checked = Math.Abs(c.CeilingDbfs - vv) < 0.05 });
            }
            items.Add(new("   Eigener Wert …", null, () => EditNumber("Limiter-Grenze (dBFS)", c.CeilingDbfs, db => _m.UpdateChannel(false, Index, ch => ch with { CeilingDbfs = Math.Min(0, db) }))));
        }
        items.Add(MenuItem.Sep);
        items.Add(new MenuItem("Kanal löschen", null, () => _m.RemoveChannel(IsInput, Index)) { Danger = true });
        PopupMenu.Show(root, anchor, items, 210);
    }

    void ShowLockMenu(RectF anchor)
    {
        var root = Root; if (root == null) return;
        var c = _m.Channel(IsInput, Index);
        bool locked = c.MaxGainDb < Theme.FaderMaxDb - 0.05;
        var items = new List<MenuItem>
        {
            new(locked ? $"Gain-Sperre: max. {Db.Format(c.MaxGainDb)} dB" : "Gain-Sperre: aus", null, null, false),
            MenuItem.Sep,
        };
        foreach (double v in new[] { 0.0, -6.0, -10.0, -20.0 })
        {
            double vv = v;
            items.Add(new($"Maximal {Db.Format(vv)} dB", null, () => SetMax(vv)) { Checked = locked && Math.Abs(c.MaxGainDb - vv) < 0.05 });
        }
        items.Add(new("Eigener Wert …", null, () => EditNumber("Maximalpegel (dB)", locked ? c.MaxGainDb : 0, db => SetMax(Math.Clamp(db, -60, Theme.FaderMaxDb)))));
        items.Add(MenuItem.Sep);
        items.Add(new("Sperre aufheben", null, () => SetMax(Theme.FaderMaxDb), locked));
        PopupMenu.Show(root, anchor, items, 200);
    }

    void SetMax(double newMax)
    {
        var root = Root; if (root == null) return;
        var c = _m.Channel(IsInput, Index);
        void Apply() => _m.UpdateChannel(IsInput, Index, ch => ch with { MaxGainDb = newMax });
        bool wasLocked = c.MaxGainDb < Theme.FaderMaxDb - 0.05;
        if (wasLocked && newMax > c.MaxGainDb + 0.05)
        {
            string nm = _m.DisplayName(IsInput, Index);
            root.Confirm("Gain-Sperre lockern?",
                $"„{nm}“ darf danach bis {(newMax >= Theme.FaderMaxDb - 0.05 ? "+12 dB (keine Sperre)" : Db.Format(newMax) + " dB")} laut werden (bisher max. {Db.Format(c.MaxGainDb)} dB). Das kann Lautsprecher und Gehör gefährden.",
                "Sperre lockern", Apply, danger: true);
        }
        else Apply();
    }

    void EditNumber(string what, double current, Action<double> apply)
    {
        var root = Root; if (root == null) return;
        root.Status.Flash(what + ": Wert eintippen, Enter bestätigt");
        root.BeginEdit(_readout.Bounds.Inflate(2), Db.Format(current).Replace(Db.Minus, '-'), Theme.Value, s =>
        {
            if (Db.TryParse(s, out double v)) apply(v); else root.Status.Flash("Ungültiger Wert: " + s, true);
        }, 10, null, HAlign.Center);
    }

    // ---- Layout --------------------------------------------------------------------------------
    public override void Layout()
    {
        var b = Bounds;
        const float pad = 6f;
        float x0 = b.X + pad, w = Math.Max(10, b.W - pad * 2);
        float y = b.Y + 6;
        bool narrow = b.W < 58;

        if (IsMaster)
        {
            _name.Bounds = new RectF(x0, y + 2, w, 32);
            y += 38;
        }
        else
        {
            _more.Bounds = new RectF(b.Right - pad - 22, y - 2, 22, 22);
            y += 22;
            _name.Bounds = new RectF(x0, y, w, 32);
            y += 34;
            float kd = narrow ? 28 : 34;
            _delay.Bounds = RectF.Centered(b.CX, y + kd / 2, kd, kd);
            y += kd + 16;
            float bh = 22;
            _mute.Bounds = new RectF(x0, y, w, bh); y += bh + 4;
            _solo.Bounds = new RectF(x0, y, w, bh); y += bh + 4;
            _phase.Bounds = new RectF(x0, y, w, bh); y += bh + 8;
            // App-Link-Feld: Eingaenge zeigen es, Ausgaenge halten denselben Platz frei, damit alle Fader fluchten
            if (_link != null) _link.Bounds = new RectF(x0, y, w, AppLinkBox.SlotHeight);
            y += AppLinkBox.SlotHeight + 8;
        }

        float bottomRow = 24;
        float by = b.Bottom - pad - bottomRow;
        if (IsMaster) _readout.Bounds = new RectF(x0, by, w, bottomRow);
        else
        {
            _lock.Bounds = new RectF(x0, by, 22, bottomRow);
            _readout.Bounds = new RectF(x0 + 24, by, Math.Max(10, w - 24), bottomRow);
        }

        float fy = y, fh = Math.Max(20, by - 8 - fy);
        float mw = Theme.MeterWidth;
        _meter.Bounds = new RectF(b.Right - pad - mw - 1, fy + 6, mw, Math.Max(10, fh - 12));
        _fader.ShowLabels = b.W >= 60;
        _fader.Bounds = new RectF(x0 - 2, fy, Math.Max(10, _meter.Bounds.X - x0 - 2), fh);
        foreach (var c in Children) c.Layout();
    }

    // ---- Zeichnen ------------------------------------------------------------------------------
    public override void Paint(Renderer g)
    {
        var b = Bounds;
        g.FillRound(b, Theme.RadiusCard, IsMaster ? Theme.Rgb(0x1E2228) : Theme.Card);
        g.StrokeRound(b, Theme.RadiusCard, IsMaster ? Theme.BorderStrong : Theme.Border);
        if (_protected)
        {
            g.FillRound(new RectF(b.X + 10, b.Y, b.W - 20, 2.5f), 1.2f, Theme.Protect);
        }
        if (!IsMaster)
        {
            g.Text((Index + 1).ToString("00"), new RectF(b.X + 8, b.Y + 4, 30, 18), Theme.TinyBold, Theme.TextFaint, HAlign.Left, VAlign.Middle, false);
            // Laufzeit unter dem Knopf
            var kr = _delay.Bounds;
            g.Text(_delayMs <= 0.004 ? "0 ms" : $"{_delayMs:0.##} ms", new RectF(b.X + 2, kr.Bottom + 1, b.W - 4, 14), Theme.Tiny,
                _delayMs > 0.004 ? Theme.Accent : Theme.TextFaint, HAlign.Center, VAlign.Middle, false);
        }
        PaintChildren(g);
    }
}
