#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared;

/// <summary>Which pieces a swap wall or swap frame names: listed ones, or a room's.</summary>
internal abstract class StructureScope
{
    private StructureScope()
    {
    }

    internal sealed class Pieces : StructureScope
    {
        internal Pieces(List<ThingId> ids)
        {
            Ids = ids;
        }

        internal List<ThingId> Ids { get; }
    }

    /// <summary>A room id as find room gives it (Room.RoomId).</summary>
    internal sealed class Room : StructureScope
    {
        internal Room(long id)
        {
            Id = id;
        }

        internal long Id { get; }
    }
}

/// <summary>A swap wall or swap frame run as the planner takes it.</summary>
internal sealed class StructureSwapArguments
{
    /// <summary>The most pieces one run swaps.</summary>
    internal const int MaximumPieces = 4096;

    /// <summary>How many pieces each list of the job's result shows when rows is left out.</summary>
    internal const int DefaultRows = ReplyDefaults.SwapListed;

    internal StructureSwapArguments(string? to, StructureScope scope, PrefabFilter? prefab, ThingId? from,
        StructureSwapOptions options, List<ThingId>? moreFrom = null)
    {
        MoreFrom = moreFrom ?? new List<ThingId>();
        To = to;
        Scope = scope;
        Prefab = prefab;
        From = from;
        RefundTo = options.RefundTo;
        SkipUnmatched = options.SkipUnmatched;
        Limit = options.Limit;
        IncludeNotes = options.IncludeNotes;
    }

    /// <summary>The target prefab name; null for swap frame's default (each frame's own prefab).</summary>
    internal string? To { get; }

    internal StructureScope Scope { get; }

    /// <summary>Only pieces this keeps are selected; null selects every piece in scope.</summary>
    internal PrefabFilter? Prefab { get; }

    /// <summary>The first holder that pays and takes source refunds; null for the player.</summary>
    internal ThingId? From { get; }

    /// <summary>materials' further holders, tried after From for each material.</summary>
    internal List<ThingId> MoreFrom { get; }

    /// <summary>Whether anything is given back (refund_to not none).</summary>
    internal bool Refund => RefundTo.GivesBack;

    /// <summary>refund_to: where the refund goes.</summary>
    internal RefundRoute RefundTo { get; }

    internal bool SkipUnmatched { get; }

    /// <summary>How many pieces each list shows: rows for the job's result, every piece for a report.</summary>
    internal int Limit { get; }

    /// <summary>include notes: the reply carries the swap's fixed explanations.</summary>
    internal bool IncludeNotes { get; }
}

internal sealed class StructureSwapOptions
{
    internal StructureSwapOptions(RefundRoute refundTo, bool skipUnmatched, int limit, bool includeNotes = false)
    {
        RefundTo = refundTo;
        SkipUnmatched = skipUnmatched;
        Limit = limit;
        IncludeNotes = includeNotes;
    }

    internal RefundRoute RefundTo { get; }

    internal bool SkipUnmatched { get; }

    internal int Limit { get; }

    internal bool IncludeNotes { get; }
}

/// <summary>
/// How a swap wall or swap frame call names its pieces: by id or by room, exactly one. Both is a problem; neither is
/// missing_target.
/// </summary>
internal static class StructureScopes
{
    internal const string MissingTarget = "missing_target";

    /// <summary>into, when given, names a prefab: a blank one is a problem.</summary>
    internal static void Into(string? into, List<OptionProblem> problems)
    {
        if (into != null && into.Trim().Length == 0)
        {
            problems.Add(new OptionProblem("into", "is a prefab's name"));
        }
    }

    /// <summary>
    /// True when the call names its pieces by id (the caller reads them); false with room set for a room, or with a
    /// problem added or missing set.
    /// </summary>
    internal static bool ByIds(Selector selector, string label, string noun, List<OptionProblem> problems,
        out StructureScope.Room? room, out VerbError? missing)
    {
        room = null;
        missing = null;
        bool ids = selector.Ids != null;
        if (ids && selector.Room != null)
        {
            problems.Add(new OptionProblem("room", $"goes without id: name the {noun}s by id or by room"));
            return false;
        }

        if (ids)
        {
            return true;
        }

        if (selector.Room == null)
        {
            missing = new VerbError(MissingTarget, $"{label}: name the {noun}s by id, or every {noun} around a room.",
                new JObject { ["options"] = new JArray("id", "room") });
            return false;
        }

        if (ThingId.TryRead(new JValue(selector.Room), out ThingId read))
        {
            room = new StructureScope.Room(read.Value);
        }
        else
        {
            problems.Add(new OptionProblem("room", "is a room's id, as find room gives it"));
        }

        return false;
    }
}
