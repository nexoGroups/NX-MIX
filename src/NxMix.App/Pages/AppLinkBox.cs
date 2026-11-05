using NxMix.App.Model;
using NxMix.App.Ui;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Darstellung der Link-Zustaende (LED-Farbe, Kurztexte) - gemeinsam fuer Streifen und Einstellungen.</summary>
internal static class LinkLook
{
    public static Color4 Led(LinkUi s) => s switch
    {
        LinkUi.Linked => Theme.Ok,
        LinkUi.Waiting or LinkUi.NeedsSetup => Theme.Solo,
        LinkUi.Failed or LinkUi.Conflict => Theme.Mute,
        _ => Theme.TextFaint,
    };

    /// <summary>(lang, kurz) - die Kurzform nimmt der schmale Streifen, wenn die lange nicht passt.</summary>
    public static (string Long, string Short) Text(LinkUi s) => s switch
    {
        LinkUi.Linked => ("VERBUNDEN", "OK"),
        LinkUi.Waiting => ("WARTET", "WARTET"),
        LinkUi.Failed => ("FEHLER", "FEHLER"),
        LinkUi.Conflict => ("KONFLIKT", "KONFL."),
        LinkUi.NeedsSetup => ("SETUP", "SETUP"),
        LinkUi.Off => ("AUS", "AUS"),
        _ => ("BEREIT", "BEREIT"),
    };

    public static string Describe(AppLink l, LinkView v)
    {
        string head = $"{l.Title} ({l.ExeName}) · {(l.Count == 2 ? "Stereo" : "mono")}";
        string state = v.State switch
        {
            LinkUi.Linked => "verbunden" + (v.ProcessCount > 1 ? $" ({v.ProcessCount} Prozesse)" : ""),
            LinkUi.Waiting => "wartet auf das Programm",
            LinkUi.Failed => "Fehler",
            LinkUi.Conflict => "Konflikt",
            LinkUi.NeedsSetup => "noch nicht startklar",
            LinkUi.Off => "ausgeschaltet",
            _ => "wird beim Start von Audio verbunden",
        };
        return head + " – " + state + (string.IsNullOrWhiteSpace(v.Message) ? "" : ": " + v.Message);
    }
}

/// <summary>
/// App-Link-Feld eines Eingangsstreifens: ohne Link ein eckiger Kettenknopf, mit Link ein Etikett mit Status-LED, Programm und Stereo-Hinweis.
/// Klick oeffnet das Auswahl-Popup. Belegt immer dieselbe Hoehe (SlotHeight), damit die Fader aller Streifen fluchten.
/// </summary>
internal sealed class AppLinkBox : Widget
{
    public const float SlotHeight = 36f, ButtonHeight = 24f;
    readonly AppModel _m;
    readonly ChannelStrip _strip;

    public AppLinkBox(AppModel m, ChannelStrip strip) { _m = m; _strip = strip; Cursor = CursorKind.Hand; }

    AppLink? Link => _m.LinkAt(_strip.Index);

    /// <summary>Tatsaechlich gezeichneter (und klickbarer) Bereich.</summary>
    public RectF DrawRect => Link != null ? new RectF(Bounds.X, Bounds.Y, Bounds.W, SlotHeight) : new RectF(Bounds.X, Bounds.Y, Bounds.W, ButtonHeight);

    public override bool AcceptsMouse => Visible;
    public override Widget? HitTest(float x, float y) => Visible && DrawRect.Contains(x, y) ? this : null;
    public override CursorKind CursorAt(float x, float y) => _m.HostRunning ? CursorKind.Arrow : CursorKind.Hand;
    public override void OnMouseEnter() => Invalidate();
    public override void OnMouseLeave() => Invalidate();
    public override void OnMouseDown(in MouseEvent e) => Invalidate();

    public override void OnMouseUp(in MouseEvent e)
    {
        Invalidate();
        if (e.Button == 0 && Enabled && DrawRect.Contains(e.X, e.Y)) Open();
    }

