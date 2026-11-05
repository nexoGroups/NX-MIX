using NxMix.Audio;
using NxMix.Audio.Apps;
using NxMix.Audio.Interop;

namespace NxMix.Tests.Audio;

/// <summary>Journal + Umleitungslogik mit Fake-Router und Fake-Prozessliste (keine echten Umleitungen).</summary>
public class AppRouteJournalTests : IDisposable
{
    private const string Parking = @"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{3e7b9fff-9481-4278-b91a-86faf8c1bc18}#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string UserDevice = @"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{35028f0a-9e90-4644-9420-57d3e980d3c7}#{e6327cad-dcec-4949-ae8a-991e976a79d2}";

    private readonly string _path = AppTestKit.TempJournal();
    private readonly FakeAppRouter _router = new();
    private readonly AppRouteJournal _journal;
    private readonly AppRouteService _svc;

    public AppRouteJournalTests()
    {
        _journal = new AppRouteJournal(_path);
        _svc = new AppRouteService(_router, _journal);
    }

    public void Dispose() => AppTestKit.Delete(_path);

    private static ProcInfo P(int pid, string exe, int parent = 4) => new(pid, parent, exe);

    // ---- Journal-Datei ---------------------------------------------------------------------------------------

    [Fact]
    public void Journal_RoundTrip_KeepsPreviousPerRole()
    {
        Assert.Empty(_journal.Load());
        _journal.Upsert(new RouteEntry { Exe = "Spotify.exe", Parking = Parking, Previous = new string?[] { UserDevice, null, UserDevice }, OwnerPid = 7, SetAtUtc = "x" });

        var again = new AppRouteJournal(_path).Load();
        RouteEntry e = Assert.Single(again);
        Assert.Equal("spotify.exe", e.Exe);
        Assert.Equal(Parking, e.Parking);
        Assert.Equal(new string?[] { UserDevice, null, UserDevice }, e.Previous);
        Assert.Equal(7, e.OwnerPid);

        Assert.NotNull(new AppRouteJournal(_path).Find("SPOTIFY.EXE"));
        Assert.True(_journal.Remove("spotify.exe"));
        Assert.False(_journal.Remove("spotify.exe"));
        Assert.Empty(new AppRouteJournal(_path).Load());
    }

