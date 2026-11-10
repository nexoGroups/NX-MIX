using System.Runtime.InteropServices;

namespace NxMix.Audio.Interop;

/// <summary>Liest Eigenschaften (Anzeigename, Geraeteformat, Enumerator/Treiberkennung zur VAIO-Erkennung) aus dem Property-Store eines Endpoints.</summary>
internal static unsafe class DeviceNames
{
    private static readonly PropertyKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly PropertyKey DeviceDesc = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);
    private static readonly PropertyKey EnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
    private static readonly PropertyKey InterfaceFriendlyName = new(new Guid("026e516e-b814-414b-83cd-856d6fef4822"), 2);
    private static readonly PropertyKey DeviceFormat = new(new Guid("f19f064d-082c-4e27-bc73-6882a1bb8e4c"), 0);

    private const ushort VtLpwstr = 31;
    private const ushort VtBlob = 65;

    public static string? ReadFriendlyName(IMMDevice device) => ReadStringProperty(device, FriendlyName);

    public static (bool IsVirtual, string Provider) ClassifyEndpoint(IMMDevice device, string friendlyName)
    {
        string? enumerator = ReadStringProperty(device, EnumeratorName);
        string? ifaceName = ReadStringProperty(device, InterfaceFriendlyName);
        string? desc = ReadStringProperty(device, DeviceDesc);
        string combined = $"{friendlyName} | {ifaceName} | {desc} | {enumerator}";

        if (ContainsAny(combined, "NxMixVAD", "NX-MIX Virtual", "NX-MIX VAIO", "NX-MIX Out "))
            return (true, AudioDeviceInfo.NxMixDriverProvider);
        if (ContainsAny(combined, "VB-Audio", "VBMatrix", "CABLE Input", "CABLE Output", "CABLE In ", "VoiceMeeter", "Hi-Fi Cable"))
            return (true, "VB-Audio");
        if (ContainsAny(combined, "Virtual Audio Cable", "EuMus", "Muzychenko"))
            return (true, "Virtual Audio Cable");
        if (ContainsAny(combined, "Steam Streaming", "NVIDIA Broadcast", "Oculus Virtual", "Sonar ", "SteelSeries Sonar", "Elgato Wave Link"))
            return (true, ifaceName ?? "Virtueller Audiotreiber");

        if (string.Equals(enumerator, "ROOT", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(enumerator, "SWD", StringComparison.OrdinalIgnoreCase))
        {
            if (ContainsAny(combined, "Virtual", "Cable", "VAIO", "Loopback", "Matrix"))
                return (true, ifaceName ?? enumerator ?? "Virtuell");
        }

        string provider = !string.IsNullOrWhiteSpace(ifaceName) ? ifaceName!
            : !string.IsNullOrWhiteSpace(enumerator) ? enumerator!
            : "WASAPI";
        return (false, provider);
    }

    public static int ReadNxMixOutputPort(IMMDevice device, string friendlyName)
    {
        const string prefix = "NX-MIX Out ";
        foreach (string? name in new[] { ReadStringProperty(device, DeviceDesc), ReadStringProperty(device, InterfaceFriendlyName), friendlyName })
        {
            int start = name?.IndexOf(prefix, StringComparison.OrdinalIgnoreCase) ?? -1;
            if (start < 0) continue;
            int digit = start + prefix.Length;
            if (digit < name!.Length && name[digit] is >= '1' and <= '3' &&
                (digit + 1 == name.Length || !char.IsDigit(name[digit + 1])))
                return name[digit] - '0';
        }
        return 0;
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        for (int i = 0; i < needles.Length; i++)
            if (haystack.Contains(needles[i], StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string? ReadStringProperty(IMMDevice device, PropertyKey key)
    {
        IPropertyStore? store = null;
        try
        {
            if (device.OpenPropertyStore(0, out store) < 0 || store == null) return null;
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
