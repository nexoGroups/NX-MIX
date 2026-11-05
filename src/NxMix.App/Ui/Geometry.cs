using System.Globalization;

namespace NxMix.App.Ui;

/// <summary>Rechteck in DIPs (X, Y, Breite, Hoehe).</summary>
internal readonly record struct RectF(float X, float Y, float W, float H)
{
    public float Right => X + W;
    public float Bottom => Y + H;
    public float CX => X + W * 0.5f;
    public float CY => Y + H * 0.5f;
    public bool IsEmpty => W <= 0 || H <= 0;

    public bool Contains(float px, float py) => px >= X && px < X + W && py >= Y && py < Y + H;

    public RectF Inflate(float d) => new(X - d, Y - d, Math.Max(0, W + 2 * d), Math.Max(0, H + 2 * d));
    public RectF Inflate(float h, float v) => new(X - h, Y - v, Math.Max(0, W + 2 * h), Math.Max(0, H + 2 * v));
    public RectF Deflate(float l, float t, float r, float b) => new(X + l, Y + t, Math.Max(0, W - l - r), Math.Max(0, H - t - b));
    public RectF Deflate(float d) => Deflate(d, d, d, d);
    public RectF Deflate(float h, float v) => Deflate(h, v, h, v);
    public RectF Offset(float dx, float dy) => new(X + dx, Y + dy, W, H);

    public static RectF FromLTRB(float l, float t, float r, float b) => new(l, t, Math.Max(0, r - l), Math.Max(0, b - t));
    public static RectF Centered(float cx, float cy, float w, float h) => new(cx - w / 2, cy - h / 2, w, h);

    public RectF Intersect(RectF o)
    {
        float l = Math.Max(X, o.X), t = Math.Max(Y, o.Y), r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return FromLTRB(l, t, Math.Max(l, r), Math.Max(t, b));
    }
}

internal enum HAlign { Left, Center, Right }
internal enum VAlign { Top, Middle, Bottom }

/// <summary>Dezibel-Hilfen und die Fader-Kennlinie.</summary>
internal static class Db
{
    public static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const char Minus = '−';

    public static bool IsOff(double db) => double.IsNaN(db) || double.IsNegativeInfinity(db);

    public static double ToLinear(double db) => IsOff(db) ? 0.0 : Math.Pow(10.0, db / 20.0);
    public static double FromLinear(double lin) => lin <= 1e-9 ? double.NegativeInfinity : 20.0 * Math.Log10(lin);

    /// <summary>"-13.0" mit typografischem Minus, "-oo" fuer aus.</summary>
    public static string Format(double db, int decimals = 1)
    {
        if (IsOff(db)) return "−∞";
        string s = Math.Round(db, decimals).ToString("F" + decimals, Inv);
        if (s.StartsWith('-')) s = Minus + s[1..];
        if (s == Minus + (decimals > 0 ? "0." + new string('0', decimals) : "0")) s = s[1..]; // "-0.0" -> "0.0"
        return s;
    }

    public static bool TryParse(string text, out double db)
    {
        text = text.Trim().Replace(Minus, '-').Replace(',', '.');
        if (text.EndsWith("dB", StringComparison.OrdinalIgnoreCase)) text = text[..^2].Trim();
        if (text is "-inf" or "-oo" or "-∞" or "off" or "aus") { db = double.NegativeInfinity; return true; }
        return double.TryParse(text, NumberStyles.Float, Inv, out db) && !double.IsNaN(db) && !double.IsInfinity(db);
    }
}

/// <summary>Kennlinie der Fader: Position 0..1 (unten..oben) gegen dB, stueckweise linear in dB (feiner um 0 dB).</summary>
internal static class FaderScale
{
    // (dB, Position)
    static readonly (double Db, double T)[] Pts =
    {
        (-72, 0.000), (-60, 0.050), (-48, 0.120), (-36, 0.215), (-24, 0.345), (-18, 0.435), (-12, 0.535),
        (-6, 0.650), (0, 0.775), (6, 0.895), (12, 1.000),
    };

    public static readonly double[] Ticks = { 12, 6, 0, -6, -12, -18, -24, -36, -48, -60 };

    public static double ToT(double db)
    {
        if (Db.IsOff(db) || db <= Pts[0].Db) return 0;
        if (db >= Pts[^1].Db) return 1;
        for (int i = 1; i < Pts.Length; i++)
        {
            if (db <= Pts[i].Db)
            {
                var a = Pts[i - 1]; var b = Pts[i];
                return a.T + (db - a.Db) / (b.Db - a.Db) * (b.T - a.T);
            }
        }
        return 1;
    }

    public static double ToDb(double t)
    {
        if (t <= 0.004) return double.NegativeInfinity;
        if (t >= 1) return Pts[^1].Db;
        for (int i = 1; i < Pts.Length; i++)
        {
            if (t <= Pts[i].T)
            {
                var a = Pts[i - 1]; var b = Pts[i];
                return a.Db + (t - a.T) / (b.T - a.T) * (b.Db - a.Db);
            }
        }
        return Pts[^1].Db;
    }
}

/// <summary>Kleine Textbausteine (Einzahl/Mehrzahl).</summary>
internal static class Fmt
{
    public static string Count(int n, string one, string many) => n + " " + (n == 1 ? one : many);
    public static string Inputs(int n) => Count(n, "Eingang", "Eingänge");
    public static string Outputs(int n) => Count(n, "Ausgang", "Ausgänge");
}
