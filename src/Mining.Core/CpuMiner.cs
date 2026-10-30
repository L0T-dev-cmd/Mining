using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Threading.Channels;

namespace Mining.Core;

/// <summary>
/// Cooperative CPU miner. Threads run below normal priority and hash only during
/// a fraction of each 100 ms period. This is a workload target, not an OS quota.
/// </summary>
public sealed class CpuMiner : IAsyncDisposable
{
    private sealed record Work(MiningJob Job, byte[] Target);
    private readonly object _lifecycle = new();
    private readonly object _nonceLock = new();
    private readonly Channel<MiningShare> _shares = Channel.CreateBounded<MiningShare>(
        new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private CancellationTokenSource? _stop;
    private Task[] _workers = [];
    private Task? _stopping;
    private Work? _work;
    private BigInteger _extraNonce;
    private int _paused;
    private long _totalHashes;
    private long _droppedShares;
    private string? _lastError;

    public ChannelReader<MiningShare> Shares => _shares.Reader;
    public long TotalHashes => Interlocked.Read(ref _totalHashes);
    public long DroppedShares => Interlocked.Read(ref _droppedShares);
    public bool IsPaused => Volatile.Read(ref _paused) != 0;
    public bool IsRunning { get { lock (_lifecycle) return _stop is { IsCancellationRequested: false }; } }
    public string? LastError => Volatile.Read(ref _lastError);
    public event Action<string>? Error;

    public void Start(MinerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        lock (_lifecycle)
        {
            if (_stop is not null || _stopping is { IsCompleted: false })
                throw new InvalidOperationException("Mining is already running or stopping.");
            _stopping = null;
            while (_shares.Reader.TryRead(out _)) { }
            Volatile.Write(ref _work, null);
            Volatile.Write(ref _paused, 0);
            Volatile.Write(ref _lastError, null);
            Interlocked.Exchange(ref _totalHashes, 0);
            Interlocked.Exchange(ref _droppedShares, 0);
            lock (_nonceLock) _extraNonce = BigInteger.Zero;
            _stop = new CancellationTokenSource();
            CancellationToken token = _stop.Token;
            // Percent is relative to all logical processors. Fewer selected
            // threads may prevent reaching the target, which is intentional.
            double duty = Math.Min(0.90, settings.CpuPercent / 100d *
                Environment.ProcessorCount / settings.Threads);
            _workers = Enumerable.Range(0, settings.Threads).Select(index =>
                Task.Factory.StartNew(() => RunWorker(index, settings.Threads, duty, token),
                    CancellationToken.None, TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)).ToArray();
        }
    }

    public void SetJob(MiningJob? job)
    {
        if (job is null)
        {
            Volatile.Write(ref _work, null);
            return;
        }
        // Make the caller's branch collection immutable from the worker's view.
        MiningJob frozen = job with { MerkleBranches = job.MerkleBranches?.ToArray()
            ?? throw new ArgumentException("Missing Merkle branches.") };
        if (frozen.ExtraNonce2Size is < 1 or > 16)
            throw new ArgumentException("Unsupported extranonce2 size.");
        // Validate all untrusted fields before waking mining threads.
        _ = Hashing.BuildHeader(frozen, new string('0', frozen.ExtraNonce2Size * 2));
        byte[] target = Hashing.TargetBytes(Hashing.DifficultyTarget(frozen.Difficulty));
        Volatile.Write(ref _work, new Work(frozen, target));
    }

    public void SetPaused(bool paused) => Volatile.Write(ref _paused, paused ? 1 : 0);

    public Task StopAsync()
    {
        lock (_lifecycle)
        {
            if (_stopping is { IsCompleted: false }) return _stopping;
            if (_stop is null) return Task.CompletedTask;
            _stop.Cancel();
            Volatile.Write(ref _work, null);
            _stopping = FinishStopAsync(_stop, _workers);
            return _stopping;
        }
    }

    private async Task FinishStopAsync(CancellationTokenSource source, Task[] workers)
    {
        await Task.WhenAll(workers).ConfigureAwait(false);
        lock (_lifecycle)
        {
            source.Dispose();
            if (ReferenceEquals(_stop, source))
            {
                _stop = null;
                _workers = [];
            }
            while (_shares.Reader.TryRead(out _)) { }
        }
    }

    private void RunWorker(int index, int threadCount, double duty, CancellationToken token)
    {
        try
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; }
            catch (PlatformNotSupportedException) { }
            catch (System.Security.SecurityException) { }
            Work? current = null;
            byte[]? header = null;
            string extraNonce2 = "";
            ulong nonce = (uint)index;
            Span<byte> hash = stackalloc byte[32];
            var cycle = new Stopwatch();
            const double periodMs = 100;
            double activeMs = periodMs * duty;
            int unreported = 0;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    Work? next = Volatile.Read(ref _work);
                    if (IsPaused || next is null)
                    {
                        token.WaitHandle.WaitOne(50);
                        continue;
                    }
                    if (!ReferenceEquals(current, next) || nonce > uint.MaxValue)
                    {
                        current = next;
                        extraNonce2 = NextExtraNonce(current.Job.ExtraNonce2Size);
                        header = Hashing.BuildHeader(current.Job, extraNonce2);
                        nonce = (uint)index;
                    }
                    cycle.Restart();
                    while (cycle.Elapsed.TotalMilliseconds < activeMs)
                    {
                        // Check cancellation/pausing at most every 64 hashes.
                        if (token.IsCancellationRequested || IsPaused ||
                            !ReferenceEquals(current, Volatile.Read(ref _work))) break;
                        for (int batch = 0; batch < 64 && nonce <= uint.MaxValue; ++batch)
                        {
                            BinaryPrimitives.WriteUInt32LittleEndian(header!.AsSpan(76, 4), (uint)nonce);
                            Hashing.DoubleSha256(header, hash);
                            ++unreported;
                            if (Hashing.MeetsTarget(hash, current.Target) &&
                                !token.IsCancellationRequested &&
                                ReferenceEquals(current, Volatile.Read(ref _work)))
                            {
                                var share = new MiningShare(current.Job.Id, extraNonce2,
                                    current.Job.NTime, Hashing.NonceHex((uint)nonce), current.Job.Generation);
                                // The network never blocks the hashing threads; a bounded
                                // backlog limits memory even when a pool stops reading.
                                if (!_shares.Writer.TryWrite(share))
                                    Interlocked.Increment(ref _droppedShares);
                            }
                            nonce += (uint)threadCount;
                        }
                        Interlocked.Add(ref _totalHashes, unreported);
                        unreported = 0;
                        if (nonce > uint.MaxValue) break;
                    }
                    int idleMs = (int)Math.Ceiling(periodMs - cycle.Elapsed.TotalMilliseconds);
                    if (idleMs > 0) token.WaitHandle.WaitOne(idleMs);
                }
            }
            finally
            {
                if (unreported != 0) Interlocked.Add(ref _totalHashes, unreported);
            }
        }
        catch (Exception e)
        {
            Volatile.Write(ref _lastError, e.Message);
            SetPaused(true);
            Volatile.Write(ref _work, null);
            // A subscriber must not crash a mining thread.
            try { Error?.Invoke(e.Message); }
            catch { }
        }
    }

    private string NextExtraNonce(int bytes)
    {
        lock (_nonceLock)
        {
            if (_extraNonce >= (BigInteger.One << (bytes * 8)))
                throw new InvalidOperationException("Se agotaron los extranonces; reinicia la conexión para recibir trabajo nuevo.");
            byte[] encoded = _extraNonce.ToByteArray(isUnsigned: true, isBigEndian: true);
            byte[] padded = new byte[bytes];
            encoded.CopyTo(padded, bytes - encoded.Length);
            ++_extraNonce;
            return Convert.ToHexString(padded).ToLowerInvariant();
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
