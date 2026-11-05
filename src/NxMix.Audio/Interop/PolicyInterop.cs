using System.Runtime.InteropServices;

namespace NxMix.Audio.Interop;

/// <summary>combase.dll: Windows-Runtime-Zeichenketten (HSTRING) und Aktivierungsfabriken.</summary>
internal static unsafe partial class Combase
{
    [LibraryImport("combase.dll")]
    public static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int WindowsCreateString(string source, uint length, out nint hstring);

    [LibraryImport("combase.dll")]
    public static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    public static partial nint WindowsGetStringRawBuffer(nint hstring, out uint length);

    public static nint CreateHString(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0; // null-HSTRING = leere Zeichenkette
        int hr = WindowsCreateString(s, (uint)s.Length, out nint h);
        if (hr < 0) throw new AudioException(hr, "WindowsCreateString");
        return h;
    }

    public static string? ReadHString(nint h)
    {
        if (h == 0) return null;
        nint p = WindowsGetStringRawBuffer(h, out uint len);
        return p == 0 || len == 0 ? null : new string((char*)p, 0, (int)len);
    }
}

/// <summary>Die undokumentierte WinRT-Fabrik "Windows.Media.Internal.AudioPolicyConfig" (IAudioPolicyConfig): Pro-App-Standardgeraet
/// (die Einstellung "App-Lautstaerke und Geraeteeinstellungen" von Windows). Es gibt je Windows-Version verschiedene IIDs/vtable-Layouts;
/// sie werden zur Laufzeit der Reihe nach durchprobiert (die Methoden werden ueber rohe vtable-Zeiger gerufen, kein ComImport).
/// Nie ClearAllPersistedApplicationDefaultEndpoints aufrufen: das loescht die Einstellungen des Nutzers fuer ALLE Apps.</summary>
internal sealed unsafe class AudioPolicyConfig : IDisposable
{
    internal readonly record struct Variant(string Name, Guid Iid, int SetSlot, int GetSlot);

    /// <summary>Bekannte Varianten (vtable-Slots: 0..2 IUnknown, 3..5 IInspectable, dann die Methoden der Schnittstelle).</summary>
    internal static readonly Variant[] Variants =
    {
        // Windows 10 21H2 (Build 21390) und Windows 11
        new("21H2/Win11 {ab3d4648}", new Guid("ab3d4648-e242-459f-b02f-541c70306324"), 25, 26),
        // Windows 10 bis 21H1
        new("Pre-21H2 {2a59116d}", new Guid("2a59116d-6c4f-45e0-a74f-707e3fef9258"), 25, 26),
    };

    private const string ClassName = "Windows.Media.Internal.AudioPolicyConfig";
    private static readonly Guid IidInspectable = new("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");

    private nint _factory;
    private readonly nint* _vtbl;

    public Variant Used { get; }

    public const int ERender = 0;
    public const int ERoleConsole = 0, ERoleMultimedia = 1, ERoleCommunications = 2;

    private AudioPolicyConfig(nint factory, Variant used)
    {
        _factory = factory;
        _vtbl = *(nint**)factory;
        Used = used;
    }

    /// <summary>IIDs, die die Fabrik auf diesem System anbietet (IInspectable.GetIids) - Diagnose.</summary>
    public static string DescribeOfferedIids()
    {
        nint cls = 0, fac = 0;
        try
        {
            cls = Combase.CreateHString(ClassName);
            int hr = Combase.RoGetActivationFactory(cls, IidInspectable, out fac);
            if (hr < 0) return $"Fabrik nicht verfuegbar (0x{hr:X8})";
            nint* vt = *(nint**)fac;
            var getIids = (delegate* unmanaged[Stdcall]<nint, uint*, Guid**, int>)vt[3];
            uint n = 0;
            Guid* iids = null;
            hr = getIids(fac, &n, &iids);
            if (hr < 0) return $"GetIids fehlgeschlagen (0x{hr:X8})";
            var parts = new List<string>();
            for (uint i = 0; i < n; i++) parts.Add(iids[i].ToString());
            Native.CoTaskMemFree((nint)iids);
            return string.Join(", ", parts);
        }
        catch (Exception ex)
        {
            return "Fehler: " + ex.Message;
        }
        finally
        {
            if (fac != 0) Marshal.Release(fac);
            if (cls != 0) Combase.WindowsDeleteString(cls);
        }
    }

