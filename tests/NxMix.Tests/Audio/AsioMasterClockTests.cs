using NxMix.Audio;
using NxMix.Audio.Host;
using NxMix.Daemon;
using NxMix.Ipc;
using NxMix.Engine;

namespace NxMix.Tests.Audio;

[Collection("AudioDevices")]
public sealed class AsioMasterClockTests
{
    private sealed class FakeAsioSession : IAsioSession
    {
        private readonly bool _failOpen;
        private AsioProcessHandler? _duplex;
        private AsioCaptureHandler? _capture;
        private int[] _inCh = Array.Empty<int>();
        private int[] _outCh = Array.Empty<int>();
        private Thread? _pumpThread;
        private volatile bool _running;

        public string DriverName { get; }
        public AsioCaps Capabilities { get; }
        public int CurrentSampleRate { get; private set; }
        public int FramesPerBuffer { get; private set; }
        public int InputLatencySamples => 64;
        public int OutputLatencySamples => 64;
        public int CallbacksExecuted => Volatile.Read(ref _callbacks);
        private int _callbacks;
        public float LastOutputPeak { get; private set; }

        public event EventHandler? DriverResetRequest;
        public event EventHandler<string?>? UnexpectedStop;

        public FakeAsioSession(string driverName, int inCh, int outCh, int sampleRate, int bufferSize, bool failOpen = false)
        {
            DriverName = driverName;
            _failOpen = failOpen;
            if (_failOpen)
                throw new InvalidOperationException($"Simulated offline ASIO hardware '{driverName}'.");
            Capabilities = new AsioCaps(inCh, outCh, 64, 2048, bufferSize, 64);
            CurrentSampleRate = sampleRate;
            FramesPerBuffer = bufferSize;
        }

        public bool IsSampleRateSupported(int sampleRate) => sampleRate is 44100 or 48000 or 96000;

        public void InitRecording(int[] inputChannels, int sampleRate, int? bufferSize, AsioCaptureHandler handler)
        {
            _inCh = inputChannels;
            CurrentSampleRate = sampleRate;
            if (bufferSize is > 0) FramesPerBuffer = bufferSize.Value;
            _capture = handler;
        }

        public void InitDuplex(int[] inputChannels, int[] outputChannels, int sampleRate, int? bufferSize, AsioProcessHandler handler)
        {
            _inCh = inputChannels;
            _outCh = outputChannels;
            CurrentSampleRate = sampleRate;
            if (bufferSize is > 0) FramesPerBuffer = bufferSize.Value;
            _duplex = handler;
        }

        public void Start()
        {
            _running = true;
            _pumpThread = new Thread(PumpLoop) { IsBackground = true, Name = $"FakeASIO:{DriverName}" };
            _pumpThread.Start();
        }

        private void PumpLoop()
        {
            int n = FramesPerBuffer;
            var simIn = Enumerable.Range(0, Math.Max(1, _inCh.Length)).Select(_ => Enumerable.Repeat(0.5f, n).ToArray()).ToArray();
            var simOut = Enumerable.Range(0, Math.Max(1, _outCh.Length)).Select(_ => new float[n]).ToArray();

            while (_running)
            {
                if (_duplex != null)
                {
                    for (int c = 0; c < simOut.Length; c++) Array.Clear(simOut[c], 0, n);
                    var blk = new AsioFrameBlock(simIn, simOut, n);
                    _duplex(in blk);
                    float pk = 0f;
                    for (int i = 0; i < n; i++)
                    {
                        float a = MathF.Abs(simOut[0][i]);
                        if (a > pk) pk = a;
                    }
                    LastOutputPeak = pk;
                }
                else if (_capture != null)
                {
                    var blk = new AsioCaptureBlock(simIn, n);
                    _capture(in blk);
                }
                Interlocked.Increment(ref _callbacks);
                Thread.Sleep(2);
            }
        }

