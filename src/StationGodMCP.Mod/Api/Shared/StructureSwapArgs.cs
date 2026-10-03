#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared;

/// <summary>Which pieces a replace_walls or replace_frames request names: listed ones, or a room's.</summary>
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

    /// <summary>A room id as the rooms tool reports it (Room.RoomId).</summary>
    internal sealed class Room : StructureScope
    {
        internal Room(long id)
        {
            Id = id;
        }

        internal long Id { get; }
    }
}

/// <summary>One request of replace_walls or replace_frames: a job to poll, or a run (dry or confirmed).</summary>
internal abstract class StructureSwapForm
{
    private StructureSwapForm()
    {
    }

    internal sealed class Poll : StructureSwapForm
    {
        internal Poll(string jobId)
        {
            JobId = jobId;
        }

        internal string JobId { get; }
    }

    internal sealed class Run : StructureSwapForm
    {
        internal Run(StructureSwapArguments arguments, bool confirmed)
        {
            Arguments = arguments;
            Confirmed = confirmed;
        }

        internal StructureSwapArguments Arguments { get; }

        /// <summary>dry_run false and confirm true: start a job. Otherwise a dry run.</summary>
        internal bool Confirmed { get; }
    }
}

/// <summary>A run's arguments as parsed.</summary>
internal sealed class StructureSwapArguments
{
    internal StructureSwapArguments(string? to, StructureScope scope, List<string>? fromPrefabs, ThingId? from,
        StructureSwapOptions options)
    {
        To = to;
        Scope = scope;
        FromPrefabs = fromPrefabs;
        From = from;
        RefundTo = options.RefundTo;
        SkipUnmatched = options.SkipUnmatched;
        Limit = options.Limit;
    }

    /// <summary>The target prefab name; null for replace_frames' default (each frame's own prefab).</summary>
    internal string? To { get; }

    internal StructureScope Scope { get; }

    /// <summary>Only pieces of these prefabs are selected; null selects every piece in scope.</summary>
    internal List<string>? FromPrefabs { get; }

    /// <summary>The thing materials come from and refunds go to; null for the player.</summary>
    internal ThingId? From { get; }

    /// <summary>Whether anything is given back (refund_to not none, refund not false).</summary>
    internal bool Refund => RefundTo.GivesBack;

    /// <summary>refund_to (with the refund flag): where the refund goes.</summary>
    internal RefundRoute RefundTo { get; }

    internal bool SkipUnmatched { get; }

    internal int Limit { get; }
}

internal sealed class StructureSwapOptions
{
    internal StructureSwapOptions(RefundRoute refundTo, bool skipUnmatched, int limit)
    {
        RefundTo = refundTo;
        SkipUnmatched = skipUnmatched;
        Limit = limit;
    }

    internal RefundRoute RefundTo { get; }

    internal bool SkipUnmatched { get; }

    internal int Limit { get; }
}

/// <summary>
/// replace_walls' and replace_frames' request forms, as upgrade_cables': a dry run (default), a confirmed run
/// (dry_run false and confirm true), or job_id alone. Exactly one of reference_ids and room_id names the pieces.
/// </summary>
internal static class StructureSwapArgs
{
    internal const int MaximumPieces = 4096;
    internal const int DefaultLimit = 200;
    internal const int MaximumPrefabs = 64;

    private static readonly string[] RunArguments =
    {
        "to", "reference_ids", "room_id", "from_prefabs", "dry_run", "confirm", "from_id", "refund",
        "refund_to", "skip_unmatched", "limit"
    };

    internal static StructureSwapForm Parse(Args args, bool targetRequired)
    {
        if (args.Has("job_id"))
        {
            args.Reject("job_id", RunArguments);
            return new StructureSwapForm.Poll(args.String("job_id").Trim());
        }

        bool dryRun = args.OptionalBool("dry_run") ?? true;
        bool confirm = args.OptionalBool("confirm") ?? false;
        if (dryRun && confirm)
        {
            throw ApiErrors.InvalidArgument("confirm: true needs dry_run: false; nothing was changed.");
        }

        if (!dryRun && !confirm)
        {
            throw ApiErrors.Refused("confirm_required",
                "A real run needs dry_run: false and confirm: true; nothing was changed.");
        }

        StructureSwapArguments arguments = new StructureSwapArguments(Target(args, targetRequired), Scope(args),
            FromPrefabs(args), args.OptionalThingId("from_id"),
            new StructureSwapOptions(RefundArgs.RouteWithFlag(args), args.OptionalBool("skip_unmatched") ?? false,
                args.OptionalInt("limit", 1, MaximumPieces) ?? DefaultLimit));
        return new StructureSwapForm.Run(arguments, !dryRun);
    }

    private static string? Target(Args args, bool required)
    {
        string? to = args.OptionalString("to")?.Trim();
        if (to != null && to.Length == 0)
        {
            throw ApiErrors.InvalidArgument("Argument 'to' must be a prefab name.");
        }

        return to == null && required
            ? throw ApiErrors.InvalidArgument("Argument 'to' is required: the prefab name of the new piece.")
            : to;
    }

    private static StructureScope Scope(Args args)
    {
        bool pieces = args.Has("reference_ids");
        if (pieces == args.Has("room_id"))
        {
            throw ApiErrors.InvalidArgument("Pass reference_ids or room_id (from rooms), exactly one of them.");
        }

        if (pieces)
        {
            return new StructureScope.Pieces(args.ThingIds("reference_ids", MaximumPieces));
        }

        return ThingId.TryRead(args.Optional("room_id"), out ThingId room)
            ? new StructureScope.Room(room.Value)
            : throw ApiErrors.InvalidArgument("Argument 'room_id' must be a room id as rooms reports it.");
    }

    private static List<string>? FromPrefabs(Args args)
    {
        if (!args.Has("from_prefabs"))
        {
            return null;
        }

        JArray array = args.Array("from_prefabs", MaximumPrefabs);
        List<string> names = new List<string>(array.Count);
        for (int index = 0; index < array.Count; index++)
        {
            string? name = array[index].Type == JTokenType.String ? ((string?)array[index])?.Trim() : null;
            if (string.IsNullOrEmpty(name))
            {
                throw ApiErrors.InvalidArgument($"from_prefabs[{index}] must be a prefab name.");
            }

            names.Add(name!);
        }

        return names;
    }
}
