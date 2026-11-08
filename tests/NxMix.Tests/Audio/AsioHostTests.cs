using NxMix.Audio;
using NxMix.Engine;
using Xunit.Abstractions;

namespace NxMix.Tests.Audio;

[Collection("AudioDevices")]
public sealed class AsioHostTests
{
    private readonly ITestOutputHelper _output;

    public AsioHostTests(ITestOutputHelper output) => _output = output;

    [Fact]
    [Trait("Category", "Hardware")]
    public void Native_asio_duplex_processes_live_callbacks_at_the_device_rate()
    {
        string? driver = Environment.GetEnvironmentVariable("NXMIX_TEST_ASIO");
        if (string.IsNullOrWhiteSpace(driver)) return;
        var devices = new DeviceCatalog().List();
        string outputId = AsioCatalog.FormatDeviceId(driver, DeviceFlow.Render);
        var output = Assert.Single(devices, d => d.Id == outputId);
        Assert.True(output.Channels > 0, output.UnavailableReason);
        string inputId = AsioCatalog.FormatDeviceId(driver, DeviceFlow.Capture);
        var input = Assert.Single(devices, d => d.Id == inputId);
        int rate = output.SampleRate;
        Assert.True(rate > 0);

        var processor = new SilentCountingProcessor(input.Channels, output.Channels, rate);
        using var host = new AudioHost();
        host.Start(new AudioHostConfig(rate, new[]
        {
            new DeviceBinding(inputId, DeviceFlow.Capture, 0, 0, input.Channels),
            new DeviceBinding(outputId, DeviceFlow.Render, 0, 0, output.Channels),
        }), processor);

        Assert.True(host.State == HostState.Running, host.GetStats().LastError);
        Assert.True(SpinWait.SpinUntil(() => processor.Frames >= rate / 2, TimeSpan.FromSeconds(5)),
            host.GetStats().LastError ?? "Der ASIO-Treiber hat keine ausreichenden Audio-Callbacks geliefert.");
        var stats = host.GetStats();
        Assert.True(host.State == HostState.Running, stats.LastError);
        // Ein Duplex-Stream liefert einen Status für beide Bindungen, unter der zuerst eingetragenen ID.
        var master = Assert.Single(stats.Devices, d => d.IsMasterClock);
        Assert.False(master.IsOffline, master.OfflineReason);
        Assert.Equal(driver, AsioCatalog.DriverName(master.DeviceId));
        Assert.Equal(0, master.Glitches);
        _output.WriteLine($"{driver}: {input.Channels} Eingänge, {output.Channels} Ausgänge, {rate} Hz, " +
            $"{stats.BlockFrames} Frames pro Puffer, {processor.Frames} verarbeitete Frames, {master.Glitches} Glitches.");
        host.Stop();
        Assert.Equal(HostState.Stopped, host.State);
    }

    // Opt-in integration check: opens only the named installed driver and renders silence.
    [Theory]
    [InlineData(false, 48000)]
    [InlineData(true, 44100)]
    [Trait("Category", "Hardware")]
    public void Native_asio_output_opens_processes_silence_and_stops(bool mixed, int rate)
    {
        string? driver = Environment.GetEnvironmentVariable("NXMIX_TEST_ASIO");
        if (string.IsNullOrWhiteSpace(driver)) return;
        var devices = new DeviceCatalog().List();
        var device = devices.First(d => d.Backend == AudioBackend.Asio && d.Flow == DeviceFlow.Render && AsioCatalog.DriverName(d.Id) == driver);
        Assert.True(device.Channels > 0, device.UnavailableReason);
        int count = Math.Min(device.Channels, 2);
        var engine = new MixerEngine(MixerSettings.Create(48000, 0, count));
        using var host = new AudioHost();
        var bindings = new List<DeviceBinding>();
        if (mixed) bindings.Add(new DeviceBinding(devices.First(d => d.Backend == AudioBackend.Windows && d.Flow == DeviceFlow.Render && d.IsDefault).Id, DeviceFlow.Render, 0, 0, count));
        bindings.Add(new DeviceBinding(device.Id, DeviceFlow.Render, 0, 0, count) { SampleRate = rate });
        host.Start(new AudioHostConfig(48000, bindings), engine);
        Assert.True(host.State == HostState.Running, host.GetStats().LastError);
        Thread.Sleep(800);
        Assert.True(host.State == HostState.Running, host.GetStats().LastError);
        Assert.Contains(host.GetStats().Devices, d => d.DeviceId == device.Id);
        Assert.Equal(mixed ? 2 : 1, host.GetStats().Devices.Count);
        host.Stop();
        Assert.Equal(HostState.Stopped, host.State);
    }
}
