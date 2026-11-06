using System.Collections.Concurrent;
using NAudio.Wave;
using NxMix.Audio.Interop;

namespace NxMix.Audio;

public static class AsioCatalog
{
    static readonly ConcurrentDictionary<string, AudioDeviceInfo[]> Cache = new();
    public static bool IsAsio(string? id) => id?.StartsWith("asio:", StringComparison.Ordinal) == true;
    public static string DriverName(string id) => Uri.UnescapeDataString(id[5..id.LastIndexOf('/')]);
    static string Id(string name, string direction) => "asio:" + Uri.EscapeDataString(name) + "/" + direction;
    public static IReadOnlyList<AudioDeviceInfo> List()
    {
        var list = new List<AudioDeviceInfo>();
        foreach (var name in AsioDevice.GetDriverNames())
        {
            if (Cache.TryGetValue(name, out var known)) { list.AddRange(known); continue; }
            try
            {
                using var device = AsioDevice.Open(name);
                int[] rates = new[] { 44100, 48000, 88200, 96000, 176400, 192000 }.Where(device.IsSampleRateSupported).ToArray();
                var cap = device.Capabilities;
                var buffers = new[] { 32, 64, 128, 192, 240, 256, 480, 512, 960, 1024, 2048, 4096, cap.BufferPreferredSize }
                    .Where(n => n >= cap.BufferMinSize && n <= cap.BufferMaxSize && (n == cap.BufferPreferredSize ||
                        (cap.BufferGranularity == -1 ? (n & (n - 1)) == 0 : cap.BufferGranularity > 0 && (n - cap.BufferMinSize) % cap.BufferGranularity == 0)))
                    .Distinct().Order().ToArray();
                var found = new List<AudioDeviceInfo>();
                if (device.Capabilities.NbInputChannels > 0) found.Add(new AudioDeviceInfo(Id(name, "in"), name, DeviceFlow.Capture,
                    device.Capabilities.NbInputChannels, device.CurrentSampleRate, false) { Backend = AudioBackend.Asio, SupportedRates = rates, SupportedBuffers = buffers });
                if (device.Capabilities.NbOutputChannels > 0) found.Add(new AudioDeviceInfo(Id(name, "out"), name, DeviceFlow.Render,
                    device.Capabilities.NbOutputChannels, device.CurrentSampleRate, false) { Backend = AudioBackend.Asio, SupportedRates = rates, SupportedBuffers = buffers });
                Cache[name] = found.ToArray(); list.AddRange(found);
            }
            catch (Exception e) { list.Add(new AudioDeviceInfo(Id(name, "out"), name + " (Treiber nicht verfügbar)", DeviceFlow.Render, 0, 0, false) { Backend = AudioBackend.Asio, UnavailableReason = e.Message }); }
        }
        return list;
    }
    public static void ShowControlPanel(string id) => ComThread.Run(() =>
    {
        using var device = AsioDevice.Open(DriverName(id)); device.ShowControlPanel(); return true;
    }, 120000);
}
