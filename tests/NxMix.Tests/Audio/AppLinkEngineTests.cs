using NxMix.Audio;
using NxMix.Audio.Apps;
using NxMix.Audio.Host;
using NxMix.Audio.Interop;
using NxMix.Engine;

namespace NxMix.Tests.Audio;

/// <summary>Ablauf des App-Link-Verwalters mit Attrappen (Router, Prozessliste, Spuren): keine echten Umleitungen, keine Geraete.</summary>
public class AppLinkEngineTests : IDisposable
{
    private const string ParkingGuid = "3e7b9fff-9481-4278-b91a-86faf8c1bc18";
    private const string ParkingId = "{0.0.0.00000000}.{" + ParkingGuid + "}";
    private const string DefaultId = "{0.0.0.00000000}.{812cc365-29a1-4385-8900-8b6c49673367}";
    private static readonly string ParkingPol = AudioPolicyConfig.PolicyDeviceId(ParkingId);

    private readonly string _journalPath = AppTestKit.TempJournal();
    private readonly FakeAppRouter _router = new();
    private readonly FakeProcessLister _lister = new();
    private readonly List<string> _events = new();
    private readonly List<AppLane> _lanes = new();
    private List<SessionRef> _sessions = new();
    private Func<int, bool>? _failLane;

    public void Dispose()
    {
        foreach (AppLane l in _lanes) l.Stop();
        AppTestKit.Delete(_journalPath);
    }

    private static readonly IReadOnlyList<AudioDeviceInfo> Devices = new[]
    {
        new AudioDeviceInfo(DefaultId, "CABLE In 16ch", DeviceFlow.Render, 16, 48000, true),
        new AudioDeviceInfo(ParkingId, "VBMatrix In 5", DeviceFlow.Render, 2, 48000, false),
    };

    private AppLinkEngine Make(IReadOnlyList<AppBinding> bindings, string? parking = ParkingId, int inputs = 8,
        ISet<int>? taken = null, IReadOnlyList<string?>? renderIds = null, IReadOnlyList<AudioDeviceInfo>? devices = null)
    {
        _router.BeforeSet = (pid, roles) => _events.Add("route");
        var deps = new AppLinkDeps
        {
            Router = _router,
            Journal = new AppRouteJournal(_journalPath),
            Lister = _lister,
            RenderDevices = devices ?? Devices,
            Sessions = () => _sessions,
            LaneFactory = (pid, ringChannels, ctx) =>
            {
                if (_failLane?.Invoke(pid) == true) throw new AudioException(0, "Testfehler beim Oeffnen");
                _events.Add("lane:" + pid);
                AppLane lane = AppLane.CreateDetached(pid, ringChannels, ctx.EngineRate, ctx);
                _lanes.Add(lane);
                return lane;
            },
        };
        var engine = new AppLinkEngine(null, bindings, parking, inputs, taken ?? new HashSet<int>(),
            () => new LaneContext(48000, 480, 480, () => false), renderIds ?? new List<string?>(), deps);
        Assert.True(engine.PrepareRoutes(out _));
        return engine;
    }

    private static ProcInfo P(int pid, int parent, string exe) => new(pid, parent, exe);

    private AppLinkStatus Only(AppLinkEngine e) => Assert.Single(e.GetStatuses());

    // ---- Fehlkonfigurationen: nichts wird umgeleitet ----------------------------------------------------------

    [Fact]
    public void NoParkingDevice_CapturesWithoutChangingWindowsRoutes()
    {
        _router.AddProcess(100, "spotify.exe");
        _lister.Processes.Add(P(100, 4, "spotify.exe"));
        var e = Make(new[] { new AppBinding("spotify.exe", 2) }, parking: null);
        e.Tick(_lister.Snapshot());

        AppLinkStatus s = Only(e);
        Assert.Equal(AppLinkState.MixedWithoutRedirect, s.State);
        Assert.Contains("Parkplatz", s.Message);
        Assert.Single(e.Slots[0].Lanes);
        Assert.Empty(_router.Log);
        Assert.Empty(new AppRouteJournal(_journalPath).Load());
        e.ReleaseAll();
        Assert.Empty(_router.Log);
    }

