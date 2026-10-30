using System.Diagnostics;

namespace Mining.Core;

public sealed record MinerSnapshot(
    bool IsRunning,
    bool IsPaused,
    bool IsConnected,
    string State,
    double HashRate,
    long TotalHashes,
    long AcceptedShares,
    long RejectedShares,
    int PendingShares,
    int Reconnects,
    TimeSpan Uptime,
    string? LastError);

/// <summary>
/// Owns one explicitly started mining session. No constructor, restore operation,
/// or connection event starts hashing without StartAsync being called first.
/// All observer events may run on background threads.
/// </summary>
public sealed class MiningSession : IAsyncDisposable
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _statistics = new();
    private CancellationTokenSource? _stop;
    private Task? _runner;
    private CpuMiner? _miner;
    private MinerSnapshot _snapshot = new(false, false, false, "Detenido", 0, 0, 0, 0, 0, 0, TimeSpan.Zero, null);
    private string _connectionState = "Detenido";
    private string? _pauseReason;
    private bool _disposed;

    public event Action<MinerSnapshot>? SnapshotChanged;
    public event Action<string>? LogMessage;
    public MinerSnapshot Snapshot { get { lock (_statistics) return _snapshot; } }

    public async Task StartAsync(MinerSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runner is { IsCompleted: false })
                throw new InvalidOperationException("La minería ya está iniciada.");
            _stop?.Dispose();
            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _miner = new CpuMiner();
            _miner.Start(settings);
            lock (_statistics)
            {
                _pauseReason = null;
                _connectionState = "Conectando";
                _snapshot = new(true, false, false, "Conectando", 0, 0, 0, 0, 0, 0, TimeSpan.Zero, null);
            }
            NotifySnapshot();
            Log("Inicio manual solicitado. El trabajo comenzará después de autorizar el worker.");
            _runner = RunSessionAsync(settings, _miner, _stop);
        }
        finally { _lifecycle.Release(); }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_stop is null) return;
            _stop.Cancel();
            _miner?.SetJob(null);
            if (_runner is not null) await _runner.ConfigureAwait(false);
            _stop.Dispose();
            _stop = null;
            _miner = null;
            _runner = null;
        }
        finally { _lifecycle.Release(); }
    }

    /// <summary>Pausing keeps Stratum connected but prevents further hashing.</summary>
    public void SetPaused(bool paused, string? reason = null)
    {
        lock (_statistics)
        {
            if (!_snapshot.IsRunning) return;
            _pauseReason = paused ? (string.IsNullOrWhiteSpace(reason) ? "Pausa manual" : reason) : null;
            _miner?.SetPaused(paused);
            _snapshot = _snapshot with
            {
                IsPaused = paused,
                State = paused ? _pauseReason! : _connectionState,
                HashRate = paused ? 0 : _snapshot.HashRate
            };
        }
        NotifySnapshot();
    }

    private async Task RunSessionAsync(MinerSettings settings, CpuMiner miner, CancellationTokenSource stop)
    {
        await using var client = new StratumClient();
        var started = Stopwatch.StartNew();
        var token = stop.Token;
        client.LogMessage += Log;
        client.ConnectionChanged += update =>
        {
            lock (_statistics)
            {
                _connectionState = update.State;
                _snapshot = _snapshot with
                {
                    IsConnected = update.Connected,
                    State = _snapshot.IsPaused ? _pauseReason! : update.State,
                    HashRate = update.Connected ? _snapshot.HashRate : 0,
                    LastError = update.Error ?? (update.Connected ? null : _snapshot.LastError),
                    Reconnects = _snapshot.Reconnects + (update.Reconnecting ? 1 : 0)
                };
            }
            NotifySnapshot();
        };
        client.JobReceived += job =>
        {
            try
            {
                miner.SetJob(job);
                lock (_statistics)
                {
                    _connectionState = "Minando";
                    _snapshot = _snapshot with { State = _snapshot.IsPaused ? _pauseReason! : _connectionState };
                }
                NotifySnapshot();
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                Fail("El trabajo recibido del pool no es compatible.");
                stop.Cancel();
            }
        };
        client.WorkInvalidated += () => miner.SetJob(null);
        client.ShareCompleted += share =>
        {
            lock (_statistics)
            {
                _snapshot = _snapshot with
                {
                    AcceptedShares = _snapshot.AcceptedShares + (share.Accepted ? 1 : 0),
                    RejectedShares = _snapshot.RejectedShares + (share.Accepted ? 0 : 1)
                };
            }
            Log(share.Accepted ? "Share aceptada por el pool." : share.Reason ?? "Share rechazada por el pool.");
            NotifySnapshot();
        };
        miner.Error += message =>
        {
            // The miner only emits locally constructed diagnostics, never
            // credentials or raw server messages.
            Fail(message);
            stop.Cancel();
        };

        Task? network = null;
        Task? shares = null;
        Task? metrics = null;
        try
        {
            network = client.RunAsync(settings, token);
            shares = ForwardSharesAsync(miner, client, token);
            metrics = PublishMetricsAsync(miner, client, started, token);
            var ended = await Task.WhenAny(network, shares, metrics).ConfigureAwait(false);
            await ended.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            Fail("La sesión se detuvo por un error interno. Revisa el registro y vuelve a iniciarla.");
        }
        finally
        {
            stop.Cancel();
            miner.SetJob(null);
            await miner.StopAsync().ConfigureAwait(false);
            if (network is not null) await ObserveShutdownAsync(network).ConfigureAwait(false);
            if (shares is not null) await ObserveShutdownAsync(shares).ConfigureAwait(false);
            if (metrics is not null) await ObserveShutdownAsync(metrics).ConfigureAwait(false);
            lock (_statistics)
            {
                _connectionState = "Detenido";
                _snapshot = _snapshot with
                {
                    IsRunning = false,
                    IsPaused = false,
                    IsConnected = false,
                    State = "Detenido",
                    HashRate = 0,
                    TotalHashes = miner.TotalHashes,
                    PendingShares = 0,
                    Uptime = started.Elapsed
                };
            }
            NotifySnapshot();
            Log("Minería detenida; no quedan hilos de cálculo activos.");
        }
    }

    private static async Task ForwardSharesAsync(CpuMiner miner, StratumClient client, CancellationToken token)
    {
        await foreach (var share in miner.Shares.ReadAllAsync(token).ConfigureAwait(false))
            client.TrySubmit(share);
    }

    private async Task PublishMetricsAsync(CpuMiner miner, StratumClient client, Stopwatch started, CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var lastHashes = 0L;
        var lastElapsed = TimeSpan.Zero;
        while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
        {
            var hashes = miner.TotalHashes;
            var elapsed = started.Elapsed;
            var seconds = (elapsed - lastElapsed).TotalSeconds;
            var rate = seconds > 0 ? Math.Max(0, (hashes - lastHashes) / seconds) : 0;
            lastHashes = hashes;
            lastElapsed = elapsed;
            // Never acquire the connection lock while holding _statistics:
            // connection observers also update session statistics.
            var pendingShares = client.PendingShares;
            lock (_statistics)
            {
                _snapshot = _snapshot with
                {
                    HashRate = _snapshot.IsPaused || !_snapshot.IsConnected ? 0 : rate,
                    TotalHashes = hashes,
                    PendingShares = pendingShares,
                    Uptime = elapsed
                };
            }
            NotifySnapshot();
        }
    }

    private void Fail(string message)
    {
        lock (_statistics) _snapshot = _snapshot with { LastError = message };
        Log(message);
        NotifySnapshot();
    }

    private void NotifySnapshot() => Emit(SnapshotChanged, Snapshot);
    private void Log(string message) => Emit(LogMessage, message);

    private static void Emit<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
            try { handler(value); } catch (Exception) { /* A closed UI must not keep mining alive. */ }
    }

    private static async Task ObserveShutdownAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _disposed = true;
    }
}
