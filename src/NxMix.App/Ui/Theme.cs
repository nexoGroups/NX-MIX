using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace NxMix.App.Ui;

/// <summary>Farbmodus der Oberflaeche. System folgt der Windows-Einstellung "App-Modus".</summary>
internal enum ThemeMode { System, Light, Dark }

/// <summary>Bedienung: Maus (kompakt) oder Touch (groessere Trefferflaechen).</summary>
internal enum Density { Mouse, Touch }

/// <summary>
/// ZENTRALE Design-Datei von NX-MIX. Farben (Hell und Dunkel), Abstaende, Radien, Groessen (Maus und Touch) und Schriften.
/// Bildsprache: Anordnung wie Mixing Station, Farben/Symbole wie das UniFi-Webinterface, Formen und Bedienelemente wie bei Apple.
/// Alle Masse in DIPs (96 dpi); Farben sind Eigenschaften, weil sie beim Umschalten des Farbmodus wechseln.
/// </summary>
internal static class Theme
{
    // ---- Helfer ------------------------------------------------------------------------------
    /// <summary>Hex 0xRRGGBB (+ optional Alpha 0..1) nach Color4.</summary>
    public static Color4 Rgb(uint hex, float alpha = 1f) =>
        new(((hex >> 16) & 0xFF) / 255f, ((hex >> 8) & 0xFF) / 255f, (hex & 0xFF) / 255f, alpha);

    public static Color4 With(this Color4 c, float alpha) => new(c.R, c.G, c.B, alpha);

