using NxMix.Audio;
using NxMix.Audio.Host;
using NxMix.Engine;
using Xunit.Abstractions;

namespace NxMix.Tests.Audio;

[CollectionDefinition("AudioDevices", DisableParallelization = true)]
public sealed class AudioDevicesCollection { }

/// <summary>Zaehlender Test-Prozessor. Schreibt AUSSCHLIESSLICH exakte Nullen in die Ausgaenge (nie Signal, nie Durchreichen der Eingaenge).</summary>
internal sealed class SilentCountingProcessor : IBlockProcessor
{
    private long _blocks, _frames;
    private double _inputPeak;

    public SilentCountingProcessor(int inputs, int outputs, int sampleRate)
    {
        InputChannels = inputs;
        OutputChannels = outputs;
        SampleRate = sampleRate;
    }

    public int InputChannels { get; }
    public int OutputChannels { get; }
    public int SampleRate { get; }
    public long Blocks => Interlocked.Read(ref _blocks);
    public long Frames => Interlocked.Read(ref _frames);
    public double InputPeak => Volatile.Read(ref _inputPeak);

    public void Process(float[][] inputs, float[][] outputs, int frames)
    {
        double peak = 0;
        for (int c = 0; c < InputChannels; c++)
            for (int i = 0; i < frames; i++)
                peak = Math.Max(peak, Math.Abs(inputs[c][i]));
        if (peak > Volatile.Read(ref _inputPeak)) Volatile.Write(ref _inputPeak, peak);

        for (int c = 0; c < OutputChannels; c++)
            Array.Clear(outputs[c], 0, frames); // nur Stille

        Interlocked.Increment(ref _blocks);
        Interlocked.Add(ref _frames, frames);
    }
}

/// <summary>Geraetetests. Ist das Standardgeraet von einer anderen Anwendung exklusiv belegt (AUDCLNT_E_DEVICE_IN_USE), weicht ein Test
/// auf das naechste Geraet aus: beim reinen Lesen (Loopback) auf jedes Render-Geraet, beim Rendern NUR auf virtuelle VB-Audio-Geraete
/// (nie auf physische Ausgaenge). Jeder andere Startfehler laesst den Test fehlschlagen.</summary>
[Collection("AudioDevices")]
public class AudioDeviceTests
{
    private const string InUseCode = "0x8889000A";
    private readonly ITestOutputHelper _out;

    public AudioDeviceTests(ITestOutputHelper output) => _out = output;

    private static int RateOf(AudioDeviceInfo d) => d.SampleRate >= 8000 ? d.SampleRate : 48000;

    private static bool IsVirtual(AudioDeviceInfo d) => d.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<AudioDeviceInfo> Candidates(bool renderOutput)
    {
        var all = new DeviceCatalog().List().Where(d => d.Flow == DeviceFlow.Render).ToList();
        AudioDeviceInfo? def = all.FirstOrDefault(d => d.IsDefault);
        if (def != null) yield return def;
        // Beim Rendern bevorzugt neutrale Kabel-/Matrix-Eingaenge (reine Software-Geraete) vor Voicemeeter-Streifen
        var others = all.Where(d => !d.IsDefault && (!renderOutput || IsVirtual(d)))
            .OrderBy(d => d.Name.Contains("CABLE", StringComparison.OrdinalIgnoreCase) ? 0
                : d.Name.Contains("VBMatrix", StringComparison.OrdinalIgnoreCase) ? 1 : 2)
            .Take(8);
        foreach (AudioDeviceInfo d in others)
            yield return d;
    }

    /// <summary>Fuehrt das Szenario auf dem ersten brauchbaren Geraet aus.</summary>
    private bool RunOnFirstUsableDevice(bool renderOutput, Func<AudioDeviceInfo, bool> scenario)
    {
        foreach (AudioDeviceInfo dev in Candidates(renderOutput))
        {
            if (scenario(dev)) return true;
        }
        _out.WriteLine("Kein nutzbares Geraet (alle exklusiv belegt oder keines vorhanden): Szenario NICHT ausgefuehrt.");
        return false;
    }

    /// <summary>true = laeuft. false = Geraet exklusiv belegt (uebersprungen). Alles andere: Fehlschlag.</summary>
    private bool StartedOrBusy(AudioHost host, AudioDeviceInfo dev)
    {
        if (host.State == HostState.Running) return true;
        string? error = host.GetStats().LastError;
        if (error != null && error.Contains(InUseCode))
        {
            _out.WriteLine($"[{dev.Name}] von einer anderen Anwendung exklusiv belegt - weiche aus.");
            return false;
        }
        Assert.Fail($"Start auf '{dev.Name}' fehlgeschlagen (State={host.State}): {error}");
        return false;
    }

