using System.Diagnostics;
using NxMix.Audio;
using NxMix.Audio.Apps;
using NxMix.Audio.Host;
using NxMix.Audio.Interop;
using NxMix.Engine;
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
        var candidates = ParkingPreference
            .SelectMany(name => render.Where(d => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .Concat(render.Where(d => d.IsVirtualEndpoint))
            .Where(d => d.Backend == AudioBackend.Windows && !d.IsDefault &&
                        !d.Name.Contains("CABLE In 16ch", StringComparison.OrdinalIgnoreCase))
            .DistinctBy(d => d.Id);
        AudioDeviceInfo? parking = candidates.FirstOrDefault(CanOpenForRender);
        if (parking == null)
        {
            skip = "Kein separates virtuelles Parkplatz-Geraet vorhanden. Windows-Ausgänge: " +
                string.Join(", ", render.Where(d => d.Backend == AudioBackend.Windows).Select(d => d.Name));
            return null;
        }
        return new Setup(parking, exe, AudioPolicyConfig.PolicyDeviceId(parking.Id));
    }

    private bool CanOpenForRender(AudioDeviceInfo device)
    {
        try
        {
            return ComThread.Run(() =>
            {
                using var endpoint = WasapiEndpoint.Open(device.Id, false, false, 48000, 480, false);
                return true;
            }, 5000);
        }
        catch (Exception ex)
        {
            _out.WriteLine($"WASAPI-Probe {device.Name}: {ex.Message}");
            return false;
        }
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
        private readonly string? _endpoint;
        private readonly bool _testTone;
        public string? OpenedOn;
        public string? Error;
        public string? OpenedName;

        public SilentPlayer(string? endpoint = null, bool testTone = false)
        {
            _endpoint = endpoint;
            _testTone = testTone;
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
                ep = WasapiEndpoint.Open(_endpoint, false, false, 48000, 480, false);
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
                var tone = new float[ep.BufferFrames];
                long frame = 0;
                nint* h = stackalloc nint[1];
                h[0] = ep.EventHandle;
                while (!_stop.IsSet)
                {
                    Native.WaitForMultipleObjects(1, h, 0, 50);
                    if (ep.Client.GetCurrentPadding(out uint pad) < 0) break;
                    int avail = (int)(ep.BufferFrames - pad);
                    if (avail <= 0) continue;
                    if (render.GetBuffer((uint)avail, out nint data) < 0) break;
                    new Span<byte>((void*)data, avail * ep.BlockAlign).Clear();
                    if (_testTone)
                    {
                        for (int i = 0; i < avail; i++)
                            tone[i] = 0.001f * MathF.Sin(2 * MathF.PI * 440 * (frame++ % ep.SampleRate) / ep.SampleRate);
                        fixed (float* samples = tone)
                        {
                            for (int channel = 0; channel < Math.Min(ep.DeviceChannels, 2); channel++)
                                PcmConvert.WriteChannel(samples, avail, (byte*)data, ep.BlockAlign, channel, ep.Kind);
                        }
                    }
                    render.ReleaseBuffer((uint)avail, _testTone ? 0 : BufferFlags.Silent);
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
    public void AudioHost_PatchesAppWithoutParkingDevice_AndLeavesWindowsRouteUnchanged()
    {
        var endpoint = new DeviceCatalog().List().FirstOrDefault(d => d.Flow == DeviceFlow.Render &&
            d.Backend == AudioBackend.Windows && !d.IsVirtualEndpoint && d.Channels >= 2 && CanOpenForRender(d));
        if (endpoint == null || !new AppRouteRestorer().IsSupported)
        {
            _out.WriteLine("UEBERSPRUNGEN: Kein nutzbarer physischer WASAPI-Ausgang oder ProcessLoopback.");
            return;
        }

        string? exe = Path.GetFileName(Environment.ProcessPath);
        Assert.False(string.IsNullOrEmpty(exe));
        using var player = new SilentPlayer(endpoint.Id, testTone: true);
        Assert.True(player.Error == null, player.Error);
        int self = Environment.ProcessId;
        string?[] before = ReadRoutes(self);
        var settings = MixerSettings.Create(48000, 4, 12);
        foreach (var row in settings.MatrixDb) Array.Fill(row, double.NegativeInfinity);
        settings.MatrixDb[2][6] = 0.0;
        settings.MatrixDb[3][8] = 0.0;
        var mixer = new MixerEngine(settings);
        using var host = new AudioHost();
        host.Start(new AudioHostConfig(48000, Array.Empty<DeviceBinding>(),
            AppBindings: new[] { new AppBinding(exe, 2, 2) }), mixer);
        Assert.True(host.State == HostState.Running, host.GetStats().LastError);
        var inputPeaks = new float[4];
        var outputPeaks = new float[12];
        Assert.True(SpinWait.SpinUntil(() =>
        {
            if (host.GetAppLinks().Single().State != AppLinkState.MixedWithoutRedirect) return false;
            mixer.ReadInputPeaks(inputPeaks);
            mixer.ReadOutputPeaks(outputPeaks);
            return inputPeaks[2] > 0.0001 && inputPeaks[3] > 0.0001 &&
                outputPeaks[6] > 0.0001 && outputPeaks[8] > 0.0001;
        }, TimeSpan.FromSeconds(8)), $"Kein Signal auf Out 07/09: {host.GetAppLinks().Single()}");
        Assert.Contains("Parkplatz", host.GetAppLinks().Single().Message);
        Assert.All(outputPeaks.Where((_, i) => i != 6 && i != 8), peak => Assert.Equal(0f, peak));
        Assert.Equal(before, ReadRoutes(self));
        _out.WriteLine($"Ohne Parkplatz auf {endpoint.Name}: In 03 → Out 07 {outputPeaks[6]:F6}, In 04 → Out 09 {outputPeaks[8]:F6}.");
    }

    [Fact]
    public void AudioHost_MetersExistingStreamAndMixesItOnlyAfterItMoves()
    {
        Setup? setup = Prepare(out string skip);
        if (setup == null) { _out.WriteLine("UEBERSPRUNGEN: " + skip); return; }
        RegisterWithAudio();
        int self = Environment.ProcessId;
        var output = new DeviceCatalog().List()
            .Where(d => d.Flow == DeviceFlow.Render && d.Backend == AudioBackend.Windows && d.Id != setup.Parking.Id)
            .OrderByDescending(d => d.IsDefault)
            .FirstOrDefault(CanOpenForRender);
        Assert.NotNull(output);
        string?[] before = ReadRoutes(self);
        string journalPath = AppTestKit.TempJournal();
        AppRouteJournal.PathOverride = journalPath;
        try
        {
            using var player = new SilentPlayer(output.Id, testTone: true);
            Assert.True(player.Error == null, player.Error);
            var mixer = new MixerEngine(MixerSettings.Create(48000, 2, 2) with
            {
                MatrixDb = new[] { new[] { 0.0, double.NegativeInfinity }, new[] { double.NegativeInfinity, 0.0 } },
            });
            using var host = new AudioHost();
            host.Start(new AudioHostConfig(48000, Array.Empty<DeviceBinding>(), BlockFrames: 480,
                AppBindings: new[] { new AppBinding(setup.Exe, 0, 2) }, ParkingDeviceId: setup.Parking.Id), mixer);
            Assert.True(host.State == HostState.Running, host.GetStats().LastError);
            var inputPeaks = new float[2];
            var outputPeaks = new float[2];
            Assert.True(SpinWait.SpinUntil(() =>
            {
                if (host.GetAppLinks().Single().State != AppLinkState.Capturing) return false;
                mixer.ReadInputPeaks(inputPeaks);
                return inputPeaks.Max() > 0.0001;
            }, TimeSpan.FromSeconds(8)),
                $"Kein Testsignal am Eingang. App-Capture: {host.GetAppLinks().SingleOrDefault()}");
            mixer.ReadOutputPeaks(outputPeaks);
            _out.WriteLine($"Laufender WASAPI-Stream auf {player.OpenedName}: Eingang {inputPeaks.Max():F6}, Mix-Ausgang {outputPeaks.Max():F6}.");
            Assert.All(outputPeaks, peak => Assert.Equal(0f, peak));
            Assert.All(ReadRoutes(self), route => Assert.Equal(setup.PolicyId, route, ignoreCase: true));

            player.Dispose();
            using var parkedPlayer = new SilentPlayer(testTone: true);
            Assert.True(parkedPlayer.Error == null, parkedPlayer.Error);
            Assert.Equal(setup.Parking.Id, parkedPlayer.OpenedOn, ignoreCase: true);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                if (host.GetAppLinks().Single().State != AppLinkState.Linked) return false;
                mixer.ReadOutputPeaks(outputPeaks);
                return outputPeaks.Max() > 0.0001;
            }, TimeSpan.FromSeconds(8)), $"Kein Mix-Ausgang nach der Umleitung. App-Capture: {host.GetAppLinks().Single()}");
            _out.WriteLine($"Nach dem Neuöffnen auf {setup.Parking.Name}: Mix-Ausgang {outputPeaks.Max():F6}.");
            host.Stop();
            Assert.Equal(before, ReadRoutes(self));
        }
        finally
        {
            new AppRouteRestorer(journalPath).RestoreAllKnown();
            AppRouteJournal.PathOverride = null;
            AppTestKit.Delete(journalPath);
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
        var config = new AudioHostConfig(rate, Array.Empty<DeviceBinding>(), BlockFrames: 480,
        AppBindings: new[] { new AppBinding(setup.Exe, 2, 2) },
        ParkingDeviceId: setup.Parking.Id);

        using var host = new AudioHost();
        SilentPlayer? player = null;
        try
        {
            host.Start(config, processor);
            Assert.True(host.State == HostState.Running, host.GetStats().LastError);

            // Der Verwalter startet Capture und setzt die Umleitung (1 Hz).
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

}
