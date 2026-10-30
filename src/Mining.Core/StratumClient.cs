using System.Globalization;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Mining.Core;

public sealed record ShareResult(bool Accepted, string? Reason);

public sealed record PoolConnectionUpdate(
    bool Connected, string State, string? Error = null, bool Reconnecting = false);

/// <summary>
/// Bitcoin Stratum v1 over TCP. Port 443 is also plain Stratum unless a pool
/// explicitly documents TLS; using that port does not enable TLS by itself.
/// The client never follows a pool-supplied redirect to another host.
/// </summary>
public sealed class StratumClient : IAsyncDisposable
{
    public const int MaximumLineBytes = 64 * 1024;
    private const int MaximumPendingShares = 64;
    private readonly CancellationTokenSource _dispose = new();
    private Connection? _current;
    private long _generation;
    private int _running;

    public event Action<MiningJob>? JobReceived;
    public event Action? WorkInvalidated;
    public event Action<ShareResult>? ShareCompleted;
    public event Action<PoolConnectionUpdate>? ConnectionChanged;
    public event Action<string>? LogMessage;

    public bool IsConnected => Volatile.Read(ref _current)?.Authorized == true;
    public int PendingShares
    {
        get
        {
            var connection = Volatile.Read(ref _current);
            if (connection is null) return 0;
            lock (connection.Sync) return connection.Pending.Count;
        }
    }

