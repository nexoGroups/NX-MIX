using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace NxMix.App.Ui;

/// <summary>
/// ZENTRALE Design-Datei von NX-MIX. Alle Farben, Abstaende, Radien, Schriften und Groessen stehen hier.
/// Wer das Aussehen aendern will (z. B. den Akzent), braucht nur diese Datei anzufassen.
/// Alle Masse sind in DIPs (geraeteunabhaengige Pixel, 96 dpi); die Skalierung uebernimmt die Zeichenschicht.
/// </summary>
internal static class Theme
{
    // ---- Farben ----------------------------------------------------------------------------
    /// <summary>Hex 0xRRGGBB (+ optional Alpha 0..1) nach Color4.</summary>
    public static Color4 Rgb(uint hex, float alpha = 1f) =>
        new(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, alpha);

    public static Color4 With(this Color4 c, float alpha) => new(c.R, c.G, c.B, alpha);

    public static Color4 Mix(Color4 a, Color4 b, float t) =>
        new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    // Grafit-Flaechen, von dunkel nach hell
    public static readonly Color4 White = new(1, 1, 1, 1);
    public static readonly Color4 Black = new(0, 0, 0, 1);
    public static readonly Color4 Well      = Rgb(0x000000); // tiefste Mulden (Fader-Schlitz, Meter-Hintergrund, Eingabefelder)
    public static readonly Color4 Window    = Rgb(0x050505); // Fensterhintergrund
    public static readonly Color4 Chrome    = Rgb(0x0A0A0A); // Titelleiste / Statusleiste
    public static readonly Color4 Panel     = Rgb(0x0D0D0D); // Gruppenflaechen
    public static readonly Color4 Card      = Rgb(0x141414); // Kanalstreifen, Karten
    public static readonly Color4 CardHover = Rgb(0x1C1C1C);
    public static readonly Color4 Raised    = Rgb(0x262626); // Tasten, Fader-Kappe
    public static readonly Color4 RaisedHi  = Rgb(0x333333);

    public static readonly Color4 Border       = Rgb(0x2A2A2A);
    public static readonly Color4 BorderStrong = Rgb(0x474747);

    public static readonly Color4 Text      = Rgb(0xECECEC);
    public static readonly Color4 TextDim   = Rgb(0xA3A3A3);
    public static readonly Color4 TextFaint = Rgb(0x6A6A6A);

    /// <summary>DER Akzent. Aendert man diese eine Zeile, aendert sich die ganze Oberflaeche.</summary>
    public static readonly Color4 Accent    = Rgb(0xD9731F);
    public static readonly Color4 AccentHi  = Rgb(0xEE8C3A);
    public static readonly Color4 AccentDim = Rgb(0xD9731F, 0.18f);
    public static readonly Color4 OnAccent  = Rgb(0x0A0500);

    // Funktionsfarben (sparsam): Stumm, Solo, Schutz
    public static readonly Color4 Mute    = Rgb(0xC8363A);
    public static readonly Color4 Solo    = Rgb(0xD4B520);
    public static readonly Color4 Protect = Rgb(0xD4B520);
    /// <summary>Helleres Rot fuer kleine Fehlertexte (besserer Kontrast auf dunklem Grund als Mute).</summary>
    public static readonly Color4 MuteText = Rgb(0xEE6A6E);
    /// <summary>Status "in Ordnung / verbunden" (LEDs, Haken).</summary>
    public static readonly Color4 Ok      = Rgb(0x3FD08A);
    /// <summary>Hintergrund von Auswahl-Popups (neutrales Grafit, zwischen Karte und Taste).</summary>
    public static readonly Color4 Popup   = Rgb(0x1A1A1A);

    // Pegelverlauf unten -> oben (Position 0..1 entlang -60..0 dBFS)
    public static readonly (float Pos, Color4 Color)[] MeterStops =
    {
        (0.00f, Rgb(0x2F9E57)), (0.70f, Rgb(0x2F9E57)),   // bis -18 dBFS gruen
        (0.70f, Rgb(0xC9A227)), (0.90f, Rgb(0xC9A227)),   // bis -6 dBFS gelb
        (0.90f, Rgb(0xD0452E)), (1.00f, Rgb(0xD0452E)),   // darueber rot
    };    public static readonly Color4 Clip = Rgb(0xE23A32);
    public static readonly Color4 PeakHold = Rgb(0xF2F4F8);

