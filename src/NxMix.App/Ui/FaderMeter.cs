using Vortice.Mathematics;

namespace NxMix.App.Ui;

/// <summary>
/// Senkrechter Fader mit dB-Skala. Doppelklick = 0 dB, Strg/Umschalt = Feinjustierung, Mausrad = 1 dB (fein 0,1 dB).
/// MaxDb ist die Gain-Sperre: darueber laesst er sich nicht ziehen; die Sperre wird auf der Schiene markiert.
/// </summary>
internal sealed class Fader : Widget
{
    public double Value { get; private set; }
    /// <summary>Gain-Sperre (ChannelDsp.MaxGainDb). Liegt sie unter dem Skalenende, wird sie markiert.</summary>
    public double MaxDb { get; set; } = Theme.FaderMaxDb;
    public double ScaleTop { get; set; } = Theme.FaderMaxDb;
    public bool ShowLabels { get; set; } = true;
    public event Action<double>? Changed;

    double Quant(double db) => Math.Round(db * 10.0) / 10.0;

    float _dragY0;
    double _dragT0;
    bool _lastFine;

    public Fader() { Cursor = CursorKind.Hand; }
    public override bool AcceptsMouse => Visible;

    public void SetValue(double db)
    {
        if (!Db.IsOff(db) && db > MaxDb) db = MaxDb;
        if (!(Value.Equals(db))) { Value = db; Invalidate(); }
    }

    // ---- Geometrie -----------------------------------------------------------------------------
    float LabelW => ShowLabels ? 19f : 0f;
    float CapW => Math.Min(28f, Math.Max(14f, Bounds.W - LabelW - 8f));
    float CapH => Theme.FaderCapHeight;
    float TrackX => Bounds.X + LabelW + 6f + CapW / 2f;
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
        if (e.Clicks >= 2) { Apply(Math.Min(0.0, MaxDb)); return; }
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
        if (!IsPressed || !Enabled) return;
        if (e.Fine != _lastFine) { _dragY0 = e.Y; _dragT0 = FaderScale.ToT(Value); _lastFine = e.Fine; }
        double k = e.Fine ? 0.1 : 1.0;
        double t = _dragT0 + (-(e.Y - _dragY0) / (YBot - YTop)) * TopScaleT * k;
        SetFromT(Math.Clamp(t, 0, TopScaleT), e.Fine);
    }

    public override void OnMouseUp(in MouseEvent e) => Invalidate();

    public override bool OnMouseWheel(in MouseEvent e)
    {
        if (!Enabled) return true;
        double step = e.Fine ? 0.1 : 1.0;
        int notches = Math.Max(1, (int)Math.Abs(Math.Round(e.Wheel)));
        double dir = Math.Sign(e.Wheel);
        double v = Db.IsOff(Value) ? Theme.FaderMinDb - 12 : Value;
        v += dir * step * notches;
        if (v < Theme.FaderMinDb && dir < 0) v = double.NegativeInfinity;
        else if (v < Theme.FaderMinDb) v = Theme.FaderMinDb;
        Apply(Db.IsOff(v) ? v : Quant(v));
        return true;
    }

    public override string? TooltipAt(float x, float y)
    {
        if (MaxDb < ScaleTop - 0.05) return $"Gain-Sperre aktiv: max. {Db.Format(MaxDb)} dB. Doppelklick = {Db.Format(Math.Min(0, MaxDb))} dB";
        return "Ziehen · Strg/Umschalt = fein · Mausrad · Doppelklick = 0 dB";
    }

    // ---- Zeichnen ------------------------------------------------------------------------------
    public override void Paint(Renderer g)
    {
        var b = Bounds;
        float tx = TrackX;
        float slotW = 4f;
        var slot = new RectF(tx - slotW / 2, YTop - 6, slotW, YBot - YTop + 12);
        bool limited = MaxDb < ScaleTop - 0.05;

        // Skala: Striche und Beschriftung links der Kappe
        float tickX1 = b.X + LabelW + 4.5f;
        foreach (double tdb in FaderScale.Ticks)
        {
            if (tdb > ScaleTop + 0.01) continue;
            float y = g.Snap(YOfDb(tdb));
            bool zero = tdb == 0;
            g.Line(tickX1 - (zero ? 5.5f : 3.5f), y, tickX1, y, zero ? Theme.TextDim : Theme.TextFaint, 1f);
            if (ShowLabels)
            {
                string s = tdb > 0 ? "+" + (int)tdb : ((int)tdb).ToString().Replace('-', Db.Minus);
                g.Text(s, new RectF(b.X - 2, y - 7, LabelW - 4.5f + 0.5f, 14), Theme.Tiny, zero ? Theme.TextDim : Theme.TextFaint, HAlign.Right, VAlign.Middle, false);
            }
        }

        // Schlitz
        g.FillRound(slot, 2f, Theme.Well);
        g.StrokeRound(slot, 2f, Theme.Border.With(0.8f));
        float capY = YOfDb(Value);
        // Gefuellter Teil unter der Kappe
        var fill = RectF.FromLTRB(slot.X + 1, capY, slot.Right - 1, YBot + 4);
        if (fill.H > 0) g.FillRound(fill, 1.2f, Enabled ? Theme.Accent.With(0.55f) : Theme.TextFaint.With(0.3f));

        // Gain-Sperre: Bereich darueber schraffiert-amber
        if (limited)
        {
            float ly = YOfDb(MaxDb);
            g.Fill(new RectF(slot.X - 1, slot.Y + 1, slotW + 2, Math.Max(0, ly - slot.Y - 1)), Theme.Protect.With(0.20f));
            g.Line(tx - CapW / 2 - 2, g.Snap(ly), tx + CapW / 2 + 2, g.Snap(ly), Theme.Protect, 1.5f);
            // kleine Dreiecke an den Raendern der Sperre
            g.Line(tx + CapW / 2 + 2, ly - 3.5f, tx + CapW / 2 + 2, ly + 3.5f, Theme.Protect, 1.5f);
            g.Line(tx - CapW / 2 - 2, ly - 3.5f, tx - CapW / 2 - 2, ly + 3.5f, Theme.Protect, 1.5f);
        }

        // Kappe
        var cap = RectF.Centered(tx, capY, CapW, CapH);
        bool hot = (IsHovered || IsPressed) && Enabled;
        g.Shadow(cap, 5, 5, 0.55f, 2);
        g.FillRound(cap, 5, hot ? Theme.Rgb(0x3A3F49) : Theme.Rgb(0x30343C));
        g.StrokeRound(cap, 5, Theme.Rgb(0x464B56));
        g.Fill(new RectF(cap.X + 5, cap.Y + 1.2f, cap.W - 10, 1), Theme.Rgb(0xFFFFFF, 0.10f));
        // Griffrillen
        for (int i = -1; i <= 1; i += 2) g.Line(cap.X + 5, cap.CY + i * 6, cap.Right - 5, cap.CY + i * 6, Theme.Rgb(0x000000, 0.35f), 1f);
        // Akzentstrich
        var line = Enabled ? (limited && Value >= MaxDb - 0.05 ? Theme.Protect : Theme.Accent) : Theme.TextFaint;
        g.FillRound(new RectF(cap.X + 3, cap.CY - 1.25f, cap.W - 6, 2.5f), 1.2f, line);
    }
}

