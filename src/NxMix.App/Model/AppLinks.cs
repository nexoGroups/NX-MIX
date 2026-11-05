using NxMix.Audio;

namespace NxMix.App.Model;

/// <summary>
/// App-Link: ein Programm (Exe-Name, z. B. "spotify.exe") ist direkt an Engine-Eingaenge gebunden.
/// FirstChannel = 0-basierter Index des ersten Eingangs, Count 1 (mono) oder 2 (Stereo-Paar: FirstChannel und FirstChannel + 1).
/// </summary>
internal sealed record AppLink(string ExeName, int FirstChannel, int Count = 2, string? DisplayName = null)
{
    public int End => FirstChannel + Count;
    public bool Covers(int input) => input >= FirstChannel && input < End;
    /// <summary>Anzeigename: gespeicherter Name aus der Programmliste, sonst der huebsch gemachte Exe-Name.</summary>
    public string Title => string.IsNullOrWhiteSpace(DisplayName) ? AppLinkRules.PrettyName(ExeName) : DisplayName!;
}

/// <summary>Alles, was ein Projekt zu App-Links speichert (Links, Hauptschalter, Parkplatz-Ausgang).</summary>
internal sealed record AppLinkSetup(IReadOnlyList<AppLink> Links, bool Enabled, string? ParkingDeviceId, string? ParkingDeviceName);

/// <summary>Zustand eines App-Links in der Oberflaeche.</summary>
internal enum LinkUi { Off, Idle, NeedsSetup, Waiting, Linked, Failed, Conflict }

internal readonly record struct LinkView(LinkUi State, string? Message, int ProcessCount);

/// <summary>Ein Programm der Auswahlliste (mehrere Audio-Sitzungen desselben Exe-Namens sind zusammengefasst).</summary>
internal sealed record AppProgram(string ExeName, string DisplayName, bool IsPlaying, int Instances);

internal static class AppLinkRules
{
    /// <summary>"spotify.exe" -> "Spotify".</summary>
    public static string PrettyName(string? exe)
    {
        var n = Path.GetFileNameWithoutExtension(exe ?? "");
        if (n.Length == 0) return exe ?? "";
        return char.ToUpperInvariant(n[0]) + n[1..];
    }

    /// <summary>Macht aus Eingaben wie "Spotify", "C:\\...\\chrome.exe" oder " VLC.EXE " einen Exe-Namen ohne Pfad.
    /// null + Fehlertext bei unbrauchbarer Eingabe.</summary>
    public static string? NormalizeExe(string? raw, out string? error)
    {
        error = null;
        var s = (raw ?? "").Trim().Trim('"').Trim();
        int cut = Math.Max(s.LastIndexOf('\\'), s.LastIndexOf('/'));
        if (cut >= 0) s = s[(cut + 1)..].Trim();
        if (s.Length == 0) { error = "Bitte den Namen des Programms eingeben, z. B. spotify.exe."; return null; }
        if (s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) { error = "Der Name enthält ungültige Zeichen."; return null; }
        if (s.Length > 80) { error = "Der Name ist zu lang."; return null; }
        if (!s.Contains('.')) s += ".exe";
        else if (!s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { error = "Es werden nur Programme (.exe) unterstützt."; return null; }
        if (s.Length <= 4) { error = "Bitte den Namen des Programms eingeben, z. B. spotify.exe."; return null; }
        return s;
    }

    /// <summary>Gruppiert Audio-Sitzungen je Exe-Name; spielende zuerst, dann alphabetisch.</summary>
    public static List<AppProgram> Group(IReadOnlyList<AppSessionInfo> sessions)
    {
        var map = new Dictionary<string, List<AppSessionInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sessions)
        {
            if (string.IsNullOrWhiteSpace(s.ExeName)) continue;
            if (!map.TryGetValue(s.ExeName, out var l)) map[s.ExeName] = l = new List<AppSessionInfo>();
            l.Add(s);
        }
        var res = new List<AppProgram>();
        foreach (var (exe, l) in map)
        {
            string name = l.OrderByDescending(x => x.IsPlaying).Select(x => x.DisplayName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? PrettyName(exe);
            res.Add(new AppProgram(exe, name, l.Any(x => x.IsPlaying), l.Count));
        }
        return res.OrderByDescending(p => p.IsPlaying).ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Bereinigt geladene Links: gueltiger Exe-Name, Bereich passt in die Eingaenge, keine Ueberlappung, kein Programm doppelt.</summary>
    public static List<AppLink> Sanitize(IEnumerable<AppLink>? links, int inputCount)
    {
        var res = new List<AppLink>();
        if (links == null) return res;
        foreach (var l in links.OrderBy(x => x.FirstChannel))
        {
            var exe = NormalizeExe(l.ExeName, out _);
            if (exe == null || l.FirstChannel < 0 || l.FirstChannel >= inputCount) continue;
            int count = Math.Clamp(l.Count, 1, 2);
            if (l.FirstChannel + count > inputCount) count = inputCount - l.FirstChannel;
            if (count < 1) continue;
            if (res.Any(r => r.FirstChannel < l.FirstChannel + count && l.FirstChannel < r.End)) continue;
            if (res.Any(r => string.Equals(r.ExeName, exe, StringComparison.OrdinalIgnoreCase))) continue;
            res.Add(new AppLink(exe, l.FirstChannel, count, string.IsNullOrWhiteSpace(l.DisplayName) ? null : l.DisplayName.Trim()));
        }
        return res;
    }
}

// ================================================================================================
// Attrappen fuer Screenshots (--demo) und den Selbsttest: erfundene Programme, gibt nichts um und nichts aus.
// ================================================================================================

/// <summary>Erfundene Programmliste (Spotify, Chrome, Discord, VLC, ...) nur fuer Screenshots/Selbsttest.</summary>
internal sealed class DemoAppCatalog : IAppCatalog
{
    public IReadOnlyList<AppSessionInfo> List() => new[]
    {
        new AppSessionInfo(4120, "spotify.exe", "Spotify", @"C:\Users\Demo\AppData\Roaming\Spotify\Spotify.exe", true),
        new AppSessionInfo(5532, "chrome.exe", "Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", true),
        new AppSessionInfo(5540, "chrome.exe", "Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe", false),
        new AppSessionInfo(2210, "Discord.exe", "Discord", @"C:\Users\Demo\AppData\Local\Discord\Discord.exe", false),
        new AppSessionInfo(7788, "vlc.exe", "VLC media player", @"C:\Program Files\VideoLAN\VLC\vlc.exe", false),
        new AppSessionInfo(9014, "firefox.exe", "Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe", true),
        new AppSessionInfo(3320, "Teams.exe", "Microsoft Teams", null, false),
    };
}

/// <summary>Attrappe fuer IAppRouteRestorer: zaehlt nur die Aufrufe.</summary>
internal sealed class DemoRestorer : IAppRouteRestorer
{
    readonly bool _supported;
    readonly string? _reason;
    public int Calls;
    public DemoRestorer(bool supported = true, string? reason = null) { _supported = supported; _reason = reason; }
    public bool IsSupported => _supported;
    public string? UnsupportedReason => _supported ? null : _reason ?? "Diese Windows-Version kann Programm-Ausgaben nicht einzeln umleiten.";
    public void RestoreAllKnown() => Calls++;
}