    [Fact]
    public void ParkingIsDefaultDevice_DoesNotRoute()
    {
        var e = Make(new[] { new AppBinding("spotify.exe", 2) }, parking: DefaultId);
        e.Tick(_lister.Snapshot());
        AppLinkStatus s = Only(e);
        Assert.Equal(AppLinkState.WaitingForApp, s.State);
        Assert.Contains("Standard", s.Message);
    }

    [Fact]
    public void ParkingDoesNotExist_DoesNotRoute()
    {
        var e = Make(new[] { new AppBinding("spotify.exe", 2) }, parking: "{0.0.0.00000000}.{ffffffff-0000-0000-0000-000000000000}");
        e.Tick(_lister.Snapshot());
        AppLinkStatus s = Only(e);
        Assert.Equal(AppLinkState.WaitingForApp, s.State);
        Assert.Contains("nicht gefunden", s.Message);
    }

    [Fact]
    public void ParkingIsAlsoEngineOutput_DoesNotRoute()
    {
        var e = Make(new[] { new AppBinding("spotify.exe", 2) }, renderIds: new List<string?> { ParkingId.ToUpperInvariant() });
        e.Tick(_lister.Snapshot());
        AppLinkStatus s = Only(e);
        Assert.Equal(AppLinkState.WaitingForApp, s.State);
        Assert.Contains("Ausgang", s.Message);
    }

    [Fact]
    public void InvalidOrClashingChannels_FailOnlyThatLink()
    {
        var e = Make(new[]
        {
            new AppBinding("a.exe", 6, 2),   // 6..7 ok bei 8 Eingaengen
            new AppBinding("b.exe", 7, 2),   // 7..8 gibt es nicht
            new AppBinding("c.exe", 6, 1),   // 6 ist schon von a.exe belegt
            new AppBinding("d.exe", 0, 2),   // 0..1 belegt durch eine Geraete-Anbindung
        }, taken: new HashSet<int> { 0, 1 });
        var st = e.GetStatuses();
        Assert.Equal(AppLinkState.WaitingForApp, st[0].State);
        Assert.Equal(AppLinkState.Failed, st[1].State);
        Assert.Contains("existiert nicht", st[1].Message);
        Assert.Equal(AppLinkState.Failed, st[2].State);
        Assert.Contains("belegt", st[2].Message);
        Assert.Equal(AppLinkState.Failed, st[3].State);
        Assert.True(e.Slots[1].Invalid && e.Slots[2].Invalid && e.Slots[3].Invalid && !e.Slots[0].Invalid);
    }

    // ---- Ablauf ---------------------------------------------------------------------------------------------

