using System.Runtime.InteropServices;

namespace NxMix.Audio.Interop;

/// <summary>Liest Eigenschaften (Anzeigename, Geraeteformat) aus dem Property-Store eines Endpoints.</summary>
internal static unsafe class DeviceNames
{
    private static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly PropertyKey DeviceFormat = new(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0);

    private const ushort VtLpwstr = 31;
    private const ushort VtBlob = 65;

    public static string? ReadFriendlyName(IMMDevice device)
    {
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(0, out store) < 0 || store == null) return null;
            PropertyKey key = FriendlyName;
            if (store.GetValue(ref key, out PropVariant pv) < 0) return null;
            try
            {
                return pv.vt == VtLpwstr && pv.ptr8 != 0 ? Marshal.PtrToStringUni(pv.ptr8) : null;
            }
            finally
            {
                Native.PropVariantClear(&pv);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            WasapiEndpoint.ReleaseCom(store);
        }
    }

    /// <summary>Standardformat des Geraets (Kanalzahl, Samplerate) ohne den Audio-Client zu aktivieren.</summary>
    public static bool TryReadDeviceFormat(IMMDevice device, out int channels, out int sampleRate)
    {
        channels = 0;
        sampleRate = 0;
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(0, out store) < 0 || store == null) return false;
            PropertyKey key = DeviceFormat;
            if (store.GetValue(ref key, out PropVariant pv) < 0) return false;
            try
            {
                if (pv.vt != VtBlob || pv.ptr16 == 0 || pv.u32At8 < (uint)sizeof(WaveFormatEx)) return false;
                MixInfo mi = WasapiEndpoint.ParseFormat(pv.ptr16);
                channels = mi.Channels;
                sampleRate = mi.SampleRate;
                return channels > 0 && sampleRate > 0;
            }
            finally
            {
                Native.PropVariantClear(&pv);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            WasapiEndpoint.ReleaseCom(store);
        }
    }
}
