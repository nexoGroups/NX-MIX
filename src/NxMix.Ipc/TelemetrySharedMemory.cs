using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Principal;
using NxMix.Audio;

namespace NxMix.Ipc;

[StructLayout(LayoutKind.Sequential, Pack = 4)]
public unsafe struct TelemetrySharedBlock
{
    public const uint LayoutVersion = 2;
    public const uint MagicValue = 0x584D584E; // 'NXMX'
    public const int MaxInputs = 128;
    public const int MaxOutputs = 128;
    public const int MaxBuses = 64;
    public const int MaxDeviceMasters = 64;
    public const int SpectrumPoints = SpectrumAnalyzer.Points;

    public uint Magic;
    public uint Version;
    public uint Seq; // Ungerade = Schreibvorgang aktiv, Gerade = konsistenter Schnappschuss
    public int DaemonPid;
    public long TimestampTicks;

    public int InputCount;
    public int OutputCount;
    public int BusCount;
    public int DeviceMasterCount;

    public int HostStateCode;
    public int BlockFrames;
    public int SampleRate;
    public int PanicMuted;

    public double CpuLoad;
    public double OutputLatencyMs;
    public long Underruns;
    public long Overruns;
    public long LimiterHits;

    public int SpectrumMode; // 0 = Aus, 1 = Input, 2 = Output
    public int SpectrumChannel;

    public fixed float InputPeaks[MaxInputs];
    public fixed float OutputPeaks[MaxOutputs];
    public fixed float BusPeaks[MaxBuses];
    public fixed float DeviceMasterPeaks[MaxDeviceMasters];
    public fixed float SpectrumPreDb[SpectrumPoints];
    public fixed float SpectrumPostDb[SpectrumPoints];
}

public readonly record struct TelemetrySnapshotFrame(
    int DaemonPid,
    HostState HostState,
    int BlockFrames,
    int SampleRate,
    bool PanicMuted,
    double CpuLoad,
    double OutputLatencyMs,
    long Underruns,
    long Overruns,
    long LimiterHits,
    int SpectrumMode,
    int SpectrumChannel);

public static class IpcNames
{
    public static string CurrentUserToken()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return id.User?.Value ?? Environment.UserName;
        }
        catch
        {
            return Environment.UserName;
        }
    }

    public static string ControlPipeName(string? customKey = null) =>
        $"NxMix.Control.v1.{SanitizeKey(customKey ?? CurrentUserToken())}";

    public static string TelemetryMapName(string? customKey = null) =>
        $@"Local\NxMix.Telemetry.v{TelemetrySharedBlock.LayoutVersion}.{SanitizeKey(customKey ?? CurrentUserToken())}";

    public static string DaemonMutexName(string? customKey = null) =>
        $@"Local\NxMix.Daemon.SingleInstance.v1.{SanitizeKey(customKey ?? CurrentUserToken())}";

    public static string UiWindowMutexName(string? customKey = null) =>
        $@"Local\NxMix.App.SingleInstance.v1.{SanitizeKey(customKey ?? CurrentUserToken())}";

    private static string SanitizeKey(string raw)
    {
        Span<char> buf = stackalloc char[Math.Min(raw.Length, 96)];
        int n = 0;
        foreach (char c in raw)
        {
            if (n >= buf.Length) break;
            buf[n++] = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_';
        }
        return n == 0 ? "default" : new string(buf[..n]);
    }
}

/// <summary>Allokationsfreier 60-Hz-Shared-Memory-Schreiber im Daemon (SeqLock).</summary>
public sealed unsafe class TelemetrySharedMemoryWriter : IDisposable
{
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _ptr;
    private uint _seq;
    private bool _disposed;

    public string MapName { get; }

    public TelemetrySharedMemoryWriter(string? customKey = null)
    {
        MapName = IpcNames.TelemetryMapName(customKey);
        int size = sizeof(TelemetrySharedBlock) + 64;
        _mmf = MemoryMappedFile.CreateOrOpen(MapName, size, MemoryMappedFileAccess.ReadWrite);
        _view = _mmf.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _ptr);

