using NxMix.Audio;
using NxMix.Engine;

namespace NxMix.App.Model;

/// <summary>Kleines Beispiel-Setup NUR fuer Screenshots (--demo). Die echte App startet leer.</summary>
internal static class DemoProject
{
    public const string ParkingId = "demo-park-1";
    public const string ParkingName = "Parkplatz (virtuelles Kabel, stumm)";

    /// <summary>Erfundene App-Links: Spotify (Stereo, verbunden), Chrome (mono, wartet), Discord (mono, Fehler).</summary>
    public static AppLinkSetup AppLinks() => new(new[]
    {
        new AppLink("spotify.exe", 6, 2, "Spotify"),
        new AppLink("chrome.exe", 8, 1, "Google Chrome"),
        new AppLink("Discord.exe", 9, 1, "Discord"),
    }, true, ParkingId, ParkingName);

    public static IReadOnlyList<AppLinkStatus> LinkStatuses() => new[]
    {
        new AppLinkStatus("spotify.exe", AppLinkState.Linked, 1, null, 0.5),
        new AppLinkStatus("chrome.exe", AppLinkState.WaitingForApp, 0, null, 0),
        new AppLinkStatus("Discord.exe", AppLinkState.Failed, 1, "Prozess-Loopback abgelehnt (Zugriff verweigert)", 0),
    };

    public static (MixerSettings Settings, List<DeviceBinding> Bindings) Create()
    {
        string[] ins = { "Front L", "Front R", "Center", "LFE", "Surround L", "", "", "", "", "", "" };
        string[] outs = { "Links Mid", "Rechts Mid", "Hi Links", "Hi Rechts", "Center", "Bass Mono", "Rear L", "Rear R" };
        var s = MixerSettings.Create(96000, ins.Length, outs.Length);
        var inputs = ins.Select(n => new ChannelDsp { Name = n }).ToArray();
        var outputs = outs.Select(n => new ChannelDsp { Name = n }).ToArray();
        // Hochtoener-Schutz als Beispiel fuer die Gain-Sperre
        outputs[2] = outputs[2] with { GainDb = -13, MaxGainDb = -13 };
        outputs[3] = outputs[3] with { GainDb = -13, MaxGainDb = -13 };
        outputs[5] = outputs[5] with { GainDb = -3.5, CeilingDbfs = -6 };
        outputs[0] = outputs[0] with { DelayMs = 0.87 };
        outputs[1] = outputs[1] with { DelayMs = 0.65 };
        outputs[7] = outputs[7] with { Mute = true };
        inputs[1] = inputs[1] with { GainDb = -2.5 };
        inputs[3] = inputs[3] with { Solo = false, InvertPhase = true };
        var m = s.MatrixDb;
        void Link(int i, int o, double db = 0) => m[i][o] = db;
        Link(0, 0); Link(1, 1); Link(0, 2); Link(1, 3); Link(2, 4); Link(3, 5, -2); Link(0, 5, -6); Link(1, 5, -6);
        Link(4, 6); Link(5, 7, -1.5); Link(4, 0, -9.5);
        Link(6, 0, -3); Link(7, 1, -3); Link(8, 4, -6); Link(9, 4, -8);
        var settings = s with { Inputs = inputs, Outputs = outputs, MatrixDb = m };
        var bindings = new List<DeviceBinding>
        {
            new("demo-in-1", DeviceFlow.Capture, 0, 0, 4),
            new("demo-out-1", DeviceFlow.Render, 0, 0, 8),
            new("demo-out-3", DeviceFlow.Capture, 0, 4, 2, true),
        };
        return (settings, bindings);
    }
}
