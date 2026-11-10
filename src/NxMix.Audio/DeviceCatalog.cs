using NxMix.Audio.Interop;

namespace NxMix.Audio;

/// <summary>Listet die aktiven Windows-Audiogeraete (Render + Capture + ASIO) mit Id, Name, Kanalzahl, Samplerate, Standard-Flag
/// sowie ehrlicher Unterscheidung zwischen physischer Hardware und echten Windows-Virtual-Audio-Endpunkten.</summary>
public sealed class DeviceCatalog : IDeviceCatalog
{
    public IReadOnlyList<AudioDeviceInfo> List()
    {
        try
        {
            return ComThread.Run(() => EnumerateAll(includeAsio: true), 20000);
        }
        catch
        {
            return Array.Empty<AudioDeviceInfo>();
        }
    }

    public IReadOnlyList<AudioDeviceInfo> ListWindows() =>
        ComThread.Run(() => EnumerateAll(includeAsio: false), 5000);

    private static IReadOnlyList<AudioDeviceInfo> EnumerateAll(bool includeAsio)
    {
        var result = new List<AudioDeviceInfo>();
        IMMDeviceEnumerator? enumerator = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            foreach (DeviceFlow flow in new[] { DeviceFlow.Render, DeviceFlow.Capture })
            {
                try { EnumerateFlow(enumerator, flow, result); }
                catch { /* ein Fluss ausgefallen: den anderen trotzdem liefern */ }
            }
        }
        catch
        {
            // kein Audio-Subsystem
        }
        finally
        {
            WasapiEndpoint.ReleaseCom(enumerator);
        }
        if (includeAsio)
        {
            try { result.AddRange(AsioCatalog.List()); } catch { }
        }
        return result;
    }

    private static void EnumerateFlow(IMMDeviceEnumerator enumerator, DeviceFlow flow, List<AudioDeviceInfo> result)
    {
        int dataFlow = flow == DeviceFlow.Render ? 0 : 1;

        string? defaultId = null;
        if (enumerator.GetDefaultAudioEndpoint(dataFlow, 0, out IMMDevice? def) >= 0 && def != null)
        {
            try { if (def.GetId(out string id) >= 0) defaultId = id; }
            finally { WasapiEndpoint.ReleaseCom(def); }
        }

        if (enumerator.EnumAudioEndpoints(dataFlow, 1 /* DEVICE_STATE_ACTIVE */, out IMMDeviceCollection? collection) < 0 || collection == null)
            return;
        try
        {
            if (collection.GetCount(out uint count) < 0) return;
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    if (collection.Item(i, out device) < 0 || device == null) continue;
                    if (device.GetId(out string id) < 0) continue;
                    string name = DeviceNames.ReadFriendlyName(device) ?? id;
                    var (isVirtual, provider) = DeviceNames.ClassifyEndpoint(device, name);
                    bool nxMix = provider == AudioDeviceInfo.NxMixDriverProvider;
                    int channels = 0, rate = 0;
                    if (nxMix) TryReadMixFormat(device, out channels, out rate);
                    if (channels == 0 && !DeviceNames.TryReadDeviceFormat(device, out channels, out rate) && !nxMix)
                        TryReadMixFormat(device, out channels, out rate);
                    bool isDefault = defaultId != null && string.Equals(defaultId, id, StringComparison.OrdinalIgnoreCase);
                    result.Add(new AudioDeviceInfo(id, name, flow, channels, rate, isDefault)
                    {
                        Backend = AudioBackend.Windows,
                        IsVirtualEndpoint = isVirtual,
                        DriverProvider = provider,
                        IsVbCable = DeviceNames.IsVbCable(device, name, flow, provider),
                        NativeOutputPort = nxMix && flow == DeviceFlow.Capture ? DeviceNames.ReadNxMixOutputPort(device, name) : 0,
                    });
                }
                catch
                {
                    // dieses Geraet ueberspringen
                }
                finally
                {
                    WasapiEndpoint.ReleaseCom(device);
                }
            }
        }
        finally
        {
            WasapiEndpoint.ReleaseCom(collection);
        }
    }

    private static void TryReadMixFormat(IMMDevice device, out int channels, out int rate)
    {
        channels = 0;
        rate = 0;
        object? o = null;
        try
        {
            Guid iid = Iids.AudioClient;
            if (device.Activate(ref iid, 23, 0, out o) < 0 || o is not IAudioClient client) return;
            if (client.GetMixFormat(out nint p) < 0) return;
            try
            {
                MixInfo mi = WasapiEndpoint.ParseFormat(p);
                channels = mi.Channels;
                rate = mi.SampleRate;
            }
            finally
            {
                Native.CoTaskMemFree(p);
            }
        }
        catch
        {
            // Format unbekannt: 0/0
        }
        finally
        {
            WasapiEndpoint.ReleaseCom(o);
        }
    }
}

/// <summary>Fuehrt COM-Code auf einem eigenen MTA-Thread aus (unabhaengig vom Apartment des Aufrufers, z. B. UI-STA).</summary>
internal static class ComThread
{
    public static T Run<T>(Func<T> work, int timeoutMs)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        })
        {
            IsBackground = true,
            Name = "NxMix.Com",
        };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        if (!thread.Join(timeoutMs)) throw new TimeoutException("Windows-Audio antwortet nicht.");
        if (error != null) throw new InvalidOperationException(error.Message, error);
        return result;
    }
}
