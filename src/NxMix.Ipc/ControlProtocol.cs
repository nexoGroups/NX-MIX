using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;
using NxMix.Engine;

namespace NxMix.Ipc;

public static class IpcJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static readonly JsonSerializerOptions PrettyOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private const int MaxFrameBytes = 16 * 1024 * 1024;

    public static async Task WriteFrameAsync<T>(Stream stream, T payload, CancellationToken ct)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(payload, Options);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, json.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(json, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T?> ReadFrameAsync<T>(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        if (!await ReadExactAsync(stream, header, ct).ConfigureAwait(false))
            return default;
        int len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len <= 0 || len > MaxFrameBytes)
            throw new InvalidDataException($"Ungültige IPC-Paketgröße: {len} Bytes.");
        byte[] buf = new byte[len];
        if (!await ReadExactAsync(stream, buf, ct).ConfigureAwait(false))
            return default;
        return JsonSerializer.Deserialize<T>(buf, Options);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int r = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct).ConfigureAwait(false);
            if (r == 0) return false;
            offset += r;
        }
        return true;
    }
}

public sealed record IpcRequestEnvelope
{
    public long RequestId { get; init; }
    public string Method { get; init; } = "";
    public MixerSettings? Mixer { get; init; }
    public SystemConfig? Config { get; init; }
    public bool BoolArg { get; init; }
    public int IntArg { get; init; }
    public int IntArg2 { get; init; }
    public int IntArg3 { get; init; }
    public double DoubleArg { get; init; }
    public Guid GuidArg { get; init; }
    public Guid? NullableGuidArg { get; init; }
    public string? StringArg { get; init; }
    public string? StringArg2 { get; init; }
    public string? StringArg3 { get; init; }
    public SnapshotScopeFlags ScopeArg { get; init; }
}

public sealed record IpcServerEnvelope
{
    /// <summary>"response" für Antwort auf RequestId, oder "event" für Broadcast an alle verbundenen Clients.</summary>
    public string Kind { get; init; } = "response";
    public long RequestId { get; init; }
    public bool Ok { get; init; } = true;
    public string? Error { get; init; }
    public string? EventName { get; init; }
    public int IntResult { get; init; }
    public DaemonRuntimeSnapshot? Snapshot { get; init; }
    public MixSnapshot? SavedSnapshot { get; init; }
    public SnapshotDiffPreview? DiffPreview { get; init; }
}