    public static Color4 Mix(Color4 a, Color4 b, float t) =>
        new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, a.A + (b.A - a.A) * t);

    public static readonly Color4 White = new(1, 1, 1, 1);
    public static readonly Color4 Black = new(0, 0, 0, 1);
    public static readonly Color4 Clear = new(0, 0, 0, 0);

    // ---- Zustand -----------------------------------------------------------------------------
    public static bool IsDark { get; private set; } = true;
    public static Density Density { get; private set; } = Density.Mouse;
    public static bool Touch => Density == Density.Touch;
    /// <summary>Zaehlt jede Umstellung (Farbmodus/Bedienung) - fuer Caches.</summary>
    public static int Version { get; private set; }

    public static void Apply(bool dark, Density density)
    {
        IsDark = dark;
        Density = density;
        if (dark) DarkPalette(); else LightPalette();
        Version++;
    }

    // ---- Flaechen ----------------------------------------------------------------------------
    /// <summary>Fensterhintergrund hinter allen Karten.</summary>
    public static Color4 Window { get; private set; }
    /// <summary>Seitenleiste und Kopfzeile.</summary>
    public static Color4 Chrome { get; private set; }
    /// <summary>Karten / Gruppen (Mixer-Bereiche, Einstellungsgruppen).</summary>
    public static Color4 Surface { get; private set; }
    /// <summary>Flaechen innerhalb von Karten (Kanalstreifen, Tabellenzeilen).</summary>
    public static Color4 Surface2 { get; private set; }
    /// <summary>Schwebende Ebenen: Menues, Dialoge, Seitenblatt.</summary>
    public static Color4 Elevated { get; private set; }
    /// <summary>Grauer Tastenhintergrund.</summary>
    public static Color4 Control { get; private set; }
    public static Color4 ControlHover { get; private set; }
    public static Color4 ControlPressed { get; private set; }
    /// <summary>Gewaehlter Abschnitt im Segment-Schalter.</summary>
    public static Color4 SegmentThumb { get; private set; }
    /// <summary>Mulden: Fader-Schlitz, Meter-Hintergrund, Eingabefelder.</summary>
    public static Color4 Well { get; private set; }
    public static Color4 Separator { get; private set; }
    public static Color4 Border { get; private set; }
    public static Color4 BorderStrong { get; private set; }
    /// <summary>Abdunklung hinter Dialogen.</summary>
    public static Color4 Scrim { get; private set; }
    public static Color4 ShadowColor { get; private set; }

    // ---- Text --------------------------------------------------------------------------------
    public static Color4 Text { get; private set; }
    public static Color4 TextSecondary { get; private set; }
    public static Color4 TextTertiary { get; private set; }
    public static Color4 OnAccent { get; private set; }

    // ---- Akzent (UniFi-Blau) und Funktionsfarben ------------------------------------------------
    public static Color4 Accent { get; private set; }
    public static Color4 AccentHover { get; private set; }
    public static Color4 AccentPressed { get; private set; }
    /// <summary>Akzent fuer Text/Symbole auf normalem Grund (im Dunkeln etwas heller).</summary>
    public static Color4 AccentText { get; private set; }
    public static Color4 AccentSoft => Accent.With(IsDark ? 0.18f : 0.12f);

    /// <summary>Stumm, Loeschen, Fehler.</summary>
    public static Color4 Red { get; private set; }
    /// <summary>Solo, Pegel ueber 0 dB.</summary>
    public static Color4 Yellow { get; private set; }
    /// <summary>Gain-Sperre, Warnungen.</summary>
    public static Color4 Orange { get; private set; }
    /// <summary>Verbunden, laeuft, in Ordnung.</summary>
    public static Color4 Green { get; private set; }
    /// <summary>Loopback.</summary>
    public static Color4 Purple { get; private set; }
    public static Color4 Teal { get; private set; }

    public static Color4 Soft(Color4 c) => c.With(IsDark ? 0.18f : 0.13f);

    // Fader-Kappe
    public static Color4 CapFill { get; private set; }
    public static Color4 CapFillHot { get; private set; }
    public static Color4 CapBorder { get; private set; }

    // Pegel
    public static readonly Color4 MeterGreen = Rgb(0x34C759);
    public static readonly Color4 MeterYellow = Rgb(0xFFCC00);
    public static readonly Color4 MeterRed = Rgb(0xFF3B30);
    public static Color4 MeterBack { get; private set; }
    public static Color4 PeakHold { get; private set; }
    public static readonly Color4 Clip = Rgb(0xFF3B30);

    /// <summary>Pegelverlauf unten -> oben (Position 0..1 entlang -60..0 dBFS).</summary>
    public static readonly (float Pos, Color4 Color)[] MeterStops =
    {
        (0.00f, Rgb(0x2DB84D)), (0.62f, Rgb(0x34C759)),   // bis ca. -23 dBFS gruen
        (0.80f, Rgb(0xFFCC00)), (0.90f, Rgb(0xFF9F0A)),   // gelb nach orange
        (0.97f, Rgb(0xFF3B30)), (1.00f, Rgb(0xFF3B30)),   // rot
    };

    static void DarkPalette()
    {
        Window = Rgb(0x0F1012);
        Chrome = Rgb(0x16171A);
        Surface = Rgb(0x1B1C20);
        Surface2 = Rgb(0x222328);
        Elevated = Rgb(0x26282D);
        Control = Rgb(0x2C2E34);
        ControlHover = Rgb(0x35383F);
        ControlPressed = Rgb(0x3F424A);
        SegmentThumb = Rgb(0x4A4D55);
        Well = Rgb(0x0B0C0E);
        Separator = Rgb(0x2A2C31);
        Border = Rgb(0x2B2D32);
        BorderStrong = Rgb(0x3B3E45);
        Scrim = Rgb(0x000000, 0.55f);
        ShadowColor = Rgb(0x000000, 0.55f);

        Text = Rgb(0xF2F3F5);
        TextSecondary = Rgb(0xA4A8B0);
        TextTertiary = Rgb(0x6B7079);
        OnAccent = Rgb(0xFFFFFF);

        Accent = Rgb(0x006FFF);
        AccentHover = Rgb(0x2E89FF);
        AccentPressed = Rgb(0x005BD1);
        AccentText = Rgb(0x4C9DFF);

        Red = Rgb(0xFF453A);
        Yellow = Rgb(0xFFD60A);
        Orange = Rgb(0xFF9F0A);
        Green = Rgb(0x32D74B);
        Purple = Rgb(0xBF5AF2);
        Teal = Rgb(0x40C8E0);

        CapFill = Rgb(0x3A3D44);
        CapFillHot = Rgb(0x464951);
        CapBorder = Rgb(0x52565F);
        MeterBack = Rgb(0x0B0C0E);
        PeakHold = Rgb(0xF2F3F5);
    }

    static void LightPalette()
    {
        Window = Rgb(0xF2F3F5);
        Chrome = Rgb(0xFFFFFF);
        Surface = Rgb(0xFFFFFF);
        Surface2 = Rgb(0xF5F6F8);
        Elevated = Rgb(0xFFFFFF);
        Control = Rgb(0xECEDF0);
        ControlHover = Rgb(0xE2E4E8);
        ControlPressed = Rgb(0xD6D9DE);
        SegmentThumb = Rgb(0xFFFFFF);
        Well = Rgb(0xE4E6EA);
        Separator = Rgb(0xE6E8EB);
        Border = Rgb(0xE1E3E7);
        BorderStrong = Rgb(0xC9CDD4);
        Scrim = Rgb(0x0B0C10, 0.30f);
        ShadowColor = Rgb(0x1A1F2B, 0.22f);

        Text = Rgb(0x1A1B1E);
        TextSecondary = Rgb(0x5A606A);
        TextTertiary = Rgb(0x8E949D);
        OnAccent = Rgb(0xFFFFFF);

        Accent = Rgb(0x006FFF);
        AccentHover = Rgb(0x1F80FF);
        AccentPressed = Rgb(0x005BD1);
        AccentText = Rgb(0x0062E0);

        Red = Rgb(0xE5383B);
        Yellow = Rgb(0xD9A400);
        Orange = Rgb(0xEC8600);
        Green = Rgb(0x1FA750);
        Purple = Rgb(0x9A45D6);
        Teal = Rgb(0x0E9CB8);

        CapFill = Rgb(0xFFFFFF);
        CapFillHot = Rgb(0xF4F6FA);
        CapBorder = Rgb(0xBFC4CC);
        MeterBack = Rgb(0xD9DCE1);
        PeakHold = Rgb(0x3A3D44);
    }

    static Theme() => Apply(true, Density.Mouse);

    // ---- Form ----------------------------------------------------------------------------------
    public const float RadiusCard = 10f;
    public const float RadiusInner = 7f;
    public const float RadiusControl = 6f;
    public const float RadiusSmall = 4f;

    // ---- Abstaende -----------------------------------------------------------------------------
    public const float Pad = 20f;            // Rand des Inhalts
    public const float Gap = 12f;            // Abstand zwischen Karten
    public const float CardPad = 16f;        // Innenabstand von Karten

    // ---- Groessen (Maus / Touch) -----------------------------------------------------------------
    static float T(float mouse, float touch) => Touch ? touch : mouse;
    public static float ControlH => T(32, 42);
    public static float ControlHSmall => T(26, 36);
    public static float RowH => T(56, 68);
    public static float MenuItemH => T(30, 42);
    public static float NavItemH => T(40, 50);
    public static float TopBarH => T(60, 64);
    public static float SidebarCollapsedW => T(64, 72);
    public const float SidebarExpandedW = 220f;
    public static float StripMinW => T(70, 92);
    public static float StripMaxW => T(108, 128);
    public const float StripGap = 8f;
    public static float StripHeaderH => T(58, 66);
    public static float MuteSoloH => T(26, 38);
    public static float FaderCapH => T(30, 44);
    public static float FaderCapMaxW => T(34, 46);
    public static float MeterW => T(6, 8);
    public static float SwitchW => T(40, 50);
    public static float SwitchH => T(24, 30);
    public static float SheetW => T(400, 440);

    public const float ResizeBorder = 6f;
    public const float MinWindowWidth = 1100f;
    public const float MinWindowHeight = 680f;
    public const float DefaultWindowWidth = 1600f;
    public const float DefaultWindowHeight = 900f;

    // ---- Wertebereiche -------------------------------------------------------------------------
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

    // ---- Schriften (Inter, liegt der App bei) ----------------------------------------------------
    public const string FontFamily = "Inter";
    public static readonly string[] FontFallbacks = { "Segoe UI Variable Text", "Segoe UI" };

    public static readonly TextStyle LargeTitle = new(22f, FontWeight.Bold);
    public static readonly TextStyle Title = new(17f, FontWeight.SemiBold);
    public static readonly TextStyle Headline = new(14f, FontWeight.SemiBold);
    public static readonly TextStyle Body = new(13f, FontWeight.Normal);
    public static readonly TextStyle BodyMedium = new(13f, FontWeight.Medium);
    public static readonly TextStyle BodyStrong = new(13f, FontWeight.SemiBold);
    public static readonly TextStyle Callout = new(12f, FontWeight.Normal);
    public static readonly TextStyle CalloutMedium = new(12f, FontWeight.Medium);
    public static readonly TextStyle CalloutStrong = new(12f, FontWeight.SemiBold);
    public static readonly TextStyle Caption = new(11f, FontWeight.Normal);
    public static readonly TextStyle CaptionMedium = new(11f, FontWeight.Medium);
    public static readonly TextStyle CaptionStrong = new(11f, FontWeight.SemiBold);
    public static readonly TextStyle Micro = new(9.5f, FontWeight.Medium);
    public static readonly TextStyle MicroStrong = new(9.5f, FontWeight.SemiBold);
    public static readonly TextStyle Overline = new(10.5f, FontWeight.SemiBold);
    public static readonly TextStyle Value = new(12f, FontWeight.SemiBold);
    public static readonly TextStyle Stat = new(19f, FontWeight.SemiBold);

    // ---- Verhalten -----------------------------------------------------------------------------
    public const float TooltipDelaySeconds = 0.55f;
    public const float MeterFallDbPerSec = 34f;
    public const float PeakHoldSeconds = 1.6f;
    public const float PeakFallDbPerSec = 22f;
}

/// <summary>Schrift (Groesse in DIPs + Gewicht). Die Familie kommt aus <see cref="Theme"/>.</summary>
internal readonly record struct TextStyle(float Size, FontWeight Weight);
