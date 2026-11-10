using NxMix.App.Model;
using NxMix.App.Ui;
using Vortice.Mathematics;

namespace NxMix.App.Pages;

/// <summary>Darstellung der App-Link-Zustaende (Farbe, Texte) - gemeinsam fuer Streifen, Kanal-Blatt und Einstellungen.</summary>
internal static class LinkLook
{
    public static Color4 Color(LinkUi s) => s switch
    {
        LinkUi.Linked => Theme.Green,
        LinkUi.Waiting or LinkUi.NeedsSetup or LinkUi.AudioBlocked or LinkUi.Capturing or LinkUi.MixedWithoutRedirect => Theme.Orange,
        LinkUi.Failed or LinkUi.Conflict => Theme.Red,
        LinkUi.Off => Theme.TextTertiary,
        _ => Theme.AccentText,
    };

    public static string Text(LinkUi s) => s switch
    {
        LinkUi.Linked => "Über NX-MIX",
        LinkUi.Capturing => "Windows-Umleitung offen",
        LinkUi.MixedWithoutRedirect => "NX-MIX + Windows",
        LinkUi.Waiting => "Wartet auf das Programm",
        LinkUi.Failed => "Fehler",
        LinkUi.Conflict => "Konflikt",
        LinkUi.NeedsSetup => "Noch nicht startklar",
        LinkUi.AudioBlocked => "Audio-Start blockiert",
        LinkUi.Off => "Ausgeschaltet",
        _ => "Audio-Engine starten",
    };

    public static string Describe(AppLink l, LinkView v)
    {
        string head = $"{l.Title} ({l.ExeName}) · {(l.Count == 2 ? "Stereo" : "mono")}";
        string state = v.State switch
        {
            LinkUi.Linked => "über NX-MIX" + (v.ProcessCount > 1 ? $" ({v.ProcessCount} Prozesse)" : ""),
            LinkUi.Capturing => "erfasstes Signal wird gemischt",
            LinkUi.MixedWithoutRedirect => "wird gemischt, Windows-Ausgabe bleibt aktiv",
            LinkUi.Waiting => "wartet auf das Programm",
            LinkUi.Failed => "Fehler",
            LinkUi.Conflict => "Konflikt",
            LinkUi.NeedsSetup => "noch nicht startklar",
            LinkUi.AudioBlocked => "Audio-Start blockiert",
            LinkUi.Off => "ausgeschaltet",
            _ => "wartet auf den Start der Audio-Engine",
        };
        return head + " – " + state + (string.IsNullOrWhiteSpace(v.Message) ? "" : ": " + v.Message);
    }

    /// <summary>"Eingang 07" bzw. "Eingang 07 + 08".</summary>
    public static string Target(AppLink l) =>
        l.Count == 2 ? $"Eingang {l.FirstChannel + 1:00} + {l.FirstChannel + 2:00}" : $"Eingang {l.FirstChannel + 1:00}";
}
