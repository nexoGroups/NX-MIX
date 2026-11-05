using System.Diagnostics;
using NxMix.Audio;
using NxMix.Audio.Apps;
using NxMix.Audio.Host;
using NxMix.Audio.Interop;
using Xunit.Abstractions;

namespace NxMix.Tests.Audio;

/// <summary>Integrationstest mit ECHTER Windows-Umleitung und ECHTEM Prozess-Loopback - aber ausschliesslich mit dem EIGENEN Testprozess
/// (testhost.exe) als "Programm", das digitale STILLE in eine Render-Sitzung schreibt. Parkplatz = ein vorhandenes virtuelles Geraet
/// (VBMatrix In 5 o. ae.; nie das Standardgeraet, nie "CABLE In 16ch"). Keine fremde App wird umgeleitet oder in der Lautstaerke veraendert.
/// Alles wird in finally zurueckgesetzt und per Get/Journal geprueft. Ohne Parkplatz-Geraet wird der Test uebersprungen.</summary>
[Collection("AudioDevices")]
public class AppLinkIntegrationTests
{
    private readonly ITestOutputHelper _out;

    public AppLinkIntegrationTests(ITestOutputHelper output) => _out = output;

    private static readonly string[] ParkingPreference =
    {
        "VBMatrix In 5", "VBMatrix In 6", "VBMatrix In 7", "VBMatrix In 8", "VBMatrix In 4", "VBMatrix In 3", "VBMatrix In 2", "VBMatrix In 1",
    };

    private sealed record Setup(AudioDeviceInfo Parking, string Exe, string PolicyId);

