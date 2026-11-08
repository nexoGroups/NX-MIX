using Vortice.Mathematics;

namespace NxMix.App.Ui;

/// <summary>
/// Senkrechter Fader mit dB-Skala. Strg/Umschalt = Feinjustierung, Mausrad = 1 dB (fein 0,1 dB).
/// MaxDb ist die Gain-Sperre: darueber laesst er sich nicht ziehen; der gesperrte Bereich ist orange markiert.
/// </summary>
internal sealed class Fader : Widget
{
    public double Value { get; private set; }
    /// <summary>Gain-Sperre (ChannelDsp.MaxGainDb). Liegt sie unter dem Skalenende, wird sie markiert.</summary>
    public double MaxDb { get; set; } = Theme.FaderMaxDb;
    public double ScaleTop { get; set; } = Theme.FaderMaxDb;
    public bool ShowLabels { get; set; } = true;
    public event Action<double>? Changed;

    static double Quant(double db) => Math.Round(db * 10.0) / 10.0;

    float _dragY0;
    int _wheelRemainder;
    double _dragT0;
    bool _lastFine, _dragging;

    public Fader() { Cursor = CursorKind.Hand; }
    public override bool AcceptsMouse => Visible;

    public void SetValue(double db)
    {
        if (!Db.IsOff(db) && db > MaxDb) db = MaxDb;
        if (!Value.Equals(db)) { Value = db; Invalidate(); }
    }

    // ---- Geometrie -----------------------------------------------------------------------------
    float LabelW => ShowLabels ? 20f : 0f;
    float CapW => Math.Min(Theme.FaderCapMaxW, Math.Max(16f, Bounds.W - LabelW - 6f));
    static float CapH => Theme.FaderCapH;
    float TrackX => Bounds.X + LabelW + 4f + CapW / 2f;
    float YTop => Bounds.Y + CapH / 2f + 1f;
    float YBot => Bounds.Bottom - CapH / 2f - 1f;

    double TopScaleT => FaderScale.ToT(ScaleTop);
    float YOfT(double t) => (float)(YBot - Math.Clamp(t / TopScaleT, 0, 1) * (YBot - YTop));
    double TOfY(float y) => Math.Clamp((YBot - y) / (YBot - YTop), 0, 1) * TopScaleT;
    float YOfDb(double db) => YOfT(FaderScale.ToT(db));

    double ClampDb(double db) => Db.IsOff(db) ? double.NegativeInfinity : Math.Min(db, MaxDb);

    void Apply(double db)
    {
        db = ClampDb(db);
        if (Value.Equals(db)) return;
        Value = db;
        Invalidate();
        Changed?.Invoke(db);
    }

    // ---- Maus ----------------------------------------------------------------------------------
    public override void OnMouseDown(in MouseEvent e)
    {
        if (e.Button != 0 || !Enabled) return;
        _dragging = true;
        float capY = YOfDb(Value);
        bool onCap = Math.Abs(e.Y - capY) <= CapH / 2f + 1;
        double t = onCap ? FaderScale.ToT(Value) : TOfY(e.Y);
        if (!onCap) SetFromT(t, e.Fine);
        _dragY0 = e.Y; _dragT0 = t; _lastFine = e.Fine;
        // Klick auf die Kappe: nicht springen, relativ ziehen
        if (onCap) _dragT0 = FaderScale.ToT(Value);
        Invalidate();
    }

    void SetFromT(double t, bool fine)
    {
        double db = FaderScale.ToDb(t);
        if (!Db.IsOff(db) && !fine && Math.Abs(db) < 0.45) db = 0.0; // Rastpunkt bei 0 dB
        Apply(Db.IsOff(db) ? db : Quant(db));
    }

    public override void OnMouseMove(in MouseEvent e)
    {
        if (!_dragging || !IsPressed || !Enabled) return;
        if (e.Fine != _lastFine) { _dragY0 = e.Y; _dragT0 = FaderScale.ToT(Value); _lastFine = e.Fine; }
        double k = e.Fine ? 0.1 : 1.0;
        double t = _dragT0 + (-(e.Y - _dragY0) / (YBot - YTop)) * TopScaleT * k;
        SetFromT(Math.Clamp(t, 0, TopScaleT), e.Fine);
    }

    public override void OnMouseUp(in MouseEvent e) { _dragging = false; Invalidate(); }