        var block = (TelemetrySharedBlock*)_ptr;
        *block = default;
        for (int i = 0; i < TelemetrySharedBlock.SpectrumPoints; i++)
        {
            block->SpectrumPreDb[i] = -100f;
            block->SpectrumPostDb[i] = -100f;
        }
        block->Magic = TelemetrySharedBlock.MagicValue;
        block->Version = TelemetrySharedBlock.LayoutVersion;
        block->DaemonPid = Environment.ProcessId;
        Volatile.Write(ref block->Seq, 0u);
    }

    public void Publish(
        ReadOnlySpan<float> inPeaks,
        ReadOnlySpan<float> outPeaks,
        ReadOnlySpan<float> busPeaks,
        ReadOnlySpan<float> devPeaks,
        in TelemetrySnapshotFrame meta,
        ReadOnlySpan<float> specPre,
        ReadOnlySpan<float> specPost)
    {
        if (_disposed || _ptr == null) return;
        var b = (TelemetrySharedBlock*)_ptr;

        uint odd = ++_seq;
        if ((odd & 1u) == 0u) odd = ++_seq;
        Volatile.Write(ref b->Seq, odd);
        Thread.MemoryBarrier();

        b->DaemonPid = meta.DaemonPid;
        b->TimestampTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        b->HostStateCode = (int)meta.HostState;
        b->BlockFrames = meta.BlockFrames;
        b->SampleRate = meta.SampleRate;
        b->PanicMuted = meta.PanicMuted ? 1 : 0;
        b->CpuLoad = meta.CpuLoad;
        b->OutputLatencyMs = meta.OutputLatencyMs;
        b->Underruns = meta.Underruns;
        b->Overruns = meta.Overruns;
        b->LimiterHits = meta.LimiterHits;
        b->SpectrumMode = meta.SpectrumMode;
        b->SpectrumChannel = meta.SpectrumChannel;

        int ni = Math.Min(inPeaks.Length, TelemetrySharedBlock.MaxInputs);
        int no = Math.Min(outPeaks.Length, TelemetrySharedBlock.MaxOutputs);
        int nb = Math.Min(busPeaks.Length, TelemetrySharedBlock.MaxBuses);
        int nd = Math.Min(devPeaks.Length, TelemetrySharedBlock.MaxDeviceMasters);
        b->InputCount = ni;
        b->OutputCount = no;
        b->BusCount = nb;
        b->DeviceMasterCount = nd;

        for (int i = 0; i < ni; i++) b->InputPeaks[i] = inPeaks[i];
        for (int i = ni; i < TelemetrySharedBlock.MaxInputs; i++) b->InputPeaks[i] = 0f;

        for (int i = 0; i < no; i++) b->OutputPeaks[i] = outPeaks[i];
        for (int i = no; i < TelemetrySharedBlock.MaxOutputs; i++) b->OutputPeaks[i] = 0f;

        for (int i = 0; i < nb; i++) b->BusPeaks[i] = busPeaks[i];
        for (int i = nb; i < TelemetrySharedBlock.MaxBuses; i++) b->BusPeaks[i] = 0f;

        for (int i = 0; i < nd; i++) b->DeviceMasterPeaks[i] = devPeaks[i];
        for (int i = nd; i < TelemetrySharedBlock.MaxDeviceMasters; i++) b->DeviceMasterPeaks[i] = 0f;

        int nsPre = Math.Min(specPre.Length, TelemetrySharedBlock.SpectrumPoints);
        for (int i = 0; i < nsPre; i++) b->SpectrumPreDb[i] = specPre[i];
        int nsPost = Math.Min(specPost.Length, TelemetrySharedBlock.SpectrumPoints);
        for (int i = 0; i < nsPost; i++) b->SpectrumPostDb[i] = specPost[i];

        Thread.MemoryBarrier();
        uint even = ++_seq;
        Volatile.Write(ref b->Seq, even);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ptr != null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _ptr = null;
        }
        _view.Dispose();
        _mmf.Dispose();
    }
}

/// <summary>Lock-freier Shared-Memory-Leser für die UI (0 Bytes Allokation pro Frame).</summary>
public sealed unsafe class TelemetrySharedMemoryReader : IDisposable
{
    private readonly string _mapName;
    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private byte* _ptr;
    private bool _disposed;

