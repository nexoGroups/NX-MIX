using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NxMix.Engine;
using NxMix.Ipc;

namespace NxMix.Daemon.ControlApi;

/// <summary>Lokaler JSON-RPC-2.0-WebSocket- &amp; HTTP-Gateway für Bitfocus Companion, Stream Deck+ und Hardware-Controller.</summary>
public sealed class CompanionWebSocketGateway : IDisposable
{
    private readonly INxMixBackend _backend;
    private readonly object _gate = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<int, WsClient> _clients = new();
    private Timer? _meterPushTimer;
    private int _nextClientId;
    private int _boundPort;
    private bool _boundLoopbackOnly = true;
    private string? _authToken;
    private bool _disposed;

    public bool IsListening => _listener is { IsListening: true };
    public int ListeningPort => _boundPort;

    private sealed class WsClient : IDisposable
    {
        public readonly WebSocket Socket;
        public readonly Channel<byte[]> Outbox;
        public volatile int MeterFps;
        public long LastMeterTickMs;

        public WsClient(WebSocket socket)
        {
            Socket = socket;
            Outbox = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });
        }

        public void EnqueueJson(object payload)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(payload, IpcJson.Options);
            Outbox.Writer.TryWrite(bytes);
        }

        public void Dispose()
        {
            Outbox.Writer.TryComplete();
            try { Socket.Dispose(); } catch { }
        }
    }

    public CompanionWebSocketGateway(INxMixBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _backend.SnapshotUpdated += OnSnapshotUpdated;
        SyncWithConfig(_backend.Snapshot.Config);
    }

    private void OnSnapshotUpdated(DaemonRuntimeSnapshot snap)
    {
        SyncWithConfig(snap.Config);
        if (_clients.IsEmpty) return;

        var msg = new
        {
            jsonrpc = "2.0",
            method = "state.updated",
            @params = BuildCompactFeedbackState(snap),
        };
        foreach (var kv in _clients)
            kv.Value.EnqueueJson(msg);
    }

    public void SyncWithConfig(SystemConfig cfg)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!cfg.CompanionApiEnabled)
            {
                StopLocked();
                return;
            }
            if (_listener is { IsListening: true }
                && _boundPort == cfg.CompanionApiPort
                && _boundLoopbackOnly == cfg.CompanionApiBindLoopbackOnly
                && _authToken == cfg.CompanionApiAuthToken)
            {
                return;
            }
            StopLocked();
            StartLocked(cfg.CompanionApiPort, cfg.CompanionApiBindLoopbackOnly, cfg.CompanionApiAuthToken);
        }
    }

    private void StartLocked(int port, bool loopbackOnly, string? authToken)
    {
        try
        {
            var listener = new HttpListener();
            string host = loopbackOnly ? "127.0.0.1" : "+";
            listener.Prefixes.Add($"http://{host}:{port}/");
            listener.Start();

            _listener = listener;
            _boundPort = port;
            _boundLoopbackOnly = loopbackOnly;
            _authToken = string.IsNullOrWhiteSpace(authToken) ? null : authToken.Trim();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _ = Task.Run(() => AcceptLoopAsync(listener, ct));
            _meterPushTimer = new Timer(_ => PushMetersTick(), null, 33, 33);
        }
        catch
        {
            // Port belegt oder URLACL verweigert -> Audio-Betrieb niemals gefährden!
            StopLocked();
        }
    }

    private void StopLocked()
    {
        _meterPushTimer?.Dispose();
        _meterPushTimer = null;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _listener?.Close(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        foreach (var kv in _clients)
        {
            if (_clients.TryRemove(kv.Key, out var c))
                c.Dispose();
        }
    }

    private bool CheckAuth(HttpListenerRequest req)
    {
        if (string.IsNullOrEmpty(_authToken)) return true;
        string? hdr = req.Headers["Authorization"];
        if (!string.IsNullOrEmpty(hdr) && hdr.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return string.Equals(hdr["Bearer ".Length..].Trim(), _authToken, StringComparison.Ordinal);
        string? q = req.QueryString["token"];
        return string.Equals(q, _authToken, StringComparison.Ordinal);
    }

    private async Task AcceptLoopAsync(HttpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            if (!CheckAuth(ctx.Request))
            {
                ctx.Response.StatusCode = 401;
                ctx.Response.Close();
                continue;
            }

            string path = ctx.Request.Url?.AbsolutePath ?? "/";
            if (ctx.Request.IsWebSocketRequest && (path.Equals("/v1/ws", StringComparison.OrdinalIgnoreCase) || path == "/"))
            {
                _ = Task.Run(() => HandleWebSocketAsync(ctx, ct));
            }
            else if (path.Equals("/v1/status", StringComparison.OrdinalIgnoreCase))
            {
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(BuildCompactFeedbackState(_backend.Snapshot), IpcJson.PrettyOptions);
                ctx.Response.StatusCode = 200;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                await ctx.Response.OutputStream.WriteAsync(json, ct).ConfigureAwait(false);
                ctx.Response.Close();
            }
            else
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
            }
        }
    }

    private async Task HandleWebSocketAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        WsClient? client = null;
        int id = Interlocked.Increment(ref _nextClientId);
        try
        {
            var wsCtx = await ctx.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
            client = new WsClient(wsCtx.WebSocket);
            _clients[id] = client;

            // Initiales State-Event senden
            client.EnqueueJson(new
            {
                jsonrpc = "2.0",
                method = "state.updated",
                @params = BuildCompactFeedbackState(_backend.Snapshot),
            });

            var writerTask = Task.Run(() => PumpOutboxAsync(client, ct), ct);
            byte[] buf = ArrayPool<byte>.Shared.Rent(65536);
            try
            {
                using var ms = new MemoryStream();
                while (!ct.IsCancellationRequested && client.Socket.State == WebSocketState.Open)
                {
                    var res = await client.Socket.ReceiveAsync(buf, ct).ConfigureAwait(false);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    if (res.MessageType != WebSocketMessageType.Text) continue;

                    ms.Write(buf, 0, res.Count);
                    if (!res.EndOfMessage) continue;

                    byte[] msgBytes = ms.ToArray();
                    ms.SetLength(0);
                    object response = await HandleRpcMessageAsync(client, msgBytes).ConfigureAwait(false);
                    client.EnqueueJson(response);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buf);
            }

            client.Outbox.Writer.TryComplete();
            await writerTask.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            _clients.TryRemove(id, out _);
            client?.Dispose();
        }
    }

    private static async Task PumpOutboxAsync(WsClient client, CancellationToken ct)
    {
        try
        {
            await foreach (byte[] msg in client.Outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (client.Socket.State != WebSocketState.Open) break;
                await client.Socket.SendAsync(msg, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }

    private async Task<object> HandleRpcMessageAsync(WsClient client, byte[] jsonBytes)
    {
        JsonElement idElem = default;
        bool hasId = false;
        try
        {
            using var doc = JsonDocument.Parse(jsonBytes);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idProp))
            {
                idElem = idProp.Clone();
                hasId = true;
            }
            string method = root.TryGetProperty("method", out var mProp) ? mProp.GetString() ?? "" : "";
            JsonElement p = root.TryGetProperty("params", out var pProp) ? pProp : default;

            object? result = await DispatchRpcMethodAsync(client, method, p).ConfigureAwait(false);
            return new { jsonrpc = "2.0", id = hasId ? (object?)idElem : null, result };
        }
        catch (Exception ex)
        {
            return new
            {
                jsonrpc = "2.0",
                id = hasId ? (object?)idElem : null,
                error = new { code = -32000, message = ex.Message },
            };
        }
    }

    private async Task<object?> DispatchRpcMethodAsync(WsClient client, string method, JsonElement p)
    {
        var snap = _backend.Snapshot;
        var mixer = snap.Mixer;

        switch (method)
        {
            case "system.ping":
                return new { ok = true, pid = snap.DaemonPid, hostState = snap.HostStats.State.ToString() };

            case "state.get":
                return BuildCompactFeedbackState(snap);

            case "channel.setGainDb":
            {
                var (isInput, idx) = ResolveChannel(mixer, p);
                double db = p.GetProperty("gainDb").GetDouble();
                var arr = (isInput ? mixer.Inputs : mixer.Outputs).ToArray();
                arr[idx] = arr[idx] with { GainDb = db };
                string? err = await _backend.ApplyMixerAsync(isInput ? mixer with { Inputs = arr } : mixer with { Outputs = arr }).ConfigureAwait(false);
                if (err != null) throw new InvalidOperationException(err);
                return new { ok = true, gainDb = arr[idx].GainDb };
            }

            case "channel.adjustGainDb":
            {
                var (isInput, idx) = ResolveChannel(mixer, p);
                double delta = p.GetProperty("deltaDb").GetDouble();
                var ch = (isInput ? mixer.Inputs : mixer.Outputs)[idx];
                string? err = await _backend.AdjustChannelGainAsync(ch.Id, isInput, delta).ConfigureAwait(false);
                if (err != null) throw new InvalidOperationException(err);
                var updated = (isInput ? _backend.Snapshot.Mixer.Inputs : _backend.Snapshot.Mixer.Outputs)[idx];
                return new { ok = true, gainDb = updated.GainDb };
            }

            case "channel.setMute":
            case "channel.toggleMute":
            {
                var (isInput, idx) = ResolveChannel(mixer, p);
                var arr = (isInput ? mixer.Inputs : mixer.Outputs).ToArray();
                bool nextMute = method == "channel.toggleMute" ? !arr[idx].Mute : p.GetProperty("mute").GetBoolean();
                arr[idx] = arr[idx] with { Mute = nextMute };
                string? err = await _backend.ApplyMixerAsync(isInput ? mixer with { Inputs = arr } : mixer with { Outputs = arr }).ConfigureAwait(false);
                if (err != null) throw new InvalidOperationException(err);
                return new { ok = true, mute = nextMute };
            }

            case "channel.setPan":
            {
                var (isInput, idx) = ResolveChannel(mixer, p);
                double pan = Math.Clamp(p.GetProperty("pan").GetDouble(), -1.0, 1.0);
                var arr = (isInput ? mixer.Inputs : mixer.Outputs).ToArray();
                arr[idx] = arr[idx] with { Pan = pan };
                string? err = await _backend.ApplyMixerAsync(isInput ? mixer with { Inputs = arr } : mixer with { Outputs = arr }).ConfigureAwait(false);
                if (err != null) throw new InvalidOperationException(err);
                return new { ok = true, pan };
            }

            case "device.setMasterDb":
            {
                int idx = ResolveDeviceMaster(mixer, p);
                double db = p.GetProperty("gainDb").GetDouble();
                var dms = mixer.DeviceMasters.ToArray();
                dms[idx] = dms[idx] with { GainDb = db };
                string? err = await _backend.ApplyMixerAsync(mixer with { DeviceMasters = dms }).ConfigureAwait(false);
                if (err != null) throw new InvalidOperationException(err);
                return new { ok = true, gainDb = db };
            }

            case "device.adjustMasterDb":
            {
                int idx = ResolveDeviceMaster(mixer, p);
                double delta = p.GetProperty("deltaDb").GetDouble();
                string? err = await _backend.AdjustDeviceMasterGainAsync(mixer.DeviceMasters[idx].Id, delta).ConfigureAwait(false);
                if (err != null) throw new InvalidOperationException(err);
                return new { ok = true, gainDb = _backend.Snapshot.Mixer.DeviceMasters[idx].GainDb };
            }

            case "device.setMute":
            case "device.toggleMute":
            {
                int idx = ResolveDeviceMaster(mixer, p);
                var dms = mixer.DeviceMasters.ToArray();
                bool nextMute = method == "device.toggleMute" ? !dms[idx].Mute : p.GetProperty("mute").GetBoolean();
                dms[idx] = dms[idx] with { Mute = nextMute };
                string? err = await _backend.ApplyMixerAsync(mixer with { DeviceMasters = dms }).ConfigureAwait(false);
                if (err != null) throw new InvalidOperationException(err);
                return new { ok = true, mute = nextMute };
            }

            case "device.toggleDim":
            {
                int idx = ResolveDeviceMaster(mixer, p);
                var dms = mixer.DeviceMasters.ToArray();
                bool nextDim = !dms[idx].Dim;
                dms[idx] = dms[idx] with { Dim = nextDim };
                string? err = await _backend.ApplyMixerAsync(mixer with { DeviceMasters = dms }).ConfigureAwait(false);
                if (err != null) throw new InvalidOperationException(err);
                return new { ok = true, dim = nextDim };
            }

            case "bus.setGainDb":
            case "bus.adjustGainDb":
            {
                int idx = ResolveBus(mixer, p);
                var buses = mixer.Buses.ToArray();
                double next = method == "bus.adjustGainDb"
                    ? Math.Clamp(buses[idx].GainDb + p.GetProperty("deltaDb").GetDouble(), -80.0, 12.0)
                    : p.GetProperty("gainDb").GetDouble();
                buses[idx] = buses[idx] with { GainDb = next };
                string? err = await _backend.ApplyMixerAsync(mixer with { Buses = buses }).ConfigureAwait(false);
                if (err != null) throw new InvalidOperationException(err);
                return new { ok = true, gainDb = next };
            }

            case "bus.toggleMute":
            {
                int idx = ResolveBus(mixer, p);
                var buses = mixer.Buses.ToArray();
                bool nextMute = !buses[idx].Mute;
                buses[idx] = buses[idx] with { Mute = nextMute };
                string? err = await _backend.ApplyMixerAsync(mixer with { Buses = buses }).ConfigureAwait(false);
                if (err != null) throw new InvalidOperationException(err);
                return new { ok = true, mute = nextMute };
            }

            case "route.setCrosspoint":
            {
                int inIdx = p.GetProperty("inputIndex").GetInt32();
                int outIdx = p.GetProperty("outputIndex").GetInt32();
                if ((uint)inIdx >= (uint)mixer.Inputs.Count || (uint)outIdx >= (uint)mixer.Outputs.Count)
                    throw new ArgumentOutOfRangeException("Kreuzpunkt-Index außerhalb des Bereichs.");
                bool active = !p.TryGetProperty("enabled", out var en) || en.GetBoolean();
                double db = active && p.TryGetProperty("gainDb", out var gProp) ? gProp.GetDouble() : (active ? 0.0 : double.NegativeInfinity);
                var m = mixer.MatrixDb.Select(r => (double[])r.Clone()).ToArray();
                m[inIdx][outIdx] = db;
                string? err = await _backend.ApplyMixerAsync(mixer with { MatrixDb = m }).ConfigureAwait(false);
                if (err != null) throw new InvalidOperationException(err);
                return new { ok = true };
            }

            case "snapshot.list":
                return snap.Snapshots;

            case "snapshot.recall":
            {
                Guid snapId = p.TryGetProperty("id", out var idP)
                    ? Guid.Parse(idP.GetString()!)
                    : snap.Snapshots.FirstOrDefault(s => string.Equals(s.Name, p.GetProperty("name").GetString(), StringComparison.OrdinalIgnoreCase))?.Id
                      ?? throw new InvalidOperationException("Snapshot nicht gefunden.");
                double fadeMs = p.TryGetProperty("fadeMs", out var fP) ? fP.GetDouble() : 100.0;
                string? err = await _backend.RecallSnapshotAsync(snapId, SnapshotScopeFlags.All, fadeMs).ConfigureAwait(false);
                if (err != null) throw new InvalidOperationException(err);
                return new { ok = true, snapshotId = snapId };
            }

            case "panic.setMute":
            {
                bool muted = p.GetProperty("mute").GetBoolean();
                await _backend.SetPanicMuteAsync(muted).ConfigureAwait(false);
                return new { ok = true, panicMuted = muted };
            }

            case "meters.subscribe":
            {
                int fps = p.TryGetProperty("fps", out var fpsProp) ? Math.Clamp(fpsProp.GetInt32(), 0, 30) : 15;
                client.MeterFps = fps;
                return new { ok = true, fps };
            }

            default:
                throw new InvalidOperationException($"Unbekannte JSON-RPC-Methode '{method}'.");
        }
    }

    private void PushMetersTick()
    {
        if (_clients.IsEmpty) return;
        bool anySub = false;
        foreach (var kv in _clients)
            if (kv.Value.MeterFps > 0) { anySub = true; break; }
        if (!anySub) return;

        var snap = _backend.Snapshot;
        int ni = snap.Mixer.Inputs.Count;
        int no = snap.Mixer.Outputs.Count;
        int nb = snap.Mixer.Buses.Count;
        int nd = snap.Mixer.DeviceMasters.Count;

        float[] inP = new float[ni];
        float[] outP = new float[no];
        float[] busP = new float[nb];
        float[] devP = new float[nd];
        Span<float> dummySpec = stackalloc float[1];
        _backend.TryReadTelemetry(inP, outP, busP, devP, dummySpec, dummySpec, out _);

        long now = Environment.TickCount64;
        var payload = new
        {
            jsonrpc = "2.0",
            method = "meters.frame",
            @params = new { inputs = inP, outputs = outP, buses = busP, devices = devP },
        };

        foreach (var kv in _clients)
        {
            var c = kv.Value;
            int fps = c.MeterFps;
            if (fps <= 0) continue;
            long minInterval = 1000 / Math.Max(1, fps);
            if (now - c.LastMeterTickMs < minInterval) continue;
            c.LastMeterTickMs = now;
            c.EnqueueJson(payload);
        }
    }

    private static (bool IsInput, int Index) ResolveChannel(MixerSettings m, JsonElement p)
    {
        bool isInput = !p.TryGetProperty("kind", out var kProp)
            || !string.Equals(kProp.GetString(), "output", StringComparison.OrdinalIgnoreCase);
        var list = isInput ? m.Inputs : m.Outputs;
        if (p.TryGetProperty("id", out var idProp) && Guid.TryParse(idProp.GetString(), out var gid))
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].Id == gid) return (isInput, i);
        }
        if (p.TryGetProperty("index", out var idxProp))
        {
            int idx = idxProp.GetInt32();
            if ((uint)idx < (uint)list.Count) return (isInput, idx);
        }
        throw new InvalidOperationException("Kanal konnte über 'id' oder 'index' nicht aufgelöst werden.");
    }

    private static int ResolveDeviceMaster(MixerSettings m, JsonElement p)
    {
        if (p.TryGetProperty("id", out var idProp) && Guid.TryParse(idProp.GetString(), out var gid))
        {
            for (int i = 0; i < m.DeviceMasters.Count; i++)
                if (m.DeviceMasters[i].Id == gid) return i;
        }
        if (p.TryGetProperty("index", out var idxProp))
        {
            int idx = idxProp.GetInt32();
            if ((uint)idx < (uint)m.DeviceMasters.Count) return idx;
        }
        throw new InvalidOperationException("Geräte-Master nicht gefunden.");
    }

    private static int ResolveBus(MixerSettings m, JsonElement p)
    {
        if (p.TryGetProperty("id", out var idProp) && Guid.TryParse(idProp.GetString(), out var gid))
        {
            for (int i = 0; i < m.Buses.Count; i++)
                if (m.Buses[i].Id == gid) return i;
        }
        if (p.TryGetProperty("index", out var idxProp))
        {
            int idx = idxProp.GetInt32();
            if ((uint)idx < (uint)m.Buses.Count) return idx;
        }
        throw new InvalidOperationException("Mix-Bus nicht gefunden.");
    }

    private static object BuildCompactFeedbackState(DaemonRuntimeSnapshot s) => new
    {
        pid = s.DaemonPid,
        revision = s.StateRevision,
        hostState = s.HostStats.State.ToString(),
        sampleRate = s.Config.SampleRate,
        blockFrames = s.HostStats.BlockFrames,
        panicMuted = s.PanicMuted,
        activeSnapshotId = s.ActiveSnapshotId,
        deviceMasters = s.Mixer.DeviceMasters.Select((d, i) => new { index = i, d.Id, d.Name, d.IsInput, d.GainDb, d.Mute, d.Dim }),
        inputs = s.Mixer.Inputs.Select((c, i) => new { index = i, c.Id, c.Name, c.GainDb, c.Mute, c.Pan }),
        outputs = s.Mixer.Outputs.Select((c, i) => new { index = i, c.Id, c.Name, c.GainDb, c.Mute }),
        buses = s.Mixer.Buses.Select((b, i) => new { index = i, b.Id, b.Name, b.Channels, b.GainDb, b.Mute }),
        snapshots = s.Snapshots.Select(x => new { x.Id, x.Name, x.DefaultFadeMs }),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _backend.SnapshotUpdated -= OnSnapshotUpdated;
        lock (_gate) StopLocked();
    }
}