        public void TriggerDriverReset() => DriverResetRequest?.Invoke(this, EventArgs.Empty);
        public void TriggerUnexpectedStop(string reason) => UnexpectedStop?.Invoke(this, reason);

        public void Stop()
        {
            _running = false;
            _pumpThread?.Join(500);
        }

        public void ShowControlPanel() { }
        public void Dispose() => Stop();
    }

    private sealed class FakeAsioBackend : IAsioBackend
    {
        private readonly Dictionary<string, Func<FakeAsioSession>> _factories = new(StringComparer.Ordinal);
        public FakeAsioSession? LastSession { get; private set; }

        public void Register(string name, Func<FakeAsioSession> factory) => _factories[name] = factory;
        public IReadOnlyList<string> GetDriverNames() => _factories.Keys.ToArray();

        public IAsioSession Open(string driverName)
        {
            if (!_factories.TryGetValue(driverName, out var f))
                throw new InvalidOperationException($"Unknown fake ASIO driver '{driverName}'.");
            LastSession = f();
            return LastSession;
        }
    }

    [Fact]
    public async Task Daemon_recovers_asio_after_callbacks_disappear_and_respects_manual_stop()
    {
        const string driver = "ReconnectASIO";
        string directory = Path.Combine(Path.GetTempPath(), "NxMixReconnect_" + Guid.NewGuid().ToString("N"));
        int connected = 1;
        var fake = new FakeAsioBackend();
        fake.Register(driver, () => new FakeAsioSession(driver, 0, 2, 48000, 256, Volatile.Read(ref connected) == 0));
        AsioBackendRegistry.Current = fake;
        try
        {
            using var daemon = new DaemonEngineController(new DaemonEngineOptions
            {
                CustomDataDir = directory,
                CustomIpcKey = "reconnect_" + Guid.NewGuid().ToString("N"),
                SkipInitialDeviceScan = true,
                InitialSystemConfigOverride = new SystemConfig
                {
                    SampleRate = 48000,
                    AutoStartAudioOnBoot = false,
                    AllowOfflineAuxDevices = false,
                    CompanionApiEnabled = false,
                    Devices = new[] { new AudioDeviceNode
                    {
                        Backend = AudioBackend.Asio,
                        EndpointId = AsioCatalog.FormatDeviceId(driver, DeviceFlow.Render),
                        Flow = DeviceFlow.Render,
                    } },
                },
            });
            await daemon.StartAudioAsync();
            Assert.Equal(HostState.Running, daemon.RawHost.State);
            var first = Assert.IsType<FakeAsioSession>(fake.LastSession);
            var outputs = daemon.Snapshot.Mixer.Outputs.Select(channel => channel.Id).ToArray();
            Volatile.Write(ref connected, 0);
            first.Stop(); // Simulates unplugging without a driver reset notification.
            Assert.True(await WaitUntil(() => daemon.RawHost.State == HostState.Faulted, 6));
            Volatile.Write(ref connected, 1);
            Assert.True(await WaitUntil(() => daemon.RawHost.State == HostState.Running && fake.LastSession is { CallbacksExecuted: > 0 } active
                && active != first, 8));
            Assert.Equal(outputs, daemon.Snapshot.Mixer.Outputs.Select(channel => channel.Id));
            await daemon.StopAudioAsync();
            await Task.Delay(6000);
            Assert.Equal(HostState.Stopped, daemon.RawHost.State);
        }
        finally
        {
            AsioBackendRegistry.ResetToDefault();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    static async Task<bool> WaitUntil(Func<bool> condition, int seconds)
    {
        long until = Environment.TickCount64 + seconds * 1000;
        while (Environment.TickCount64 < until)
        {
            if (condition()) return true;
            await Task.Delay(40);
        }
        return condition();
    }

    [Fact]
    public void Asio_duplex_device_drives_engine_directly_as_master_clock_without_resampling()
    {
        var fake = new FakeAsioBackend();
        fake.Register("UFX_FakeASIO", () => new FakeAsioSession("UFX_FakeASIO", inCh: 4, outCh: 4, sampleRate: 48000, bufferSize: 256));
        AsioBackendRegistry.Current = fake;
        try
        {
            var catalog = AsioCatalog.List();
            Assert.Contains(catalog, d => d.Id == "asio:UFX_FakeASIO/in" && d.Channels == 4);
            Assert.Contains(catalog, d => d.Id == "asio:UFX_FakeASIO/out" && d.Channels == 4);

            var s = MixerSettings.Create(48000, 2, 2);
            s.MatrixDb[0][0] = 0.0;
            s.MatrixDb[1][1] = 0.0;
            var engine = MixerEngine.CreatePooled(s);

            using var host = new AudioHost();
            var bindings = new[]
            {
                new DeviceBinding("asio:UFX_FakeASIO/in", DeviceFlow.Capture, 0, 0, 2),
                new DeviceBinding("asio:UFX_FakeASIO/out", DeviceFlow.Render, 0, 0, 2) { BufferFrames = 256 },
            };

            host.Start(new AudioHostConfig(48000, bindings, BlockFrames: 256), engine);
            Assert.Equal(HostState.Running, host.State);

            // Wait until > 15 callbacks (fade-in complete at 256 frames * 15 = 3840 frames > 2400 fade frames):
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (fake.LastSession!.CallbacksExecuted < 25 && sw.ElapsedMilliseconds < 2000)
                Thread.Sleep(10);

            var stats = host.GetStats();
            Assert.Equal(HostState.Running, stats.State);
            Assert.Equal(256, stats.BlockFrames);
            var masterStat = Assert.Single(stats.Devices);
            Assert.True(masterStat.IsMasterClock);
            Assert.Equal(0.0, masterStat.ResampleRatioPpm);
            Assert.InRange(fake.LastSession.LastOutputPeak, 0.49f, 0.51f);

            host.Stop();
            Assert.Equal(HostState.Stopped, host.State);
        }
        finally
        {
            AsioBackendRegistry.ResetToDefault();
        }
    }

    [Fact]
    public void Offline_auxiliary_device_is_marked_offline_without_stopping_master_when_allowed()
    {
        var fake = new FakeAsioBackend();
        fake.Register("MainASIO", () => new FakeAsioSession("MainASIO", inCh: 2, outCh: 2, sampleRate: 48000, bufferSize: 256));
        fake.Register("UnpluggedASIO", () => new FakeAsioSession("UnpluggedASIO", inCh: 2, outCh: 0, sampleRate: 48000, bufferSize: 256, failOpen: true));
        AsioBackendRegistry.Current = fake;
        try
        {
            var engine = MixerEngine.CreatePooled(MixerSettings.Create(48000, 4, 2));
            using var host = new AudioHost();
            var bindings = new[]
            {
                new DeviceBinding("asio:MainASIO/out", DeviceFlow.Render, 0, 0, 2),
                new DeviceBinding("asio:UnpluggedASIO/in", DeviceFlow.Capture, 0, 2, 2),
            };

            host.Start(new AudioHostConfig(48000, bindings, BlockFrames: 256, AllowOfflineAuxDevices: true), engine);
            Assert.Equal(HostState.Running, host.State);

            var stats = host.GetStats();
            Assert.Equal(2, stats.Devices.Count);
            Assert.Contains(stats.Devices, d => d.Name.Contains("MainASIO") && d.IsMasterClock && !d.IsOffline);
            Assert.Contains(stats.Devices, d => d.DeviceId == "asio:UnpluggedASIO/in" && d.IsOffline && !string.IsNullOrWhiteSpace(d.OfflineReason));

            host.Stop();
        }
        finally
        {
            AsioBackendRegistry.ResetToDefault();
        }
    }
}
