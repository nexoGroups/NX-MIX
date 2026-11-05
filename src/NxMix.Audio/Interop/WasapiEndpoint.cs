using System.Runtime.InteropServices;
using System.Text;

namespace NxMix.Audio.Interop;

internal readonly record struct MixInfo(int Channels, int SampleRate, uint ChannelMask, SampleKind Kind, bool IsFloat);

/// <summary>Ein geoeffneter, initialisierter WASAPI-Stream (Render, Capture oder Loopback-Capture), noch nicht gestartet.
/// Wird auf dem Thread erzeugt und benutzt, der den Stream betreibt (MTA).</summary>
internal sealed unsafe class WasapiEndpoint : IDisposable
{
    private const uint ClsctxAll = 23;
    private IMMDeviceEnumerator? _enumerator;
    private IMMDevice? _device;
    private IAudioClient? _client;
    private bool _started;
    private bool _disposed;

    public bool IsCapture { get; }
    public bool Loopback { get; }
    public string DeviceId { get; private set; } = "";
    public string Name { get; private set; } = "";
    public bool Exclusive { get; private set; }
    public bool EventDriven { get; private set; }
    public bool LowLatency { get; private set; }
    public IAudioClient Client => _client!;
    public IAudioRenderClient? RenderClient { get; private set; }
    public IAudioCaptureClient? CaptureClient { get; private set; }
    public SampleKind Kind { get; private set; }
    public int DeviceChannels { get; private set; }
    public int SampleRate { get; private set; }
    public uint BufferFrames { get; private set; }
    public int PeriodFrames { get; private set; }
    public double StreamLatencyMs { get; private set; }
    public nint EventHandle { get; private set; }
    public int BytesPerSample => PcmConvert.BytesPerSample(Kind);
    public int BlockAlign => DeviceChannels * BytesPerSample;

    private WasapiEndpoint(bool capture, bool loopback)
    {
        IsCapture = capture || loopback;
        Loopback = loopback;
    }

    /// <summary>Oeffnet ein Geraet. deviceId leer oder "default" = Standardgeraet. loopback: Capture auf einem Render-Geraet.</summary>
    public static WasapiEndpoint Open(string? deviceId, bool capture, bool loopback, int engineRate, int blockFrames, bool exclusive)
    {
        var ep = new WasapiEndpoint(capture, loopback);
        try
        {
            ep.OpenCore(deviceId, engineRate, blockFrames, exclusive && !loopback);
            return ep;
        }
        catch
        {
            ep.Dispose();
            throw;
        }
    }

    private void OpenCore(string? deviceId, int engineRate, int blockFrames, bool exclusive)
    {
        _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
        int flow = IsCapture && !Loopback ? 1 : 0; // eRender=0, eCapture=1 (Loopback liegt auf einem Render-Endpoint)
        IMMDevice? device;
        if (string.IsNullOrWhiteSpace(deviceId) || deviceId.Equals("default", StringComparison.OrdinalIgnoreCase))
            Hr.Check(_enumerator.GetDefaultAudioEndpoint(flow, 0, out device), "Standardgeraet");
        else
            Hr.Check(_enumerator.GetDevice(deviceId, out device), $"Geraet '{deviceId}' nicht gefunden");
        _device = device;

        Hr.Check(_device.GetId(out string id), "GetId");
        DeviceId = id;
        Name = DeviceNames.ReadFriendlyName(_device) ?? id;
        if (_device.GetState(out uint state) >= 0 && state != 1)
            throw new AudioException(0, $"Geraet '{Name}' ist nicht aktiv (Status {state})");

        EventHandle = Native.CreateEvent(0, 0, 0, null);
        if (EventHandle == 0) throw new AudioException(Marshal.GetHRForLastWin32Error(), "Event konnte nicht erzeugt werden");

        Exclusive = exclusive;
        if (exclusive) OpenExclusive(engineRate, blockFrames);
        else OpenShared(engineRate, blockFrames);

        Hr.Check(Client.GetBufferSize(out uint frames), "GetBufferSize");
        BufferFrames = frames;
        if (Exclusive) PeriodFrames = (int)frames;
        if (Client.GetStreamLatency(out long latency) >= 0) StreamLatencyMs = latency / 10000.0;
        if (EventDriven) Hr.Check(Client.SetEventHandle(EventHandle), "SetEventHandle");

        Guid iid = IsCapture ? Iids.AudioCaptureClient : Iids.AudioRenderClient;
        Hr.Check(Client.GetService(ref iid, out object service), "GetService");
        if (IsCapture) CaptureClient = (IAudioCaptureClient)service;
        else RenderClient = (IAudioRenderClient)service;
    }

