#nullable enable

using System.Collections.Generic;
using System.Globalization;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.Pure.Verbs;

/// <summary>What the profiling what is asked to do.</summary>
internal enum ProfilingAction
{
    On,
    Off,
    Reset,
    Report
}

/// <summary>
/// server profiling's call, checked without the game: action (on, off, reset or report, any case), and with on only
/// slow_frame_ms (SlowFrameLimits) and csv. The what's label prefixes every refusal ("server profiling: ...").
/// </summary>
internal sealed class ProfilingOrder
{
    /// <summary>The actions, as a refusal about one lists them.</summary>
    private static readonly string[] Words = { "on", "off", "reset", "report" };

    private ProfilingOrder(ProfilingAction action, double? slowFrameMs, bool? csv)
    {
        Action = action;
        SlowFrameMs = slowFrameMs;
        Csv = csv;
    }

    /// <summary>On, off, reset or report.</summary>
    public ProfilingAction Action { get; }

    /// <summary>
    /// The slow-frame threshold asked for; null keeps the session's (SlowFrameLimits.DefaultMs for a new session). The
    /// catalogue defaults the option, so a call that leaves it out is told apart from one that gives the same number.
    /// </summary>
    public double? SlowFrameMs { get; }

    /// <summary>Start (true) or stop (false) the CSV; null leaves it as it is.</summary>
    public bool? Csv { get; }

    /// <summary>
    /// The order server profiling's options give; null with every problem added. given is the reader they were bound
    /// from, which says whether the call gave slow_frame_ms: the catalogue fills its default in either way.
    /// </summary>
    internal static ProfilingOrder? Of(ServerProfilingOptions options, OptionReader given, List<OptionProblem> problems)
    {
        int before = problems.Count;
        ProfilingAction? action = ActionOf(options.Action, problems);
        double? slowFrameMs = given.Has("slow_frame_ms") ? options.SlowFrameMs : null;
        bool? csv = options.Csv;
        if (action is { } word && word != ProfilingAction.On && (slowFrameMs.HasValue || csv.HasValue))
        {
            if (slowFrameMs.HasValue)
            {
                problems.Add(new OptionProblem("slow_frame_ms", "goes with action: on only"));
            }

            if (csv.HasValue)
            {
                problems.Add(new OptionProblem("csv", "goes with action: on only"));
            }
        }
        else if (slowFrameMs.HasValue && !SlowFrameLimits.Allowed(slowFrameMs.Value))
        {
            problems.Add(new OptionProblem("slow_frame_ms",
                $"is {Number(SlowFrameLimits.MinimumMs)} to {Number(SlowFrameLimits.MaximumMs)}"));
        }

        return problems.Count > before
            ? null
            : new ProfilingOrder(action!.Value, slowFrameMs, csv);
    }

    // action: on, off, reset or report, any case; null with a problem naming the valid words.
    private static ProfilingAction? ActionOf(string? given, List<OptionProblem> problems)
    {
        string word = given?.Trim().ToLowerInvariant() ?? string.Empty;
        switch (word)
        {
            case "on":
                return ProfilingAction.On;
            case "off":
                return ProfilingAction.Off;
            case "reset":
                return ProfilingAction.Reset;
            case "report":
                return ProfilingAction.Report;
            default:
                string words = string.Join(", ", Words);
                problems.Add(word.Length == 0
                    ? new OptionProblem("action", "is required: one of " + words)
                    : new OptionProblem("action", $"'{word}' is one of " + words, NearestNames.Of(word, Words)));
                return null;
        }
    }

    private static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
}
