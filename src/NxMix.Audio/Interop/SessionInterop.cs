using System.Runtime.InteropServices;

namespace NxMix.Audio.Interop;

// Audio-Sitzungen (Programme, die auf einem Render-Geraet Ton ausgeben). Nur lesend benutzt.

internal static class SessionIids
{
    public static readonly Guid AudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
}

[ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    // IAudioSessionManager
    [PreserveSig] int GetAudioSessionControl(nint audioSessionGuid, uint streamFlags, out nint sessionControl);
    [PreserveSig] int GetSimpleAudioVolume(nint audioSessionGuid, uint streamFlags, out nint audioVolume);
    // IAudioSessionManager2
    [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnum);
    [PreserveSig] int RegisterSessionNotification(nint sessionNotification);
    [PreserveSig] int UnregisterSessionNotification(nint sessionNotification);
    [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, nint duckNotification);
    [PreserveSig] int UnregisterDuckNotification(nint duckNotification);
}

[ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    [PreserveSig] int GetCount(out int sessionCount);
    [PreserveSig] int GetSession(int sessionCount, out IAudioSessionControl2 session);
}

[ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    // IAudioSessionControl
    [PreserveSig] int GetState(out int state);
    [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string displayName);
    [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
    [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string iconPath);
    [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
    [PreserveSig] int GetGroupingParam(out Guid groupingParam);
    [PreserveSig] int SetGroupingParam(ref Guid groupingParam, ref Guid eventContext);
    [PreserveSig] int RegisterAudioSessionNotification(nint client);
    [PreserveSig] int UnregisterAudioSessionNotification(nint client);
    // IAudioSessionControl2
    [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
    [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string retVal);
    [PreserveSig] int GetProcessId(out uint retVal);
    [PreserveSig] int IsSystemSoundsSession();
    [PreserveSig] int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
}

/// <summary>Eine Audio-Sitzung eines Prozesses auf einem Render-Geraet.</summary>
internal readonly record struct SessionRef(int ProcessId, int State, string DeviceId, string DeviceName);

/// <summary>Zaehlt die Audio-Sitzungen aller aktiven Render-Geraete (oder eines bestimmten). Wirft nie; Geraete, die sich nicht abfragen lassen, werden uebersprungen.
/// Systemtoene (IsSystemSoundsSession) und PID 0 sind ausgeschlossen. Muss auf einem MTA-Thread laufen (ComThread.Run).</summary>
internal static class SessionEnumeration
{
    public static List<SessionRef> Enumerate(string? onlyDeviceId = null)
    {
        var result = new List<SessionRef>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (enumerator.EnumAudioEndpoints(0, 1, out collection) < 0 || collection == null) return result;
            if (collection.GetCount(out uint count) < 0) return result;
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    if (collection.Item(i, out device) < 0 || device == null) continue;
                    if (device.GetId(out string id) < 0) continue;
                    if (onlyDeviceId != null && !string.Equals(onlyDeviceId, id, StringComparison.OrdinalIgnoreCase)) continue;
                    EnumerateDevice(device, id, result);
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
        catch
        {
            // kein Audio-Subsystem: leere Liste
        }
        finally
        {
            WasapiEndpoint.ReleaseCom(collection);
            WasapiEndpoint.ReleaseCom(enumerator);
        }
        return result;
    }

    private static void EnumerateDevice(IMMDevice device, string id, List<SessionRef> result)
    {
        object? mgrObj = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            Guid iid = SessionIids.AudioSessionManager2;
            if (device.Activate(ref iid, 23, 0, out mgrObj) < 0 || mgrObj is not IAudioSessionManager2 mgr) return;
            if (mgr.GetSessionEnumerator(out sessions) < 0 || sessions == null) return;
            if (sessions.GetCount(out int n) < 0) return;
            string? name = null;
            for (int i = 0; i < n; i++)
            {
                IAudioSessionControl2? s = null;
                try
                {
                    if (sessions.GetSession(i, out s) < 0 || s == null) continue;
                    if (s.IsSystemSoundsSession() == 0) continue; // S_OK = Systemtoene
                    if (s.GetProcessId(out uint pid) < 0 || pid == 0) continue;
                    if (s.GetState(out int state) < 0) state = 0;
                    result.Add(new SessionRef((int)pid, state, id, name ??= DeviceNames.ReadFriendlyName(device) ?? id));
                }
                catch
                {
                    // Sitzung ueberspringen
                }
                finally
                {
                    WasapiEndpoint.ReleaseCom(s);
                }
            }
        }
        finally
        {
            WasapiEndpoint.ReleaseCom(sessions);
            WasapiEndpoint.ReleaseCom(mgrObj);
        }
    }
}