    private IAudioClient Activate()
    {
        Guid iid = Iids.AudioClient;
        Hr.Check(_device!.Activate(ref iid, ClsctxAll, 0, out object o), "IAudioClient aktivieren");
        return (IAudioClient)o;
    }

    private MixInfo QueryMix(out long defaultPeriod, out long minimumPeriod)
    {
        IAudioClient c = Activate();
        try
        {
            Hr.Check(c.GetMixFormat(out nint p), "GetMixFormat");
            MixInfo mi;
            try { mi = ParseFormat(p); }
            finally { Native.CoTaskMemFree(p); }
            Hr.Check(c.GetDevicePeriod(out defaultPeriod, out minimumPeriod), "GetDevicePeriod");
            return mi;
        }
        finally
        {
            ReleaseCom(c);
        }
    }

    internal static MixInfo ParseFormat(nint p)
    {
        var f = (WaveFormatEx*)p;
        int channels = f->nChannels;
        int rate = (int)f->nSamplesPerSec;
        int bits = f->wBitsPerSample;
        uint mask = 0;
        bool isFloat = f->wFormatTag == 3;
        if (f->wFormatTag == 0xFFFE && f->cbSize >= 22)
        {
            var e = (WaveFormatExtensible*)p;
            mask = e->dwChannelMask;
            isFloat = e->SubFormat == Iids.SubtypeFloat;
        }
        SampleKind kind = isFloat ? SampleKind.Float32 : bits switch
        {
            16 => SampleKind.Int16,
            24 => SampleKind.Int24,
            _ => SampleKind.Int32,
        };
        return new MixInfo(channels, rate, mask, kind, isFloat);
    }

    internal static WaveFormatExtensible BuildFormat(SampleKind kind, int channels, int rate, uint mask, int validBits)
    {
        int container = PcmConvert.BytesPerSample(kind) * 8;
        var fmt = new WaveFormatExtensible
        {
            wValidBitsPerSample = (ushort)validBits,
            dwChannelMask = mask,
            SubFormat = kind == SampleKind.Float32 ? Iids.SubtypeFloat : Iids.SubtypePcm,
        };
        fmt.Format.wFormatTag = 0xFFFE;
        fmt.Format.nChannels = (ushort)channels;
        fmt.Format.nSamplesPerSec = (uint)rate;
        fmt.Format.wBitsPerSample = (ushort)container;
        fmt.Format.nBlockAlign = (ushort)(channels * container / 8);
        fmt.Format.nAvgBytesPerSec = (uint)(rate * fmt.Format.nBlockAlign);
        fmt.Format.cbSize = 22;
        return fmt;
    }

    // ---- Shared Mode ------------------------------------------------------------------------------------------