    public override bool OnMouseWheel(in MouseEvent e)
    {
        if (!Enabled) return true;
        double step = e.Fine ? 0.1 : 1.0;
        const int unitsPerNotch = 120;
        _wheelRemainder += (int)MathF.Round(e.Wheel * unitsPerNotch);
        int notches = _wheelRemainder / unitsPerNotch;
        _wheelRemainder %= unitsPerNotch;
        if (notches == 0 || (Db.IsOff(Value) && notches < 0)) return true;

        double v = Db.IsOff(Value) ? Theme.FaderMinDb + step * (notches - 1) : Value + step * notches;
        if (v < Theme.FaderMinDb) v = double.NegativeInfinity;
        Apply(Db.IsOff(v) ? v : Quant(v));
        return true;
    }

    public override string? TooltipAt(float x, float y)
    {
        const string wheel = "Mausrad ±1 dB · Strg/Umschalt ±0,1 dB";
        return MaxDb < ScaleTop - 0.05 ? $"Gain-Sperre: max. {Db.Format(MaxDb)} dB · {wheel}" : wheel;
    }

    // ---- Zeichnen ------------------------------------------------------------------------------
    public override void Paint(Renderer g)
    {
        var b = Bounds;
        float tx = TrackX;
        float slotW = Theme.Touch ? 5f : 4f;
        var slot = new RectF(tx - slotW / 2, YTop - 4, slotW, YBot - YTop + 8);
        bool limited = MaxDb < ScaleTop - 0.05;

        // Skala links der Kappe
        float tickR = tx - CapW / 2 - 2;
        foreach (double tdb in FaderScale.Ticks)
        {
            if (tdb > ScaleTop + 0.01) continue;
            float y = g.Snap(YOfDb(tdb));
            bool zero = tdb == 0;
            if (ShowLabels)
            {
                string s = tdb > 0 ? "+" + (int)tdb : ((int)tdb).ToString().Replace('-', Db.Minus);
                g.Text(s, new RectF(b.X - 4, y - 7, LabelW + 1, 14), zero ? Theme.MicroStrong : Theme.Micro,
                    zero ? Theme.TextSecondary : Theme.TextTertiary, HAlign.Right, VAlign.Middle, false);
            }
            else g.Line(tickR - (zero ? 5 : 3), y, tickR, y, zero ? Theme.TextSecondary : Theme.TextTertiary.With(0.7f), 1f);
        }

        // Schlitz
        g.FillPill(slot, Theme.IsDark ? Theme.Well : Theme.Rgb(0xD6D9DE));
        float capY = YOfDb(Value);

        // Gain-Sperre: Bereich darueber orange
        if (limited)
        {
            float ly = YOfDb(MaxDb);
            var lockArea = RectF.FromLTRB(slot.X - 1.5f, slot.Y, slot.Right + 1.5f, ly);
            g.FillPill(lockArea, Theme.Orange.With(0.28f));
            g.FillRound(new RectF(tx - CapW / 2 - 3, g.Snap(ly) - 1, CapW + 6, 2), 1, Theme.Orange);
        }

        // Kappe
        var cap = RectF.Centered(tx, capY, CapW, CapH);
        float hover = HoverAmount((IsHovered || _dragging) && Enabled);
        float press = PressAmount(_dragging && Enabled);
        g.Shadow(cap, 7, 5, 1.1f + press * 0.3f, 2);
        g.FillRound(cap, 7, Theme.Mix(Theme.CapFill, Theme.CapFillHot, hover));
        g.StrokeRound(cap, 7, Theme.CapBorder);
        // Griffrillen
        var grip = Theme.IsDark ? Theme.Rgb(0x000000, 0.28f) : Theme.Rgb(0x000000, 0.08f);
        for (int i = -1; i <= 1; i += 2) g.Line(cap.X + 6, g.Snap(cap.CY + i * CapH * 0.22f), cap.Right - 6, g.Snap(cap.CY + i * CapH * 0.22f), grip, 1f);
        var line = Enabled ? (limited && Value >= MaxDb - 0.05 ? Theme.Orange : Theme.Accent) : Theme.TextTertiary;
        g.FillPill(new RectF(cap.X + 4, cap.CY - 1.25f, cap.W - 8, 2.5f), line);
    }
}

