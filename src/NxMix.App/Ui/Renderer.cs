using System.Numerics;
using Vortice;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;

namespace NxMix.App.Ui;

/// <summary>
/// Duenne Zeichenschicht ueber Direct2D/DirectWrite. Gleiche API fuer das Fenster (HwndRenderTarget) und fuer
/// den Offscreen-Screenshot (WIC-Bitmap). Alle Koordinaten sind DIPs; das Ziel-DPI wird am Rendertarget gesetzt.
/// Schrift: Inter aus dem Ordner "Fonts" neben der Exe (eigene Schriftsammlung); fehlt sie, Segoe UI.
/// </summary>
internal sealed class Renderer : IDisposable
{
    public static readonly ID2D1Factory1 D2D = D2D1.D2D1CreateFactory<ID2D1Factory1>(D2DFactoryType.SingleThreaded);
    public static readonly IDWriteFactory5 DW = DWrite.DWriteCreateFactory<IDWriteFactory5>();

    public ID2D1RenderTarget Rt { get; }
    /// <summary>Physische Pixel je DIP (DPI / 96).</summary>
    public float Scale { get; }

    readonly ID2D1SolidColorBrush _solid;
    readonly ID2D1StrokeStyle _round;
    ID2D1LinearGradientBrush? _meterBrush;
    ID2D1GradientStopCollection? _meterStops;
    ID2D1Layer? _opacityLayer;
    static readonly Dictionary<(float, FontWeight, int, bool, bool), IDWriteTextFormat> s_formats = new();
    static readonly Dictionary<(string, float, FontWeight), float> s_widths = new();
    readonly Dictionary<(int, int, int), ID2D1PathGeometry> _arcs = new();
    static readonly (string Family, IDWriteFontCollection? Collection) s_font = ResolveFont();

    /// <summary>Name der tatsaechlich benutzten Schrift (Inter oder Ersatz) - fuer Einstellungen/Selbsttest.</summary>
    public static string FontName => s_font.Family;

