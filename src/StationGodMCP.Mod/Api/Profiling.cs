#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// profiling: switches the runtime profiler on or off, clears its session, or reads its report (ProfilingControl).
/// Changes nothing in the world; on the host only, where requests run.
/// </summary>
internal static class ProfilingApi
{
    internal static ProfilingView Handle(Args args) => ProfilingControl.Run(ProfilingRequest.Of(args));
}
