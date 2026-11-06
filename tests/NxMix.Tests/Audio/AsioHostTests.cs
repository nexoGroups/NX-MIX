using NxMix.Audio;
using NxMix.Engine;

namespace NxMix.Tests.Audio;

[Collection("AudioDevices")]
public sealed class AsioHostTests
{
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
