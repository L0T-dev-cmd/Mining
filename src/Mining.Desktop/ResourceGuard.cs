using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Mining.Desktop;

internal readonly record struct ResourceReading(double? SystemCpuPercent, double? MinerCpuPercent,
    bool OnBattery, bool Busy, string? PauseReason);

/// <summary>
/// Uses the Windows CPU counters, not a thermal estimate. Three busy samples pause mining;
/// five samples below the recovery threshold resume it, avoiding rapid pause/resume cycles.
/// </summary>
internal sealed class ResourceGuard : IDisposable
{
    private readonly Process _process = Process.GetCurrentProcess();
    private ulong _previousIdle, _previousTotal;
    private TimeSpan _previousProcessTime;
    private long _previousTimestamp;
    private int _busySamples, _quietSamples;
    private bool _busy;

    public void Reset()
    {
        _previousIdle = _previousTotal = 0;
        _previousTimestamp = 0;
        _busySamples = _quietSamples = 0;
        _busy = false;
    }

    public ResourceReading Read(bool pauseOnBattery, bool pauseWhenBusy)
    {
        double? systemCpu = null;
        if (GetSystemTimes(out var idleTime, out var kernelTime, out var userTime))
        {
            var idle = idleTime.Value;
            var total = kernelTime.Value + userTime.Value;
            if (_previousTotal != 0 && total > _previousTotal && idle >= _previousIdle)
                systemCpu = Math.Clamp(100.0 * (1 - (double)(idle - _previousIdle) / (total - _previousTotal)), 0, 100);
            _previousIdle = idle;
            _previousTotal = total;
        }

        double? processCpu = null;
        var timestamp = Stopwatch.GetTimestamp();
        var processTime = _process.TotalProcessorTime;
        if (_previousTimestamp != 0)
        {
            var elapsedSeconds = (double)(timestamp - _previousTimestamp) / Stopwatch.Frequency;
            if (elapsedSeconds > 0)
                processCpu = Math.Clamp(100 * (processTime - _previousProcessTime).TotalSeconds /
                                       (elapsedSeconds * Environment.ProcessorCount), 0, 100);
        }
        _previousTimestamp = timestamp;
        _previousProcessTime = processTime;

        if (!pauseWhenBusy)
        {
            _busy = false;
            _busySamples = _quietSamples = 0;
        }
        else if (systemCpu.HasValue)
        {
            _busySamples = systemCpu >= 85 ? _busySamples + 1 : 0;
            _quietSamples = systemCpu <= 65 ? _quietSamples + 1 : 0;
            if (_busySamples >= 3) _busy = true;
            if (_quietSamples >= 5) _busy = false;
        }

        var power = SystemInformation.PowerStatus;
        var onBattery = power.PowerLineStatus == PowerLineStatus.Offline &&
                        !power.BatteryChargeStatus.HasFlag(BatteryChargeStatus.NoSystemBattery);
        var reasons = new List<string>(2);
        if (pauseOnBattery && onBattery) reasons.Add("equipo usando batería");
        if (pauseWhenBusy && _busy) reasons.Add("CPU del equipo ocupada");
        return new(systemCpu, processCpu, onBattery, _busy,
            reasons.Count == 0 ? null : string.Join(" y ", reasons));
    }

    public void Dispose() => _process.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public readonly ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);
}