    [Fact]
    public void Lifecycle_WaitForApp_CaptureFirst_ThenRoute_CloseWhenGone_ReleaseAtEnd()
    {
        var e = Make(new[] { new AppBinding("Spotify.exe", 2) });

        e.Tick(_lister.Snapshot());
        Assert.Equal(AppLinkState.WaitingForApp, Only(e).State);
        Assert.Empty(_router.Log);

        // Spotify startet (Haupt- und Hilfsprozesse)
        _lister.Processes.AddRange(new[] { P(100, 50, "Spotify.exe"), P(101, 100, "Spotify.exe"), P(102, 100, "Spotify.exe") });
        _router.AddProcess(100, "spotify.exe");
        e.Tick(_lister.Snapshot());

        Assert.Equal(new[] { "lane:100", "route" }, _events);
        AppLinkStatus s = Only(e);
        Assert.Equal(AppLinkState.Linked, s.State);
        Assert.Equal(3, s.ProcessCount);
        Assert.Equal(ParkingPol, _router.ValueOf("spotify.exe")[0]);
        Assert.Single(e.Slots[0].Lanes);

        e.Tick(_lister.Snapshot());                              // stabil: nichts doppelt
        Assert.Equal(new[] { "lane:100", "route" }, _events);

        // Spotify wird beendet: Spur zu, Umleitung bleibt (gilt je Programm und kann ohne laufenden Prozess nicht entfernt werden)
        _lister.Processes.Clear();
        e.Tick(_lister.Snapshot());
        Assert.Equal(AppLinkState.WaitingForApp, Only(e).State);
        Assert.Empty(e.Slots[0].Lanes);
        Assert.False(_lanes[0].IsAlive);
        Assert.Equal(ParkingPol, _router.ValueOf("spotify.exe")[0]);

        // neu gestartet (neue PID): von der ersten Sekunde an umgeleitet, kein zweites Set
        _lister.Processes.Add(P(900, 50, "Spotify.exe"));
        _router.Known.Clear();
        _router.AddProcess(900, "spotify.exe");
        int sets = _router.Log.Count;
        e.Tick(_lister.Snapshot());
        Assert.Equal(AppLinkState.Linked, Only(e).State);
        Assert.Equal(sets, _router.Log.Count);
        Assert.Equal(new[] { "lane:100", "route", "lane:900" }, _events);

        // Ende: Spuren zu, Umleitung zurueck, Journal leer
        e.ReleaseAll();
        Assert.Empty(e.Slots[0].Lanes);
        Assert.All(_router.ValueOf("spotify.exe"), v => Assert.Null(v));
        Assert.Empty(new AppRouteJournal(_journalPath).Load());
        e.ReleaseAll();                                          // mehrfach aufrufbar
    }

    [Fact]
    public void ProcessWithoutAudioYet_OpensCapture_ThenLinksWhenRoutable()
    {
        _lister.Processes.Add(P(100, 50, "spotify.exe"));        // Windows kennt die PID noch nicht
        var e = Make(new[] { new AppBinding("spotify.exe", 2) });
        e.Tick(_lister.Snapshot());
        AppLinkStatus s = Only(e);
        Assert.Equal(AppLinkState.Capturing, s.State);
        Assert.Equal(1, s.ProcessCount);
        Assert.NotNull(s.Message);
        Assert.Equal(new[] { "lane:100" }, _events);

        _router.AddProcess(100, "spotify.exe");
        e.Tick(_lister.Snapshot());
        Assert.Equal(AppLinkState.Linked, Only(e).State);
    }

    [Fact]
    public void TwoIndependentBrowserInstances_GetOneLaneEach()
    {
        _lister.Processes.AddRange(new[]
        {
            P(100, 50, "chrome.exe"), P(101, 100, "chrome.exe"), P(102, 101, "chrome.exe"),
            P(200, 60, "chrome.exe"), P(201, 200, "chrome.exe"),
        });
        _router.AddProcess(101, "chrome.exe");
        var e = Make(new[] { new AppBinding("chrome.exe", 2) });
        e.Tick(_lister.Snapshot());
        Assert.Equal(new[] { 100, 200 }, e.Slots[0].Lanes.Select(l => l.Pid).OrderBy(x => x).ToArray());
        Assert.Equal(5, Only(e).ProcessCount);
        Assert.Equal(1, _events.Count(x => x == "route")); // eine Umleitung je Programm, nicht je Prozess
    }

    [Fact]
    public void LaneOpenFailure_IsReported_AndRetriedNextTick()
    {
        _lister.Processes.Add(P(100, 50, "spotify.exe"));
        _router.AddProcess(100, "spotify.exe");
        _failLane = _ => true;
        var e = Make(new[] { new AppBinding("spotify.exe", 2) });
        e.Tick(_lister.Snapshot());
        AppLinkStatus s = Only(e);
        Assert.Equal(AppLinkState.Failed, s.State);
        Assert.Contains("Testfehler", s.Message);
        Assert.Empty(_router.Log);

        _failLane = null;
        e.Tick(_lister.Snapshot());
        Assert.Equal(AppLinkState.Linked, Only(e).State);
    }

