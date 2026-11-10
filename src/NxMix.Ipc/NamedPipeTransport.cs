using System.Collections.Concurrent;
using System.IO.Pipes;
using NxMix.Engine;

namespace NxMix.Ipc;

/// <summary>Multi-Client Named-Pipe-Server im NX-MIX Daemon. Bedient mehrere gleichzeitige Clients (UI, Tray, CLI) und verteilt Status-Broadcasts.</summary>
public sealed class NamedPipeControlServer : IDisposable
{
    private readonly string _pipeName;
    private readonly INxMixBackend _engine;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<int, ClientSession> _clients = new();
    private readonly List<Task> _acceptTasks = new();
    private int _nextClientId;
    private bool _disposed;

    private sealed class ClientSession : IDisposable
    {
        public readonly NamedPipeServerStream Stream;
        public readonly SemaphoreSlim WriteLock = new(1, 1);

        public ClientSession(NamedPipeServerStream stream) => Stream = stream;

        public async Task SendAsync(IpcServerEnvelope msg, CancellationToken ct)
        {
            await WriteLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Stream.IsConnected)
                    await IpcJson.WriteFrameAsync(Stream, msg, ct).ConfigureAwait(false);
            }
            finally
            {
                WriteLock.Release();
            }
        }

        public void Dispose()
        {
            try { Stream.Dispose(); } catch { }
            WriteLock.Dispose();
        }
    }

    public string PipeName => _pipeName;

    public NamedPipeControlServer(INxMixBackend engine, string? customKey = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _pipeName = IpcNames.ControlPipeName(customKey);
        _engine.SnapshotUpdated += OnEngineSnapshotUpdated;
    }

    public void Start(int listenerConcurrency = 3)
    {
        for (int i = 0; i < Math.Max(1, listenerConcurrency); i++)
            _acceptTasks.Add(Task.Run(() => AcceptLoopAsync(_cts.Token)));
    }

    private void OnEngineSnapshotUpdated(DaemonRuntimeSnapshot snap)
    {
        if (_disposed || _clients.IsEmpty) return;
        var env = new IpcServerEnvelope
        {
            Kind = "event",
            EventName = "snapshot",
            Snapshot = snap,
        };
        foreach (var kv in _clients)
        {
            _ = Task.Run(async () =>
            {
                try { await kv.Value.SendAsync(env, _cts.Token).ConfigureAwait(false); }
                catch { }
            });
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var session = new ClientSession(server);
                int id = Interlocked.Increment(ref _nextClientId);
                _clients[id] = session;
                _ = Task.Run(() => HandleClientAsync(id, session, ct));
                server = null; // Ownership transferred to session
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
                break;
            }
            catch
            {
                server?.Dispose();
                try { await Task.Delay(150, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private async Task HandleClientAsync(int clientId, ClientSession session, CancellationToken ct)
    {
        try
        {
            // Initialen Zustand sofort senden
            await session.SendAsync(new IpcServerEnvelope
            {
                Kind = "event",
                EventName = "snapshot",
                Snapshot = _engine.Snapshot,
            }, ct).ConfigureAwait(false);

            while (!ct.IsCancellationRequested && session.Stream.IsConnected)
            {
                var req = await IpcJson.ReadFrameAsync<IpcRequestEnvelope>(session.Stream, ct).ConfigureAwait(false);
                if (req == null) break;
                var resp = await DispatchAsync(req).ConfigureAwait(false);
                await session.SendAsync(resp, ct).ConfigureAwait(false);
            }
        }
        catch
        {
            // Client hat die Verbindung beendet oder ist abgestürzt -> Audio im Daemon läuft unberührt weiter!
        }
        finally
        {
            _clients.TryRemove(clientId, out _);
            session.Dispose();
        }
    }

    private async Task<IpcServerEnvelope> DispatchAsync(IpcRequestEnvelope req)
    {
        try
        {
            switch (req.Method)
            {
                case "getSnapshot":
                    return new IpcServerEnvelope { RequestId = req.RequestId, Snapshot = _engine.Snapshot };

                case "applyMixer":
                {
                    string? err = req.Mixer != null ? await _engine.ApplyMixerAsync(req.Mixer).ConfigureAwait(false) : "Mixer fehlt.";
                    return new IpcServerEnvelope { RequestId = req.RequestId, Ok = err == null, Error = err, Snapshot = _engine.Snapshot };
                }

                case "adjustChannelGain":
                {
                    string? err = await _engine.AdjustChannelGainAsync(req.GuidArg, req.BoolArg, req.DoubleArg).ConfigureAwait(false);
                    return new IpcServerEnvelope { RequestId = req.RequestId, Ok = err == null, Error = err };
                }

                case "adjustDeviceMasterGain":
                {
                    string? err = await _engine.AdjustDeviceMasterGainAsync(req.GuidArg, req.DoubleArg).ConfigureAwait(false);
                    return new IpcServerEnvelope { RequestId = req.RequestId, Ok = err == null, Error = err };
                }

                case "setPanicMute":
                    await _engine.SetPanicMuteAsync(req.BoolArg).ConfigureAwait(false);
                    return new IpcServerEnvelope { RequestId = req.RequestId, Snapshot = _engine.Snapshot };

                case "applySystemConfig":
                {
                    string? err = req.Config != null
                        ? await _engine.ApplySystemConfigAsync(req.Config, req.BoolArg).ConfigureAwait(false)
                        : "SystemConfig fehlt.";
                    return new IpcServerEnvelope { RequestId = req.RequestId, Ok = err == null, Error = err, Snapshot = _engine.Snapshot };
                }

                case "refreshDevices":
                    await _engine.RefreshDevicesAsync().ConfigureAwait(false);
                    return new IpcServerEnvelope { RequestId = req.RequestId, Snapshot = _engine.Snapshot };

                case "startAudio":
                    await _engine.StartAudioAsync().ConfigureAwait(false);
                    return new IpcServerEnvelope { RequestId = req.RequestId, Snapshot = _engine.Snapshot };

                case "stopAudio":
                    await _engine.StopAudioAsync().ConfigureAwait(false);
                    return new IpcServerEnvelope { RequestId = req.RequestId, Snapshot = _engine.Snapshot };

                case "showAsioPanel":
                {
                    string? err = await _engine.ShowAsioControlPanelAsync(req.StringArg ?? "").ConfigureAwait(false);
                    return new IpcServerEnvelope { RequestId = req.RequestId, Ok = err == null, Error = err };
                }

                case "restoreAppRoutes":
                {
                    int n = await _engine.RestoreOrphanedAppRoutesAsync().ConfigureAwait(false);
                    return new IpcServerEnvelope { RequestId = req.RequestId, IntResult = n };
                }

                case "playOutputTestTone":
                {
                    string? err = await _engine.PlayOutputTestToneAsync(req.GuidArg).ConfigureAwait(false);
                    return new IpcServerEnvelope { RequestId = req.RequestId, Ok = err == null, Error = err };
                }

                case "subscribeSpectrum":
                    await _engine.SubscribeSpectrumAsync(req.IntArg, req.IntArg2, req.IntArg3).ConfigureAwait(false);
                    return new IpcServerEnvelope { RequestId = req.RequestId };

                case "saveSnapshot":
                {
                    var saved = await _engine.SaveSnapshotAsync(
                        req.NullableGuidArg,
                        req.StringArg ?? "Snapshot",
                        req.StringArg2 ?? "",
                        req.StringArg3 ?? "#3B82F6",
                        req.DoubleArg,
                        req.ScopeArg).ConfigureAwait(false);
                    return new IpcServerEnvelope { RequestId = req.RequestId, SavedSnapshot = saved, Snapshot = _engine.Snapshot };
                }

                case "getSnapshotFile":
                    return new IpcServerEnvelope
                    {
                        RequestId = req.RequestId,
                        SavedSnapshot = await _engine.GetSnapshotAsync(req.GuidArg).ConfigureAwait(false),
                    };

                case "importSnapshot":
                    var imported = await _engine.ImportSnapshotAsync(req.SnapshotFile ?? throw new InvalidDataException("Snapshot fehlt.")).ConfigureAwait(false);
                    return new IpcServerEnvelope { RequestId = req.RequestId, SavedSnapshot = imported, Snapshot = _engine.Snapshot };

                case "renameSnapshot":
                {
                    var renamed = await _engine.RenameSnapshotAsync(req.GuidArg, req.StringArg ?? "").ConfigureAwait(false);
                    return new IpcServerEnvelope
                    {
                        RequestId = req.RequestId,
                        Ok = renamed != null,
                        Error = renamed == null ? "Snapshot nicht gefunden." : null,
                        SavedSnapshot = renamed,
                        Snapshot = _engine.Snapshot,
                    };
                }

                case "deleteSnapshot":
                    await _engine.DeleteSnapshotAsync(req.GuidArg).ConfigureAwait(false);
                    return new IpcServerEnvelope { RequestId = req.RequestId, Snapshot = _engine.Snapshot };

                case "duplicateSnapshot":
                {
                    var dup = await _engine.DuplicateSnapshotAsync(req.GuidArg).ConfigureAwait(false);
                    return new IpcServerEnvelope { RequestId = req.RequestId, SavedSnapshot = dup, Snapshot = _engine.Snapshot };
                }

                case "previewSnapshot":
                {
                    var diff = await _engine.PreviewSnapshotAsync(req.GuidArg, req.ScopeArg).ConfigureAwait(false);
                    return new IpcServerEnvelope { RequestId = req.RequestId, DiffPreview = diff };
                }

                case "recallSnapshot":
                {
                    string? err = await _engine.RecallSnapshotAsync(req.GuidArg, req.ScopeArg, req.DoubleArg).ConfigureAwait(false);
                    return new IpcServerEnvelope { RequestId = req.RequestId, Ok = err == null, Error = err, Snapshot = _engine.Snapshot };
                }

                case "shutdownDaemon":
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(50).ConfigureAwait(false);
                        await _engine.RequestDaemonShutdownAsync().ConfigureAwait(false);
                    });
                    return new IpcServerEnvelope { RequestId = req.RequestId };

                default:
                    return new IpcServerEnvelope { RequestId = req.RequestId, Ok = false, Error = $"Unbekannter IPC-Befehl '{req.Method}'." };
            }
        }
        catch (Exception ex)
        {
            return new IpcServerEnvelope { RequestId = req.RequestId, Ok = false, Error = ex.Message };
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _engine.SnapshotUpdated -= OnEngineSnapshotUpdated;
        _cts.Cancel();
        foreach (var kv in _clients)
        {
            if (_clients.TryRemove(kv.Key, out var s))
                s.Dispose();
        }
        _cts.Dispose();
    }
}

/// <summary>Auto-Reconnect Named-Pipe- + Shared-Memory-Client für die NX-MIX Oberfläche.
/// Schließen oder Dispose() dieses Clients trennt NUR die Pipe-Sitzung und stoppt NIEMALS die Audio-Engine!</summary>
public sealed class NamedPipeBackendClient : INxMixBackend
{
    private readonly string _pipeName;
    private readonly TelemetrySharedMemoryReader _telemetry;
    private readonly Func<CancellationToken, Task>? _daemonLauncher;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<IpcServerEnvelope>> _pending = new();

    private NamedPipeClientStream? _pipe;
    private Task? _loopTask;
    private DaemonRuntimeSnapshot _snapshot = new();
    private long _nextReqId;
    private volatile bool _connected;
    private bool _disposed;

    public bool IsConnected => _connected;
    public string ConnectionSummary => _connected
        ? $"Verbunden mit NX-MIX Daemon (PID {_snapshot.DaemonPid})"
        : "Verbinde mit NX-MIX Hintergrund-Engine …";

    public DaemonRuntimeSnapshot Snapshot => Volatile.Read(ref _snapshot);

    public event Action<DaemonRuntimeSnapshot>? SnapshotUpdated;
    public event Action<bool>? ConnectionChanged;

    public NamedPipeBackendClient(string? customKey = null, Func<CancellationToken, Task>? daemonLauncher = null)
    {
        _pipeName = IpcNames.ControlPipeName(customKey);
        _telemetry = new TelemetrySharedMemoryReader(customKey);
        _daemonLauncher = daemonLauncher;
    }

    public async Task ConnectAsync(TimeSpan initialTimeout)
    {
        var readyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(DaemonRuntimeSnapshot _) => readyTcs.TrySetResult(true);
        SnapshotUpdated += Handler;
        try
        {
            _loopTask ??= Task.Run(() => ConnectionLoopAsync(_cts.Token));
            using var timeoutCts = new CancellationTokenSource(initialTimeout);
            using var reg = timeoutCts.Token.Register(() => readyTcs.TrySetResult(false));
            await readyTcs.Task.ConfigureAwait(false);
        }
        finally
        {
            SnapshotUpdated -= Handler;
        }
    }

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        bool triedLaunch = false;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeClientStream? client = null;
            try
            {
                client = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try
                {
                    await client.ConnectAsync(600, ct).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    client.Dispose();
                    if (!triedLaunch && _daemonLauncher != null)
                    {
                        triedLaunch = true;
                        try { await _daemonLauncher(ct).ConfigureAwait(false); } catch { }
                    }
                    await Task.Delay(250, ct).ConfigureAwait(false);
                    continue;
                }

                triedLaunch = false; // Falls Daemon später explizit neu gestartet wird, erneut auto-launch erlauben
                _pipe = client;
                SetConnected(true);

                while (!ct.IsCancellationRequested && client.IsConnected)
                {
                    var env = await IpcJson.ReadFrameAsync<IpcServerEnvelope>(client, ct).ConfigureAwait(false);
                    if (env == null) break;

                    if (env.Snapshot != null)
                        UpdateLocalSnapshot(env.Snapshot);

                    if (env.Kind == "response" && _pending.TryRemove(env.RequestId, out var tcs))
                        tcs.TrySetResult(env);
                }
            }
            catch (OperationCanceledException)
            {
                client?.Dispose();
                break;
            }
            catch
            {
                // Verbindung verloren -> Pending Requests sauber abbrechen und automatisch neu verbinden
            }
            finally
            {
                _pipe = null;
                client?.Dispose();
                FailAllPending("IPC-Verbindung zum Daemon getrennt.");
                SetConnected(false);
            }

            if (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(350, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private void SetConnected(bool value)
    {
        if (!value)
            _telemetry.ResetMapping();
        if (_connected == value) return;
        _connected = value;
        ConnectionChanged?.Invoke(value);
    }

    private void UpdateLocalSnapshot(DaemonRuntimeSnapshot snap)
    {
        Volatile.Write(ref _snapshot, snap);
        SnapshotUpdated?.Invoke(snap);
    }

    private void FailAllPending(string reason)
    {
        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var tcs))
                tcs.TrySetResult(new IpcServerEnvelope { Ok = false, Error = reason });
        }
    }

    private async Task<IpcServerEnvelope> SendRequestAsync(IpcRequestEnvelope req)
    {
        if (_disposed) return new IpcServerEnvelope { Ok = false, Error = "Client geschlossen." };
        var pipe = _pipe;
        if (pipe == null || !pipe.IsConnected)
            return new IpcServerEnvelope { Ok = false, Error = "Nicht mit NX-MIX Daemon verbunden." };

        long id = Interlocked.Increment(ref _nextReqId);
        req = req with { RequestId = id };
        var tcs = new TaskCompletionSource<IpcServerEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        try
        {
            await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
            try
            {
                await IpcJson.WriteFrameAsync(pipe, req, _cts.Token).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            using var reg = timeout.Token.Register(() =>
            {
                if (_pending.TryRemove(id, out var pending))
                    pending.TrySetResult(new IpcServerEnvelope { RequestId = id, Ok = false, Error = "Zeitüberschreitung bei IPC-Anfrage." });
            });
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _pending.TryRemove(id, out _);
            return new IpcServerEnvelope { RequestId = id, Ok = false, Error = ex.Message };
        }
    }

    public bool TryReadTelemetry(
        Span<float> inPeaks,
        Span<float> outPeaks,
        Span<float> busPeaks,
        Span<float> devPeaks,
        Span<float> specPre,
        Span<float> specPost,
        out TelemetrySnapshotFrame meta,
        Span<float> busChannelPeaks = default)
    {
        if (!_connected)
        {
            meta = default;
            inPeaks.Clear();
            outPeaks.Clear();
            busPeaks.Clear();
            busChannelPeaks.Clear();
            devPeaks.Clear();
            return false;
        }
        return _telemetry.TryRead(inPeaks, outPeaks, busPeaks, devPeaks, specPre, specPost, out meta, busChannelPeaks);
    }

    public async Task<string?> ApplyMixerAsync(MixerSettings mixer)
    {
        // Optimistische lokale Spiegelung für sofortiges UI-Gefühl beim Fader-Ziehen
        Volatile.Write(ref _snapshot, _snapshot with { Mixer = mixer });
        var r = await SendRequestAsync(new IpcRequestEnvelope { Method = "applyMixer", Mixer = mixer }).ConfigureAwait(false);
        return r.Ok ? null : r.Error;
    }

    public async Task<string?> AdjustChannelGainAsync(Guid channelId, bool isInput, double deltaDb)
    {
        var r = await SendRequestAsync(new IpcRequestEnvelope
        {
            Method = "adjustChannelGain",
            GuidArg = channelId,
            BoolArg = isInput,
            DoubleArg = deltaDb,
        }).ConfigureAwait(false);
        return r.Ok ? null : r.Error;
    }

    public async Task<string?> AdjustDeviceMasterGainAsync(Guid deviceId, double deltaDb)
    {
        var r = await SendRequestAsync(new IpcRequestEnvelope
        {
            Method = "adjustDeviceMasterGain",
            GuidArg = deviceId,
            DoubleArg = deltaDb,
        }).ConfigureAwait(false);
        return r.Ok ? null : r.Error;
    }

    public async Task SetPanicMuteAsync(bool muted) =>
        await SendRequestAsync(new IpcRequestEnvelope { Method = "setPanicMute", BoolArg = muted }).ConfigureAwait(false);

    public async Task<string?> ApplySystemConfigAsync(SystemConfig config, bool reopenAudioIfNeeded = true)
    {
        var r = await SendRequestAsync(new IpcRequestEnvelope
        {
            Method = "applySystemConfig",
            Config = config,
            BoolArg = reopenAudioIfNeeded,
        }).ConfigureAwait(false);
        return r.Ok ? null : r.Error;
    }

    public async Task RefreshDevicesAsync() =>
        await SendRequestAsync(new IpcRequestEnvelope { Method = "refreshDevices" }).ConfigureAwait(false);

    public async Task StartAudioAsync() =>
        await SendRequestAsync(new IpcRequestEnvelope { Method = "startAudio" }).ConfigureAwait(false);

    public async Task StopAudioAsync() =>
        await SendRequestAsync(new IpcRequestEnvelope { Method = "stopAudio" }).ConfigureAwait(false);

    public async Task<string?> ShowAsioControlPanelAsync(string endpointId)
    {
        var r = await SendRequestAsync(new IpcRequestEnvelope { Method = "showAsioPanel", StringArg = endpointId }).ConfigureAwait(false);
        return r.Ok ? null : r.Error;
    }

    public async Task<int> RestoreOrphanedAppRoutesAsync()
    {
        var r = await SendRequestAsync(new IpcRequestEnvelope { Method = "restoreAppRoutes" }).ConfigureAwait(false);
        return r.IntResult;
    }

    public async Task<string?> PlayOutputTestToneAsync(Guid channelId)
    {
        var response = await SendRequestAsync(new IpcRequestEnvelope { Method = "playOutputTestTone", GuidArg = channelId }).ConfigureAwait(false);
        return response.Ok ? null : response.Error;
    }

    public async Task SubscribeSpectrumAsync(int mode, int channelIndex, int windowSize = 0) =>
        await SendRequestAsync(new IpcRequestEnvelope { Method = "subscribeSpectrum", IntArg = mode, IntArg2 = channelIndex, IntArg3 = windowSize }).ConfigureAwait(false);

    public async Task<MixSnapshot> SaveSnapshotAsync(Guid? existingId, string name, string notes, string colorHex, double defaultFadeMs, SnapshotScopeFlags defaultScope)
    {
        var r = await SendRequestAsync(new IpcRequestEnvelope
        {
            Method = "saveSnapshot",
            NullableGuidArg = existingId,
            StringArg = name,
            StringArg2 = notes,
            StringArg3 = colorHex,
            DoubleArg = defaultFadeMs,
            ScopeArg = defaultScope,
        }).ConfigureAwait(false);
        return r.SavedSnapshot ?? new MixSnapshot { Name = name };
    }

    public async Task<MixSnapshot?> GetSnapshotAsync(Guid snapshotId)
    {
        var response = await SendRequestAsync(new IpcRequestEnvelope { Method = "getSnapshotFile", GuidArg = snapshotId }).ConfigureAwait(false);
        return response.SavedSnapshot;
    }

    public async Task<MixSnapshot> ImportSnapshotAsync(MixSnapshot snapshot)
    {
        var response = await SendRequestAsync(new IpcRequestEnvelope { Method = "importSnapshot", SnapshotFile = snapshot }).ConfigureAwait(false);
        return response.SavedSnapshot ?? throw new InvalidDataException("Der importierte Snapshot fehlt.");
    }

    public async Task<MixSnapshot?> RenameSnapshotAsync(Guid snapshotId, string newName)
    {
        var r = await SendRequestAsync(new IpcRequestEnvelope
        {
            Method = "renameSnapshot",
            GuidArg = snapshotId,
            StringArg = newName,
        }).ConfigureAwait(false);
        return r.SavedSnapshot;
    }

    public async Task DeleteSnapshotAsync(Guid snapshotId) =>
        await SendRequestAsync(new IpcRequestEnvelope { Method = "deleteSnapshot", GuidArg = snapshotId }).ConfigureAwait(false);

    public async Task<MixSnapshot?> DuplicateSnapshotAsync(Guid snapshotId)
    {
        var r = await SendRequestAsync(new IpcRequestEnvelope { Method = "duplicateSnapshot", GuidArg = snapshotId }).ConfigureAwait(false);
        return r.SavedSnapshot;
    }

    public async Task<SnapshotDiffPreview?> PreviewSnapshotAsync(Guid snapshotId, SnapshotScopeFlags scope)
    {
        var r = await SendRequestAsync(new IpcRequestEnvelope { Method = "previewSnapshot", GuidArg = snapshotId, ScopeArg = scope }).ConfigureAwait(false);
        return r.DiffPreview;
    }

    public async Task<string?> RecallSnapshotAsync(Guid snapshotId, SnapshotScopeFlags scope, double fadeMs)
    {
        var r = await SendRequestAsync(new IpcRequestEnvelope
        {
            Method = "recallSnapshot",
            GuidArg = snapshotId,
            ScopeArg = scope,
            DoubleArg = fadeMs,
        }).ConfigureAwait(false);
        return r.Ok ? null : r.Error;
    }

    public async Task RequestDaemonShutdownAsync() =>
        await SendRequestAsync(new IpcRequestEnvelope { Method = "shutdownDaemon" }).ConfigureAwait(false);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _pipe?.Dispose(); } catch { }
        _telemetry.Dispose();
        _writeLock.Dispose();
        _cts.Dispose();
    }
}
