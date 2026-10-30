using System.Text.Json;
using Mining.Core;

namespace Mining.Desktop;

internal sealed record DesktopSettings
{
    public string Worker { get; init; } = "";
    public string Host { get; init; } = "btc.viabtc.io";
    public int Port { get; init; } = 3333;
    public int Threads { get; init; } = Math.Max(1, Math.Min(2, Environment.ProcessorCount / 2));
    public int CpuPercent { get; init; } = 25;
    public bool PauseOnBattery { get; init; } = true;
    public bool PauseWhenBusy { get; init; } = true;

    public MinerSettings ToMinerSettings() => new()
    {
        Worker = Worker.Trim(), Host = Host.Trim(), Port = Port, Password = "x",
        Threads = Threads, CpuPercent = CpuPercent,
        PauseOnBattery = PauseOnBattery, PauseWhenBusy = PauseWhenBusy
    };
}

internal static class SettingsStore
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "MineriaBitcoin", "settings.json");

    public static DesktopSettings Load(out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(FilePath)) return new();
            if (new FileInfo(FilePath).Length > 16384) throw new InvalidDataException();
            var settings = JsonSerializer.Deserialize<DesktopSettings>(File.ReadAllText(FilePath))
                           ?? throw new InvalidDataException();
            // Allow a blank worker while setting up; core performs full validation before mining.
            if (settings.Worker is null || settings.Host is null || settings.Host.Length > 253
                || settings.Worker.Length > 128 || settings.CpuPercent is < 5 or > 75
                || settings.Threads < 1 || settings.Port is < 1 or > 65535)
                throw new InvalidDataException();
            return settings with { Threads = Math.Min(settings.Threads, Environment.ProcessorCount) };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
        {
            warning = "No se pudo leer la configuración guardada. Se usarán valores iniciales; revisa tu trabajador.";
            return new();
        }
    }

    public static void Save(DesktopSettings settings)
    {
        var directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