/// <summary>Pegelmeter: Verlauf, Spitzenhalten, Clip-Punkt (Klick loescht), optionale Limiter-Marke.</summary>
internal sealed class Meter : Widget
{
    float _db = -120f;
    float _peakDb = -120f;
    float _hold;
    bool _clip;
    /// <summary>Limiter-Grenze des Ausgangs in dBFS (Marke am Meter); NaN = keine.</summary>
    public double CeilingDb { get; set; } = double.NaN;
    public string? ExtraTooltip { get; set; }

    public bool Clipped => _clip;
    public override bool AcceptsMouse => Visible;
    public override CursorKind CursorAt(float x, float y) => CursorKind.Hand;
    public override void OnMouseDown(in MouseEvent e) { _clip = false; Invalidate(); }

    public override string? TooltipAt(float x, float y) =>
        (_clip ? "Clip! Klick setzt die Anzeige zurück. " : "Klick: Clip-Anzeige zurücksetzen. ") + (ExtraTooltip ?? "");

    /// <summary>Neuer Spitzenwert (linear) seit dem letzten Aufruf. true = Anzeige hat sich geaendert.</summary>
    public bool Feed(float linearPeak, float dt)
    {
        float newDb = linearPeak <= 1e-6f ? -120f : 20f * MathF.Log10(linearPeak);
        float oldDb = _db, oldPeak = _peakDb; bool oldClip = _clip;
        if (newDb >= _db) _db = newDb;
        else _db = Math.Max(newDb, _db - Theme.MeterFallDbPerSec * dt);
        if (newDb >= _peakDb) { _peakDb = newDb; _hold = Theme.PeakHoldSeconds; }
        else if (_hold > 0) _hold -= dt;
        else _peakDb = Math.Max(_db, _peakDb - Theme.PeakFallDbPerSec * dt);
        if (linearPeak >= 0.9995f) _clip = true;
        return Math.Abs(_db - oldDb) > 0.04f && (_db > -80 || oldDb > -80) || Math.Abs(_peakDb - oldPeak) > 0.04f && _peakDb > -80 || _clip != oldClip;
    }

    public void Reset() { _db = _peakDb = -120f; _hold = 0; _clip = false; Invalidate(); }

    static float Frac(double db) => (float)Math.Clamp((db - Theme.MeterMinDb) / (Theme.MeterMaxDb - Theme.MeterMinDb), 0, 1);

    public override void Paint(Renderer g)
    {
        var b = Bounds;
        float w = b.W;
        var clipR = new RectF(b.X, b.Y, w, w);
        var bar = new RectF(b.X, b.Y + w + 3, w, Math.Max(0, b.H - w - 3));
        g.FillPill(clipR, _clip ? Theme.Clip : Theme.MeterBack);
        g.FillPill(bar, Theme.MeterBack);
        if (bar.H <= 2) return;
        float bottom = bar.Bottom, fh = bar.H;
        float level = Frac(_db);
        if (level > 0.002f)
        {
            float top = bottom - fh * level;
            g.FillMeterGradient(new RectF(bar.X, top, bar.W, bottom - top), bottom, fh, bar.W / 2);
        }
        // feine Skalenstriche alle 6 dB (wie LED-Leiter, aber ruhig)
        for (double db = Theme.MeterMinDb + 6; db < Theme.MeterMaxDb; db += 6)
        {
            float y = g.Snap(bottom - fh * Frac(db));
            g.Fill(new RectF(bar.X, y, bar.W, 1f), Theme.MeterBack.With(0.55f));
        }
        if (_peakDb > Theme.MeterMinDb + 1)
        {
            float py = bottom - fh * Frac(_peakDb);
            var c = _peakDb >= -0.5f ? Theme.Clip : Theme.PeakHold;
            g.FillRound(new RectF(bar.X, Math.Max(bar.Y, py - 1), bar.W, 2f), 1, c);
        }
        if (!double.IsNaN(CeilingDb) && CeilingDb > Theme.MeterMinDb && CeilingDb < Theme.MeterMaxDb + 0.01)
        {
            float cy = g.Snap(bottom - fh * Frac(CeilingDb));
            g.FillRound(new RectF(b.X - 3, cy - 0.75f, w + 6, 1.5f), 0.75f, Theme.Orange);
        }
    }
}
