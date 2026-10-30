using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Mining.Core;

namespace Mining.Tests;

internal static class StratumTests
{
    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("Stratum authenticates, buffers jobs and correlates share results", ProtocolRoundTrip);
        yield return ("Stratum refuses work after authorization rejection", AuthorizationRejected);
        yield return ("Stratum rejects oversized messages and reconnects", OversizedMessageReconnect);
        yield return ("Session metrics, incoming jobs and stop do not deadlock", SessionConcurrentMetricsAndStop);
        yield return ("Stratum preserves valid work until clean job or extranonce change", DifficultyAndExtraNonceChanges);
    }

    private static async Task ProtocolRoundTrip()
    {
        using var pool = new MockPool();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new StratumClient();
        var jobs = new ConcurrentQueue<MiningJob>();
        var results = new ConcurrentQueue<ShareResult>();
        client.JobReceived += jobs.Enqueue;
        client.ShareCompleted += results.Enqueue;
        var running = client.RunAsync(CpuMinerTests.Settings(pool.Port), stop.Token);
        try
        {
            await using var peer = await pool.AcceptAsync(stop.Token);
            var subscribe = await peer.ReadAsync(stop.Token);
            Check.Equal("mining.subscribe", subscribe.GetProperty("method").GetString());
            await peer.SendFragmentedAsync(new { id = Id(subscribe), result = new object[] { new object[] { new object[] { "mining.notify", "test-subscription" } }, "abcd", 2 }, error = (object?)null });
            var authorize = await peer.ReadAsync(stop.Token);
            Check.Equal("mining.authorize", authorize.GetProperty("method").GetString());
            Check.Equal("test.worker", authorize.GetProperty("params")[0].GetString());
            Check.Equal("x", authorize.GetProperty("params")[1].GetString());

            await peer.SendAsync(new { id = (object?)null, method = "mining.set_difficulty", @params = new[] { 0.5m } });
            await peer.NotifyAsync("before-auth", clean: true);
            await Task.Delay(50);
            Check.Equal(0, jobs.Count, "Unauthorized jobs must never reach the CPU");
            await peer.SendAsync(new { id = Id(authorize), result = true, error = (object?)null });
            await Check.Eventually(() => jobs.Count == 1 && client.IsConnected, "Authorized buffered job was not published.");
            var job = jobs.Single();
            Check.Equal("abcd", job.ExtraNonce1);
            Check.Equal(2, job.ExtraNonce2Size);
            Check.Equal(0.5m, job.Difficulty);

            Check.True(client.TrySubmit(new MiningShare(job.Id, "0001", job.NTime, "7c2bac1d", job.Generation)), "Current share should enter submission queue.");
            var accepted = await peer.ReadAsync(stop.Token);
            Check.Equal("mining.submit", accepted.GetProperty("method").GetString());
            var submitted = accepted.GetProperty("params");
            Check.Equal(5, submitted.GetArrayLength());
            Check.Equal("test.worker", submitted[0].GetString());
            Check.Equal("before-auth", submitted[1].GetString());
            Check.Equal("0001", submitted[2].GetString());
            Check.Equal("7c2bac1d", submitted[4].GetString());
            await peer.SendAsync(new { id = 999999, result = true, error = (object?)null });
            await Task.Delay(30);
            Check.Equal(0, results.Count, "Unknown response IDs must not alter share accounting");
            await peer.SendAsync(new { id = Id(accepted), result = true, error = (object?)null });
            await Check.Eventually(() => results.Count == 1, "Accepted share response missing.");
            Check.True(results.First().Accepted, "Accepted share marked rejected.");

            Check.True(client.TrySubmit(new MiningShare(job.Id, "0002", job.NTime, "7c2bac1e", job.Generation)), "Second current share should be queued.");
            var rejected = await peer.ReadAsync(stop.Token);
            await peer.SendAsync(new { id = Id(rejected), result = false, error = new object?[] { 23, "Low difficulty share", null } });
            await Check.Eventually(() => results.Count == 2, "Rejected share response missing.");
            Check.True(!results.Last().Accepted, "Rejected share marked accepted.");
            await Check.Eventually(() => client.PendingShares == 0, "Completed shares leaked pending entries.");

            await peer.NotifyAsync("replacement", clean: true);
            await Check.Eventually(() => jobs.Count == 2, "Replacement job missing.");
            Check.True(jobs.Last().Generation != job.Generation, "New work needs a new generation.");
            Check.True(!client.TrySubmit(new MiningShare(job.Id, "0003", job.NTime, "7c2bac1f", job.Generation)), "Stale share must be rejected before transmission.");
        }
        finally { stop.Cancel(); await ObserveStop(running); }
        Check.True(!client.IsConnected, "Cancellation must close the connection.");
    }

    private static async Task AuthorizationRejected()
    {
        using var pool = new MockPool();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var client = new StratumClient();
        var jobs = new ConcurrentQueue<MiningJob>();
        var states = new ConcurrentQueue<PoolConnectionUpdate>();
        client.JobReceived += jobs.Enqueue;
        client.ConnectionChanged += states.Enqueue;
        var running = client.RunAsync(CpuMinerTests.Settings(pool.Port), stop.Token);
        try
        {
            await using var peer = await pool.AcceptAsync(stop.Token);
            await peer.SubscribeAsync(stop.Token);
            var auth = await peer.ReadAsync(stop.Token);
            await peer.NotifyAsync("unauthorized", clean: true);
            await peer.SendAsync(new { id = Id(auth), result = false, error = new object?[] { 24, "Unauthorized worker", null } });
            await Check.Eventually(() => states.Any(s => s.Error != null), "Authorization denial was not surfaced.");
            Check.True(!client.IsConnected, "Rejected worker must not be connected.");
            Check.Equal(0, jobs.Count, "Rejected authorization published mining work");
            Check.True(!client.TrySubmit(new MiningShare("unauthorized", "0000", "495fab29", "00000000", 0)), "Disconnected client accepted a share.");
        }
        finally { stop.Cancel(); await ObserveStop(running); }
    }

    private static async Task OversizedMessageReconnect()
    {
        using var pool = new MockPool();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var client = new StratumClient();
        var states = new ConcurrentQueue<PoolConnectionUpdate>();
        var invalidated = 0;
        client.ConnectionChanged += states.Enqueue;
        client.WorkInvalidated += () => Interlocked.Increment(ref invalidated);
        var running = client.RunAsync(CpuMinerTests.Settings(pool.Port), stop.Token);
        try
        {
            await using (var peer = await pool.AcceptAsync(stop.Token))
            {
                await peer.SubscribeAsync(stop.Token);
                var auth = await peer.ReadAsync(stop.Token);
                await peer.SendAsync(new { id = Id(auth), result = true, error = (object?)null });
                await Check.Eventually(() => client.IsConnected, "Connection did not authorize.");
                await peer.SendRawAsync(new string('a', 65537) + "\n");
                await Check.Eventually(() => states.Any(s => s.Error != null), "Oversized frame did not fail.");
                Check.True(Volatile.Read(ref invalidated) > 0, "Connection failure must invalidate work.");
            }
            await using var next = await pool.AcceptAsync(stop.Token);
            var newSubscribe = await next.ReadAsync(stop.Token);
            Check.Equal("mining.subscribe", newSubscribe.GetProperty("method").GetString(), "Reconnect must perform a fresh subscription");
        }
        finally { stop.Cancel(); await ObserveStop(running); }
    }

    private static long Id(JsonElement request) => request.GetProperty("id").GetInt64();

    private static async Task DifficultyAndExtraNonceChanges()
    {
        using var pool = new MockPool();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var client = new StratumClient();
        var jobs = new ConcurrentQueue<MiningJob>();
        var invalidations = 0;
        client.JobReceived += jobs.Enqueue;
        client.WorkInvalidated += () => Interlocked.Increment(ref invalidations);
        var running = client.RunAsync(CpuMinerTests.Settings(pool.Port), stop.Token);
        try
        {
            await using var peer = await pool.AcceptAsync(stop.Token);
            await peer.SubscribeAsync(stop.Token);
            var auth = await peer.ReadAsync(stop.Token);
            await peer.SendAsync(new { id = Id(auth), result = true, error = (object?)null });
            await peer.SendAsync(new { id = (object?)null, method = "mining.set_difficulty", @params = new[] { 2m } });
            await peer.NotifyAsync("difficulty-two", clean: true);
            await Check.Eventually(() => jobs.Count == 1, "First difficulty job missing.");
            var first = jobs.Single();
            Check.Equal(2m, first.Difficulty);
            await peer.SendAsync(new { id = (object?)null, method = "mining.set_difficulty", @params = new[] { 4m } });
            await peer.NotifyAsync("difficulty-four", clean: false);
            await Check.Eventually(() => jobs.Count == 2, "Next difficulty job missing.");
            var second = jobs.Last();
            Check.Equal(2m, first.Difficulty, "Difficulty updates must not mutate already issued work");
            Check.Equal(4m, second.Difficulty);
            Check.True(client.TrySubmit(new MiningShare(first.Id, "0001", first.NTime, "00000001", first.Generation)),
                "clean_jobs=false must preserve valid shares from earlier work.");
            var submit = await peer.ReadAsync(stop.Token);
            Check.Equal("difficulty-two", submit.GetProperty("params")[1].GetString());
            await peer.SendAsync(new { id = Id(submit), result = true, error = (object?)null });

            var invalidationsBefore = Volatile.Read(ref invalidations);
            await peer.SendAsync(new { id = (object?)null, method = "mining.set_extranonce", @params = new object[] { "aabbccdd", 4 } });
            await Check.Eventually(() => Volatile.Read(ref invalidations) > invalidationsBefore, "Extranonce change did not invalidate work.");
            Check.True(!client.TrySubmit(new MiningShare(first.Id, "0002", first.NTime, "00000002", first.Generation)), "Old extranonce must be discarded.");
            Check.True(!client.TrySubmit(new MiningShare(second.Id, "0002", second.NTime, "00000002", second.Generation)), "Latest old-extranonce work must also be discarded.");
            await peer.NotifyAsync("new-extranonce", clean: false);
            await Check.Eventually(() => jobs.Count == 3, "New-extranonce job missing.");
            Check.Equal("aabbccdd", jobs.Last().ExtraNonce1);
            Check.Equal(4, jobs.Last().ExtraNonce2Size);
        }
        finally { stop.Cancel(); await ObserveStop(running); }
    }

    private static async Task SessionConcurrentMetricsAndStop()
    {
        using var pool = new MockPool();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var session = new MiningSession();
        Check.True(!session.Snapshot.IsRunning, "Constructing the session must not start mining.");
        var events = 0;
        session.SnapshotChanged += _ => Interlocked.Increment(ref events);
        await session.StartAsync(CpuMinerTests.Settings(pool.Port), stop.Token);
        session.SetPaused(true, "Prueba de concurrencia");
        try
        {
            await using var peer = await pool.AcceptAsync(stop.Token);
            await peer.SubscribeAsync(stop.Token);
            var auth = await peer.ReadAsync(stop.Token);
            await peer.SendAsync(new { id = Id(auth), result = true, error = (object?)null });
            await Check.Eventually(() => session.Snapshot.IsConnected, "Session did not connect.");
            // Cross at least one periodic metrics tick while the network reader
            // publishes jobs. A paused session exercises locks without mining.
            var sending = Task.Run(async () =>
            {
                for (var i = 0; i < 400; ++i)
                {
                    await peer.NotifyAsync($"concurrent-{i}", clean: true);
                    await Task.Delay(4, stop.Token);
                }
            }, stop.Token);
            var observing = Task.Run(async () =>
            {
                for (var i = 0; i < 400; ++i)
                {
                    Check.True(session.Snapshot.IsPaused, "Automatic job delivery must preserve explicit pause.");
                    await Task.Delay(4, stop.Token);
                }
            }, stop.Token);
            await Task.WhenAll(sending, observing).WaitAsync(TimeSpan.FromSeconds(5));
            Check.True(session.Snapshot.Uptime >= TimeSpan.FromSeconds(1), "No metrics interval completed.");
            Check.True(Volatile.Read(ref events) > 20, "Concurrent job events did not execute.");
            Check.Equal(0L, session.Snapshot.TotalHashes, "Paused session must not hash.");
            await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(1));
            Check.True(!session.Snapshot.IsRunning && !session.Snapshot.IsConnected, "Session stop left mining active.");
            var count = session.Snapshot.TotalHashes;
            await Task.Delay(50);
            Check.Equal(count, session.Snapshot.TotalHashes);
        }
        finally
        {
            stop.Cancel();
            await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task ObserveStop(Task task)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (OperationCanceledException) { }
    }

    private sealed class MockPool : IDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        public MockPool() => listener.Start();
        public async Task<Peer> AcceptAsync(CancellationToken cancellationToken) => new(await listener.AcceptTcpClientAsync(cancellationToken));
        public void Dispose() => listener.Stop();
    }

    private sealed class Peer : IAsyncDisposable
    {
        private readonly TcpClient client;
        private readonly StreamReader reader;
        private readonly NetworkStream stream;
        public Peer(TcpClient client)
        {
            this.client = client;
            stream = client.GetStream();
            reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        }
        public async Task<JsonElement> ReadAsync(CancellationToken cancellationToken)
        {
            var line = await reader.ReadLineAsync(cancellationToken) ?? throw new EndOfStreamException("Miner closed mock connection.");
            using var document = JsonDocument.Parse(line);
            return document.RootElement.Clone();
        }
        public Task SendAsync(object message) => SendRawAsync(JsonSerializer.Serialize(message) + "\n");
        public async Task SendRawAsync(string message) => await stream.WriteAsync(Encoding.UTF8.GetBytes(message));
        public async Task SendFragmentedAsync(object message)
        {
            var text = JsonSerializer.Serialize(message) + "\n";
            await SendRawAsync(text[..7]);
            await Task.Delay(15);
            await SendRawAsync(text[7..]);
        }
        public async Task SubscribeAsync(CancellationToken cancellationToken)
        {
            var subscribe = await ReadAsync(cancellationToken);
            Check.Equal("mining.subscribe", subscribe.GetProperty("method").GetString());
            await SendAsync(new { id = Id(subscribe), result = new object[] { Array.Empty<object>(), "abcd", 2 }, error = (object?)null });
        }
        public Task NotifyAsync(string id, bool clean) => SendAsync(new
        {
            id = (object?)null, method = "mining.notify",
            @params = new object[] { id, new string('0', 64), HashingTests.GenesisCoinbase[..^8], "0000", Array.Empty<string>(), "00000001", "1d00ffff", "495fab29", clean }
        });
        public ValueTask DisposeAsync()
        {
            reader.Dispose();
            client.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
