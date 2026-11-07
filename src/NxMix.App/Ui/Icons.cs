using System.Numerics;
using Vortice.Mathematics;

namespace NxMix.App.Ui;

/// <summary>Alle Symbole der Oberfläche im klaren Vektor-Linienstil.</summary>
internal enum Icon
{
    None, Mixer, Decks, Matrix, Devices, Vaio, Dsp, Snapshots, Engine, Settings, Sidebar,
    Plus, Minus, Close, ChevronDown, ChevronUp, ChevronLeft, ChevronRight,
    Check, Lock, LockOpen, Link, Trash, Refresh, Warn, Info, Search, More, Play, Stop, Mic, Speaker, Loopback,
    Export, Import, Document, Clock, Shield, Keyboard, Phase, Bars, Edit, ArrowRight, UpDown, Palette, Hand,
    Mute, Copy,
}

internal static class Icons
{
    [ThreadStatic] static Vector2[]? t_buf;

    static float W(float s) => Math.Max(1.25f, s * 0.075f);

    public static void Draw(Renderer g, Icon icon, float cx, float cy, float s, Color4 c)
    {
        if (icon == Icon.None || c.A <= 0) return;
        float k = s / 24f;
        Vector2 P(float x, float y) => new(cx + (x - 12) * k, cy + (y - 12) * k);
        float w = W(s);
        void L(float x1, float y1, float x2, float y2) { var a = P(x1, y1); var b = P(x2, y2); g.Line(a.X, a.Y, b.X, b.Y, c, w, true); }
        void Poly(bool closed, params float[] xy)
        {
            int n = xy.Length / 2;
            var buf = t_buf is { Length: >= 64 } ? t_buf : (t_buf = new Vector2[64]);
            for (int i = 0; i < n; i++) buf[i] = P(xy[2 * i], xy[2 * i + 1]);
            g.Polyline(buf.AsSpan(0, n), c, w, closed);
        }
        void FillPoly(params float[] xy)
        {
            int n = xy.Length / 2;
            var buf = t_buf is { Length: >= 64 } ? t_buf : (t_buf = new Vector2[64]);
            for (int i = 0; i < n; i++) buf[i] = P(xy[2 * i], xy[2 * i + 1]);
            g.Polyline(buf.AsSpan(0, n), c, w, true, fill: true);
        }
        void Circle(float x, float y, float r) { var p = P(x, y); g.StrokeCircle(p.X, p.Y, r * k, c, w); }
        void Dot(float x, float y, float r) { var p = P(x, y); g.FillCircle(p.X, p.Y, r * k, c); }
        void Arc(float x, float y, float r, float start, float sweep) { var p = P(x, y); g.Arc(p.X, p.Y, r * k, start, sweep, c, w); }
        void RRect(float x1, float y1, float x2, float y2, float r)
        {
            var a = P(x1, y1); var b = P(x2, y2);
            var rect = RectF.FromLTRB(a.X, a.Y, b.X, b.Y);
            g.StrokeRound(rect.Inflate(w / 2), r * k + w / 2, c, w);
        }

        switch (icon)
        {
            case Icon.Mixer:
                {
                    float[] xs = { 6, 12, 18 }, ks = { 14.5f, 8.5f, 16 };
                    for (int i = 0; i < 3; i++)
                    {
                        L(xs[i], 3.5f, xs[i], ks[i] - 2.6f);
                        L(xs[i], ks[i] + 2.6f, xs[i], 20.5f);
                        Circle(xs[i], ks[i], 2.4f);
                    }
                    break;
                }
            case Icon.Decks:
                Poly(true, 12, 4, 20.5f, 8.5f, 12, 13, 3.5f, 8.5f);
                Poly(false, 3.5f, 12.5f, 12, 17, 20.5f, 12.5f);
                Poly(false, 3.5f, 16.5f, 12, 21, 20.5f, 16.5f);
                break;
            case Icon.Matrix:
                RRect(4, 4, 10.5f, 10.5f, 2); RRect(13.5f, 4, 20, 10.5f, 2);
                RRect(4, 13.5f, 10.5f, 20, 2); RRect(13.5f, 13.5f, 20, 20, 2);
                break;
            case Icon.Devices:
                RRect(3, 7, 21, 17, 2.5f);
                Circle(8, 12, 2.2f); Circle(13.5f, 12, 2.2f);
                L(17.5f, 10, 17.5f, 14);
                break;
            case Icon.Vaio:
                RRect(5, 7.5f, 13, 16.5f, 2);
                L(2.5f, 10, 5, 10); L(2.5f, 14, 5, 14);
                Poly(false, 13, 12, 17, 12, 17, 8, 21.5f, 8);
                Poly(false, 17, 12, 17, 16, 21.5f, 16);
                break;
            case Icon.Dsp:
                L(3, 18.5f, 21, 18.5f);
                Poly(false, 3.5f, 15.5f, 7.5f, 15f, 10.5f, 6.5f, 13.5f, 16.5f, 17f, 11.5f, 20.5f, 12f);
                break;
            case Icon.Snapshots:
                Poly(true, 6, 4, 18, 4, 18, 20, 12, 15.5f, 6, 20);
                L(9, 8.5f, 15, 8.5f);
                break;
            case Icon.Engine:
                RRect(6.5f, 6.5f, 17.5f, 17.5f, 2);
                RRect(9.5f, 9.5f, 14.5f, 14.5f, 1);
                L(9, 3.5f, 9, 6.5f); L(15, 3.5f, 15, 6.5f);
                L(9, 17.5f, 9, 20.5f); L(15, 17.5f, 15, 20.5f);
                L(3.5f, 9, 6.5f, 9); L(3.5f, 15, 6.5f, 15);
                L(17.5f, 9, 20.5f, 9); L(17.5f, 15, 20.5f, 15);
                break;
            case Icon.Settings:
                {
                    var buf = t_buf is { Length: >= 64 } ? t_buf : (t_buf = new Vector2[64]);
                    int n = 48;
                    for (int i = 0; i < n; i++)
                    {
                        float a = i * MathF.PI * 2 / n;
                        float phase = i % 6;
                        float r = phase is 0 or 1 or 2 ? 9.2f : 7.0f;
                        buf[i] = P(12 + MathF.Cos(a + 0.065f) * r, 12 + MathF.Sin(a + 0.065f) * r);
                    }
                    g.Polyline(buf.AsSpan(0, n), c, w, true);
                    Circle(12, 12, 3);
                    break;
                }
            case Icon.Sidebar:
                RRect(3, 4.5f, 21, 19.5f, 3);
                L(9.5f, 4.5f, 9.5f, 19.5f);
                break;
            case Icon.Plus: L(12, 5, 12, 19); L(5, 12, 19, 12); break;
            case Icon.Minus: L(5, 12, 19, 12); break;
            case Icon.Close: L(6, 6, 18, 18); L(6, 18, 18, 6); break;
            case Icon.ChevronDown: Poly(false, 6, 9, 12, 15, 18, 9); break;
            case Icon.ChevronUp: Poly(false, 6, 15, 12, 9, 18, 15); break;
            case Icon.ChevronLeft: Poly(false, 15, 6, 9, 12, 15, 18); break;
            case Icon.ChevronRight: Poly(false, 9, 6, 15, 12, 9, 18); break;
            case Icon.UpDown: Poly(false, 8, 9.5f, 12, 5.5f, 16, 9.5f); Poly(false, 8, 14.5f, 12, 18.5f, 16, 14.5f); break;
            case Icon.Check: Poly(false, 5, 12.5f, 10, 17.5f, 19.5f, 7); break;
            case Icon.Lock:
            case Icon.LockOpen:
                {
                    RRect(5, 11, 19, 20.5f, 2.5f);
                    if (icon == Icon.Lock)
                    {
                        L(8, 11, 8, 8); L(16, 11, 16, 8);
                        Arc(12, 8, 4, 180, 180);
                    }
                    else
                    {
                        L(8, 11, 8, 6.5f); L(16, 6.5f, 16, 7.5f);
                        Arc(12, 6.5f, 4, 180, 180);
                    }
                    Dot(12, 15.75f, 1.3f);
                    break;
                }
            case Icon.Link:
                RRect(2.5f, 8.5f, 13, 15.5f, 3.5f);
                RRect(11, 8.5f, 21.5f, 15.5f, 3.5f);
                break;
            case Icon.Trash:
                L(4, 6.5f, 20, 6.5f);
                Poly(false, 9, 6.5f, 9, 4, 15, 4, 15, 6.5f);
                Poly(false, 6, 6.5f, 7, 20, 17, 20, 18, 6.5f);
                L(10, 10.5f, 10, 16); L(14, 10.5f, 14, 16);
                break;
            case Icon.Refresh:
                Arc(12, 12, 7.5f, -50, 300);
                Poly(false, 16.4f, 3.6f, 17.2f, 6.3f, 14.4f, 7.2f);
                break;
            case Icon.Warn:
                Poly(true, 12, 3.5f, 21.5f, 19.5f, 2.5f, 19.5f);
                L(12, 9.5f, 12, 13.5f);
                Dot(12, 16.5f, 1.2f);
                break;
            case Icon.Info:
                Circle(12, 12, 9);
                L(12, 11, 12, 16.5f);
                Dot(12, 7.8f, 1.2f);
                break;
            case Icon.Search:
                Circle(10.5f, 10.5f, 6.5f);
                L(15.5f, 15.5f, 20, 20);
                break;
            case Icon.More:
                Dot(5.5f, 12, 1.8f); Dot(12, 12, 1.8f); Dot(18.5f, 12, 1.8f);
                break;
            case Icon.Play:
                FillPoly(7.5f, 4.5f, 19.5f, 12, 7.5f, 19.5f);
                break;
            case Icon.Stop:
                {
                    var a = P(6, 6); var b = P(18, 18);
                    g.FillRound(RectF.FromLTRB(a.X, a.Y, b.X, b.Y), 2.5f * k, c);
                    break;
                }
            case Icon.Mic:
                RRect(9, 3, 15, 14, 3);
                Arc(12, 11, 6, 0, 180);
                L(12, 17, 12, 21); L(9, 21, 15, 21);
                break;
            case Icon.Speaker:
                Poly(true, 3.5f, 9, 7.5f, 9, 12.5f, 4.5f, 12.5f, 19.5f, 7.5f, 15, 3.5f, 15);
                Arc(13, 12, 4, -45, 90);
                Arc(13, 12, 7.5f, -50, 100);
                break;
            case Icon.Mute:
                Poly(true, 3.5f, 9, 7.5f, 9, 12.5f, 4.5f, 12.5f, 19.5f, 7.5f, 15, 3.5f, 15);
                L(15.5f, 9, 20.5f, 15); L(20.5f, 9, 15.5f, 15);
                break;
            case Icon.Copy:
                RRect(8, 8, 19.5f, 19.5f, 2);
                Poly(false, 6, 15.5f, 4.5f, 15.5f, 4.5f, 4.5f, 15.5f, 4.5f, 15.5f, 6);
                break;
            case Icon.Loopback:
                Poly(false, 5, 11, 5, 8, 18.5f, 8);
                Poly(false, 15.5f, 5, 18.5f, 8, 15.5f, 11);
                Poly(false, 19, 13, 19, 16, 5.5f, 16);
                Poly(false, 8.5f, 13, 5.5f, 16, 8.5f, 19);
                break;
            case Icon.Export:
                Poly(false, 4, 14, 4, 20, 20, 20, 20, 14);
                L(12, 15, 12, 4);
                Poly(false, 7.5f, 8.5f, 12, 4, 16.5f, 8.5f);
                break;
            case Icon.Import:
                Poly(false, 4, 14, 4, 20, 20, 20, 20, 14);
                L(12, 4, 12, 15);
                Poly(false, 7.5f, 10.5f, 12, 15, 16.5f, 10.5f);
                break;
            case Icon.Document:
                Poly(true, 6, 3, 14, 3, 19, 8, 19, 21, 6, 21);
                Poly(false, 14, 3, 14, 8, 19, 8);
                break;
            case Icon.Clock:
                Circle(12, 12, 9);
                Poly(false, 12, 7, 12, 12, 15.5f, 14);
                break;
            case Icon.Shield:
                Poly(true, 12, 3, 19.5f, 6, 19.5f, 11.5f, 17.5f, 16, 12, 21, 6.5f, 16, 4.5f, 11.5f, 4.5f, 6);
                break;
            case Icon.Keyboard:
                RRect(2.5f, 6, 21.5f, 18, 2.5f);
                Dot(6.5f, 10, 1); Dot(10, 10, 1); Dot(13.5f, 10, 1); Dot(17, 10, 1);
                L(8, 14.5f, 16, 14.5f);
                break;
            case Icon.Phase:
                Circle(12, 12, 6.5f);
                L(6, 18, 18, 6);
                break;
            case Icon.Bars:
                {
                    float[] hs = { 9, 15, 11 };
                    for (int i = 0; i < 3; i++)
                    {
                        var a = P(5 + i * 5.5f, 20 - hs[i]); var b = P(8.5f + i * 5.5f, 20);
                        g.FillRound(RectF.FromLTRB(a.X, a.Y, b.X, b.Y), 1 * k, c);
                    }
                    break;
                }
            case Icon.Edit:
                Poly(true, 5, 19, 5, 15.5f, 15, 5.5f, 18.5f, 9, 8.5f, 19);
                L(13, 7.5f, 16.5f, 11);
                break;
            case Icon.ArrowRight:
                L(4, 12, 20, 12);
                Poly(false, 14, 6, 20, 12, 14, 18);
                break;
            case Icon.Palette:
                Circle(12, 12, 9);
                Dot(8, 10, 1.4f); Dot(12, 7.5f, 1.4f); Dot(16, 10, 1.4f); Dot(10, 15.5f, 1.4f);
                break;
            case Icon.Hand:
                RRect(8, 3, 12, 14, 2);
                Poly(false, 12, 9, 17.5f, 10, 19, 12, 18, 18, 15, 21, 9.5f, 21, 5, 15, 6.5f, 13.5f, 8, 14);
                break;
        }
    }

    public static void StatusDot(Renderer g, float cx, float cy, Color4 c, float r = 3.5f, bool halo = true)
    {
        if (halo) g.FillCircle(cx, cy, r + 2.5f, c.With(0.22f));
        g.FillCircle(cx, cy, r, c);
    }
}
