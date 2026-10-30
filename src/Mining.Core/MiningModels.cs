using System.Threading.Channels;

namespace Mining.Core;

/// <summary>Explicit user settings. No setting enables mining automatically.</summary>
public sealed record MinerSettings
{
    public string Host { get; init; } = "btc.viabtc.io";
    public int Port { get; init; } = 3333;
    public string Worker { get; init; } = "";
    public string Password { get; init; } = "x";
    public int Threads { get; init; } = Math.Max(1, Math.Min(2, Environment.ProcessorCount / 2));
    public int CpuPercent { get; init; } = 25;
    public bool PauseOnBattery { get; init; } = true;
    public bool PauseWhenBusy { get; init; } = true;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) || Host.Length > 253 ||
            Uri.CheckHostName(Host) == UriHostNameType.Unknown || Host.Contains(':'))
            throw new ArgumentException("Introduce un nombre de servidor válido, sin esquema ni puerto.");
        if (Port is < 1 or > 65535)
            throw new ArgumentException("El puerto debe estar entre 1 y 65535.");
        if (string.IsNullOrWhiteSpace(Worker) || Worker.Length > 128 ||
            !Worker.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-') ||
            Worker.IndexOf('.') <= 0 || Worker.EndsWith('.'))
            throw new ArgumentException("El trabajador debe tener el formato cuenta.trabajador (letras, números, punto, guion o guion bajo).");
        if (Password is null || Password.Length > 256 || Password.Any(char.IsControl))
            throw new ArgumentException("La contraseña del trabajador no es válida.");
        if (Threads < 1 || Threads > Math.Max(1, Environment.ProcessorCount))
            throw new ArgumentException($"Selecciona entre 1 y {Environment.ProcessorCount} hilos.");
        if (CpuPercent is < 5 or > 75)
            throw new ArgumentException("El objetivo de uso de CPU debe estar entre 5 % y 75 %.");
    }
}

/// <summary>A Stratum V1 job. Generation identifies the originating connection.</summary>
public sealed record MiningJob(
    string Id,
    string PreviousHash,
    string CoinbasePrefix,
    string CoinbaseSuffix,
    IReadOnlyList<string> MerkleBranches,
    string Version,
    string Bits,
    string NTime,
    bool CleanJobs,
    string ExtraNonce1,
    int ExtraNonce2Size,
    decimal Difficulty,
    long Generation = 0);

/// <summary>Hex fields are ready for mining.submit. Nonce is numeric big endian hex.</summary>
public sealed record MiningShare(
    string JobId,
    string ExtraNonce2,
    string NTime,
    string Nonce,
    long Generation);