    private void OpenShared(int engineRate, int blockFrames)
    {
        MixInfo mix = QueryMix(out long defaultPeriod, out _);
        var errors = new StringBuilder();

        // 1. Kleine Perioden per IAudioClient3, wenn keine Raten-Umwandlung noetig ist.
        if (!Loopback && mix.IsFloat && mix.SampleRate == engineRate && TryInitLowLatency(mix, blockFrames, errors))
            return;

        // 2. Klassisch: erst mit AUTOCONVERTPCM auf die Engine-Rate, dann im Mix-Format des Geraets (eigene Umwandlung).
        //    Loopback: zuerst eventgesteuert, sonst Polling (aeltere Windows-Versionen).
        bool[] eventModes = Loopback ? new[] { true, false } : new[] { true };
        foreach (bool eventMode in eventModes)
        {
            foreach (bool convert in new[] { true, false })
            {
                if (TryInitShared(mix, engineRate, eventMode, convert, defaultPeriod, errors)) return;
            }
        }
        throw new AudioException(0, $"Geraet '{Name}' konnte nicht im Shared Mode geoeffnet werden. {errors}");
    }

    private bool TryInitLowLatency(in MixInfo mix, int blockFrames, StringBuilder errors)
    {
        IAudioClient c = Activate();
        nint pMix = 0;
        bool ok = false;
        try
        {
            if (c is not IAudioClient3 c3)
            {
                errors.Append("[IAudioClient3 nicht verfuegbar] ");
                return false;
            }
            int hr = c.GetMixFormat(out pMix);
            if (hr < 0) { errors.Append($"[GetMixFormat {Hr.Describe(hr)}] "); return false; }
            hr = c3.GetSharedModeEnginePeriod(pMix, out _, out uint fundamental, out uint minPeriod, out uint maxPeriod);
            if (hr < 0 || fundamental == 0) { errors.Append($"[GetSharedModeEnginePeriod {Hr.Describe(hr)}] "); return false; }
            uint want = (uint)Math.Max(blockFrames, 1);
            uint period = (want + fundamental - 1) / fundamental * fundamental;
            period = Math.Clamp(period, minPeriod, maxPeriod);
            hr = c3.InitializeSharedAudioStream(StreamFlags.EventCallback, period, pMix, 0);
            if (hr < 0) { errors.Append($"[InitializeSharedAudioStream {Hr.Describe(hr)}] "); return false; }

            _client = c;
            Kind = mix.Kind;
            DeviceChannels = mix.Channels;
            SampleRate = mix.SampleRate;
            EventDriven = true;
            LowLatency = true;
            PeriodFrames = (int)period;
            ok = true;
            return true;
        }
        catch (Exception ex)
        {
            errors.Append($"[IAudioClient3: {ex.Message}] ");
            return false;
        }
        finally
        {
            if (pMix != 0) Native.CoTaskMemFree(pMix);
            if (!ok) ReleaseCom(c);
        }
    }

    private bool TryInitShared(in MixInfo mix, int engineRate, bool eventMode, bool convert, long defaultPeriod, StringBuilder errors)
    {
        IAudioClient c = Activate();
        nint mixPtr = 0;
        bool ok = false;
        try
        {
            uint flags = (eventMode ? StreamFlags.EventCallback : 0u) | (Loopback ? StreamFlags.Loopback : 0u);
            WaveFormatExtensible fmt = default;
            nint pFmt;
            SampleKind kind;
            int rate;
            if (convert)
            {
                fmt = BuildFormat(SampleKind.Float32, mix.Channels, engineRate, mix.ChannelMask, 32);
                pFmt = (nint)(&fmt);
                flags |= StreamFlags.AutoConvertPcm | StreamFlags.SrcDefaultQuality;
                kind = SampleKind.Float32;
                rate = engineRate;
            }
            else
            {
                Hr.Check(c.GetMixFormat(out mixPtr), "GetMixFormat");
                pFmt = mixPtr;
                kind = mix.Kind;
                rate = mix.SampleRate;
            }

            long duration = eventMode ? Math.Max(2 * defaultPeriod, 200_000) : 1_000_000; // Event: 2 Perioden, Polling: 100 ms
            int hr = c.Initialize(0, flags, duration, 0, pFmt, 0);
            if (hr < 0)
            {
                errors.Append($"[{(convert ? "Autokonvert" : "Mixformat")}{(eventMode ? "" : "+Polling")}: {Hr.Describe(hr)}] ");
                return false;
            }

            _client = c;
            Kind = kind;
            DeviceChannels = mix.Channels;
            SampleRate = rate;
            EventDriven = eventMode;
            PeriodFrames = (int)Math.Max(1, defaultPeriod * rate / 10_000_000L);
            ok = true;
            return true;
        }
        catch (Exception ex)
        {
            errors.Append($"[{ex.Message}] ");
            return false;
        }
        finally
        {
            if (mixPtr != 0) Native.CoTaskMemFree(mixPtr);
            if (!ok) ReleaseCom(c);
        }
    }

