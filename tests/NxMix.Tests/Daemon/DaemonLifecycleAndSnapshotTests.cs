using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NxMix.Audio;
using NxMix.Daemon;
using NxMix.Daemon.ControlApi;
using NxMix.Daemon.Persistence;
using NxMix.Engine;
using NxMix.Ipc;

namespace NxMix.Tests.Daemon;

public sealed class DaemonLifecycleAndSnapshotTests
{
    private sealed class SimContinuousAudioHost : IAudioHost
    {
        private Thread? _worker;
        private volatile bool _running;
        public HostState State => _running ? HostState.Running : HostState.Stopped;
        public int StartCount;
        public int StopCount;
        public long FramesProcessed;
        public event Action<string>? Faulted { add { } remove { } }

        public void Start(AudioHostConfig config, IBlockProcessor processor)
        {
            Interlocked.Increment(ref StartCount);
            _running = true;
            _worker = new Thread(() =>
            {
                var ins = Enumerable.Range(0, Math.Max(1, processor.InputChannels))
                    .Select(_ => Enumerable.Repeat(0.25f, 256).ToArray()).ToArray();
                var outs = Enumerable.Range(0, Math.Max(1, processor.OutputChannels))
                    .Select(_ => new float[256]).ToArray();
                while (_running)
                {
                    processor.Process(ins, outs, 256);
                    Interlocked.Add(ref FramesProcessed, 256);
                    Thread.Sleep(2);
                }
            }) { IsBackground = true };
            _worker.Start();
        }

        public void Stop()
        {
            if (!_running) return;
            Interlocked.Increment(ref StopCount);
            _running = false;
            _worker?.Join(500);
        }

        public HostStats GetStats() => new(
            State, null, 256, 0.04, 0, 0, 5.3,
            new[] { new DeviceStat("sim:asio/out", "SimASIO Master", 1.0, 0.0, 0, IsMasterClock: true) })
        {
            MasterClockName = "SimASIO Master",
        };

        public void Dispose() => Stop();
    }

    private sealed class EmptyCatalog : IDeviceCatalog
    {
        public IReadOnlyList<AudioDeviceInfo> List() => new[]
        {
            new AudioDeviceInfo("sim:asio/in", "SimASIO In", DeviceFlow.Capture, 8, 48000, false) { Backend = AudioBackend.Asio },
            new AudioDeviceInfo("sim:asio/out", "SimASIO Out", DeviceFlow.Render, 8, 48000, true) { Backend = AudioBackend.Asio },
            new AudioDeviceInfo("sim:vb/out", "CABLE Input (VB-Audio Virtual Cable)", DeviceFlow.Render, 2, 48000, false)
            {
                Backend = AudioBackend.Windows,
                IsVirtualEndpoint = true,
                DriverProvider = "VB-Audio Software",
            },
        };
    }

    private sealed class EmptyApps : IAppCatalog
    {
        public IReadOnlyList<AppSessionInfo> List() => Array.Empty<AppSessionInfo>();
    }

    private sealed class NativeCatalog : IDeviceCatalog
    {
        public int Channels { get; set; } = 2;
        public IReadOnlyList<AudioDeviceInfo> List() =>
        [
            new("sim:nx-in", "NX-MIX In 1", DeviceFlow.Render, Channels, 48000, false)
                { IsVirtualEndpoint = true, DriverProvider = AudioDeviceInfo.NxMixDriverProvider },
            new("sim:nx-out", "NX-MIX Out 1", DeviceFlow.Capture, Channels, 48000, false)
                { IsVirtualEndpoint = true, DriverProvider = AudioDeviceInfo.NxMixDriverProvider, NativeOutputPort = 1 },
        ];
    }

    private sealed class NoopRestorer : IAppRouteRestorer
    {
        public bool IsSupported => true;
        public string? UnsupportedReason => null;
        public void RestoreAllKnown() { }
    }