    [Fact]
    public void WindowsIgnoresRedirect_CaptureContinuesAndStatusShowsIncompleteRouting()
    {
        _lister.Processes.Add(P(100, 50, "spotify.exe"));
        _router.AddProcess(100, "spotify.exe");
        _router.IgnoreSet = true;
        var e = Make(new[] { new AppBinding("spotify.exe", 2) });
        e.Tick(_lister.Snapshot());
        Assert.Equal(AppLinkState.Capturing, Only(e).State);
        Assert.Single(e.Slots[0].Lanes);
        Assert.Equal("lane:100", _events[0]);

        var settings = MixerSettings.Create(48000, 8, 2);
        foreach (var row in settings.MatrixDb) Array.Fill(row, double.NegativeInfinity);
        settings.MatrixDb[2][0] = 0;
        settings.MatrixDb[3][1] = 0;
        var processor = new MixerEngine(settings);
        var mix = new MixCore(processor, 48000, 480, [], [], e.Slots);
        mix.Prepare();
        var samples = Enumerable.Repeat(0.25f, 960).ToArray();
        var ring = e.Slots[0].Lanes[0].Ring!;
        for (int i = 0; i < 20; i++)
        {
            ring.Write(samples, 480);
            mix.ProcessChunk(480);
        }
        Assert.Contains(mix.Outputs[0], sample => sample > 0.1f);
        Assert.Contains(mix.Outputs[1], sample => sample > 0.1f);
    }

    [Fact]
    public void StreamStillAudibleElsewhere_IsCapturedAndReportedUntilItMoves()
    {
        _lister.Processes.Add(P(100, 50, "spotify.exe"));
        _router.AddProcess(100, "spotify.exe");
        _sessions = new List<SessionRef> { new(100, 1, DefaultId, "Lautsprecher (Realtek)") };
        var e = Make(new[] { new AppBinding("spotify.exe", 2) });
        e.Tick(_lister.Snapshot());
        AppLinkStatus s = Only(e);
        Assert.Equal(AppLinkState.Capturing, s.State);
        Assert.Contains("Lautsprecher (Realtek)", s.Message);

        _sessions = new List<SessionRef> { new(100, 1, ParkingId, "VBMatrix In 5") }; // jetzt auf dem Parkplatz
        e.Tick(_lister.Snapshot());
        Assert.Equal(AppLinkState.Linked, Only(e).State);
        Assert.Null(Only(e).Message);
    }

    [Fact]
    public void RunningAppsOfEarlierRun_PendingEntries_AreReleasedWhenAppReturns()
    {
        var journal = new AppRouteJournal(_journalPath);
        journal.Upsert(new RouteEntry { Exe = "old.exe", Parking = ParkingPol, Pending = true });
        _router.Preset("old.exe", ParkingPol);
        var e = Make(new[] { new AppBinding("other.exe", 2) });

        e.Tick(_lister.Snapshot());
        Assert.Single(journal.Load());                           // nicht gestartet: bleibt stehen

        _lister.Processes.Add(P(300, 4, "old.exe"));
        _router.AddProcess(300, "old.exe");
        e.Tick(_lister.Snapshot());
        Assert.Empty(journal.Load());
        Assert.All(_router.ValueOf("old.exe"), v => Assert.Null(v));
    }

    // ---- Mischen im Master-Thread -----------------------------------------------------------------------------

    private static AppLane FedLane(int pid, int ringChannels, float value, int frames)
    {
        var ctx = new LaneContext(48000, 480, 480, () => false);
        AppLane lane = AppLane.CreateDetached(pid, ringChannels, 48000, ctx);
        var data = new float[frames * ringChannels];
        Array.Fill(data, value);
        lane.Ring!.Write(data, frames);
        return lane;
    }