    public Renderer(ID2D1RenderTarget rt, float scale, bool clearType = true)
    {
        Rt = rt;
        Scale = scale;
        Rt.AntialiasMode = AntialiasMode.PerPrimitive;
        Rt.TextAntialiasMode = clearType ? Vortice.Direct2D1.TextAntialiasMode.Cleartype : Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        _solid = Rt.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        _round = D2D.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round,
            EndCap = CapStyle.Round,
            LineJoin = LineJoin.Round,
        });
    }

    /// <summary>Laedt Inter (vier Schnitte) als eigene Schriftsammlung; sonst die erste vorhandene Systemschrift.</summary>
    static (string, IDWriteFontCollection?) ResolveFont()
    {
        try
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "Fonts");
            var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "Inter-*.ttf") : Array.Empty<string>();
            if (files.Length > 0)
            {
                using var builder = DW.CreateFontSetBuilder();
                foreach (var f in files)
                {
                    using var file = DW.CreateFontFileReference(f, null);
                    builder.AddFontFile(file);
                }
                using var set = builder.CreateFontSet();
                var coll = DW.CreateFontCollectionFromFontSet(set);
                if (coll.FindFamilyName(Theme.FontFamily, out _)) return (Theme.FontFamily, coll);
                coll.Dispose();
            }
        }
        catch { /* Ersatzschrift */ }
        try
        {
            using var sys = DW.GetSystemFontCollection(false);
            foreach (var f in Theme.FontFallbacks)
                if (sys.FindFamilyName(f, out _)) return (f, null);
        }
        catch { /* letzte Rueckfallebene */ }
        return ("Segoe UI", null);
    }

    // ---- Zeichnen: Flaechen ------------------------------------------------------------------
    public float Snap(float v) => MathF.Round(v * Scale) / Scale;

    static RawRectF Raw(RectF r) => new(r.X, r.Y, r.Right, r.Bottom);

    public void Clear(Color4 c) => Rt.Clear(c);

    public void Fill(RectF r, Color4 c)
    {
        if (r.IsEmpty || c.A <= 0) return;
        _solid.Color = c;
        Rt.FillRectangle(Raw(r), _solid);
    }

    public void FillRound(RectF r, float radius, Color4 c)
    {
        if (r.IsEmpty || c.A <= 0) return;
        radius = Math.Min(radius, Math.Min(r.W, r.H) / 2);
        _solid.Color = c;
        if (radius < 0.5f) { Rt.FillRectangle(Raw(r), _solid); return; }
        var rr = new RoundedRectangle(new System.Drawing.RectangleF(r.X, r.Y, r.W, r.H), radius, radius);
        Rt.FillRoundedRectangle(ref rr, _solid);
    }

    /// <summary>Rand INNEN an der Rechteckkante (der Strich bleibt in r).</summary>
    public void StrokeRound(RectF r, float radius, Color4 c, float width = 1f)
    {
        if (r.IsEmpty || c.A <= 0) return;
        var i = r.Deflate(width / 2);
        radius = Math.Max(0, Math.Min(radius - width / 2, Math.Min(i.W, i.H) / 2));
        _solid.Color = c;
        if (radius < 0.5f) { Rt.DrawRectangle(Raw(i), _solid, width); return; }
        var rr = new RoundedRectangle(new System.Drawing.RectangleF(i.X, i.Y, i.W, i.H), radius, radius);
        Rt.DrawRoundedRectangle(rr, _solid, width);
    }

    /// <summary>Pillenform (Radius = halbe Hoehe).</summary>
    public void FillPill(RectF r, Color4 c) => FillRound(r, Math.Min(r.W, r.H) / 2, c);

    public void Line(float x1, float y1, float x2, float y2, Color4 c, float width = 1f, bool roundCaps = false)
    {
        if (c.A <= 0) return;
        _solid.Color = c;
        if (roundCaps) Rt.DrawLine(new Vector2(x1, y1), new Vector2(x2, y2), _solid, width, _round);
        else Rt.DrawLine(new Vector2(x1, y1), new Vector2(x2, y2), _solid, width);
    }

    public void FillCircle(float cx, float cy, float r, Color4 c)
    {
        if (c.A <= 0) return;
        _solid.Color = c;
        Rt.FillEllipse(new Ellipse(new Vector2(cx, cy), r, r), _solid);
    }

    public void StrokeCircle(float cx, float cy, float r, Color4 c, float width = 1f)
    {
        if (c.A <= 0) return;
        _solid.Color = c;
        Rt.DrawEllipse(new Ellipse(new Vector2(cx, cy), r, r), _solid, width);
    }

    /// <summary>Linienzug (Symbole): Punkte in DIPs, runde Enden und Ecken.</summary>
    public void Polyline(ReadOnlySpan<Vector2> pts, Color4 c, float width, bool closed = false, bool fill = false)
    {
        if (pts.Length < 2 || c.A <= 0) return;
        using var geo = D2D.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.BeginFigure(pts[0], fill ? FigureBegin.Filled : FigureBegin.Hollow);
            for (int i = 1; i < pts.Length; i++) sink.AddLine(pts[i]);
            sink.EndFigure(closed || fill ? FigureEnd.Closed : FigureEnd.Open);
            sink.Close();
        }
        _solid.Color = c;
        if (fill) Rt.FillGeometry(geo, _solid);
        else Rt.DrawGeometry(geo, _solid, width, _round);
    }

    public void PushClip(RectF r) => Rt.PushAxisAlignedClip(Raw(r), AntialiasMode.Aliased);
    public void PopClip() => Rt.PopAxisAlignedClip();

    public void PushOpacity(RectF bounds, float opacity)
    {
        _opacityLayer ??= Rt.CreateLayer();
        Rt.PushLayer(new LayerParameters
        {
            ContentBounds = Raw(bounds),
            MaskTransform = Matrix3x2.Identity,
            Opacity = opacity,
        }, _opacityLayer);
    }

    public void PopOpacity() => Rt.PopLayer();

    /// <summary>Weicher Schatten: mehrere abgestufte transparente Rundrechtecke unter r.</summary>
    public void Shadow(RectF r, float radius, float blur, float strength = 1f, float dy = 2f)
    {
        var baseC = Theme.ShadowColor;
        const int steps = 6;
        for (int i = steps; i >= 1; i--)
        {
            float g = blur * i / steps;
            float a = baseC.A * strength * 0.22f * (1f - (i - 1f) / steps);
            FillRound(r.Inflate(g).Offset(0, dy), radius + g, baseC.With(a));
        }
    }

    /// <summary>Zeichnet einen Bogen (Grad, 0 = rechts, im Uhrzeigersinn positiv) als Linienzug; Geometrie wird zwischengespeichert.</summary>
    public void Arc(float cx, float cy, float radius, float startDeg, float sweepDeg, Color4 c, float width)
    {
        if (MathF.Abs(sweepDeg) < 0.5f || c.A <= 0) return;
        var key = ((int)MathF.Round(radius * 8), (int)MathF.Round(startDeg * 4), (int)MathF.Round(sweepDeg * 4));
        if (!_arcs.TryGetValue(key, out var geo))
        {
            if (_arcs.Count > 400) { foreach (var g in _arcs.Values) g.Dispose(); _arcs.Clear(); }
            geo = D2D.CreatePathGeometry();
            using var sink = geo.Open();
            int n = Math.Max(4, (int)(MathF.Abs(sweepDeg) / 4f));
            for (int i = 0; i <= n; i++)
            {
                float a = (startDeg + sweepDeg * i / n) * MathF.PI / 180f;
                var p = new Vector2(MathF.Cos(a) * radius, MathF.Sin(a) * radius);
                if (i == 0) sink.BeginFigure(p, FigureBegin.Hollow); else sink.AddLine(p);
            }
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
            _arcs[key] = geo;
        }
        var old = Rt.Transform;
        Rt.Transform = Matrix3x2.CreateTranslation(cx, cy) * old;
        _solid.Color = c;
        Rt.DrawGeometry(geo, _solid, width, _round);
        Rt.Transform = old;
    }

    /// <summary>Senkrechter Meter-Balken mit dem Verlauf aus <see cref="Theme.MeterStops"/> (unten = Pos 0).</summary>
    public void FillMeterGradient(RectF bar, float bottom, float fullHeight, float radius = 0f)
    {
        if (bar.IsEmpty) return;
        if (_meterBrush == null)
        {
            var stops = new GradientStop[Theme.MeterStops.Length];
            for (int i = 0; i < stops.Length; i++) stops[i] = new GradientStop(Theme.MeterStops[i].Pos, Theme.MeterStops[i].Color);
            _meterStops = Rt.CreateGradientStopCollection(stops, Gamma.StandardRgb, ExtendMode.Clamp);
            _meterBrush = Rt.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(0, 0), new Vector2(0, 1)), _meterStops);
        }
        _meterBrush.Transform = Matrix3x2.CreateScale(1f, -fullHeight) * Matrix3x2.CreateTranslation(0, bottom);
        radius = Math.Min(radius, Math.Min(bar.W, bar.H) / 2);
        if (radius < 0.5f) Rt.FillRectangle(Raw(bar), _meterBrush);
        else
        {
            var rr = new RoundedRectangle(new System.Drawing.RectangleF(bar.X, bar.Y, bar.W, bar.H), radius, radius);
            Rt.FillRoundedRectangle(ref rr, _meterBrush);
        }
    }

    // ---- Text --------------------------------------------------------------------------------
    static IDWriteTextFormat GetFormat(TextStyle st, HAlign h, VAlign v, bool ellipsis, bool wrap)
    {
        var key = (st.Size, st.Weight, (int)h * 3 + (int)v, ellipsis, wrap);
        if (s_formats.TryGetValue(key, out var f)) return f;
        f = DW.CreateTextFormat(s_font.Family, s_font.Collection, st.Weight, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, st.Size, "de-de");
        f.TextAlignment = h switch { HAlign.Left => TextAlignment.Leading, HAlign.Center => TextAlignment.Center, _ => TextAlignment.Trailing };
        f.ParagraphAlignment = v switch { VAlign.Top => ParagraphAlignment.Near, VAlign.Middle => ParagraphAlignment.Center, _ => ParagraphAlignment.Far };
        f.WordWrapping = wrap ? WordWrapping.Wrap : WordWrapping.NoWrap;
        if (ellipsis)
        {
            using var sign = DW.CreateEllipsisTrimmingSign(f);
            f.SetTrimming(new Trimming { Granularity = TrimmingGranularity.Character }, sign);
        }
        s_formats[key] = f;
        return f;
    }

    public void Text(string? s, RectF r, TextStyle st, Color4 c, HAlign h = HAlign.Left, VAlign v = VAlign.Middle,
        bool ellipsis = true, bool wrap = false)
    {
        if (string.IsNullOrEmpty(s) || r.W < 1 || r.H < 1 || c.A <= 0) return;
        var fmt = GetFormat(st, h, v, ellipsis, wrap);
        _solid.Color = c;
        Rt.DrawText(s, fmt, new Rect(r.X, r.Y, r.W, r.H), _solid, DrawTextOptions.Clip);
    }

    public static float MeasureWidth(string s, TextStyle st)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var key = (s, st.Size, st.Weight);
        if (s_widths.TryGetValue(key, out var w)) return w;
        var fmt = GetFormat(st, HAlign.Left, VAlign.Top, false, false);
        using var layout = DW.CreateTextLayout(s, fmt, 4096f, 256f);
        w = layout.Metrics.WidthIncludingTrailingWhitespace;
        if (s_widths.Count > 4000) s_widths.Clear();
        s_widths[key] = w;
        return w;
    }

    /// <summary>Hoehe eines umbrochenen Textes bei gegebener Breite.</summary>
    public static float TextHeight(string s, TextStyle st, float width)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var fmt = GetFormat(st, HAlign.Left, VAlign.Top, false, true);
        using var layout = DW.CreateTextLayout(s, fmt, Math.Max(10, width), 4096f);
        return layout.Metrics.Height;
    }

    /// <summary>Einzeiliges Layout (links/oben, ohne Umbruch) fuer Texteditor-Treffertests. Der Aufrufer gibt es frei.</summary>
    public IDWriteTextLayout CreateLayout(string s, TextStyle st)
    {
        var fmt = GetFormat(st, HAlign.Left, VAlign.Top, false, false);
        return DW.CreateTextLayout(s.Length == 0 ? " " : s, fmt, 8192f, 512f);
    }

    public void DrawLayout(IDWriteTextLayout layout, float x, float y, Color4 c)
    {
        _solid.Color = c;
        Rt.DrawTextLayout(new Vector2(x, y), layout, _solid, DrawTextOptions.Clip);
    }

    public void Dispose()
    {
        foreach (var g in _arcs.Values) g.Dispose();
        _arcs.Clear();
        _meterBrush?.Dispose(); _meterStops?.Dispose();
        _opacityLayer?.Dispose();
        _round.Dispose(); _solid.Dispose();
    }
}
