using System.Collections.Concurrent;
using NxMix.Audio.Host;

namespace NxMix.Audio;

public static class AsioCatalog
{
    private static readonly ConcurrentDictionary<string, AudioDeviceInfo[]> Cache = new();

    public static bool IsAsio(string? id) => id?.StartsWith("asio:", StringComparison.Ordinal) == true;

    public static string DriverName(string id)
    {
        int slash = id.LastIndexOf('/');
        return slash > 5 ? Uri.UnescapeDataString(id[5..slash]) : Uri.UnescapeDataString(id[5..]);
    }

    public static string FormatDeviceId(string driverName, DeviceFlow flow) =>
        "asio:" + Uri.EscapeDataString(driverName) + "/" + (flow == DeviceFlow.Capture ? "in" : "out");

    public static void ClearCache() => Cache.Clear();

    public static IReadOnlyList<AudioDeviceInfo> List()
    {
        var list = new List<AudioDeviceInfo>();
        IAsioBackend backend = AsioBackendRegistry.Current;
        foreach (var name in backend.GetDriverNames())
        {
            if (Cache.TryGetValue(name, out var known)) { list.AddRange(known); continue; }
            try
            {
                using IAsioSession device = backend.Open(name);
                int[] rates = new[] { 44100, 48000, 88200, 96000, 176400, 192000 }.Where(device.IsSampleRateSupported).ToArray();
                var cap = device.Capabilities;
                var buffers = new[] { 32, 64, 128, 192, 240, 256, 480, 512, 960, 1024, 2048, 4096, cap.BufferPreferredSize }
                    .Where(n => n > 0 && n >= cap.BufferMinSize && n <= cap.BufferMaxSize && (n == cap.BufferPreferredSize ||
                        (cap.BufferGranularity == -1 ? (n & (n - 1)) == 0 : cap.BufferGranularity > 0 && (n - cap.BufferMinSize) % cap.BufferGranularity == 0)))
                    .Distinct().Order().ToArray();
                var found = new List<AudioDeviceInfo>();
                int curRate = device.CurrentSampleRate;
                if (cap.InputChannels > 0)
                {
                    found.Add(new AudioDeviceInfo(FormatDeviceId(name, DeviceFlow.Capture), name, DeviceFlow.Capture,
                        cap.InputChannels, curRate, false)
                    {
                        Backend = AudioBackend.Asio,
                        SupportedRates = rates,
                        SupportedBuffers = buffers,
                        DriverProvider = "ASIO",
                    });
                }
                if (cap.OutputChannels > 0)
                {
                    found.Add(new AudioDeviceInfo(FormatDeviceId(name, DeviceFlow.Render), name, DeviceFlow.Render,
                        cap.OutputChannels, curRate, false)
                    {
                        Backend = AudioBackend.Asio,
                        SupportedRates = rates,
                        SupportedBuffers = buffers,
                        DriverProvider = "ASIO",
                    });
                }
                Cache[name] = found.ToArray();
                list.AddRange(found);
            }
            catch (Exception e)
            {
                list.Add(new AudioDeviceInfo(FormatDeviceId(name, DeviceFlow.Render), name + " (Treiber nicht verfügbar)", DeviceFlow.Render, 0, 0, false)
                {
                    Backend = AudioBackend.Asio,
                    UnavailableReason = e.Message,
                    DriverProvider = "ASIO",
                });
            }
        }
        return list;
    }

    public static void ShowControlPanel(string idOrDriverName)
    {
        string driver = IsAsio(idOrDriverName) ? DriverName(idOrDriverName) : idOrDriverName;
        using IAsioSession device = AsioBackendRegistry.Current.Open(driver);
        device.ShowControlPanel();
    }
}
