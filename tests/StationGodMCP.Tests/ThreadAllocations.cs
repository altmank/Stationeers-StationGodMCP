#nullable enable

using System;

namespace StationGodMCP.Tests;

/// <summary>
/// The bytes a piece of work allocates on the calling thread (GC.GetAllocatedBytesForCurrentThread).
/// <para>
/// The counter counts a thread's whole allocation buffer as allocated when the thread takes it, less the unused end
/// when a collection hands the buffer back. A background collection (another test's, in a parallel run) that
/// suspends the thread mid-measurement can retire a part-used buffer without taking the unused end back off, and the
/// work then reads as thousands of bytes it never asked for. So a blocking gen0 collection retires this thread's
/// buffer first: work that allocates nothing never takes a new one, a collection during it has nothing to miscount, and
/// work that allocates is counted to the byte.
/// </para>
/// </summary>
internal static class ThreadAllocations
{
    internal static long Of(Action work)
    {
        GC.Collect(0, GCCollectionMode.Forced, blocking: true);
        long before = GC.GetAllocatedBytesForCurrentThread();
        work();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