    private static string CreateTempDir()
    {
        string p = Path.Combine(Path.GetTempPath(), "NxMixTests_" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(p);
        return p;
    }

    private static int GetFreeTcpPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    [Fact]
    public void Shared_memory_telemetry_roundtrips_peaks_and_spectrum_with_zero_allocations()
    {
        string key = "shm_" + Guid.NewGuid().ToString("N")[..10];
        using var writer = new TelemetrySharedMemoryWriter(key);
        using var reader = new TelemetrySharedMemoryReader(key);

        float[] inP = { 0.125f, 0.5f, 0.75f };
        float[] outP = { 0.25f, 0.8f };
        float[] busP = { 0.42f };
        float[] busChannels = { 0.42f, 0.1f };
        float[] devP = { 0.8f, 0.5f };
        float[] pre = Enumerable.Range(0, SpectrumAnalyzer.Points).Select(i => -80f + i * 0.25f).ToArray();
        float[] post = Enumerable.Range(0, SpectrumAnalyzer.Points).Select(i => -70f + i * 0.25f).ToArray();
        var meta = new TelemetrySnapshotFrame(1234, HostState.Running, 256, 48000, false, 0.07, 5.33, 0, 0, 2, 2, 1);

        writer.Publish(inP, outP, busP, devP, in meta, pre, post, busChannels);

        float[] rIn = new float[3];
        float[] rOut = new float[2];
        float[] rBus = new float[1];
        float[] rBusChannels = new float[2];
        float[] rDev = new float[2];
        float[] rPre = new float[SpectrumAnalyzer.Points];
        float[] rPost = new float[SpectrumAnalyzer.Points];

        Assert.True(reader.TryRead(rIn, rOut, rBus, rDev, rPre, rPost, out var rMeta, rBusChannels));
        Assert.Equal(HostState.Running, rMeta.HostState);
        Assert.Equal(256, rMeta.BlockFrames);
        Assert.Equal(2, rMeta.LimiterHits);
        Assert.Equal(0.75f, rIn[2], 5);
        Assert.Equal(0.42f, rBus[0], 5);
        Assert.Equal(busChannels, rBusChannels);
        Assert.Equal(0.8f, rDev[0], 5);
        Assert.Equal(256, rPre.Length);
        Assert.Equal(pre[^1], rPre[^1], 4);
        Assert.Equal(post[^1], rPost[^1], 4);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            Assert.True(reader.TryRead(rIn, rOut, rBus, rDev, rPre, rPost, out _, rBusChannels));
        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());
    }