    public void Open(bool manual = false)
    {
        var root = Root; if (root == null) return;
        if (_m.HostRunning)
        {
            root.Status.Flash("App-Links lassen sich nur bei gestopptem Audio ändern – zuerst „Audio stoppen“.", true);
            return;
        }
        AppLinkPicker.Show(root, _m, _strip.Index, DrawRect, manual);
    }

    public override string? TooltipAt(float x, float y)
    {
        var l = Link;
        if (l == null) return _m.HostRunning ? "App-Links lassen sich nur bei gestopptem Audio ändern" : "App-Link: ein Programm (z. B. Spotify) an diesen Eingang binden";
        return LinkLook.Describe(l, _m.ViewOf(l));
    }

    public override void Paint(Renderer g)
    {
        var l = Link;
        var r = DrawRect;
        bool hot = IsHovered && !_m.HostRunning;
        if (l == null)
        {
            bool dis = _m.HostRunning;
            g.Fill(r, dis ? Theme.Card : hot ? Theme.RaisedHi : Theme.Raised);
            g.StrokeRect(r, Theme.BorderStrong.With(dis ? 0.35f : 0.7f));
            Icons.Link(g, r.CX, r.CY, 15, dis ? Theme.TextFaint : hot ? Theme.Text : Theme.TextDim);
            return;
        }

        var v = _m.ViewOf(l);
        bool off = v.State == LinkUi.Off;
        g.Fill(r, off ? Theme.Card : hot ? Theme.Accent.With(0.28f) : Theme.AccentDim);
        g.StrokeRect(r, off ? Theme.Border : Theme.Accent.With(hot ? 1f : 0.6f));

        // Zeile 1: LED + Status
        var led = LinkLook.Led(v.State);
        float lx = r.X + 7.5f, cy1 = r.Y + 10;
        var (lng, sht) = LinkLook.Text(v.State);
        float avail = r.W - 15 - 3;
        string? st = Renderer.MeasureWidth(lng, Theme.TinyBold) <= avail ? lng : Renderer.MeasureWidth(sht, Theme.TinyBold) <= avail ? sht : null;
        if (st != null)
        {
            g.FillCircle(lx, cy1, 3f, led);
            g.Text(st, new RectF(r.X + 14, r.Y + 3, avail, 14), Theme.TinyBold, off ? Theme.TextDim : Theme.Text, HAlign.Left, VAlign.Middle, false);
        }
        else g.FillCircle(r.CX, cy1, 4f, led); // sehr schmaler Streifen: nur die LED (Text im Tooltip)

        // Zeile 2: Programm ohne ".exe" (bei Stereo: zweite Haelfte "Stereo · R"); breite Streifen zeigen zusaetzlich die Kette
        bool second = _strip.Index != l.FirstChannel;
        float y2 = r.Y + 19;
        if (second)
        {
            float aw = r.W - 9;
            string rt = Renderer.MeasureWidth("Stereo · R", Theme.Tiny) <= aw ? "Stereo · R" : Renderer.MeasureWidth("Stereo R", Theme.Tiny) <= aw ? "Stereo R" : "R";
            g.Text(rt, new RectF(r.X + 6, y2, aw, 14), Theme.Tiny, Theme.TextDim, HAlign.Left, VAlign.Middle, false);
        }
        else
        {
            bool chain = r.W >= 76;
            if (chain) Icons.Link(g, r.X + 12, y2 + 7, 10, Theme.TextDim, 1.1f);
            string t = l.ExeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? l.ExeName[..^4] : l.ExeName;
            float tx = chain ? r.X + 20 : r.X + 6;
            g.Text(t, new RectF(tx, y2, r.Right - tx - 3, 14), Theme.Tiny, Theme.TextDim, HAlign.Left, VAlign.Middle, true);
        }
    }
}
