namespace NxMix.Audio.Interop;

/// <summary>Fehler der Audio-Schicht (mit HRESULT, wenn bekannt).</summary>
internal sealed class AudioException : Exception
{
    public AudioException(int hresult, string message) : base(message)
    {
        HResult = hresult;
    }
}

internal static class Hr
{
    public const int SOk = 0;
    public const int SFalse = 1;
    public const int AudclntSBufferEmpty = 0x08890001;

    public const int NotInitialized = unchecked((int)0x88890001);
    public const int DeviceInvalidated = unchecked((int)0x88890004);
    public const int UnsupportedFormat = unchecked((int)0x88890008);
    public const int DeviceInUse = unchecked((int)0x8889000A);
    public const int ExclusiveModeNotAllowed = unchecked((int)0x8889000E);
    public const int BufferSizeNotAligned = unchecked((int)0x88890019);
    public const int InvalidDevicePeriod = unchecked((int)0x88890020);
    public const int ResourcesInvalidated = unchecked((int)0x88890026);

    public static void Check(int hr, string what)
    {
        if (hr < 0) throw new AudioException(hr, what + ": " + Describe(hr));
    }

    public static string Describe(int hr)
    {
        string text = hr switch
        {
            DeviceInvalidated => "Geraet entfernt oder Format geaendert (AUDCLNT_E_DEVICE_INVALIDATED)",
            ResourcesInvalidated => "Geraeteressourcen ungueltig (AUDCLNT_E_RESOURCES_INVALIDATED)",
            UnsupportedFormat => "Format nicht unterstuetzt (AUDCLNT_E_UNSUPPORTED_FORMAT)",
            DeviceInUse => "Geraet wird exklusiv von einer anderen Anwendung benutzt (AUDCLNT_E_DEVICE_IN_USE)",
            ExclusiveModeNotAllowed => "Exklusivmodus ist fuer dieses Geraet in Windows nicht erlaubt",
            BufferSizeNotAligned => "Puffergroesse nicht ausgerichtet",
            InvalidDevicePeriod => "Ungueltige Geraeteperiode",
            NotInitialized => "Audio-Client nicht initialisiert",
            _ => "Fehler",
        };
        return $"{text} [0x{hr:X8}]";
    }
}