    [Fact]
    public async Task Virtual_playback_loopback_maps_to_a_stereo_input_and_keeps_the_endpoint_id()
    {
        string dir = CreateTempDir();
        try
        {
            var cfg = new SystemConfig
            {
                AutoStartAudioOnBoot = false,
                CompanionApiEnabled = false,
                Devices = new[] { new AudioDeviceNode { EndpointId = "sim:asio/out", Flow = DeviceFlow.Render } },
            };
            using var controller = new DaemonEngineController(new DaemonEngineOptions
            {
                CustomDataDir = dir,
                CustomIpcKey = "vaio_loopback_" + Guid.NewGuid().ToString("N"),
                DeviceCatalog = new EmptyCatalog(),
                AppCatalog = new EmptyApps(),
                AppRouteRestorer = new NoopRestorer(),
                HostFactory = () => new SimContinuousAudioHost(),
                InitialSystemConfigOverride = cfg,
            });
            await controller.ApplySystemConfigAsync(cfg with
            {
                Vaios = new[]
                {
                    new VaioSlotConfig { Name = "NX-MIX In 1", EndpointOrExe = "sim:vb/out", Loopback = true },
                },
            });

            var saved = new SystemConfigStore(new DaemonPaths(dir)).Load();
            var loopback = Assert.Single(controller.BuildAudioHostConfig(saved).Bindings, b => b.Loopback);
            Assert.Equal(DeviceFlow.Capture, loopback.Flow);
            Assert.Equal("sim:vb/out", loopback.DeviceId);
            Assert.Equal(2, loopback.Count);
            Assert.Equal("NX-MIX In 1 L", controller.Snapshot.Mixer.Inputs[0].Name);
            Assert.Equal("NX-MIX In 1 R", controller.Snapshot.Mixer.Inputs[1].Name);
            Assert.Equal(0.0, controller.Snapshot.Mixer.MatrixDb[0][0]);
            Assert.Equal(0.0, controller.Snapshot.Mixer.MatrixDb[1][1]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Native_WaveRT_switches_active_width_without_replacing_channels_or_routes()
    {
        string dir = CreateTempDir();
        try
        {
            var input = new VaioSlotConfig
            {
                Name = "NX-MIX In 1", Kind = VaioSlotKind.NxMixKernelDriver, Loopback = true, Channels = 8,
                EndpointOrExe = "sim:nx-in",
            };
            var output = new VaioSlotConfig
            {
                Name = "NX-MIX Out 1", Kind = VaioSlotKind.NxMixKernelDriver, Flow = DeviceFlow.Render, Channels = 8,
                EndpointOrExe = "sim:nx-out",
            };
            var config = new SystemConfig { Vaios = [input, output], AutoStartAudioOnBoot = true, CompanionApiEnabled = false };
            var catalog = new NativeCatalog();
            var host = new SimContinuousAudioHost();
            using var controller = new DaemonEngineController(new DaemonEngineOptions
            {
                CustomDataDir = dir, CustomIpcKey = "native_formats_" + Guid.NewGuid().ToString("N"),
                DeviceCatalog = catalog, AppCatalog = new EmptyApps(), AppRouteRestorer = new NoopRestorer(),
                HostFactory = () => host, InitialSystemConfigOverride = config,
            });
            var state = controller.Snapshot.Mixer;
            Assert.Equal(8, state.Inputs.Count);
            Assert.Equal(8, state.Outputs.Count);
            var outputs = state.Outputs.ToArray();
            outputs[7] = outputs[7] with { ColorIndex = 5, GainDb = -12 };
            state.MatrixDb[7][7] = -3;
            Assert.Null(await controller.ApplyMixerAsync(state with { Outputs = outputs }));

            foreach (int channels in new[] { 2, 6, 8, 2 })
            {
                catalog.Channels = channels;
                await controller.RefreshDevicesAsync();
                var bindings = controller.BuildAudioHostConfig(config).Bindings;
                Assert.Equal(channels, Assert.Single(bindings, b => b.Loopback).Count);
                var render = Assert.Single(bindings, b => b.Flow == DeviceFlow.Render);
                Assert.Equal(channels, render.Count);
                Assert.Equal(1, render.NativeOutputPort);
                Assert.Equal(outputs[7], controller.Snapshot.Mixer.Outputs[7]);
                Assert.Equal(-3, controller.Snapshot.Mixer.MatrixDb[7][7]);
            }
            Assert.Equal(4, host.StartCount);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Duplicate_app_links_are_migrated_and_old_channel_labels_are_removed()
    {
        var oldLink = new AppLinkEntry { ExeName = "Spotify", Count = 2 };
        var keptLink = new AppLinkEntry { ExeName = "SPOTIFY.EXE", FirstChannel = 2, Count = 2 };
        var oldConfig = new SystemConfig { AppLinks = new[] { oldLink } };
        var empty = MixerSettings.Create(48000, 4, 2);
        empty = empty with { Inputs = empty.Inputs.Select(channel => channel with { Name = "" }).ToArray() };
        var mixer = DaemonEngineController.ReconcileMixerWithSystemConfig(empty, oldConfig);
        var inputs = mixer.Inputs.ToArray();
        inputs[1] = inputs[1] with { Name = "Eigener Kanalname" };
        mixer = mixer with { Inputs = inputs };

        var clean = SystemConfigStore.Sanitize(new SystemConfig
        {
            AppLinks = new[] { keptLink, oldLink },
            Vaios = new[] { new VaioSlotConfig { Kind = VaioSlotKind.AppCapture, EndpointOrExe = "Spotify.exe" } },
        });
        Assert.Equal(keptLink.Id, Assert.Single(clean.AppLinks).Id);
        Assert.Empty(clean.Vaios);

        var next = DaemonEngineController.ReconcileMixerWithSystemConfig(mixer, clean, oldConfig);
        Assert.Null(next.Inputs[0].DeviceId);
        Assert.Empty(next.Inputs[0].Name);
        Assert.Equal("Eigener Kanalname", next.Inputs[1].Name);
        Assert.DoesNotContain(next.DeviceMasters, master => master.Id == oldLink.Id);
        Assert.Equal(keptLink.Id, next.Inputs[2].DeviceId);
        Assert.Equal(keptLink.Id, next.Inputs[3].DeviceId);
    }

    [Fact]
    public void Removing_app_and_output_clears_owned_channels_and_keeps_later_bindings()
    {
        var spotify = new AppLinkEntry { ExeName = "Spotify.exe", Count = 2 };
        var output = new AudioDeviceNode { EndpointId = "sim:out", Flow = DeviceFlow.Render, ChannelCount = 2 };
        var later = output with { Id = Guid.NewGuid(), EndpointId = "sim:later", EngineFirstChannel = 2 };
        var config = new SystemConfig { AppLinks = [spotify], Devices = [output, later] };
        var mixer = DaemonEngineController.ReconcileMixerWithSystemConfig(MixerSettings.Create(48000, 0, 0), config);
        Guid laterChannel = mixer.Outputs[2].Id;
        mixer.MatrixDb[0][0] = 0;

        var withoutFirst = config with { AppLinks = [], Devices = [later] };
        var removed = DaemonEngineController.ReconcileMixerWithSystemConfig(mixer, withoutFirst, config);
        Assert.Empty(removed.Inputs);
        Assert.All(removed.Outputs.Take(2), channel => Assert.True(channel.IsVacant));
        Assert.Equal(laterChannel, removed.Outputs[2].Id);
        Assert.DoesNotContain(removed.DeviceMasters, master => master.Id == output.Id || master.Id == spotify.Id);

        var added = DaemonEngineController.ReconcileMixerWithSystemConfig(removed, config, withoutFirst);
        Assert.Equal(2, added.Inputs.Count);
        Assert.Equal(4, added.Outputs.Count);
        Assert.Equal(laterChannel, added.Outputs[2].Id);
        Assert.Equal(0, added.MatrixDb[0][0]);

        var noDevices = config with { AppLinks = [], Devices = [] };
        var cleared = DaemonEngineController.ReconcileMixerWithSystemConfig(added, noDevices, config);
        Assert.Empty(cleared.Inputs);
        Assert.Empty(cleared.Outputs);
    }

    [Fact]
    public void Startup_removes_legacy_duplicate_output_block_with_matching_custom_labels()
    {
        var old = new AudioDeviceNode { FriendlyName = "ASIO", Flow = DeviceFlow.Render, ChannelCount = 2 };
        var current = old with { Id = Guid.NewGuid(), EngineFirstChannel = 2 };
        var mixer = DaemonEngineController.ReconcileMixerWithSystemConfig(MixerSettings.Create(48000, 0, 0),
            new SystemConfig { Devices = [old, current] });
        mixer = mixer with
        {
            Outputs = mixer.Outputs.Select((c, i) => c with { AutoCreatedForDevice = null, Name = i % 2 == 0 ? "Links Mid" : "Rechts Mid" }).ToArray(),
        };

        var clean = DaemonEngineController.ReconcileMixerWithSystemConfig(mixer, new SystemConfig { Devices = [current] });
        Assert.All(clean.Outputs.Take(2), c => Assert.True(c.IsVacant));
        Assert.Equal(mixer.Outputs[2].Id, clean.Outputs[2].Id);
        Assert.Equal("Links Mid", clean.Outputs[2].Name);
        Assert.Equal(current.Id, Assert.Single(clean.DeviceMasters).Id);
    }

    [Fact]
    public void Startup_restores_orphaned_asio_matrix_and_crossover_on_active_output_block()
    {
        var old = new AudioDeviceNode
        {
            EndpointId = "sim:asio/umc", FriendlyName = "UMC ASIO Driver", Backend = AudioBackend.Asio,
            Flow = DeviceFlow.Render, ChannelCount = 2,
        };
        var current = old with { Id = Guid.NewGuid(), EngineFirstChannel = 2 };
        var mixer = DaemonEngineController.ReconcileMixerWithSystemConfig(MixerSettings.Create(48000, 2, 0),
            new SystemConfig { Devices = [old, current] });
        var outputs = mixer.Outputs.Select(c => c with { AutoCreatedForDevice = null, Filters = new List<FilterSpec>() }).ToArray();
        outputs[0] = outputs[0] with { Name = "Rechts Mid", Filters = [new FilterSpec(FilterType.HighPass, 80, 0.707, 0, 4, true)] };
        outputs[1] = outputs[1] with { Name = "Links Hi", GainDb = -12, MaxGainDb = -3 };
        foreach (var row in mixer.MatrixDb) Array.Fill(row, double.NegativeInfinity);
        mixer.MatrixDb[0][0] = -3;
        var bus = new MixBus();
        var busRoute = new BusRoute(new RoutePort(RouteKind.Bus, bus.Id), new RoutePort(RouteKind.Output, outputs[1].Id), -6);
        mixer = mixer with { Outputs = outputs, Buses = [bus], BusRoutes = [busRoute] };
        var config = new SystemConfig { Devices = [current] };

        var restored = DaemonEngineController.ReconcileMixerWithSystemConfig(mixer, config);

        Assert.All(restored.Outputs.Take(2), c => Assert.True(c.IsVacant));
        Assert.Equal(outputs[2].Id, restored.Outputs[2].Id);
        Assert.Equal("Rechts Mid", restored.Outputs[2].Name);
        Assert.Equal(outputs[0].Filters, restored.Outputs[2].Filters);
        Assert.Equal("Links Hi", restored.Outputs[3].Name);
        Assert.Equal(-12, restored.Outputs[3].GainDb);
        Assert.Equal(-3, restored.Outputs[3].MaxGainDb);
        Assert.Equal(-3, restored.MatrixDb[0][2]);
        Assert.Equal(double.NegativeInfinity, restored.MatrixDb[0][0]);
        Assert.Equal(busRoute with { Target = busRoute.Target with { Id = outputs[3].Id } }, Assert.Single(restored.BusRoutes));
        Assert.Equal(current.Id, Assert.Single(restored.DeviceMasters).Id);

        mixer.MatrixDb[0][2] = -9;
        var alreadyRouted = DaemonEngineController.ReconcileMixerWithSystemConfig(mixer, config);
        Assert.Equal(-9, alreadyRouted.MatrixDb[0][2]);
        Assert.Equal(-3, alreadyRouted.MatrixDb[0][0]);
        Assert.Empty(alreadyRouted.Outputs[2].Filters);
    }

    [Fact]
    public async Task App_capture_without_render_output_reports_why_audio_cannot_start()
    {
        string dir = CreateTempDir();
        try
        {
            var cfg = new SystemConfig
            {
                CompanionApiEnabled = false,
                ParkingDeviceId = "sim:vb/out",
                AppLinks = new[] { new AppLinkEntry { ExeName = "Spotify.exe", FirstChannel = 2 } },
            };
            using var controller = new DaemonEngineController(new DaemonEngineOptions
            {
                CustomDataDir = dir,
                CustomIpcKey = "app_capture_" + Guid.NewGuid().ToString("N"),
                DeviceCatalog = new EmptyCatalog(),
                AppCatalog = new EmptyApps(),
                AppRouteRestorer = new NoopRestorer(),
                HostFactory = () => new SimContinuousAudioHost(),
                InitialSystemConfigOverride = cfg,
            });

            Assert.Equal(HostState.Stopped, controller.Snapshot.HostStats.State);
            Assert.Contains("Ausgabegerät", controller.Snapshot.AudioStartBlockedReason);

            await controller.ApplySystemConfigAsync(cfg with
            {
                Devices = new[] { new AudioDeviceNode { EndpointId = "sim:asio/out", Flow = DeviceFlow.Render } },
            });
            Assert.Null(controller.Snapshot.AudioStartBlockedReason);
            Assert.Equal(HostState.Running, controller.Snapshot.HostStats.State);
            Assert.Equal(0.0, controller.Snapshot.Mixer.MatrixDb[2][0]);
            Assert.Equal(0.0, controller.Snapshot.Mixer.MatrixDb[3][1]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Client_disconnect_and_reconnect_never_interrupt_continuous_audio_engine()
    {
        string dir = CreateTempDir();
        string key = "pipe_" + Guid.NewGuid().ToString("N")[..10];
        var simHost = new SimContinuousAudioHost();
        try
        {
            var outDev = new AudioDeviceNode
            {
                EndpointId = "sim:asio/out",
                FriendlyName = "SimASIO Out",
                Flow = DeviceFlow.Render,
                Backend = AudioBackend.Asio,
                ChannelCount = 2,
            };
            var inDev = new AudioDeviceNode
            {
                EndpointId = "sim:asio/in",
                FriendlyName = "SimASIO In",
                Flow = DeviceFlow.Capture,
                Backend = AudioBackend.Asio,
                ChannelCount = 2,
            };
            var initCfg = new SystemConfig
            {
                SampleRate = 48000,
                BlockFrames = 256,
                AutoStartAudioOnBoot = true,
                CompanionApiEnabled = false,
                Devices = new[] { inDev, outDev },
            };

            using var controller = new DaemonEngineController(new DaemonEngineOptions
            {
                CustomDataDir = dir,
                CustomIpcKey = key,
                DeviceCatalog = new EmptyCatalog(),
                AppCatalog = new EmptyApps(),
                AppRouteRestorer = new NoopRestorer(),
                HostFactory = () => simHost,
                InitialSystemConfigOverride = initCfg,
            });
            using var server = new NamedPipeControlServer(controller, key);
            server.Start();

            // Audio auto-started at daemon boot without any UI client!
            Assert.Equal(HostState.Running, simHost.State);
            Assert.Equal(1, simHost.StartCount);
            Assert.Equal(0, simHost.StopCount);

            // Connect Client 1, adjust mix, then close Client 1:
            using (var client1 = new NamedPipeBackendClient(key))
            {
                await client1.ConnectAsync(TimeSpan.FromSeconds(5));
                Assert.True(client1.IsConnected);
                Assert.Equal(HostState.Running, client1.Snapshot.HostStats.State);
                Assert.Equal(1, client1.Snapshot.VaioAvailability.ExternalVirtualEndpointCount);

                var mix = client1.Snapshot.Mixer;
                var outs = mix.Outputs.ToArray();
                outs[0] = outs[0] with { GainDb = 0.0 };
                outs[1] = outs[1] with { GainDb = -6.0 };
                Assert.Null(await client1.ApplyMixerAsync(mix with { Outputs = outs }));
                Assert.Null(await client1.AdjustDeviceMasterGainAsync(outDev.Id, -10.0));
            } // Client 1 closed!

            long framesAtDisconnect = Interlocked.Read(ref simHost.FramesProcessed);
            await Task.Delay(60);
            long framesAfterDisconnect = Interlocked.Read(ref simHost.FramesProcessed);

            // Audio kept running seamlessly with 0 stops!
            Assert.Equal(0, simHost.StopCount);
            Assert.True(framesAfterDisconnect > framesAtDisconnect);

            // Connect Client 2 and verify exact live state without restarting audio:
            using var client2 = new NamedPipeBackendClient(key);
            await client2.ConnectAsync(TimeSpan.FromSeconds(5));
            Assert.True(client2.IsConnected);
            Assert.Equal(0, simHost.StopCount);
            Assert.Equal(0.0, client2.Snapshot.Mixer.Outputs[0].GainDb, 3);
            Assert.Equal(-6.0, client2.Snapshot.Mixer.Outputs[1].GainDb, 3);
            var outDm = client2.Snapshot.Mixer.DeviceMasters.First(d => d.Id == outDev.Id);
            Assert.Equal(-10.0, outDm.GainDb, 3);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Real_child_daemon_process_survives_client_exit_and_syncs_multiple_clients()
    {
        string dir = CreateTempDir();
        string key = "proc_" + Guid.NewGuid().ToString("N")[..10];
        string daemonDll = typeof(DaemonEngineController).Assembly.Location;

        using var proc = Process.Start(new ProcessStartInfo("dotnet", $"\"{daemonDll}\" --headless --key {key} --data-dir \"{dir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;

        try
        {
            using (var c1 = new NamedPipeBackendClient(key))
            {
                await c1.ConnectAsync(TimeSpan.FromSeconds(8));
                Assert.True(c1.IsConnected);
                Assert.Equal(proc.Id, c1.Snapshot.DaemonPid);

                var s0 = MixerSettings.Create(48000, 4, 4);
                s0 = s0 with
                {
                    Inputs = s0.Inputs.Select((ch, i) => i == 0 ? ch with { Name = "Host Mic", GainDb = -3.5 } : ch).ToArray(),
                };
                Assert.Null(await c1.ApplyMixerAsync(s0));
            } // c1 closed; child daemon process must keep running!

            await Task.Delay(150);
            Assert.False(proc.HasExited);

            // Connect TWO clients simultaneously (e.g. UI + secondary controller) and verify push sync:
            using var c2 = new NamedPipeBackendClient(key);
            using var c3 = new NamedPipeBackendClient(key);
            await c2.ConnectAsync(TimeSpan.FromSeconds(5));
            await c3.ConnectAsync(TimeSpan.FromSeconds(5));

            Assert.Equal("Host Mic", c2.Snapshot.Mixer.Inputs[0].Name);
            Assert.Equal(-3.5, c2.Snapshot.Mixer.Inputs[0].GainDb, 3);

            var syncReceived = new TaskCompletionSource<DaemonRuntimeSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            c3.SnapshotUpdated += s =>
            {
                if (Math.Abs(s.Mixer.Inputs[0].GainDb - 2.0) < 0.05)
                    syncReceived.TrySetResult(s);
            };

            Assert.Null(await c2.AdjustChannelGainAsync(c2.Snapshot.Mixer.Inputs[0].Id, isInput: true, deltaDb: 5.5)); // -3.5 + 5.5 = +2.0 dB
            var updatedOnC3 = await syncReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2.0, updatedOnC3.Mixer.Inputs[0].GainDb, 3);

            await c2.RequestDaemonShutdownAsync();
            Assert.True(proc.WaitForExit(5000));
        }
        finally
        {
            if (!proc.HasExited) { try { proc.Kill(); } catch { } }
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Snapshot_selective_recall_honors_recall_safe_and_never_touches_system_config()
    {
        string dir = CreateTempDir();
        string key = "snap_" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            using var controller = new DaemonEngineController(new DaemonEngineOptions
            {
                CustomDataDir = dir,
                CustomIpcKey = key,
                SkipInitialDeviceScan = true,
                AppRouteRestorer = new NoopRestorer(),
                InitialSystemConfigOverride = new SystemConfig
                {
                    SampleRate = 48000,
                    BlockFrames = 256,
                    AutoStartAudioOnBoot = false,
                    CompanionApiEnabled = false,
                    ParkingDeviceId = "parking-keep-me",
                },
            });

            var m = MixerSettings.Create(48000, 2, 2);
            m = m with
            {
                Inputs = new[]
                {
                    m.Inputs[0] with { Name = "Moderator", GainDb = 0.0, Mute = false },
                    m.Inputs[1] with { Name = "Music", GainDb = -12.0, Mute = false },
                },
            };
            await controller.ApplyMixerAsync(m);

            // Der frühere Summenfader wird einmalig auf die Ausgangspegel übertragen.
            await controller.ApplyMixerAsync(controller.Snapshot.Mixer with { MasterGainDb = -6.0 });
            Assert.Equal(0.0, controller.Snapshot.Mixer.MasterGainDb);
            Assert.Equal(-6.0, controller.Snapshot.Mixer.Outputs[0].GainDb);

            // Save Snapshot "Talk": Moderator 0 dB, Music -12 dB
            var talkSnap = await controller.SaveSnapshotAsync(null, "Talk", "Moderation", "#3B82F6", 150.0, SnapshotScopeFlags.All);

            var exported = Assert.IsType<MixSnapshot>(await controller.GetSnapshotAsync(talkSnap.Id));
            var imported = await controller.ImportSnapshotAsync(exported);
            Assert.NotEqual(talkSnap.Id, imported.Id);
            Assert.Equal(talkSnap.Id, controller.Snapshot.ActiveSnapshotId);
            Assert.Equal(-12.0, imported.CapturedState.Inputs[1].GainDb);

            // Change live state AND put Moderator on RecallSafe (Fader + Mute), and change SystemConfig.BlockFrames to 512:
            await controller.ApplySystemConfigAsync(controller.Snapshot.Config with { BlockFrames = 512 });
            var liveChanged = controller.Snapshot.Mixer with
            {
                Inputs = new[]
                {
                    controller.Snapshot.Mixer.Inputs[0] with { GainDb = -4.0, Mute = true, RecallSafe = RecallSafeFlags.Fader | RecallSafeFlags.Mute },
                    controller.Snapshot.Mixer.Inputs[1] with { GainDb = 0.0, Mute = true },
                },
            };
            await controller.ApplyMixerAsync(liveChanged);

            var preview = await controller.PreviewSnapshotAsync(talkSnap.Id, SnapshotScopeFlags.All);
            Assert.NotNull(preview);
            Assert.Equal(2, preview!.SafeSkippedCount); // Moderator Fader & Mute blocked by RecallSafe

            Assert.Null(await controller.RecallSnapshotAsync(talkSnap.Id, SnapshotScopeFlags.All, fadeMs: 250.0));

            var after = controller.Snapshot.Mixer;
            // Moderator stayed RecallSafe (-4.0 dB, Mute=true):
            Assert.Equal(-4.0, after.Inputs[0].GainDb, 3);
            Assert.True(after.Inputs[0].Mute);
            // Music was recalled to Talk (-12.0 dB, Mute=false):
            Assert.Equal(-12.0, after.Inputs[1].GainDb, 3);
            Assert.False(after.Inputs[1].Mute);

            // SystemConfig stayed 100% untouched (BlockFrames = 512, ParkingDeviceId = "parking-keep-me"):
            Assert.Equal(512, controller.Snapshot.Config.BlockFrames);
            Assert.Equal("parking-keep-me", controller.Snapshot.Config.ParkingDeviceId);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task Companion_websocket_gateway_handles_atomic_encoder_steps_and_feedback()
    {
        string dir = CreateTempDir();
        string key = "ws_" + Guid.NewGuid().ToString("N")[..10];
        int port = GetFreeTcpPort();
        try
        {
            using var controller = new DaemonEngineController(new DaemonEngineOptions
            {
                CustomDataDir = dir,
                CustomIpcKey = key,
                SkipInitialDeviceScan = true,
                AppRouteRestorer = new NoopRestorer(),
                InitialSystemConfigOverride = new SystemConfig
                {
                    SampleRate = 48000,
                    AutoStartAudioOnBoot = false,
                    CompanionApiEnabled = true,
                    CompanionApiPort = port,
                },
            });
            await controller.ApplyMixerAsync(MixerSettings.Create(48000, 2, 2));

            using var gateway = new CompanionWebSocketGateway(controller);
            Assert.True(gateway.IsListening);

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/v1/ws"), CancellationToken.None);

            // Receive initial state.updated push:
            string firstMsg = await ReceiveWsTextAsync(ws);
            Assert.Contains("state.updated", firstMsg);

            // Send 4 relative Stream Deck+ rotary encoder ticks (+1.5 dB each -> +6.0 dB total):
            for (int i = 1; i <= 4; i++)
            {
                string req = JsonSerializer.Serialize(new
                {
                    jsonrpc = "2.0",
                    id = i,
                    method = "channel.adjustGainDb",
                    @params = new { kind = "input", index = 0, deltaDb = 1.5 },
                });
                await ws.SendAsync(Encoding.UTF8.GetBytes(req), WebSocketMessageType.Text, true, CancellationToken.None);
            }

            // Wait briefly for all 4 RPCs to process:
            var sw = Stopwatch.StartNew();
            while (Math.Abs(controller.Snapshot.Mixer.Inputs[0].GainDb - 6.0) > 0.01 && sw.ElapsedMilliseconds < 3000)
                await Task.Delay(15);

            Assert.Equal(6.0, controller.Snapshot.Mixer.Inputs[0].GainDb, 4);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static async Task<string> ReceiveWsTextAsync(ClientWebSocket ws)
    {
        byte[] buf = new byte[16384];
        using var ms = new MemoryStream();
        while (true)
        {
            var res = await ws.ReceiveAsync(buf, CancellationToken.None);
            ms.Write(buf, 0, res.Count);
            if (res.EndOfMessage) break;
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
