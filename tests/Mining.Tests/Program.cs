using System.Diagnostics;

namespace Mining.Tests;

internal static class Program
{
    public static async Task<int> Main()
    {
        var tests = HashingTests.Cases().Concat(CpuMinerTests.Cases()).Concat(StratumTests.Cases()).ToArray();
        var failed = 0;
        foreach (var (name, test) in tests)
        {
            var timer = Stopwatch.StartNew();
            try
            {
                await test().WaitAsync(TimeSpan.FromSeconds(15));
                Console.WriteLine($"PASS {name} ({timer.ElapsedMilliseconds} ms)");
            }
            catch (Exception error)
            {
                failed++;
                Console.Error.WriteLine($"FAIL {name}: {error}");
            }
        }
        Console.WriteLine($"{tests.Length - failed} passed; {failed} failed; {tests.Length} executed.");
        return failed == 0 ? 0 : 1;
    }
}

internal static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message ?? "Values differ"}. Expected: {expected}; actual: {actual}");
    }

    public static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException($"Expected {typeof(T).Name}.");
    }

    public static async Task Eventually(Func<bool> condition, string message, int timeoutMilliseconds = 3000)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.ElapsedMilliseconds >= timeoutMilliseconds) throw new TimeoutException(message);
            await Task.Delay(15);
        }
    }

    public static (string, Func<Task>) Sync(string name, Action test) => (name, () => { test(); return Task.CompletedTask; });
}
