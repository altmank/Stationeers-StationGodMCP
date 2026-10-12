#nullable enable

using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace StationGodMCP.Tests;

/// <summary>
/// Many tests block a pool thread while they wait (a pipe client's Next, a fake main thread's frames), and the test
/// classes run in parallel. The pool adds threads past its minimum only about one a second, so on a busy machine a
/// pipe server's accept or a connection's reply could wait seconds for a thread, past a test's wait for it. A minimum
/// well above what the suite blocks at once starts that work at once.
/// </summary>
internal static class ThreadPoolSetup
{
    private const int MinimumThreads = 256;

    [ModuleInitializer]
    internal static void StartPoolWorkWithoutWaitingForInjection()
    {
        ThreadPool.GetMinThreads(out int workers, out int ports);
        ThreadPool.SetMinThreads(Math.Max(workers, MinimumThreads), Math.Max(ports, MinimumThreads));
    }
}
