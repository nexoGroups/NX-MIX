using Vortice.Mathematics;

namespace NxMix.App.Ui;

/// <summary>Kleine, selbst gezeichnete Symbole (Linien/Formen, keine Icon-Schrift noetig).</summary>
internal static class Icons
{
    /// <summary>Schloss: geschlossen (Sperre aktiv) oder offen.</summary>
    public static void Lock(Renderer g, float cx, float cy, float s, Color4 c, bool closed = true)
    {
        float bw = s * 0.86f, bh = s * 0.52f;
        float top = cy - s * 0.52f;
        var body = new RectF(cx - bw / 2, cy + s * 0.50f - bh, bw, bh);
        float r = bw * 0.30f;                      // Radius des Buegels
        float lw = Math.Max(1.4f, s * 0.15f);
        float legTop = top + r;
        float legBot = body.Y + 0.5f;
        float ax = cx - r;                         // linkes Buegelbein
        float bx = cx + r;                         // rechtes Buegelbein
        if (closed)
        {
            g.Arc(cx, legTop, r, 180, 180, c, lw);
            g.Line(ax, legTop, ax, legBot, c, lw);
            g.Line(bx, legTop, bx, legBot, c, lw);
        }
        else
        {
            // offen: der Buegel ist rechts angehoben
            float dy = -s * 0.16f;
            g.Arc(cx, legTop + dy, r, 180, 180, c, lw);
            g.Line(ax, legTop + dy, ax, legBot, c, lw);
            g.Line(bx, legTop + dy, bx, legTop + dy + r * 0.9f, c, lw);
        }
        g.FillRound(body, Math.Max(1.8f, s * 0.14f), c);
        g.FillCircle(cx, body.CY, Math.Max(1.0f, s * 0.085f), Theme.Card);
    }

    /// <summary>Pfeil in eine Wand hinein (Eingang / Aufnahme).</summary>
    public static void InArrow(Renderer g, float cx, float cy, float s, Color4 c)
    {
        float h = s / 2;
        g.Line(cx - h, cy, cx + h * 0.55f, cy, c, 1.6f, true);
        g.Line(cx + h * 0.55f, cy, cx - h * 0.1f, cy - h * 0.55f, c, 1.6f, true);
        g.Line(cx + h * 0.55f, cy, cx - h * 0.1f, cy + h * 0.55f, c, 1.6f, true);
        g.Line(cx + h, cy - h * 0.7f, cx + h, cy + h * 0.7f, c, 1.6f, true);
    }

    /// <summary>Pfeil aus einer Wand heraus (Ausgang / Wiedergabe).</summary>
    public static void OutArrow(Renderer g, float cx, float cy, float s, Color4 c)
    {
        float h = s / 2;
        g.Line(cx - h, cy - h * 0.7f, cx - h, cy + h * 0.7f, c, 1.6f, true);
        g.Line(cx - h * 0.55f, cy, cx + h, cy, c, 1.6f, true);
        g.Line(cx + h, cy, cx + h * 0.35f, cy - h * 0.55f, c, 1.6f, true);
        g.Line(cx + h, cy, cx + h * 0.35f, cy + h * 0.55f, c, 1.6f, true);
    }

    public static void Dots(Renderer g, float cx, float cy, float s, Color4 c)
    {
        float r = Math.Max(1.2f, s * 0.11f), d = s * 0.32f;
        g.FillCircle(cx - d, cy, r, c); g.FillCircle(cx, cy, r, c); g.FillCircle(cx + d, cy, r, c);
    }

    public static void Plus(Renderer g, float cx, float cy, float s, Color4 c, float w = 1.7f)
    {
        float h = s / 2;
        g.Line(cx - h, cy, cx + h, cy, c, w, true);
        g.Line(cx, cy - h, cx, cy + h, c, w, true);
    }

    public static void Minus(Renderer g, float cx, float cy, float s, Color4 c, float w = 1.7f) =>
        g.Line(cx - s / 2, cy, cx + s / 2, cy, c, w, true);

    public static void Cross(Renderer g, float cx, float cy, float s, Color4 c, float w = 1.4f)
    {
        float h = s / 2;
        g.Line(cx - h, cy - h, cx + h, cy + h, c, w, true);
        g.Line(cx - h, cy + h, cx + h, cy - h, c, w, true);
    }

    /// <summary>dir: 0 = unten, 1 = oben, 2 = links, 3 = rechts</summary>
    public static void Chevron(Renderer g, float cx, float cy, float s, Color4 c, int dir, float w = 1.5f)
    {
        float h = s / 2, q = s / 4;
        switch (dir)
        {
            case 0: g.Line(cx - h, cy - q, cx, cy + q, c, w, true); g.Line(cx, cy + q, cx + h, cy - q, c, w, true); break;
            case 1: g.Line(cx - h, cy + q, cx, cy - q, c, w, true); g.Line(cx, cy - q, cx + h, cy + q, c, w, true); break;
            case 2: g.Line(cx + q, cy - h, cx - q, cy, c, w, true); g.Line(cx - q, cy, cx + q, cy + h, c, w, true); break;
            default: g.Line(cx - q, cy - h, cx + q, cy, c, w, true); g.Line(cx + q, cy, cx - q, cy + h, c, w, true); break;
        }
    }