    /// <summary>Erzeugt die Fabrik in der ersten funktionierenden Variante. null + error, wenn keine passt.</summary>
    public static AudioPolicyConfig? TryCreate(out string? error)
    {
        error = null;
        nint cls = 0;
        try
        {
            cls = Combase.CreateHString(ClassName);
            var errors = new List<string>();
            foreach (Variant v in Variants)
            {
                int hr = Combase.RoGetActivationFactory(cls, v.Iid, out nint factory);
                if (hr >= 0 && factory != 0)
                    return new AudioPolicyConfig(factory, v);
                errors.Add($"{v.Name}: 0x{hr:X8}");
            }
            error = "Keine bekannte IAudioPolicyConfig-Variante verfuegbar (" + string.Join("; ", errors) + "). Angebotene IIDs: " + DescribeOfferedIids();
            return null;
        }
        catch (Exception ex)
        {
            error = "IAudioPolicyConfig nicht verfuegbar: " + ex.Message;
            return null;
        }
        finally
        {
            if (cls != 0) Combase.WindowsDeleteString(cls);
        }
    }

    /// <summary>Setzt das Pro-App-Standardgeraet des Prozesses (Rollen Console/Multimedia/Communications). policyDeviceId = Policy-Id
    /// (siehe PolicyDeviceId) oder null/leer = zuruecksetzen. Gibt den ersten Fehler-HRESULT zurueck (0 = alles OK).</summary>
    public int Set(int processId, string? policyDeviceId)
    {
        int first = 0;
        for (int role = 0; role < 3; role++)
        {
            int hr = SetRole(processId, role, policyDeviceId);
            if (hr < 0 && first == 0) first = hr;
        }
        return first;
    }

    /// <summary>Wie Set, aber nur fuer eine Rolle (0 Console, 1 Multimedia, 2 Communications).</summary>
    public int SetRole(int processId, int role, string? policyDeviceId)
    {
        nint h = 0;
        try
        {
            h = Combase.CreateHString(policyDeviceId);
            var set = (delegate* unmanaged[Stdcall]<nint, uint, int, int, nint, int>)_vtbl[Used.SetSlot];
            return set(_factory, (uint)processId, ERender, role, h);
        }
        finally
        {
            if (h != 0) Combase.WindowsDeleteString(h);
        }
    }

    /// <summary>Liest das Pro-App-Standardgeraet. hr &lt; 0: Fehler. policyDeviceId == null: kein Pro-App-Wert gesetzt.</summary>
    public int Get(int processId, out string? policyDeviceId, int role = ERoleConsole)
    {
        policyDeviceId = null;
        nint h = 0;
        var get = (delegate* unmanaged[Stdcall]<nint, uint, int, int, nint*, int>)_vtbl[Used.GetSlot];
        int hr = get(_factory, (uint)processId, ERender, role, &h);
        if (hr >= 0 && h != 0)
        {
            policyDeviceId = Combase.ReadHString(h);
            Combase.WindowsDeleteString(h);
        }
        return hr;
    }

    public void Dispose()
    {
        nint f = Interlocked.Exchange(ref _factory, 0);
        if (f != 0) Marshal.Release(f);
    }

    // ---- Geraete-Id-Format ------------------------------------------------------------------------------------

    private const string Prefix = @"\\?\SWD#MMDEVAPI#";
    private const string SuffixRender = "#{e6327cad-dcec-4949-ae8a-991e976a79d2}";
    private const string SuffixCapture = "#{2eef81be-33fa-4800-9670-1cd474972c3f}";

    /// <summary>MMDevice-Id ("{0.0.0.00000000}.{guid}") -> Policy-Id fuer ein Render-Geraet.</summary>
    public static string PolicyDeviceId(string mmDeviceId) => Prefix + mmDeviceId + SuffixRender;

    /// <summary>Umkehrung von PolicyDeviceId (auch fuer Capture-Ids). null bei leerer Eingabe.</summary>
    public static string? UnpackDeviceId(string? policyId)
    {
        if (string.IsNullOrEmpty(policyId)) return null;
        string s = policyId;
        if (s.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) s = s[Prefix.Length..];
        if (s.EndsWith(SuffixRender, StringComparison.OrdinalIgnoreCase)) s = s[..^SuffixRender.Length];
        else if (s.EndsWith(SuffixCapture, StringComparison.OrdinalIgnoreCase)) s = s[..^SuffixCapture.Length];
        return s;
    }
}
