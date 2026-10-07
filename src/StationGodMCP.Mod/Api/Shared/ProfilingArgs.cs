#nullable enable

using System.Globalization;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.Api.Shared;

/// <summary>What the profiling method is asked to do.</summary>
internal enum ProfilingAction
{
    On,
    Off,
    Reset,
    Report
}

/// <summary>
/// The profiling method's arguments, checked: action (on, off, reset or report, any case), and with on only,
/// slow_frame_ms (SlowFrameLimits) and csv. A value that breaks a rule is invalid_argument naming it.
/// </summary>
internal sealed class ProfilingRequest
{
    private ProfilingRequest(ProfilingAction action, double? slowFrameMs, bool? csv)
    {
        Action = action;
        SlowFrameMs = slowFrameMs;
        Csv = csv;
    }

    internal ProfilingAction Action { get; }

    /// <summary>The slow-frame threshold asked for; null keeps the session's (SlowFrameLimits.DefaultMs for a new one).</summary>
    internal double? SlowFrameMs { get; }

    /// <summary>Start (true) or stop (false) the CSV; null leaves it as it is.</summary>
    internal bool? Csv { get; }

    internal static ProfilingRequest Of(Args args)
    {
        ProfilingAction action = (args.OptionalString("action")?.Trim().ToLowerInvariant()) switch
        {
            "on" => ProfilingAction.On,
            "off" => ProfilingAction.Off,
            "reset" => ProfilingAction.Reset,
            "report" => ProfilingAction.Report,
            _ => throw ApiErrors.InvalidArgument("Argument 'action' must be on, off, reset or report.")
        };
        double? slowFrameMs = args.OptionalDouble("slow_frame_ms");
        bool? csv = args.OptionalBool("csv");
        if (action != ProfilingAction.On && (slowFrameMs.HasValue || csv.HasValue))
        {
            throw ApiErrors.InvalidArgument("Arguments 'slow_frame_ms' and 'csv' go with action on only.");
        }

        if (slowFrameMs.HasValue && !SlowFrameLimits.Allowed(slowFrameMs.Value))
        {
            throw ApiErrors.InvalidArgument(
                $"Argument 'slow_frame_ms' must be from {Number(SlowFrameLimits.MinimumMs)} to {Number(SlowFrameLimits.MaximumMs)}.");
        }

        return new ProfilingRequest(action, slowFrameMs, csv);
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