    /// <summary>Runs until canceled. Calling this method is an explicit start.</summary>
    public async Task RunAsync(MinerSettings settings, CancellationToken cancellationToken)
    {
        settings.Validate();
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("El cliente ya está iniciado.");
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _dispose.Token);
        var token = lifetime.Token;
        var failures = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                Emit(ConnectionChanged, new(false, "Conectando"));
                var connectedAt = DateTimeOffset.UtcNow;
                try
                {
                    await RunConnectionAsync(settings, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception) when (exception is IOException or InvalidDataException or SocketException or
                    TimeoutException or OperationCanceledException or JsonException or FormatException or ArgumentException)
                {
                    var error = DescribeFailure(exception);
                    // Short-lived sessions must also back off, including servers that
                    // repeatedly accept the socket and immediately close it.
                    if (DateTimeOffset.UtcNow - connectedAt > TimeSpan.FromMinutes(1)) failures = 0;
                    var delay = TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures++, 5)));
                    Emit(ConnectionChanged, new(false, "Reconectando", error, true));
                    Emit(LogMessage, $"{error} Reintento en {delay.TotalSeconds:0} s.");
                    await Task.Delay(delay, token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            Volatile.Write(ref _current, null);
            Emit(WorkInvalidated);
            Interlocked.Exchange(ref _running, 0);
            Emit(ConnectionChanged, new(false, "Detenido"));
        }
    }

    /// <summary>
    /// Nonblocking admission to a bounded queue. False means the share was stale,
    /// the connection was unavailable, or the queue was full; it was not sent.
    /// </summary>
    public bool TrySubmit(MiningShare share)
    {
        var connection = Volatile.Read(ref _current);
        if (connection is null) return false;
        lock (connection.Sync)
        {
            if (!connection.Authorized || !connection.ValidJobs.TryGetValue(share.Generation, out var job) ||
                share.JobId != job.Id || share.NTime != job.NTime ||
                !IsHex(share.ExtraNonce2, connection.ExtraNonce2Size * 2) ||
                !IsHex(share.Nonce, 8)) return false;
            return connection.Submissions.Writer.TryWrite(share);
        }
    }

    private async Task RunConnectionAsync(MinerSettings settings, CancellationToken token)
    {
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(settings.Host, settings.Port, token)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(15), token).ConfigureAwait(false);
        using var connection = new Connection(tcp.GetStream(), token);
        Volatile.Write(ref _current, connection);
        Task? writer = null;
        Task? watchdog = null;
        var reader = ReadMessagesAsync(connection);
        try
        {
            await SendAsync(connection, new
            {
                id = 1, method = "mining.subscribe", @params = new[] { "Mining.Desktop/0.1" }
            }).ConfigureAwait(false);
            await connection.Subscribed.Task.WaitAsync(TimeSpan.FromSeconds(15), connection.Token)
                .ConfigureAwait(false);
            await SendAsync(connection, new
            {
                id = 2, method = "mining.authorize", @params = new[] { settings.Worker, settings.Password }
            }).ConfigureAwait(false);
            await connection.Authenticated.Task.WaitAsync(TimeSpan.FromSeconds(15), connection.Token)
                .ConfigureAwait(false);
            lock (connection.Sync)
            {
                connection.Authorized = true;
                Emit(ConnectionChanged, new(true, "Conectado; esperando trabajo"));
                if (connection.Job is not null) Emit(JobReceived, connection.Job);
            }
            Emit(LogMessage, "Suscripción y autorización del worker completadas.");
            writer = WriteSharesAsync(connection, settings.Worker);
            watchdog = WatchPendingAsync(connection);
            var completed = await Task.WhenAny(reader, writer, watchdog).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            throw new IOException("El pool cerró la conexión.");
        }
        finally
        {
            lock (connection.Sync)
            {
                connection.Authorized = false;
                connection.Job = null;
                connection.ValidJobs.Clear();
            }
            Volatile.Write(ref _current, null);
            Emit(WorkInvalidated);
            connection.Cancellation.Cancel();
            connection.Submissions.Writer.TryComplete();
            tcp.Dispose();
            await ObserveShutdownAsync(reader).ConfigureAwait(false);
            if (writer is not null) await ObserveShutdownAsync(writer).ConfigureAwait(false);
            if (watchdog is not null) await ObserveShutdownAsync(watchdog).ConfigureAwait(false);
        }
    }

    private async Task ReadMessagesAsync(Connection connection)
    {
        try
        {
            var lines = new BoundedLineReader(connection.Stream);
            while (true)
            {
                var bytes = await lines.ReadAsync(connection.Token).ConfigureAwait(false);
                if (bytes is null) throw new IOException("El pool cerró la conexión.");
                if (bytes.Length == 0) continue;
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
                var message = document.RootElement;
                if (message.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Respuesta Stratum inválida.");
                if (message.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
                {
                    await HandleNotificationAsync(connection, method.GetString()!, message).ConfigureAwait(false);
                }
                else
                {
                    HandleResponse(connection, message);
                }
            }
        }
        catch (Exception exception)
        {
            // Unblock a handshake immediately if its reader fails, rather than
            // leaving it hanging until the handshake timeout expires.
            connection.Subscribed.TrySetException(exception);
            connection.Authenticated.TrySetException(exception);
            throw;
        }
    }

    private void HandleResponse(Connection connection, JsonElement message)
    {
        if (!message.TryGetProperty("id", out var idElement) || !TryReadId(idElement, out var id)) return;
        var failed = message.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null;
        var hasResult = message.TryGetProperty("result", out var result);
        if (id == 1)
        {
            if (failed || !hasResult || result.ValueKind != JsonValueKind.Array || result.GetArrayLength() < 3)
                throw new InvalidDataException("El pool rechazó la suscripción Stratum.");
            var extraNonce1 = ReadHex(result[1], null, 64);
            var extraNonce2Size = ReadExtraNonceSize(result[2]);
            lock (connection.Sync)
            {
                connection.ExtraNonce1 = extraNonce1;
                connection.ExtraNonce2Size = extraNonce2Size;
                connection.HasSubscription = true;
            }
            connection.Subscribed.TrySetResult(true);
            return;
        }
        if (id == 2)
        {
            if (failed || !hasResult || result.ValueKind != JsonValueKind.True)
                throw new InvalidDataException("El pool rechazó el worker. Revisa el formato cuenta.worker.");
            connection.Authenticated.TrySetResult(true);
            return;
        }
        lock (connection.Sync)
        {
            if (!connection.Pending.Remove(id)) return;
        }
        var accepted = !failed && hasResult && result.ValueKind == JsonValueKind.True;
        // Never log server-provided strings: a server may echo the submitted
        // credentials or send UI control characters in an error message.
        var reason = accepted ? null : ReadSafeErrorCode(error);
        Emit(ShareCompleted, new(accepted, reason));
    }

    private async Task HandleNotificationAsync(Connection connection, string method, JsonElement message)
    {
        if (method == "client.reconnect")
            throw new IOException("El pool solicitó reconectar al servidor configurado.");
        if (method is "client.get_version" or "mining.ping")
        {
            if (message.TryGetProperty("id", out var id) && TryReadId(id, out var requestId))
                await SendAsync(connection, new { id = requestId, result = "Mining.Desktop/0.1", error = (string?)null })
                    .ConfigureAwait(false);
            return;
        }
        if (method is not ("mining.set_difficulty" or "mining.set_extranonce" or "mining.notify")) return;
        if (!message.TryGetProperty("params", out var parameters) || parameters.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Parámetros Stratum inválidos.");
        switch (method)
        {
            case "mining.set_difficulty":
                if (parameters.GetArrayLength() < 1 || parameters[0].ValueKind != JsonValueKind.Number ||
                    !parameters[0].TryGetDecimal(out var difficulty) || difficulty <= 0)
                    throw new InvalidDataException("Dificultad Stratum inválida.");
                // The new difficulty applies to the NEXT job, not to work
                // already being hashed/submitted under the previous target.
                lock (connection.Sync) connection.Difficulty = difficulty;
                break;
            case "mining.set_extranonce":
                if (parameters.GetArrayLength() < 2)
                    throw new InvalidDataException("Extranonce Stratum inválido.");
                var extraNonce1 = ReadHex(parameters[0], null, 64);
                var extraNonce2Size = ReadExtraNonceSize(parameters[1]);
                lock (connection.Sync)
                {
                    connection.ExtraNonce1 = extraNonce1;
                    connection.ExtraNonce2Size = extraNonce2Size;
                    connection.Job = null;
                    connection.ValidJobs.Clear();
                }
                Emit(WorkInvalidated);
                break;
            case "mining.notify":
                if (parameters.GetArrayLength() < 9)
                    throw new InvalidDataException("Trabajo Stratum incompleto.");
                var jobId = ReadIdentifier(parameters[0]);
                var previous = ReadHex(parameters[1], 64);
                var prefix = ReadHex(parameters[2], null, 32000);
                var suffix = ReadHex(parameters[3], null, 32000);
                if (parameters[4].ValueKind != JsonValueKind.Array || parameters[4].GetArrayLength() > 32)
                    throw new InvalidDataException("Ramas Merkle inválidas.");
                var branches = parameters[4].EnumerateArray().Select(x => ReadHex(x, 64)).ToArray();
                var version = ReadHex(parameters[5], 8);
                var bits = ReadHex(parameters[6], 8);
                var time = ReadHex(parameters[7], 8);
                if (parameters[8].ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException("Bandera clean_jobs inválida.");
                lock (connection.Sync)
                {
                    if (!connection.HasSubscription)
                        throw new InvalidDataException("El pool envió trabajo antes de la suscripción.");
                    connection.Job = new MiningJob(jobId, previous, prefix, suffix, branches, version, bits, time,
                        parameters[8].GetBoolean(), connection.ExtraNonce1, connection.ExtraNonce2Size,
                        connection.Difficulty, Interlocked.Increment(ref _generation));
                    if (connection.Job.CleanJobs) connection.ValidJobs.Clear();
                    // Keep in-flight solutions for jobs the pool has not invalidated.
                    // Bound retained work even if a server never sends clean_jobs.
                    if (connection.ValidJobs.Count >= 32)
                        connection.ValidJobs.Remove(connection.ValidJobs.Keys.Min());
                    connection.ValidJobs[connection.Job.Generation] = connection.Job;
                    if (connection.Authorized) Emit(JobReceived, connection.Job);
                }
                break;
        }
    }

    private async Task WriteSharesAsync(Connection connection, string worker)
    {
        await foreach (var share in connection.Submissions.Reader.ReadAllAsync(connection.Token).ConfigureAwait(false))
        {
            long id;
            lock (connection.Sync)
            {
                if (!connection.Authorized || !connection.ValidJobs.ContainsKey(share.Generation) ||
                    connection.Pending.Count >= MaximumPendingShares) continue;
                id = ++connection.NextRequestId;
                connection.Pending[id] = DateTimeOffset.UtcNow;
            }
            await SendAsync(connection, new
            {
                id, method = "mining.submit",
                @params = new[] { worker, share.JobId, share.ExtraNonce2, share.NTime, share.Nonce }
            }).ConfigureAwait(false);
        }
    }

    private static async Task WatchPendingAsync(Connection connection)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(connection.Token).ConfigureAwait(false))
        {
            lock (connection.Sync)
                if (connection.Pending.Values.Any(sent => DateTimeOffset.UtcNow - sent > TimeSpan.FromSeconds(60)))
                    throw new TimeoutException("El pool no confirmó las shares enviadas.");
        }
    }

    private static async Task SendAsync(Connection connection, object message)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
        await connection.WriteLock.WaitAsync(connection.Token).ConfigureAwait(false);
        try
        {
            await connection.Stream.WriteAsync(bytes, connection.Token).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(15), connection.Token).ConfigureAwait(false);
            await connection.Stream.WriteAsync(new byte[] { (byte)'\n' }, connection.Token).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(15), connection.Token).ConfigureAwait(false);
        }
        finally { connection.WriteLock.Release(); }
    }

    private static string ReadIdentifier(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Identificador de trabajo inválido.");
        var value = element.GetString()!;
        if (value.Length is < 1 or > 256 || value.Any(char.IsControl))
            throw new InvalidDataException("Identificador de trabajo inválido.");
        return value;
    }

    private static string ReadHex(JsonElement element, int? exactLength, int maximumLength = 128)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new InvalidDataException("Campo hexadecimal Stratum inválido.");
        var value = element.GetString()!;
        if (value.Length > maximumLength || !IsHex(value, exactLength))
            throw new InvalidDataException("Campo hexadecimal Stratum inválido.");
        return value;
    }

    private static bool IsHex(string value, int? exactLength) =>
        (exactLength is null || value.Length == exactLength) && value.Length % 2 == 0 &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');

    private static int ReadExtraNonceSize(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var size) || size is < 1 or > 16)
            throw new InvalidDataException("Tamaño de extranonce2 no compatible (1–16 bytes).");
        return size;
    }

    private static bool TryReadId(JsonElement element, out long id)
    {
        id = 0;
        return element.ValueKind == JsonValueKind.Number ? element.TryGetInt64(out id) :
            element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), NumberStyles.None,
                CultureInfo.InvariantCulture, out id);
    }

    private static string ReadSafeErrorCode(JsonElement error)
    {
        if (error.ValueKind == JsonValueKind.Array && error.GetArrayLength() > 0 &&
            error[0].ValueKind == JsonValueKind.Number && error[0].TryGetInt32(out var code))
            return $"Share rechazada por el pool (código {code}).";
        return "Share rechazada por el pool.";
    }

    private static string DescribeFailure(Exception exception) => exception switch
    {
        SocketException socket => $"No se pudo conectar con el pool ({socket.SocketErrorCode}).",
        TimeoutException => "El pool no respondió dentro del tiempo permitido.",
        InvalidDataException invalid => invalid.Message,
        JsonException or FormatException or ArgumentException => "El pool envió un mensaje Stratum inválido.",
        OperationCanceledException => "Se interrumpió la conexión con el pool.",
        _ => "Se perdió la conexión con el pool."
    };

    private static async Task ObserveShutdownAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception) { /* The primary task's failure is reported by the caller. */ }
    }

    private static void Emit<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList())
            try { handler(value); } catch (Exception) { /* Observers cannot break a network session. */ }
    }

    private static void Emit(Action? handlers)
    {
        if (handlers is null) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception) { }
    }

    public ValueTask DisposeAsync()
    {
        _dispose.Cancel();
        return ValueTask.CompletedTask;
    }

    private sealed class Connection(NetworkStream stream, CancellationToken token) : IDisposable
    {
        public readonly object Sync = new();
        public readonly NetworkStream Stream = stream;
        public readonly CancellationTokenSource Cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        public readonly SemaphoreSlim WriteLock = new(1, 1);
        public readonly TaskCompletionSource<bool> Subscribed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Authenticated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly Channel<MiningShare> Submissions = Channel.CreateBounded<MiningShare>(new BoundedChannelOptions(64)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false
        });
        public readonly Dictionary<long, DateTimeOffset> Pending = new();
        public readonly Dictionary<long, MiningJob> ValidJobs = new();
        public CancellationToken Token => Cancellation.Token;
        public volatile bool Authorized;
        public bool HasSubscription;
        public string ExtraNonce1 = "";
        public int ExtraNonce2Size;
        public decimal Difficulty = 1m;
        public MiningJob? Job;
        public long NextRequestId = 2;
        public void Dispose()
        {
            // Observe exceptional handshake tasks even if the earlier stage failed.
            _ = Subscribed.Task.Exception;
            _ = Authenticated.Task.Exception;
            Cancellation.Dispose();
            WriteLock.Dispose();
        }
    }

    private sealed class BoundedLineReader(NetworkStream stream)
    {
        private readonly byte[] _buffer = new byte[8192];
        private int _position;
        private int _length;

        public async Task<byte[]?> ReadAsync(CancellationToken token)
        {
            using var line = new MemoryStream();
            while (true)
            {
                if (_position == _length)
                {
                    _length = await stream.ReadAsync(_buffer, token).AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(180), token).ConfigureAwait(false);
                    _position = 0;
                    if (_length == 0)
                    {
                        if (line.Length != 0) throw new InvalidDataException("Mensaje Stratum truncado.");
                        return null;
                    }
                }
                var newline = Array.IndexOf(_buffer, (byte)'\n', _position, _length - _position);
                var end = newline < 0 ? _length : newline;
                var count = end - _position;
                if (line.Length + count > MaximumLineBytes)
                    throw new InvalidDataException("El mensaje del pool supera el límite de 64 KiB.");
                line.Write(_buffer, _position, count);
                _position = newline < 0 ? _length : end + 1;
                if (newline >= 0) return line.ToArray();
            }
        }
    }
}