    [Fact]
    public void Journal_Upsert_ReplacesSameExe_KeepsOthers()
    {
        _journal.Upsert(new RouteEntry { Exe = "a.exe", Parking = Parking });
        _journal.Upsert(new RouteEntry { Exe = "b.exe", Parking = Parking });
        _journal.Upsert(new RouteEntry { Exe = "A.EXE", Parking = UserDevice });
        var all = _journal.Load();
        Assert.Equal(2, all.Count);
        Assert.Equal(UserDevice, all.Single(e => e.Exe == "a.exe").Parking);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ nicht json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"routes\":[{\"exe\":\"\"},{\"previous\":5}]}")]
    public void Journal_Tolerates_BrokenFiles(string content)
    {
        File.WriteAllText(_path, content);
        var j = new AppRouteJournal(_path);
        Assert.Empty(j.Load());
        // ... und bleibt benutzbar
        j.Upsert(new RouteEntry { Exe = "a.exe", Parking = Parking });
        Assert.Single(j.Load());
    }

    [Fact]
    public void Journal_NoTempFileLeftBehind()
    {
        _journal.Upsert(new RouteEntry { Exe = "a.exe", Parking = Parking });
        Assert.False(File.Exists(_path + ".tmp"));
    }

    // ---- Umleiten --------------------------------------------------------------------------------------------

    [Fact]
    public void EnsureRouted_WritesJournalBeforeSet_ThenVerifies()
    {
        _router.AddProcess(100, "spotify.exe");
        bool journalHadEntryAtSet = false;
        _router.BeforeSet = (_, roles) =>
        {
            if (roles.All(r => r == Parking)) journalHadEntryAtSet = new AppRouteJournal(_path).Find("spotify.exe") != null;
        };

        RouteResult r = _svc.EnsureRouted("spotify.exe", new[] { 100 }, Parking, out string? msg);

        Assert.Equal(RouteResult.Routed, r);
        Assert.Null(msg);
        Assert.True(journalHadEntryAtSet, "Das Journal muss VOR dem Umleiten geschrieben sein.");
        Assert.All(_router.ValueOf("spotify.exe"), v => Assert.Equal(Parking, v));
        Assert.Single(_journal.Load());
    }

    [Fact]
    public void EnsureRouted_IsIdempotent_AndAdoptsExistingRouteWithoutJournal()
    {
        _router.AddProcess(100, "spotify.exe");
        Assert.Equal(RouteResult.Routed, _svc.EnsureRouted("spotify.exe", new[] { 100 }, Parking, out _));
        int sets = _router.Log.Count;
        Assert.Equal(RouteResult.AlreadyRouted, _svc.EnsureRouted("spotify.exe", new[] { 100 }, Parking, out _));
        Assert.Equal(sets, _router.Log.Count); // kein zweites Set

        // Journal verloren, Windows steht noch auf dem Parkplatz: wieder eintragen, damit es freigegeben wird
        _journal.Remove("spotify.exe");
        Assert.Equal(RouteResult.AlreadyRouted, _svc.EnsureRouted("spotify.exe", new[] { 100 }, Parking, out _));
        Assert.Single(_journal.Load());
    }

    [Fact]
    public void EnsureRouted_UnknownProcess_IsNotReady_AndTouchesNothing()
    {
        // Prozess laeuft, hat aber noch kein Audio benutzt: Windows kennt die PID nicht (E_INVALIDARG)
        RouteResult r = _svc.EnsureRouted("spotify.exe", new[] { 100, 101 }, Parking, out string? msg);
        Assert.Equal(RouteResult.NotReady, r);
        Assert.NotNull(msg);
        Assert.Empty(_router.Log);
        Assert.Empty(_journal.Load());
    }

    [Fact]
    public void EnsureRouted_UsesFirstProcessWindowsKnows()
    {
        _router.AddProcess(101, "chrome.exe"); // nur das Kind kennt Windows
        RouteResult r = _svc.EnsureRouted("chrome.exe", new[] { 100, 101 }, Parking, out _);
        Assert.Equal(RouteResult.Routed, r);
        Assert.Equal(Parking, _router.ValueOf("chrome.exe")[0]);
    }

    [Fact]
    public void EnsureRouted_WhenWindowsIgnoresSet_Fails_AndCleansJournal()
    {
        _router.AddProcess(100, "spotify.exe");
        _router.IgnoreSet = true;
        RouteResult r = _svc.EnsureRouted("spotify.exe", new[] { 100 }, Parking, out string? msg);
        Assert.Equal(RouteResult.Failed, r);
        Assert.NotNull(msg);
        Assert.Empty(_journal.Load());
        Assert.All(_router.ValueOf("spotify.exe"), v => Assert.Null(v));
    }

    // ---- Zuruecksetzen ---------------------------------------------------------------------------------------

    [Fact]
    public void Release_RestoresPreviousValue_NotPlainDefault()
    {
        _router.AddProcess(100, "spotify.exe");
        _router.Preset("spotify.exe", UserDevice, UserDevice, null); // der Nutzer hatte Spotify auf ein Geraet gelegt
        Assert.Equal(RouteResult.Routed, _svc.EnsureRouted("spotify.exe", new[] { 100 }, Parking, out _));
        Assert.Equal(new string?[] { UserDevice, UserDevice, null }, _journal.Find("spotify.exe")!.Previous);

        bool ok = _svc.Release("spotify.exe", new[] { P(100, "spotify.exe") }, out string? msg);

        Assert.True(ok, msg);
        Assert.Equal(new string?[] { UserDevice, UserDevice, null }, _router.ValueOf("spotify.exe"));
        Assert.Empty(_journal.Load());
    }

    [Fact]
    public void Release_WithNoPreviousValue_ResetsToSystemDefault()
    {
        _router.AddProcess(100, "spotify.exe");
        _svc.EnsureRouted("spotify.exe", new[] { 100 }, Parking, out _);
        Assert.True(_svc.Release("spotify.exe", new[] { P(100, "spotify.exe") }, out _));
        Assert.All(_router.ValueOf("spotify.exe"), v => Assert.Null(v));
        Assert.Empty(_journal.Load());
    }

    [Fact]
    public void Release_ExistingEntryFromEarlierRun_KeepsOriginalPreviousValue()
    {
        // Absturz-Szenario: Journal + Umleitung stehen noch; der naechste Lauf darf den Originalwert nicht mit "Parkplatz" ueberschreiben
        _router.AddProcess(100, "spotify.exe");
        _router.Preset("spotify.exe", UserDevice);
        _svc.EnsureRouted("spotify.exe", new[] { 100 }, Parking, out _);

        var next = new AppRouteService(_router, new AppRouteJournal(_path)); // "neuer Lauf"
        Assert.Equal(RouteResult.AlreadyRouted, next.EnsureRouted("spotify.exe", new[] { 100 }, Parking, out _));
        Assert.True(next.Release("spotify.exe", new[] { P(100, "spotify.exe") }, out _));
        Assert.All(_router.ValueOf("spotify.exe"), v => Assert.Equal(UserDevice, v));
    }

    [Fact]
    public void Release_WhenAppNotRunning_StaysPending_AndIsCompletedLater()
    {
        _router.AddProcess(100, "spotify.exe");
        _svc.EnsureRouted("spotify.exe", new[] { 100 }, Parking, out _);
        _router.Known.Clear(); // Programm beendet: Windows laesst sich nicht mehr ueber seine PID umstellen

        Assert.False(_svc.Release("spotify.exe", Array.Empty<ProcInfo>(), out string? msg));
        Assert.NotNull(msg);
        RouteEntry pending = Assert.Single(_journal.Load());
        Assert.True(pending.Pending);
        Assert.Equal(Parking, _router.ValueOf("spotify.exe")[0]); // Windows-Zustand unveraendert (nichts erfunden)

        // Programm startet wieder (neue PID, Windows kennt es)
        _router.AddProcess(222, "spotify.exe");
        Assert.True(_svc.Release("spotify.exe", new[] { P(222, "spotify.exe") }, out _));
        Assert.All(_router.ValueOf("spotify.exe"), v => Assert.Null(v));
        Assert.Empty(_journal.Load());
    }

    [Fact]
    public void Release_LeavesAlone_WhatSomeoneElseChangedMeanwhile()
    {
        _router.AddProcess(100, "spotify.exe");
        _svc.EnsureRouted("spotify.exe", new[] { 100 }, Parking, out _);
        _router.Preset("spotify.exe", UserDevice); // der Nutzer stellt es in den Windows-Einstellungen um
        int sets = _router.Log.Count;

        Assert.True(_svc.Release("spotify.exe", new[] { P(100, "spotify.exe") }, out _));
        Assert.Equal(sets, _router.Log.Count); // nichts angefasst
        Assert.All(_router.ValueOf("spotify.exe"), v => Assert.Equal(UserDevice, v));
        Assert.Empty(_journal.Load());
    }

    [Fact]
    public void Release_OtherAppsAreNeverTouched()
    {
        _router.AddProcess(100, "spotify.exe");
        _router.AddProcess(200, "discord.exe");
        _router.Preset("discord.exe", UserDevice);
        _svc.EnsureRouted("spotify.exe", new[] { 100 }, Parking, out _);
        _svc.Release("spotify.exe", new[] { P(100, "spotify.exe"), P(200, "discord.exe") }, out _);
        Assert.All(_router.ValueOf("discord.exe"), v => Assert.Equal(UserDevice, v));
        Assert.DoesNotContain(_router.Log, l => l.StartsWith("Set 200"));
    }

    // ---- Wiederherstellung nach Absturz (IAppRouteRestorer) ---------------------------------------------------

    [Fact]
    public void RestoreAllKnown_RepairsEverythingFromJournal_AfterSimulatedCrash()
    {
        _router.AddProcess(100, "spotify.exe");
        _router.AddProcess(200, "chrome.exe");
        _router.Preset("chrome.exe", UserDevice);
        _svc.EnsureRouted("spotify.exe", new[] { 100 }, Parking, out _);
        _svc.EnsureRouted("chrome.exe", new[] { 200 }, Parking, out _);
        // ... NX-MIX stuerzt ab. Neuer Prozess:
        var lister = new FakeProcessLister { Processes = { P(100, "spotify.exe"), P(200, "chrome.exe") } };
        var restorer = new AppRouteRestorer(_path, _router, lister);

        restorer.RestoreAllKnown();

        Assert.All(_router.ValueOf("spotify.exe"), v => Assert.Null(v));
        Assert.All(_router.ValueOf("chrome.exe"), v => Assert.Equal(UserDevice, v));
        Assert.Empty(new AppRouteJournal(_path).Load());

        restorer.RestoreAllKnown(); // mehrfach aufrufbar
        Assert.Empty(new AppRouteJournal(_path).Load());
    }

    [Fact]
    public void RestoreAllKnown_KeepsEntriesOfAppsThatAreNotRunning()
    {
        _router.AddProcess(100, "spotify.exe");
        _svc.EnsureRouted("spotify.exe", new[] { 100 }, Parking, out _);
        _router.Known.Clear();
        var restorer = new AppRouteRestorer(_path, _router, new FakeProcessLister());
        restorer.RestoreAllKnown();
        Assert.True(Assert.Single(new AppRouteJournal(_path).Load()).Pending);
    }

    [Fact]
    public void RestoreAllKnown_NeverThrows_OnGarbage()
    {
        File.WriteAllText(_path, "\0\0 garbage {{{");
        new AppRouteRestorer(_path, _router, new FakeProcessLister()).RestoreAllKnown();
        new AppRouteRestorer(Path.Combine(_path, "gibt", "es", "nicht"), _router, new FakeProcessLister()).RestoreAllKnown();
        // Echter Router (Pfad ueber IAudioPolicyConfig), aber nur ein erfundenes Programm im Journal: nichts Echtes wird angefasst
        File.Delete(_path);
        _journal.Upsert(new RouteEntry { Exe = "nxmix-test-gibt-es-nicht.exe", Parking = Parking });
        new AppRouteRestorer(_path, null, new FakeProcessLister { Processes = { P(1, "x.exe") } }).RestoreAllKnown();
        Assert.True(Assert.Single(new AppRouteJournal(_path).Load()).Pending);
    }

    [Fact]
    public void Restorer_IsSupported_DoesNotThrow_AndExplainsWhenUnsupported()
    {
        var r = new AppRouteRestorer();
        bool supported = r.IsSupported;
        if (!supported) Assert.False(string.IsNullOrWhiteSpace(r.UnsupportedReason));
        else Assert.Null(r.UnsupportedReason);
        Assert.Equal(supported, r.IsSupported); // stabil
    }

    // ---- Geraete-Id-Format -----------------------------------------------------------------------------------

    [Fact]
    public void PolicyDeviceId_RoundTrips()
    {
        string mm = "{0.0.0.00000000}.{3e7b9fff-9481-4278-b91a-86faf8c1bc18}";
        string pol = AudioPolicyConfig.PolicyDeviceId(mm);
        Assert.Equal(@"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{3e7b9fff-9481-4278-b91a-86faf8c1bc18}#{e6327cad-dcec-4949-ae8a-991e976a79d2}", pol);
        Assert.Equal(mm, AudioPolicyConfig.UnpackDeviceId(pol));
        Assert.Null(AudioPolicyConfig.UnpackDeviceId(""));
        Assert.Null(AudioPolicyConfig.UnpackDeviceId(null));
    }
}