    public TelemetrySharedMemoryReader(string? customKey = null)
    {
        _mapName = IpcNames.TelemetryMapName(customKey);
    }

    private bool EnsureMapped()
    {
        if (_ptr != null) return true;
        try
        {
            _mmf = MemoryMappedFile.OpenExisting(_mapName, MemoryMappedFileRights.Read);
            _view = _mmf.CreateViewAccessor(0, sizeof(TelemetrySharedBlock), MemoryMappedFileAccess.Read);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _ptr);
            return _ptr != null;
        }
        catch
        {
            CloseMapping();
            return false;
        }
    }

    public bool TryRead(
        Span<float> inPeaks,
        Span<float> outPeaks,
        Span<float> busPeaks,
        Span<float> devPeaks,
        Span<float> specPre,
        Span<float> specPost,
        out TelemetrySnapshotFrame meta)
    {
        meta = default;
        if (_disposed || !EnsureMapped()) return false;

        var src = (TelemetrySharedBlock*)_ptr;
        for (int attempt = 0; attempt < 6; attempt++)
        {
            uint s1 = Volatile.Read(ref src->Seq);
            if ((s1 & 1u) != 0u)
            {
                Thread.SpinWait(8);
                continue;
            }
            if (src->Magic != TelemetrySharedBlock.MagicValue || src->Version != TelemetrySharedBlock.LayoutVersion) return false;

            TelemetrySharedBlock copy = *src;
            Thread.MemoryBarrier();
            uint s2 = Volatile.Read(ref src->Seq);
            if (s1 != s2) continue;

            int ni = Math.Min(inPeaks.Length, TelemetrySharedBlock.MaxInputs);
            for (int i = 0; i < ni; i++) inPeaks[i] = copy.InputPeaks[i];
            if (ni < inPeaks.Length) inPeaks[ni..].Clear();

            int no = Math.Min(outPeaks.Length, TelemetrySharedBlock.MaxOutputs);
            for (int i = 0; i < no; i++) outPeaks[i] = copy.OutputPeaks[i];
            if (no < outPeaks.Length) outPeaks[no..].Clear();

            int nb = Math.Min(busPeaks.Length, TelemetrySharedBlock.MaxBuses);
            for (int i = 0; i < nb; i++) busPeaks[i] = copy.BusPeaks[i];
            if (nb < busPeaks.Length) busPeaks[nb..].Clear();

            int nd = Math.Min(devPeaks.Length, TelemetrySharedBlock.MaxDeviceMasters);
            for (int i = 0; i < nd; i++) devPeaks[i] = copy.DeviceMasterPeaks[i];
            if (nd < devPeaks.Length) devPeaks[nd..].Clear();

            int nsp = Math.Min(specPre.Length, TelemetrySharedBlock.SpectrumPoints);
            for (int i = 0; i < nsp; i++) specPre[i] = copy.SpectrumPreDb[i];
            int nspo = Math.Min(specPost.Length, TelemetrySharedBlock.SpectrumPoints);
            for (int i = 0; i < nspo; i++) specPost[i] = copy.SpectrumPostDb[i];

            meta = new TelemetrySnapshotFrame(
                copy.DaemonPid,
                (HostState)copy.HostStateCode,
                copy.BlockFrames,
                copy.SampleRate,
                copy.PanicMuted != 0,
                copy.CpuLoad,
                copy.OutputLatencyMs,
                copy.Underruns,
                copy.Overruns,
                copy.LimiterHits,
                copy.SpectrumMode,
                copy.SpectrumChannel);
            return true;
        }
        return false;
    }

    public void ResetMapping() => CloseMapping();

    private void CloseMapping()
    {
        if (_ptr != null && _view != null)
        {
            try { _view.SafeMemoryMappedViewHandle.ReleasePointer(); } catch { }
            _ptr = null;
        }
        try { _view?.Dispose(); } catch { }
        try { _mmf?.Dispose(); } catch { }
        _view = null;
        _mmf = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CloseMapping();
    }
}
