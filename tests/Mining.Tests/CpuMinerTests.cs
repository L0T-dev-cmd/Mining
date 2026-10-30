using System.Diagnostics;
using Mining.Core;

namespace Mining.Tests;

internal static class CpuMinerTests
{
    internal static MinerSettings Settings(int port = 3333) => new()
    {
        Host = "127.0.0.1", Port = port, Worker = "test.worker", Password = "x",
        Threads = 1, CpuPercent = 5, PauseOnBattery = false, PauseWhenBusy = false
    };

    public static IEnumerable<(string, Func<Task>)> Cases()
    {
        yield return ("CPU requires work and respects pause, resume and stop", async () =>
        {
            var miner = new CpuMiner();
            try
            {
                miner.Start(Settings());
                await Task.Delay(60);
                Check.Equal(0L, miner.TotalHashes, "No pool job means no hashing");
                miner.SetPaused(true);
                miner.SetJob(HashingTests.GenesisJob(difficulty: decimal.MaxValue));
                await Task.Delay(60);
                Check.Equal(0L, miner.TotalHashes, "Paused worker must not hash a new job");
                miner.SetPaused(false);
                await Check.Eventually(() => miner.TotalHashes > 0, "Worker did not resume.");
                miner.SetPaused(true);
                await Task.Delay(100);
                var pausedCount = miner.TotalHashes;
                await Task.Delay(100);
                Check.Equal(pausedCount, miner.TotalHashes, "Hashing must remain paused");
                var watch = Stopwatch.StartNew();
                await miner.StopAsync();
                Check.True(watch.Elapsed < TimeSpan.FromSeconds(1), "Stop should interrupt throttling promptly.");
                Check.True(!miner.IsRunning, "Miner must report stopped.");
                var stoppedCount = miner.TotalHashes;
                await Task.Delay(50);
                Check.Equal(stoppedCount, miner.TotalHashes, "Stopped worker continued hashing");
            }
            finally { await miner.StopAsync(); }
        });
        yield return ("CPU stops promptly when the share queue is full", async () =>
        {
            var miner = new CpuMiner();
            try
            {
                miner.Start(Settings());
                miner.SetJob(HashingTests.GenesisJob(generation: 7, difficulty: 0.0000000000000000000000000001m));
                await Check.Eventually(() => miner.TotalHashes > 256, "Easy synthetic job did not execute.");
                Check.True(miner.Shares.CanCount && miner.Shares.Count == 64, "Synthetic shares must fill, but never exceed, the bounded queue.");
                Check.True(miner.DroppedShares > 0, "Overflow must be recorded without blocking hashing.");
                var watch = Stopwatch.StartNew();
                await miner.StopAsync();
                Check.True(watch.Elapsed < TimeSpan.FromSeconds(1), "A full queue must never block stop.");
                var shares = new List<MiningShare>();
                while (miner.Shares.TryRead(out var share)) shares.Add(share);
                Check.Equal(0, shares.Count, "Stop must clear outstanding shares.");
            }
            finally { await miner.StopAsync(); }
        });
        yield return Check.Sync("Settings reject invalid resource and account values", () =>
        {
            Check.Throws<ArgumentException>(() => (Settings() with { Worker = "" }).Validate());
            Check.Throws<ArgumentException>(() => (Settings() with { Port = 0 }).Validate());
            Check.Throws<ArgumentException>(() => (Settings() with { Threads = 0 }).Validate());
            Check.Throws<ArgumentException>(() => (Settings() with { CpuPercent = 0 }).Validate());
        });
    }
}