    private static float[][] Inputs(int channels, int n) => Enumerable.Range(0, channels).Select(_ => new float[n]).ToArray();

    [Fact]
    public void Slot_NoLane_WritesExactSilence_AndDisabledSlotToo()
    {
        var slot = new AppLinkSlot(new AppBinding("a.exe", 2, 2));
        var inputs = Inputs(6, 256);
        foreach (float[] ch in inputs) Array.Fill(ch, 0.7f);
        slot.Bind(inputs, 256);
        slot.ReadInto(inputs, 256);
        Assert.All(inputs[2], v => Assert.Equal(0f, v));
        Assert.All(inputs[3], v => Assert.Equal(0f, v));
        Assert.All(inputs[4], v => Assert.Equal(0.7f, v)); // fremde Kanaele unberuehrt

        slot.SetLanes(new[] { FedLane(1, 2, 0.3f, 8000) });
        slot.Disabled = true;
        slot.ReadInto(inputs, 256);
        Assert.All(inputs[2], v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Slot_Stereo_DeliversLaneSignal_ThroughRingAndReader()
    {
        var slot = new AppLinkSlot(new AppBinding("a.exe", 0, 2));
        var inputs = Inputs(2, 480);
        slot.Bind(inputs, 480);
        AppLane lane = FedLane(1, 2, 0.25f, 8000);
        _lanes.Add(lane);
        slot.SetLanes(new[] { lane });
        slot.ReadInto(inputs, 480);
        Assert.Equal(0.25f, inputs[0][479], 3);
        Assert.Equal(0.25f, inputs[1][479], 3);
        Assert.InRange(slot.GetStatus().BufferFill, 0.0, 1.0);
    }

    [Fact]
    public void Slot_Mono_And_TwoLanes_AreSummed()
    {
        var mono = new AppLinkSlot(new AppBinding("a.exe", 1, 1));
        var inputs = Inputs(4, 480);
        mono.Bind(inputs, 480);
        mono.SetLanes(new[] { FedLane(1, 1, 0.1f, 8000), FedLane(2, 1, 0.2f, 8000) });
        mono.ReadInto(inputs, 480);
        Assert.Equal(0.3f, inputs[1][479], 3);
        Assert.All(inputs[0], v => Assert.Equal(0f, v));
        Assert.All(inputs[2], v => Assert.Equal(0f, v));

        // Count = 3: Stereo auf die ersten beiden, der dritte Kanal bleibt stumm
        var wide = new AppLinkSlot(new AppBinding("b.exe", 0, 3));
        var inputs3 = Inputs(4, 480);
        foreach (float[] ch in inputs3) Array.Fill(ch, 0.9f);
        wide.Bind(inputs3, 480);
        wide.SetLanes(new[] { FedLane(3, 2, 0.4f, 8000) });
        wide.ReadInto(inputs3, 480);
        Assert.Equal(0.4f, inputs3[0][479], 3);
        Assert.Equal(0.4f, inputs3[1][479], 3);
        Assert.All(inputs3[2], v => Assert.Equal(0f, v));
        Assert.All(inputs3[3], v => Assert.Equal(0.9f, v));
    }

    [Fact]
    public void Slot_Underrun_GivesSilence_NotGarbage()
    {
        var slot = new AppLinkSlot(new AppBinding("a.exe", 0, 2));
        var inputs = Inputs(2, 480);
        slot.Bind(inputs, 480);
        slot.SetLanes(new[] { AppLane.CreateDetached(1, 2, 48000, new LaneContext(48000, 480, 480, () => false)) }); // leerer Ring
        foreach (float[] ch in inputs) Array.Fill(ch, 0.5f);
        slot.ReadInto(inputs, 480);
        Assert.All(inputs[0], v => Assert.Equal(0f, v));
        Assert.All(inputs[1], v => Assert.Equal(0f, v));
    }
}
