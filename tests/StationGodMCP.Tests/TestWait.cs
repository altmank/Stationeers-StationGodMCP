#nullable enable

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace StationGodMCP.Tests;

/// <summary>
/// The one way a test waits for something another thread does. The limit is a safety net so a broken test ends, not a
/// speed check: under the thread-pool load of the whole parallel suite a continuation can run seconds late, so the
/// default is generous and a test that wants "no sooner than" or "at most" asserts that where the clock starts.
/// </summary>
internal static class TestWait
{
    /// <summary>How long a wait may take before the test gives up.</summary>
    internal static readonly TimeSpan DefaultLimit = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(10);

    internal static void Until(Func<bool> condition, string what, TimeSpan? limit = null)
    {
        TimeSpan bound = limit ?? DefaultLimit;
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > bound)
            {
                throw new TimeoutException($"{what} never held within {bound}");
            }

            Thread.Sleep(Poll);
        }
    }

    internal static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? limit = null)
    {
        TimeSpan bound = limit ?? DefaultLimit;
        Stopwatch watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > bound)
            {
                throw new TimeoutException($"{what} never held within {bound}");
            }

            await Task.Delay(Poll);
        }
    }
}