    /// <summary>null (+ Begruendung) = Test ueberspringen.</summary>
    private Setup? Prepare(out string skip)
    {
        skip = "";
        if (!new AppRouteRestorer().IsSupported)
        {
            skip = "Pro-App-Umleitung/Prozess-Loopback auf diesem Windows nicht verfuegbar: " + new AppRouteRestorer().UnsupportedReason;
            return null;
        }
        string exe = Path.GetFileName(Environment.ProcessPath ?? "") ?? "";
        if (string.IsNullOrEmpty(exe) || exe.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase) || exe.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Die Umleitung gilt je Exe: bei dotnet.exe waeren auch fremde .NET-Programme betroffen
            skip = $"Der Testprozess ist '{exe}': die Umleitung wuerde alle gleichnamigen Programme erfassen.";
            return null;
        }
        var render = new DeviceCatalog().List().Where(d => d.Flow == DeviceFlow.Render).ToList();
        AudioDeviceInfo? parking = null;
        foreach (string name in ParkingPreference)
        {
            parking = render.FirstOrDefault(d => !d.IsDefault && d.Name.Contains(name, StringComparison.OrdinalIgnoreCase)
                                                 && !d.Name.Contains("CABLE In 16ch", StringComparison.OrdinalIgnoreCase));
            if (parking != null) break;
        }
        if (parking == null)
        {
            skip = "Kein virtuelles Parkplatz-Geraet (VBMatrix In n) vorhanden.";
            return null;
        }
        return new Setup(parking, exe, AudioPolicyConfig.PolicyDeviceId(parking.Id));
    }

    /// <summary>Macht den Testprozess fuer die Windows-Audioverwaltung "bekannt" (Voraussetzung fuer Pro-App-Umleitung) und liest den Ist-Zustand.</summary>
    private static string?[] ReadRoutes(int pid)
    {
        return ComThread.Run(() =>
        {
            using PolicyAppRouter router = PolicyAppRouter.TryCreate(out _)!;
            var roles = new string?[3];
            int hr = router.Get(pid, roles);
            Assert.True(hr >= 0, $"Get fehlgeschlagen 0x{hr:X8}");
            return roles;
        }, 20000);
    }

    private static void RegisterWithAudio()
    {
        // Eine Geraete-Abfrage genuegt, damit Windows den Prozess kennt
        ComThread.Run(() =>
        {
            var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            en.GetDefaultAudioEndpoint(0, 0, out IMMDevice? dev);
            WasapiEndpoint.ReleaseCom(dev);
            WasapiEndpoint.ReleaseCom(en);
            return 0;
        }, 20000);
        new DeviceCatalog().List();
    }

    /// <summary>Spielt digitale Stille ueber das (von der Umleitung bestimmte) Standardgeraet des Prozesses.</summary>
    private sealed unsafe class SilentPlayer : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _stop = new(false);
        private readonly ManualResetEventSlim _ready = new(false);
        public string? OpenedOn;
        public string? Error;
        public string? OpenedName;

        public SilentPlayer()
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "TestSilence" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
            _ready.Wait(15000);
        }

        private void Run()
        {
            WasapiEndpoint? ep = null;
            try
            {
                ep = WasapiEndpoint.Open(null, false, false, 48000, 480, false); // Standardgeraet DIESES Prozesses (laut Umleitung)
                OpenedOn = ep.DeviceId;
                OpenedName = ep.Name;
                ep.Start();
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                _ready.Set();
                ep?.Dispose();
                return;
            }
            _ready.Set();
            try
            {
                IAudioRenderClient render = ep.RenderClient!;
                nint* h = stackalloc nint[1];
                h[0] = ep.EventHandle;
                while (!_stop.IsSet)
                {
                    Native.WaitForMultipleObjects(1, h, 0, 50);
                    if (ep.Client.GetCurrentPadding(out uint pad) < 0) break;
                    int avail = (int)(ep.BufferFrames - pad);
                    if (avail <= 0) continue;
                    if (render.GetBuffer((uint)avail, out nint data) < 0) break;
                    new Span<byte>((void*)data, avail * ep.BlockAlign).Clear(); // exakte Stille
                    render.ReleaseBuffer((uint)avail, BufferFlags.Silent);
                }
            }
            catch
            {
                // Test beendet
            }
            finally
            {
                ep.Dispose();
            }
        }

        public void Dispose()
        {
            _stop.Set();
            _thread.Join(5000);
        }
    }

    [Fact]
    public void OwnProcess_Redirect_ProcessLoopback_ReceivesBlocks_AndEverythingIsRestored()
    {
        Setup? setup = Prepare(out string skip);
        if (setup == null) { _out.WriteLine("UEBERSPRUNGEN: " + skip); return; }
        _out.WriteLine($"Testprozess: {setup.Exe} (PID {Environment.ProcessId}); Parkplatz: {setup.Parking.Name}");

        RegisterWithAudio();
        int self = Environment.ProcessId;
        string?[] before = ReadRoutes(self);
        _out.WriteLine("Vorher: " + string.Join(" | ", before.Select(b => b ?? "(kein Wert)")));
        string journalPath = AppTestKit.TempJournal();
        var journal = new AppRouteJournal(journalPath);

        SilentPlayer? player = null;
        try
        {
            // 1. Umleitung auf den Parkplatz setzen, per Get verifizieren
            RouteResult rr = ComThread.Run(() =>
            {
                using PolicyAppRouter router = PolicyAppRouter.TryCreate(out _)!;
                var svc = new AppRouteService(router, journal);
                RouteResult r = svc.EnsureRouted(setup.Exe, new[] { self }, setup.PolicyId, out string? msg);
                _out.WriteLine($"EnsureRouted: {r} {msg}");
                return r;
            }, 20000);
            Assert.True(rr is RouteResult.Routed or RouteResult.AlreadyRouted, "Umleitung nicht gesetzt: " + rr);
            Assert.All(ReadRoutes(self), v => Assert.Equal(setup.PolicyId, v, ignoreCase: true));
            Assert.Equal(before, journal.Find(setup.Exe)!.Previous.Select(p => p).ToArray()); // Vorher-Wert im Journal

            // 2. Der Prozess oeffnet sein Standardgeraet: es muss der Parkplatz sein (digitale Stille)
            player = new SilentPlayer();
            _out.WriteLine($"Stille-Stream auf: {player.OpenedName} {player.OpenedOn} {player.Error}");
            Assert.Null(player.Error);
            Assert.Equal(setup.Parking.Id, player.OpenedOn, ignoreCase: true);
            Thread.Sleep(300);
            var mine = ComThread.Run(() => SessionEnumeration.Enumerate(setup.Parking.Id), 20000).Where(s => s.ProcessId == self).ToList();
            Assert.NotEmpty(mine); // die Sitzung des Testprozesses liegt tatsaechlich auf dem Parkplatz

            // 3. Prozess-Loopback oeffnen und Bloecke empfangen
            (long frames, long packets, bool allZero) = ComThread.Run(() =>
            {
                using ProcessLoopbackEndpoint ep = ProcessLoopbackEndpoint.Open(self, 48000);
                ep.Start();
                _out.WriteLine($"Prozess-Loopback offen: {ep.SampleRate} Hz, {ep.Channels} ch, Puffer {ep.BufferFrames}, Event={ep.EventDriven}");
                long fr = 0, pk = 0;
                bool zero = true;
                var sw = Stopwatch.StartNew();
                unsafe
                {
                    nint* h = stackalloc nint[1];
                    h[0] = ep.EventHandle;
                    while (sw.ElapsedMilliseconds < 1200)
                    {
                        if (ep.EventDriven) Native.WaitForMultipleObjects(1, h, 0, 100); else Thread.Sleep(3);
                        while (ep.CaptureClient!.GetNextPacketSize(out uint next) >= 0 && next > 0)
                        {
                            if (ep.CaptureClient.GetBuffer(out nint data, out uint n, out uint flags, out _, out _) < 0) break;
                            pk++; fr += n;
                            if ((flags & BufferFlags.Silent) == 0)
                            {
                                float* f = (float*)data;
                                for (int i = 0; i < n * 2; i++) if (f[i] != 0f) zero = false;
                            }
                            ep.CaptureClient.ReleaseBuffer(n);
                        }
                    }
                }
                return (fr, pk, zero);
            }, 30000);
            _out.WriteLine($"Empfangen: {packets} Pakete, {frames} Frames, nur Nullen: {allZero}");
            Assert.True(packets > 20, $"Zu wenige Bloecke: {packets}");
            Assert.True(frames > 48000 / 2, $"Zu wenige Frames: {frames}");
            Assert.True(allZero, "Der Parkplatz-Strom war nicht digital still.");
        }
        finally
        {
            player?.Dispose();
            // 4. Alles zuruecksetzen - auch bei Fehlern
            bool released = ComThread.Run(() =>
            {
                using PolicyAppRouter? router = PolicyAppRouter.TryCreate(out _);
                if (router == null) return false;
                return new AppRouteService(router, journal).Release(setup.Exe, new SystemProcessLister().Snapshot(), out _);
            }, 20000);
            string?[] after = ReadRoutes(self);
            _out.WriteLine("Nachher: " + string.Join(" | ", after.Select(b => b ?? "(kein Wert)")));
            try
            {
                Assert.True(released, "Release meldete Misserfolg");
                Assert.Equal(before, after);                       // genau der Vorher-Zustand
                Assert.Empty(journal.Load());                       // nichts uebrig im Journal
            }
            finally
            {
                AppTestKit.Delete(journalPath);
            }
        }
    }

    [Fact]
    public void AudioHost_LinksOwnProcess_FeedsEngineInputs_AndRestoresOnStop()
    {
        Setup? setup = Prepare(out string skip);
        if (setup == null) { _out.WriteLine("UEBERSPRUNGEN: " + skip); return; }

        RegisterWithAudio();
        int self = Environment.ProcessId;
        string?[] before = ReadRoutes(self);
        string journalPath = AppTestKit.TempJournal();
        AppRouteJournal.PathOverride = journalPath;
        const int rate = 48000;
        var processor = new SilentCountingProcessor(inputs: 4, outputs: 0, sampleRate: rate); // schreibt nichts aus, misst nur die Eingaenge
        var config = new AudioHostConfig(rate, new[]
        {
            // Takt: Software-Takt (nur Lesen). Eingang 0..1: Loopback des Parkplatzes (nur lesend)
            new DeviceBinding(setup.Parking.Id, DeviceFlow.Capture, 0, 0, 2, Loopback: true),
        }, BlockFrames: 480,
        AppBindings: new[] { new AppBinding(setup.Exe, 2, 2) },
        ParkingDeviceId: setup.Parking.Id);

        using var host = new AudioHost();
        SilentPlayer? player = null;
        try
        {
            host.Start(config, processor);
            Assert.True(host.State == HostState.Running, host.GetStats().LastError);

            // Der Verwalter leitet um (1 Hz) und startet erst danach den Prozess-Loopback
            AppLinkStatus? st = null;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 8000)
            {
                st = host.GetAppLinks().Single();
                if (st.State == AppLinkState.Linked) break;
                Thread.Sleep(100);
            }
            _out.WriteLine($"Status: {st}");
            Assert.Equal(AppLinkState.Linked, st!.State);
            Assert.True(st.ProcessCount >= 1);
            Assert.All(ReadRoutes(self), v => Assert.Equal(setup.PolicyId, v, ignoreCase: true));
            Assert.NotNull(AppRouteJournalFind(journalPath, setup.Exe));

            // Das Programm (unser Testprozess) oeffnet jetzt sein Standardgeraet: landet auf dem Parkplatz
            player = new SilentPlayer();
            Assert.Null(player.Error);
            Assert.Equal(setup.Parking.Id, player.OpenedOn, ignoreCase: true);

            Thread.Sleep(1500);
            AppLane lane = host.CurrentRun!.Apps!.Slots[0].Lanes.Single();
            _out.WriteLine($"Spur: PID {lane.Pid}, {lane.SampleRate} Hz, Ring geschrieben {lane.Ring!.TotalWritten}, Fuellstand {lane.Reader!.FillFraction:F2}, " +
                           $"Unterlaeufe {lane.Reader.Underruns}, Ueberlaeufe {lane.Reader.Overruns}, Alloc {lane.SteadyStateAllocatedBytes} B, Spitze Eingaenge {processor.InputPeak}");
            Assert.True(lane.Ring.TotalWritten > rate / 2, $"Nur {lane.Ring.TotalWritten} Frames im Ring");
            Assert.True(processor.Frames > rate / 2);
            Assert.Equal(0.0, processor.InputPeak);                           // digitale Stille
            Assert.Equal(0, lane.SteadyStateAllocatedBytes);                  // keine Allokationen im Capture-Thread
            Assert.Equal(0, lane.Reader.Overruns);
            AppLinkStatus linked = host.GetAppLinks().Single();
            Assert.InRange(linked.BufferFill, 0.0, 1.0);

            // Stop(): Spur zu, Umleitung zurueck, Journal leer
            player.Dispose();
            player = null;
            var stop = Stopwatch.StartNew();
            host.Stop();
            _out.WriteLine($"Stop() dauerte {stop.ElapsedMilliseconds} ms");
            Assert.Equal(HostState.Stopped, host.State);
            Assert.Empty(host.GetAppLinks());
            Assert.Equal(before, ReadRoutes(self));
            Assert.Empty(new AppRouteJournal(journalPath).Load());
        }
        finally
        {
            player?.Dispose();
            host.Dispose();
            // Rueckversicherung: Absturz-Wiederherstellung ueber das Journal (mehrfach aufrufbar)
            new AppRouteRestorer(journalPath).RestoreAllKnown();
            AppRouteJournal.PathOverride = null;
            try
            {
                Assert.Equal(before, ReadRoutes(self));
            }
            finally
            {
                AppTestKit.Delete(journalPath);
            }
        }
    }

    private static RouteEntry? AppRouteJournalFind(string path, string exe) => new AppRouteJournal(path).Find(exe);

    [Fact]
    public void AudioHost_WithoutParkingDevice_ReportsFailed_AndRedirectsNothing()
    {
        // Braucht nur irgendein virtuelles Geraet fuer die (lesende) Takt-Anbindung
        var dev = new DeviceCatalog().List().FirstOrDefault(d => d.Flow == DeviceFlow.Render && !d.IsDefault
            && d.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) && !d.Name.Contains("CABLE In 16ch", StringComparison.OrdinalIgnoreCase));
        if (dev == null) { _out.WriteLine("UEBERSPRUNGEN: kein virtuelles Geraet."); return; }

        string journalPath = AppTestKit.TempJournal();
        AppRouteJournal.PathOverride = journalPath;
        try
        {
            const int rate = 48000;
            var processor = new SilentCountingProcessor(inputs: 6, outputs: 0, sampleRate: rate);
            var config = new AudioHostConfig(rate, new[] { new DeviceBinding(dev.Id, DeviceFlow.Capture, 0, 0, 2, Loopback: true) },
                AppBindings: new[] { new AppBinding("spotify.exe", 2, 2), new AppBinding("chrome.exe", 4, 2) }); // ParkingDeviceId fehlt
            using var host = new AudioHost();
            host.Start(config, processor);
            Assert.True(host.State == HostState.Running, host.GetStats().LastError);

            IReadOnlyList<AppLinkStatus> links = host.GetAppLinks();
            Assert.Equal(2, links.Count);
            Assert.All(links, l =>
            {
                Assert.Equal(AppLinkState.Failed, l.State);
                Assert.Contains("Parkplatz", l.Message);
            });
            Assert.Equal(new[] { "spotify.exe", "chrome.exe" }, links.Select(l => l.ExeName).ToArray());
            Thread.Sleep(1300);                                              // mindestens ein Verwalter-Takt
            Assert.Empty(new AppRouteJournal(journalPath).Load());           // nichts umgeleitet
            Assert.Equal(0.0, processor.InputPeak);
            host.Stop();
        }
        finally
        {
            AppRouteJournal.PathOverride = null;
            AppTestKit.Delete(journalPath);
        }
    }
}