    // ---- Exklusiv-Modus ---------------------------------------------------------------------------------------

    private void OpenExclusive(int engineRate, int blockFrames)
    {
        MixInfo mix = QueryMix(out _, out long minPeriod);
        var candidates = new (SampleKind Kind, int Valid)[]
        {
            (SampleKind.Float32, 32), (SampleKind.Int32, 24), (SampleKind.Int32, 32), (SampleKind.Int24, 24), (SampleKind.Int16, 16),
        };
        long periodHns = Math.Max(minPeriod, (long)(blockFrames * 10_000_000.0 / engineRate));
        var errors = new StringBuilder();

        foreach (var cand in candidates)
        {
            WaveFormatExtensible fmt = BuildFormat(cand.Kind, mix.Channels, engineRate, mix.ChannelMask, cand.Valid);
            IAudioClient c = Activate();
            bool ok = false;
            try
            {
                int hr = c.IsFormatSupported(1, (nint)(&fmt), 0);
                if (hr != Hr.SOk)
                {
                    errors.Append($"[{cand.Kind}/{cand.Valid}: {(hr == Hr.SFalse ? "nicht unterstuetzt" : Hr.Describe(hr))}] ");
                    continue;
                }
                hr = c.Initialize(1, StreamFlags.EventCallback, periodHns, periodHns, (nint)(&fmt), 0);
                if (hr == Hr.BufferSizeNotAligned)
                {
                    c.GetBufferSize(out uint alignedFrames);
                    ReleaseCom(c);
                    c = Activate();
                    periodHns = (long)(10_000_000.0 / engineRate * alignedFrames + 0.5);
                    hr = c.Initialize(1, StreamFlags.EventCallback, periodHns, periodHns, (nint)(&fmt), 0);
                }
                if (hr < 0)
                {
                    errors.Append($"[{cand.Kind}/{cand.Valid}: {Hr.Describe(hr)}] ");
                    continue;
                }

                _client = c;
                Kind = cand.Kind;
                DeviceChannels = mix.Channels;
                SampleRate = engineRate;
                EventDriven = true;
                ok = true;
                return;
            }
            finally
            {
                if (!ok) ReleaseCom(c);
            }
        }
        throw new AudioException(0, $"Geraet '{Name}' unterstuetzt im Exklusivmodus keines der Formate bei {engineRate} Hz. {errors}");
    }

    // ---- Betrieb ----------------------------------------------------------------------------------------------

    public void Start()
    {
        Hr.Check(Client.Start(), "Audio-Client starten");
        _started = true;
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        Client.Stop();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_started)
        {
            try { _client?.Stop(); } catch { /* Geraet evtl. weg */ }
            _started = false;
        }
        ReleaseCom(CaptureClient);
        ReleaseCom(RenderClient);
        ReleaseCom(_client);
        ReleaseCom(_device);
        ReleaseCom(_enumerator);
        CaptureClient = null;
        RenderClient = null;
        _client = null;
        _device = null;
        _enumerator = null;
        if (EventHandle != 0)
        {
            Native.CloseHandle(EventHandle);
            EventHandle = 0;
        }
    }

    internal static void ReleaseCom(object? o)
    {
        if (o == null) return;
        try
        {
            if (Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
        }
        catch
        {
            // ignorieren
        }
    }
}