    private void Dump(AudioHost host)
    {
        HostStats s = host.GetStats();
        _out.WriteLine($"State={s.State} Block={s.BlockFrames} CPU={s.CpuLoad:P1} Underruns={s.Underruns} Overruns={s.Overruns} Latenz={s.OutputLatencyMs:F1} ms Fehler={s.LastError}");
        foreach (DeviceStat d in s.Devices)
            _out.WriteLine($"  {d.Name}: fill={d.BufferFill:F2} ppm={d.ResampleRatioPpm:F1} glitches={d.Glitches}");
    }

    [Fact]
    public void DeviceCatalog_List_DoesNotThrow_AndEntriesArePlausible()
    {
        IReadOnlyList<AudioDeviceInfo> list = new DeviceCatalog().List();
        Assert.NotNull(list);
        foreach (AudioDeviceInfo d in list)
        {
            _out.WriteLine($"{d.Flow,-7} {(d.IsDefault ? "*" : " ")} {d.Channels,2} ch {d.SampleRate,6} Hz  {d.Name}");
            Assert.False(string.IsNullOrWhiteSpace(d.Id));
            Assert.False(string.IsNullOrWhiteSpace(d.Name));
            Assert.InRange(d.Channels, 0, 256);
            Assert.InRange(d.SampleRate, 0, 768000);
        }
        Assert.True(list.Count(d => d.Flow == DeviceFlow.Render && d.IsDefault) <= 1);
        Assert.True(list.Count(d => d.Flow == DeviceFlow.Capture && d.IsDefault) <= 1);
    }