/// <summary>Pegelmeter: Verlauf, Spitzenhalten, Clip-Anzeige (Klick loescht), optionale Limiter-Marke.</summary>
internal sealed class Meter : Widget
{
    float _db = -120f;
    float _peakDb = -120f;
    float _hold;
    bool _clip;
    /// <summary>Limiter-Grenze des Ausgangs in dBFS (Marke am Meter); NaN = keine.</summary>
    public double CeilingDb { get; set; } = double.NaN;
    public string? ExtraTooltip { get; set; }

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
        const float ledH = 8f;
        var clipR = new RectF(b.X, b.Y, b.W, ledH);
        var bar = new RectF(b.X, b.Y + ledH + 2, b.W, Math.Max(0, b.H - ledH - 2));
        g.FillRound(clipR, 2f, _clip ? Theme.Clip : Theme.Well);
        if (_clip) g.Shadow(clipR, 2, 3, 0.0f);
        g.FillRound(bar, 2f, Theme.Well);
        var inner = bar.Deflate(1);
        if (inner.H <= 0) return;
        float bottom = inner.Bottom;
        float fh = inner.H;
        float level = Frac(_db);
        if (level > 0.002f)
        {
            float top = bottom - fh * level;
            g.FillMeterGradient(new RectF(inner.X, top, inner.W, bottom - top), bottom, fh);
        }
        // Raster: feine dunkle Linien (LED-Anmutung) alle 3 dB
        for (double db = Theme.MeterMinDb + 3; db < Theme.MeterMaxDb; db += 3)
        {
            float y = g.Snap(bottom - fh * Frac(db));
            g.Fill(new RectF(inner.X, y, inner.W, 1f), Theme.Well.With(0.85f));
        }
        // Spitzenhalten
        if (_peakDb > Theme.MeterMinDb + 1)
        {
            float py = bottom - fh * Frac(_peakDb);
            var c = _peakDb >= -0.5f ? Theme.Clip : Theme.PeakHold;
            g.Fill(new RectF(inner.X, Math.Max(inner.Y, py - 1), inner.W, 2f), c);
        }
        // Limiter-Marke
        if (!double.IsNaN(CeilingDb) && CeilingDb > Theme.MeterMinDb && CeilingDb < Theme.MeterMaxDb + 0.01)
        {
            float cy = g.Snap(bottom - fh * Frac(CeilingDb));
            g.Line(b.X - 3, cy, b.Right + 3, cy, Theme.Protect, 1.4f);
        }
    }
}
