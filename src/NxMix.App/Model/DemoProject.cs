using NxMix.Audio;
using NxMix.Engine;
using NxMix.Ipc;

namespace NxMix.App.Model;

/// <summary>Vollständiges Beispiel-Livesetup für Screenshots (--demo) und den Selbsttest (--selftest).</summary>
internal static class DemoProject
{
    public const string ParkingId = "demo-vcable-in";
    public const string ParkingName = "CABLE Input (VB-Audio Virtual Cable, stumm)";

    public static readonly Guid InDevId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid OutDevId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid VaioCableId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid MusicBusId = Guid.Parse("44444444-4444-4444-4444-444444444444");

    public static IReadOnlyList<AppLinkStatus> LinkStatuses() => new[]
    {
        new AppLinkStatus("spotify.exe", AppLinkState.Linked, 1, null, 0.5),
        new AppLinkStatus("chrome.exe", AppLinkState.WaitingForApp, 0, null, 0),
        new AppLinkStatus("Discord.exe", AppLinkState.Failed, 1, "Prozess-Loopback abgelehnt (Zugriff verweigert)", 0),
    };

    public static (SystemConfig Config, MixerSettings Mixer, UiDeckCollection Decks) Create(bool runningOnBoot = false)
    {
        var config = new SystemConfig
        {
            SampleRate = 48000,
            BlockFrames = 256,
            PreferredMasterDeviceId = "asio:RME Fireface USB/out",
            AutoStartAudioOnBoot = runningOnBoot,
            AllowOfflineAuxDevices = true,
            AppLinksEnabled = true,
            ParkingDeviceId = ParkingId,
            ParkingDeviceName = ParkingName,
            CompanionApiEnabled = false, // In Headless-Tests keinen Port belegen
            Devices = new[]
            {
                new AudioDeviceNode
                {
                    Id = InDevId,
                    EndpointId = "asio:RME Fireface USB/in",
                    FriendlyName = "RME Fireface USB (ASIO)",
                    CustomTitle = "RME Fireface In",
                    Flow = DeviceFlow.Capture,
                    Backend = AudioBackend.Asio,
                    DeviceFirstChannel = 0,
                    EngineFirstChannel = 0,
                    ChannelCount = 4,
                },
                new AudioDeviceNode
                {
                    Id = OutDevId,
                    EndpointId = "asio:RME Fireface USB/out",
                    FriendlyName = "RME Fireface USB (ASIO)",
                    CustomTitle = "RME Studio 8ch Out",
                    Flow = DeviceFlow.Render,
                    Backend = AudioBackend.Asio,
                    DeviceFirstChannel = 0,
                    EngineFirstChannel = 0,
                    ChannelCount = 8,
                },
            },
            Vaios = new[]
            {
                new VaioSlotConfig
                {
                    Id = VaioCableId,
                    Name = "VB-Cable Zuspieler",
                    Kind = VaioSlotKind.ExternalDriverEndpoint,
                    Flow = DeviceFlow.Capture,
                    Channels = 2,
                    EngineFirstChannel = 4,
                    EndpointOrExe = "demo-vcable-out",
                },
            },
            AppLinks = new[]
            {
                new AppLinkEntry { ExeName = "spotify.exe", DisplayName = "Spotify", FirstChannel = 6, Count = 2 },
                new AppLinkEntry { ExeName = "chrome.exe", DisplayName = "Google Chrome", FirstChannel = 8, Count = 1 },
                new AppLinkEntry { ExeName = "Discord.exe", DisplayName = "Discord", FirstChannel = 9, Count = 1 },
            },
        };

        string[] inNames = { "Moderator Mic", "Gast Mic", "Synth L", "Synth R", "VAIO L", "VAIO R", "Spotify L", "Spotify R", "Chrome", "Discord" };
        string[] outNames = { "Mid Links", "Mid Rechts", "Tweeter L", "Tweeter R", "Center", "Sub Mono", "Nearfield L", "Nearfield R" };

        Guid stereoSynthLink = Guid.NewGuid();
        Guid stereoMainLink = Guid.NewGuid();

        var inputs = new ChannelDsp[inNames.Length];
        for (int i = 0; i < inputs.Length; i++)
        {
            Guid? devId = i < 4 ? InDevId : i < 6 ? VaioCableId : null;
            int devCh = i < 4 ? i : i < 6 ? i - 4 : i;
            inputs[i] = new ChannelDsp
            {
                Name = inNames[i],
                DeviceId = devId,
                DeviceChannelIndex = devCh,
                LinkGroupId = (i is 2 or 3) ? stereoSynthLink : null,
                GainDb = i == 1 ? -2.5 : 0.0,
                Filters = i < 2 ? new[] { new FilterSpec(FilterType.HighPass, 80, 0.7071, 0, Order: 2) } : Array.Empty<FilterSpec>(),
                RecallSafe = i == 0 ? (RecallSafeFlags.Fader | RecallSafeFlags.Mute) : RecallSafeFlags.None,
            };
        }

        var outputs = new ChannelDsp[outNames.Length];
        for (int o = 0; o < outputs.Length; o++)
        {
            outputs[o] = new ChannelDsp
            {
                Name = outNames[o],
                DeviceId = OutDevId,
                DeviceChannelIndex = o,
                LinkGroupId = (o is 0 or 1) ? stereoMainLink : null,
                GainDb = o is 2 or 3 ? -13.0 : o == 5 ? -3.5 : 0.0,
                MaxGainDb = o is 2 or 3 ? -12.0 : 12.0,
                CeilingDbfs = o == 5 ? -6.0 : -1.0,
                DelayMs = o == 0 ? 0.85 : o == 1 ? 0.65 : 0.0,
                Filters = o is 2 or 3
                    ? new[] { new FilterSpec(FilterType.HighPass, 2200, 0.7071, 0, Order: 4, LinkwitzRiley: true) }
                    : o == 5
                        ? new[] { new FilterSpec(FilterType.LowPass, 85, 0.7071, 0, Order: 4, LinkwitzRiley: true) }
                        : Array.Empty<FilterSpec>(),
                Correction = o is 0 or 1
                    ? new OutputCorrection("nahfeld-raumkorrektur.txt", new[]
                    {
                        new FilterSpec(FilterType.Peak, 63.0, 4.0, -4.8),
                        new FilterSpec(FilterType.LowShelf, 110.0, 0.7071, 1.5),
                    }, Bypassed: false, PreampDb: -1.5)
                    : null,
            };
        }

        var m = new double[inputs.Length][];
        for (int i = 0; i < inputs.Length; i++)
        {
            m[i] = new double[outputs.Length];
            Array.Fill(m[i], double.NegativeInfinity);
        }
        m[0][0] = 0; m[0][1] = 0;
        m[1][0] = 0; m[1][1] = 0;
        m[2][0] = 0; m[3][1] = 0;
        m[0][2] = -3; m[1][3] = -3;
        m[2][5] = -6; m[3][5] = -6;
        m[4][0] = -2; m[5][1] = -2;

        var bus = new MixBus { Id = MusicBusId, Name = "Musik-Bus", Channels = 2, GainDb = -3.0 };
        var busRoutes = new[]
        {
            new BusRoute(new RoutePort(RouteKind.Input, inputs[6].Id), new RoutePort(RouteKind.Bus, MusicBusId, 0), 0),
            new BusRoute(new RoutePort(RouteKind.Input, inputs[7].Id), new RoutePort(RouteKind.Bus, MusicBusId, 1), 0),
            new BusRoute(new RoutePort(RouteKind.Bus, MusicBusId, 0), new RoutePort(RouteKind.Output, outputs[0].Id), 0),
            new BusRoute(new RoutePort(RouteKind.Bus, MusicBusId, 1), new RoutePort(RouteKind.Output, outputs[1].Id), 0),
        };

        var masters = new[]
        {
            new DeviceMasterDsp { Id = InDevId, Name = "RME Fireface In", IsInput = true, GainDb = 0.0 },
            new DeviceMasterDsp { Id = VaioCableId, Name = "VB-Cable Zuspieler", IsInput = true, GainDb = 0.0 },
            new DeviceMasterDsp { Id = OutDevId, Name = "RME Studio 8ch Out", IsInput = false, GainDb = -2.0 },
        };

        var mixer = new MixerSettings
        {
            SampleRate = 48000,
            DeviceMasters = masters,
            Inputs = inputs,
            Outputs = outputs,
            MatrixDb = m,
            Buses = new[] { bus },
            BusRoutes = busRoutes,
            MasterGainDb = 0.0,
        };

        var streamDeck = new UiDeckDefinition
        {
            Name = "Streaming",
            ColorHex = "#10B981",
            VisibleInTopBar = true,
            Density = DeckDensity.Detailed,
            Items = new[]
            {
                new DeckSlotItem { Kind = DeckItemKind.InputChannel, TargetId = inputs[0].Id },
                new DeckSlotItem { Kind = DeckItemKind.InputChannel, TargetId = inputs[1].Id },
                new DeckSlotItem { Kind = DeckItemKind.MixBus, TargetId = MusicBusId },
                new DeckSlotItem { Kind = DeckItemKind.Spacer },
                new DeckSlotItem { Kind = DeckItemKind.DeviceMaster, TargetId = OutDevId },
            },
        };

        var decks = new UiDeckCollection
        {
            ActiveDeckId = null,
            ExpandedMainDeviceIds = new[] { InDevId },
            Decks = new[] { streamDeck },
        };

        return (config, mixer, decks);
    }
}