    [Fact]
    public void DeviceCatalog_List_WorksFromStaThread()
    {
        IReadOnlyList<AudioDeviceInfo>? list = null;
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { list = new DeviceCatalog().List(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        Assert.True(t.Join(30000));
        Assert.Null(error);
        Assert.NotNull(list);
    }

    [Fact]
    public void Loopback_OfDefaultRenderDevice_ReadOnly_DeliversBlocks()
    {
        RunOnFirstUsableDevice(renderOutput: false, dev =>
        {
            int rate = RateOf(dev);
            int ch = Math.Max(1, Math.Min(2, dev.Channels));
            var processor = new SilentCountingProcessor(inputs: ch, outputs: 0, sampleRate: rate);
            var config = new AudioHostConfig(rate, new[] { new DeviceBinding(dev.Id, DeviceFlow.Capture, 0, 0, ch, Loopback: true) }, BlockFrames: rate / 100);

            using var host = new AudioHost();
            string? fault = null;
            host.Faulted += m => fault = m;
            host.Start(config, processor);
            if (!StartedOrBusy(host, dev)) return false;
            _out.WriteLine($"Loopback auf: {dev.Name} ({dev.Channels} ch, {dev.SampleRate} Hz)");

            Thread.Sleep(1000);
            Dump(host);
            HostStats stats = host.GetStats();

            Assert.Null(fault);
            Assert.Equal(HostState.Running, stats.State);
            Assert.True(processor.Blocks > 10, $"Nur {processor.Blocks} Bloecke");
            Assert.True(processor.Frames > rate / 2, $"Nur {processor.Frames} Frames");
            Assert.Single(stats.Devices);
            Assert.InRange(stats.CpuLoad, 0.0, 1.0);
            Assert.Equal(0, stats.Overruns);
            _out.WriteLine($"Eingangs-Spitzenpegel (nur gelesen): {processor.InputPeak:F4}, Bloecke {processor.Blocks}, Frames {processor.Frames}");

            host.Stop();
            Assert.Equal(HostState.Stopped, host.State);
            Assert.Null(host.CurrentRun);
            return true;
        });
    }

    [Fact]
    public void Render_ExactSilence_ToDefaultDevice_SharedMode()
    {
        RunOnFirstUsableDevice(renderOutput: true, dev =>
        {
            int rate = RateOf(dev);
            int ch = Math.Max(1, Math.Min(2, dev.Channels));
            // NUR digitale Stille: Prozessor ohne Eingaenge, schreibt ausschliesslich Nullen.
            var processor = new SilentCountingProcessor(inputs: 0, outputs: ch, sampleRate: rate);
            var config = new AudioHostConfig(rate, new[] { new DeviceBinding(dev.Id, DeviceFlow.Render, 0, 0, ch) }, BlockFrames: rate / 100, UseExclusiveMode: false);

            using var host = new AudioHost();
            string? fault = null;
            host.Faulted += m => fault = m;
            host.Start(config, processor);
            if (!StartedOrBusy(host, dev)) return false;
            _out.WriteLine($"Stille auf: {dev.Name} ({dev.Channels} ch, {dev.SampleRate} Hz)");

            Thread.Sleep(1500); // max. 2 s
            Dump(host);
            HostStats stats = host.GetStats();

            Assert.Null(fault);
            Assert.Equal(HostState.Running, stats.State);
            Assert.True(processor.Frames > rate / 2, $"Nur {processor.Frames} Frames verarbeitet");
            Assert.Single(stats.Devices);
            Assert.InRange(stats.CpuLoad, 0.0, 1.0);
            Assert.True(stats.OutputLatencyMs > 0 && stats.OutputLatencyMs < 500, "Latenz " + stats.OutputLatencyMs);

            host.Stop();
            Assert.Equal(HostState.Stopped, host.State);
            return true;
        });
    }

    [Fact]
    public void Render_PlusLoopback_SameDevice_ExercisesRingPath_WithSilenceOnly()
    {
        RunOnFirstUsableDevice(renderOutput: true, dev =>
        {
            int rate = RateOf(dev);
            int ch = Math.Max(1, Math.Min(2, dev.Channels));
            // Master = Render (nur Stille). Zusaetzlich liest ein Loopback-Stream (nur lesend) ueber Ringpuffer + adaptives Resampling.
            // Der Prozessor ignoriert die Eingaenge: auf dem Geraet kommt ausschliesslich exakte Stille an.
            var processor = new SilentCountingProcessor(inputs: ch, outputs: ch, sampleRate: rate);
            var config = new AudioHostConfig(rate, new[]
            {
                new DeviceBinding(dev.Id, DeviceFlow.Render, 0, 0, ch),
                new DeviceBinding(dev.Id, DeviceFlow.Capture, 0, 0, ch, Loopback: true),
            }, BlockFrames: rate / 100);

            using var host = new AudioHost();
            string? fault = null;
            host.Faulted += m => fault = m;
            host.Start(config, processor);
            if (!StartedOrBusy(host, dev)) return false;
            _out.WriteLine($"Render+Loopback auf: {dev.Name} ({dev.Channels} ch, {dev.SampleRate} Hz)");

            Thread.Sleep(1500);
            Dump(host);
            HostStats stats = host.GetStats();
            HostRun run = host.CurrentRun!;
            long written = run.Captures[0].Ring!.TotalWritten;
            _out.WriteLine($"Loopback-Ring: {written} Frames geschrieben, Spitzenpegel {processor.InputPeak:F4}");

            Assert.Null(fault);
            Assert.Equal(HostState.Running, stats.State);
            Assert.Equal(2, stats.Devices.Count);
            Assert.True(processor.Frames > rate / 2);
            Assert.True(written > 0, "Der Loopback-Stream hat keine Frames geliefert.");

            host.Stop();
            Assert.Equal(HostState.Stopped, host.State);
            return true;
        });
    }

    [Fact]
    public void AllPaths_MasterRender_SecondaryRender_Capture_Loopback_SilenceOnly()
    {
        // Nur virtuelle Software-Geraete; der Prozessor schreibt ausschliesslich Nullen (die Eingaenge werden nur gemessen).
        var list = new DeviceCatalog().List();
        var renders = Candidates(renderOutput: true).Where(d => IsVirtual(d)).Take(8).ToList();
        AudioDeviceInfo? cap = list.FirstOrDefault(d => d.Flow == DeviceFlow.Capture && IsVirtual(d) && d.Channels >= 2);
        if (renders.Count < 2 || cap == null) { _out.WriteLine("Zu wenige virtuelle Geraete: Szenario NICHT ausgefuehrt."); return; }

        const int rate = 48000;
        for (int i = 0; i + 1 < renders.Count; i++)
        {
            AudioDeviceInfo a = renders[i], b = renders[i + 1];
            var processor = new SilentCountingProcessor(inputs: 4, outputs: 4, sampleRate: rate);
            var config = new AudioHostConfig(rate, new[]
            {
                new DeviceBinding(a.Id, DeviceFlow.Render, 0, 0, 2),                    // Master
                new DeviceBinding(b.Id, DeviceFlow.Render, 0, 2, 2),                    // zweites Render-Geraet (Ringpuffer + Resampling)
                new DeviceBinding(cap.Id, DeviceFlow.Capture, 0, 0, 2),                 // Capture (Ringpuffer + Resampling)
                new DeviceBinding(a.Id, DeviceFlow.Capture, 0, 2, 2, Loopback: true),   // Loopback des Masters (nur lesend)
            }, BlockFrames: 480);

            using var host = new AudioHost();
            string? fault = null;
            host.Faulted += m => fault = m;
            host.Start(config, processor);
            if (!StartedOrBusy(host, a)) continue;
            _out.WriteLine($"Master: {a.Name} | Render2: {b.Name} | Capture: {cap.Name} | Loopback: {a.Name}");

            Thread.Sleep(1800);
            Dump(host);
            HostStats stats = host.GetStats();
            HostRun run = host.CurrentRun!;
            long capWritten = run.Captures.First(c => !c.Spec!.Loopback).Ring!.TotalWritten;
            long loopWritten = run.Captures.First(c => c.Spec!.Loopback).Ring!.TotalWritten;
            long renderRead = run.Renders[0].Ring!.TotalRead;
            _out.WriteLine($"Capture-Ring geschrieben: {capWritten}, Loopback-Ring geschrieben: {loopWritten}, Render-Ring gelesen: {renderRead}");

            Assert.Null(fault);
            Assert.Equal(HostState.Running, stats.State);
            Assert.Equal(4, stats.Devices.Count);
            Assert.True(processor.Frames > rate, $"Nur {processor.Frames} Frames");
            Assert.True(capWritten > 0, "Capture lieferte nichts");
            Assert.True(loopWritten > 0, "Loopback lieferte nichts");
            Assert.True(renderRead > 0, "Das zweite Render-Geraet hat nichts aus dem Ringpuffer gelesen");
            Assert.All(stats.Devices, d => Assert.InRange(d.BufferFill, 0.0, 1.0));

            // Diagnose: welcher WASAPI-Pfad wurde genommen, und haben die Audio-Threads nach dem Einschwingen NICHTS allokiert?
            StreamBase[] streams = new[] { run.Master }.Concat(run.Captures).Concat(run.Renders).ToArray();
            foreach (StreamBase s in streams)
            {
                var ep = s.Endpoint;
                _out.WriteLine($"  [{s.Name}] LowLatency={ep?.LowLatency} Periode={ep?.PeriodFrames} Puffer={ep?.BufferFrames} " +
                               $"Event={ep?.EventDriven} Exklusiv={ep?.Exclusive} Rate={ep?.SampleRate} Format={ep?.Kind} Alloc={s.SteadyStateAllocatedBytes} B");
            }
            Assert.All(streams, s => Assert.Equal(0, s.SteadyStateAllocatedBytes));

            var sw = System.Diagnostics.Stopwatch.StartNew();
            host.Stop();
            _out.WriteLine($"Stop() dauerte {sw.ElapsedMilliseconds} ms");
            Assert.True(sw.ElapsedMilliseconds < 1500, "Stop() zu langsam: " + sw.ElapsedMilliseconds);
            Assert.Equal(HostState.Stopped, host.State);
            return;
        }
        _out.WriteLine("Alle Geraetepaare belegt: Szenario NICHT ausgefuehrt.");
    }

    private sealed class ThrowingProcessor : IBlockProcessor
    {
        private readonly int _throwAfter;
        private int _calls;

        public ThrowingProcessor(int inputs, int outputs, int rate, int throwAfter)
        {
            InputChannels = inputs;
            OutputChannels = outputs;
            SampleRate = rate;
            _throwAfter = throwAfter;
        }

        public int InputChannels { get; }
        public int OutputChannels { get; }
        public int SampleRate { get; }

        public void Process(float[][] inputs, float[][] outputs, int frames)
        {
            // Nur Stille ausgeben, bis der Fehler ausgeloest wird
            for (int c = 0; c < OutputChannels; c++) Array.Clear(outputs[c], 0, frames);
            if (++_calls >= _throwAfter) throw new InvalidOperationException("Testfehler im Prozessor");
        }
    }

    [Theory]
    [InlineData(false)] // Software-Takt (nur Loopback-Lesen)
    [InlineData(true)]  // Master-Render auf virtuellem Geraet (nur Stille)
    public void RuntimeError_InProcessor_FaultsHost_ReleasesEverything_NoCrash(bool deviceMaster)
    {
        RunOnFirstUsableDevice(renderOutput: deviceMaster, dev =>
        {
            int rate = RateOf(dev);
            int ch = Math.Max(1, Math.Min(2, dev.Channels));
            var processor = new ThrowingProcessor(inputs: deviceMaster ? 0 : ch, outputs: deviceMaster ? ch : 0, rate, throwAfter: 20);
            var binding = deviceMaster
                ? new DeviceBinding(dev.Id, DeviceFlow.Render, 0, 0, ch)
                : new DeviceBinding(dev.Id, DeviceFlow.Capture, 0, 0, ch, Loopback: true);

            using var host = new AudioHost();
            using var raised = new ManualResetEventSlim(false);
            string? fault = null;
            host.Faulted += m => { fault = m; raised.Set(); };
            host.Start(new AudioHostConfig(rate, new[] { binding }, BlockFrames: rate / 100), processor);
            if (!StartedOrBusy(host, dev)) return false;

            Assert.True(raised.Wait(10000), "Faulted-Event kam nicht");
            Assert.Contains("Testfehler", fault);
            Assert.Equal(HostState.Faulted, host.State);
            Assert.Contains("Testfehler", host.GetStats().LastError);

            var sw = System.Diagnostics.Stopwatch.StartNew();
            host.Stop();
            Assert.True(sw.ElapsedMilliseconds < 3000, "Stop() nach Fehler zu langsam: " + sw.ElapsedMilliseconds);
            Assert.Equal(HostState.Stopped, host.State);
            Assert.Null(host.CurrentRun);

            // danach laeuft ein neuer Start wieder normal
            host.Start(new AudioHostConfig(rate, new[] { binding }, BlockFrames: rate / 100), new SilentCountingProcessor(deviceMaster ? 0 : ch, deviceMaster ? ch : 0, rate));
            Assert.True(host.State == HostState.Running, host.GetStats().LastError);
            Thread.Sleep(150);
            host.Stop();
            return true;
        });
    }

    [Fact]
    public void Mmcss_ProAudio_CanBeEnteredAndLeft()
    {
        nint handle = NxMix.Audio.Interop.Mmcss.Enter();
        try
        {
            Assert.NotEqual(0, handle);
        }
        finally
        {
            NxMix.Audio.Interop.Mmcss.Leave(handle);
        }
    }

    [Fact]
    public void ExclusiveModeFormats_AreBuiltAsExtensibleWithCorrectSizes()
    {
        // Der Exklusivmodus wird bewusst nicht auf echten Geraeten getestet (er wuerde laufende Anwendungen verdraengen).
        // Hier nur der Format-Aufbau fuer IsFormatSupported/Initialize.
        var f = NxMix.Audio.Interop.WasapiEndpoint.BuildFormat(NxMix.Audio.Interop.SampleKind.Int32, 10, 96000, 0x3FF, 24);
        Assert.Equal(0xFFFE, f.Format.wFormatTag);
        Assert.Equal(10, f.Format.nChannels);
        Assert.Equal(96000u, f.Format.nSamplesPerSec);
        Assert.Equal(32, f.Format.wBitsPerSample);
        Assert.Equal(40, f.Format.nBlockAlign);
        Assert.Equal(96000u * 40, f.Format.nAvgBytesPerSec);
        Assert.Equal(22, f.Format.cbSize);
        Assert.Equal(24, f.wValidBitsPerSample);
        Assert.Equal(0x3FFu, f.dwChannelMask);
        Assert.Equal(new Guid("00000001-0000-0010-8000-00aa00389b71"), f.SubFormat);

        var p = NxMix.Audio.Interop.WasapiEndpoint.BuildFormat(NxMix.Audio.Interop.SampleKind.Int24, 2, 48000, 3, 24);
        Assert.Equal(24, p.Format.wBitsPerSample);
        Assert.Equal(6, p.Format.nBlockAlign);

        var fl = NxMix.Audio.Interop.WasapiEndpoint.BuildFormat(NxMix.Audio.Interop.SampleKind.Float32, 2, 48000, 3, 32);
        Assert.Equal(new Guid("00000003-0000-0010-8000-00aa00389b71"), fl.SubFormat);
    }

    [Fact]
    public void Start_WithUnknownDevice_FaultsWithoutThrowing()
    {
        var processor = new SilentCountingProcessor(0, 2, 48000);
        var config = new AudioHostConfig(48000, new[]
        {
            new DeviceBinding("{0.0.0.00000000}.{00000000-dead-beef-0000-000000000000}", DeviceFlow.Render, 0, 0, 2),
        });

        using var host = new AudioHost();
        using var raised = new ManualResetEventSlim(false);
        string? fault = null;
        host.Faulted += m => { fault = m; raised.Set(); };

        host.Start(config, processor); // darf nicht werfen

        Assert.Equal(HostState.Faulted, host.State);
        Assert.True(raised.Wait(5000), "Faulted-Event kam nicht");
        Assert.False(string.IsNullOrWhiteSpace(fault));
        _out.WriteLine(fault);
        Assert.Equal(fault, host.GetStats().LastError);
        Assert.Equal(0, processor.Blocks);

        host.Stop();
        Assert.Equal(HostState.Stopped, host.State);
    }

    [Fact]
    public void Start_WithInvalidConfig_FaultsWithMessage()
    {
        using var host = new AudioHost();
        using var raised = new ManualResetEventSlim(false);
        host.Faulted += _ => raised.Set();

        // Binding verlangt Engine-Ausgang 0..3, der Prozessor hat nur 2
        host.Start(new AudioHostConfig(48000, new[] { new DeviceBinding("default", DeviceFlow.Render, 0, 0, 4) }),
            new SilentCountingProcessor(0, 2, 48000));
        Assert.Equal(HostState.Faulted, host.State);
        Assert.True(raised.Wait(5000));
        Assert.Contains("Engine", host.GetStats().LastError);

        // Samplerate passt nicht zum Prozessor
        raised.Reset();
        host.Start(new AudioHostConfig(96000, new[] { new DeviceBinding("default", DeviceFlow.Render, 0, 0, 2) }),
            new SilentCountingProcessor(0, 2, 48000));
        Assert.Equal(HostState.Faulted, host.State);
        Assert.True(raised.Wait(5000));
        Assert.Contains("Samplerate", host.GetStats().LastError);

        // Keine Bindings
        raised.Reset();
        host.Start(new AudioHostConfig(48000, Array.Empty<DeviceBinding>()), new SilentCountingProcessor(0, 2, 48000));
        Assert.Equal(HostState.Faulted, host.State);
        Assert.True(raised.Wait(5000));
    }

    [Fact]
    public void StopAndDispose_AreIdempotent()
    {
        var host = new AudioHost();
        Assert.Equal(HostState.Stopped, host.State);
        host.Stop();
        host.Stop();
        HostStats s = host.GetStats();
        Assert.Equal(HostState.Stopped, s.State);
        Assert.Empty(s.Devices);
        host.Dispose();
        host.Dispose();
        host.Stop();
        Assert.Throws<ObjectDisposedException>(() =>
            host.Start(new AudioHostConfig(48000, new[] { new DeviceBinding("default", DeviceFlow.Render, 0, 0, 2) }), new SilentCountingProcessor(0, 2, 48000)));
        Assert.Throws<ArgumentNullException>(() => new AudioHost().Start(null!, new SilentCountingProcessor(0, 2, 48000)));
    }

    [Fact]
    public void Restart_StopStart_Works_AndRepeatedStopIsHarmless()
    {
        RunOnFirstUsableDevice(renderOutput: false, dev =>
        {
            int rate = RateOf(dev);
            int ch = Math.Max(1, Math.Min(2, dev.Channels));
            var processor = new SilentCountingProcessor(ch, 0, rate);
            var config = new AudioHostConfig(rate, new[] { new DeviceBinding(dev.Id, DeviceFlow.Capture, 0, 0, ch, Loopback: true) }, BlockFrames: rate / 100);

            using var host = new AudioHost();
            host.Start(config, processor);
            if (!StartedOrBusy(host, dev)) return false;
            host.Stop();
            for (int i = 0; i < 3; i++)
            {
                host.Start(config, processor);
                Assert.True(host.State == HostState.Running, host.GetStats().LastError);
                Thread.Sleep(200);
                host.Stop();
                host.Stop();
                Assert.Equal(HostState.Stopped, host.State);
            }
            // Start bei laufendem Betrieb startet neu
            host.Start(config, processor);
            host.Start(config, processor);
            Assert.True(host.State == HostState.Running, host.GetStats().LastError);
            Thread.Sleep(200);
            host.Dispose();
            Assert.Equal(HostState.Stopped, host.State);
            Assert.True(processor.Blocks > 0);
            return true;
        });
    }
}