    // ---- Form ---------------------------------------------------------------------------------
    /// <summary>Groesster erlaubter Eckenradius ueberall (0 = komplett eckig).</summary>
    public const float MaxCorner = 0f;
    public const bool SquareDots = true;
    public const bool Shadows = false;
    /// <summary>Abstand der LED-Trennstriche im Pegelbalken in DIPs (0 = durchgehend).</summary>
    public const float MeterSegmentPx = 4f;

    // ---- Abstaende / Radien ------------------------------------------------------------------
    public const float Pad = 16f;            // Seitenrand
    public const float Gap = 8f;             // normaler Abstand
    public const float GapSmall = 4f;
    public const float RadiusPanel = 0f;
    public const float RadiusCard = 0f;
    public const float RadiusControl = 0f;

    public const float TitleBarHeight = 46f;
    public const float StatusBarHeight = 28f;
    public const float ResizeBorder = 6f;
    public const float MinWindowWidth = 1180f;
    public const float MinWindowHeight = 680f;
    public const float DefaultWindowWidth = 1600f;
    public const float DefaultWindowHeight = 900f;

    // Mixer
    public const float StripMinWidth = 52f;
    public const float StripMaxWidth = 112f;
    public const float PanelHeaderHeight = 32f;
    public const float PanelInner = 10f;
    public const float StripGap = 6f;
    public const float FaderCapHeight = 34f;
    public const float MeterWidth = 10f;

    // ---- Wertebereiche -----------------------------------------------------------------------
    public const double FaderMinDb = -60.0;       // darunter: -unendlich
    public const double FaderMaxDb = 12.0;        // oberes Skalenende
    public const double MasterMaxDb = 6.0;
    public const double MeterMinDb = -60.0;
    public const double MeterMaxDb = 0.0;
    public const double MatrixMinDb = -60.0;
    public const double MatrixMaxDb = 12.0;
    /// <summary>Gesperrte Fader: ein Kanal gilt als geschuetzt, wenn sein MaxGainDb unter diesem Wert liegt (Hochtoener).</summary>
    public const double ProtectBelowDb = -6.0;
    /// <summary>Matrix-Kreuzungspunkte auf geschuetzte Ausgaenge duerfen nicht ueber diesen Wert angehoben werden.</summary>
    public const double ProtectedMatrixMaxDb = 0.0;
    public const double DelayMaxMs = 20.0;

    // ---- Schriften ---------------------------------------------------------------------------
    public const string FontFamily = "Bahnschrift";
    public const string FontFamilyFallback = "Segoe UI";

    public static readonly TextStyle Body      = new(12.5f, FontWeight.Normal);
    public static readonly TextStyle BodyBold  = new(12.5f, FontWeight.SemiBold);
    public static readonly TextStyle Small     = new(11f, FontWeight.Normal);
    public static readonly TextStyle SmallBold = new(11f, FontWeight.SemiBold);
    public static readonly TextStyle Tiny      = new(9.5f, FontWeight.Normal);
    public static readonly TextStyle TinyBold  = new(9.5f, FontWeight.SemiBold);
    public static readonly TextStyle Caps      = new(10f, FontWeight.SemiBold);
    public static readonly TextStyle Tab       = new(13f, FontWeight.SemiBold);
    public static readonly TextStyle Brand     = new(15f, FontWeight.Bold);
    public static readonly TextStyle Heading   = new(17f, FontWeight.SemiBold);
    public static readonly TextStyle Value     = new(12f, FontWeight.SemiBold);
    public static readonly TextStyle Cell      = new(11.5f, FontWeight.SemiBold);

    // ---- Verhalten ---------------------------------------------------------------------------
    public const float TooltipDelaySeconds = 0.55f;
    public const float MeterFallDbPerSec = 34f;
    public const float PeakHoldSeconds = 1.6f;
    public const float PeakFallDbPerSec = 22f;
}

/// <summary>Schrift (Groesse in DIPs + Gewicht). Die Familie kommt aus <see cref="Theme"/>.</summary>
internal readonly record struct TextStyle(float Size, FontWeight Weight);