    public static void Hamburger(Renderer g, float cx, float cy, float s, Color4 c)
    {
        float h = s / 2, d = s * 0.32f;
        g.Line(cx - h, cy - d, cx + h, cy - d, c, 1.5f, true);
        g.Line(cx - h, cy, cx + h, cy, c, 1.5f, true);
        g.Line(cx - h, cy + d, cx + h, cy + d, c, 1.5f, true);
    }

    public static void Trash(Renderer g, float cx, float cy, float s, Color4 c)
    {
        float w = s * 0.62f, top = cy - s * 0.30f, bot = cy + s * 0.5f;
        g.Line(cx - s * 0.5f, top, cx + s * 0.5f, top, c, 1.4f, true);
        g.Line(cx - s * 0.16f, top, cx - s * 0.16f, top - s * 0.16f, c, 1.4f, true);
        g.Line(cx + s * 0.16f, top, cx + s * 0.16f, top - s * 0.16f, c, 1.4f, true);
        g.Line(cx - s * 0.16f, top - s * 0.16f, cx + s * 0.16f, top - s * 0.16f, c, 1.4f, true);
        g.StrokeRound(new RectF(cx - w / 2, top + 1.5f, w, bot - top - 1.5f), 2, c, 1.4f);
    }

    public static void Play(Renderer g, float cx, float cy, float s, Color4 c)
    {
        // Dreieck ueber drei Linien (gefuellt durch viele Linien waere unnoetig): gefuelltes Dreieck per Linienschar
        float h = s / 2;
        float x0 = cx - h * 0.6f, x1 = cx + h * 0.8f;
        for (float y = -h; y <= h; y += 0.5f)
        {
            float t = 1 - Math.Abs(y) / h;
            g.Line(x0, cy + y, x0 + (x1 - x0) * t, cy + y, c, 0.9f);
        }
    }

    public static void Stop(Renderer g, float cx, float cy, float s, Color4 c) =>
        g.FillRound(RectF.Centered(cx, cy, s * 0.8f, s * 0.8f), 2f, c);

    public static void Refresh(Renderer g, float cx, float cy, float s, Color4 c)
    {
        g.Arc(cx, cy, s * 0.42f, -60, 280, c, 1.5f);
        float a = (-60) * MathF.PI / 180f;
        float ax = cx + MathF.Cos(a) * s * 0.42f, ay = cy + MathF.Sin(a) * s * 0.42f;
        g.Line(ax, ay, ax + s * 0.28f, ay + s * 0.02f, c, 1.5f, true);
        g.Line(ax, ay, ax - 0.5f, ay - s * 0.28f, c, 1.5f, true);
    }

    /// <summary>Pfeil nach rechts (Eingang -> Mixer) oder links.</summary>
    public static void Arrow(Renderer g, float cx, float cy, float s, Color4 c, bool right = true)
    {
        float h = s / 2;
        g.Line(cx - h, cy, cx + h, cy, c, 1.5f, true);
        if (right) { g.Line(cx + h, cy, cx + h * 0.35f, cy - h * 0.55f, c, 1.5f, true); g.Line(cx + h, cy, cx + h * 0.35f, cy + h * 0.55f, c, 1.5f, true); }
        else { g.Line(cx - h, cy, cx - h * 0.35f, cy - h * 0.55f, c, 1.5f, true); g.Line(cx - h, cy, cx - h * 0.35f, cy + h * 0.55f, c, 1.5f, true); }
    }

    /// <summary>Kette aus zwei eckigen Gliedern (App-Link).</summary>
    public static void Link(Renderer g, float cx, float cy, float s, Color4 c, float w = 1.4f)
    {
        float lw = s * 0.62f, lh = s * 0.42f, ov = s * 0.26f;
        float total = 2 * lw - ov;
        float x0 = MathF.Round(cx - total / 2), y0 = MathF.Round(cy - lh / 2);
        g.StrokeRect(new RectF(x0, y0, lw, lh), c, w);
        g.StrokeRect(new RectF(x0 + lw - ov, y0, lw, lh), c, w);
        g.Fill(new RectF(x0 + lw - ov, MathF.Round(cy - w / 2), ov, w), c); // Glied-Steg durch die Ueberlappung
    }

    /// <summary>Drei Balken wie ein Pegelanzeiger ("spielt gerade").</summary>
    public static void EqBars(Renderer g, float cx, float cy, float s, Color4 c)
    {
        float bw = MathF.Max(2f, s * 0.2f), gap = bw * 0.7f, h = s;
        float x = cx - (3 * bw + 2 * gap) / 2;
        float[] hs = { 0.55f, 1f, 0.7f };
        for (int i = 0; i < 3; i++)
        {
            float bh = h * hs[i];
            g.Fill(new RectF(MathF.Round(x + i * (bw + gap)), MathF.Round(cy + h / 2 - bh), bw, bh), c);
        }
    }

    public static void Warn(Renderer g, float cx, float cy, float s, Color4 c)
    {
        float h = s / 2;
        g.Line(cx, cy - h, cx - h, cy + h * 0.8f, c, 1.5f, true);
        g.Line(cx, cy - h, cx + h, cy + h * 0.8f, c, 1.5f, true);
        g.Line(cx - h, cy + h * 0.8f, cx + h, cy + h * 0.8f, c, 1.5f, true);
        g.Line(cx, cy - h * 0.25f, cx, cy + h * 0.25f, c, 1.5f, true);
        g.FillCircle(cx, cy + h * 0.52f, 0.9f, c);
    }
}
