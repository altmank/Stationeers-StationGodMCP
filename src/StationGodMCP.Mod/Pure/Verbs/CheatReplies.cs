#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// remove terrain's report: the dig's reading as its body, and the box as its one target, [box, terrain, voxels to dig
/// -> 0] with the what's undo mark.
/// </summary>
internal static class TerrainDigReplies
{
    internal const string TargetName = "terrain";

    internal static ReportReply Reported(TerrainDigView reading, string undoMark) =>
        Report.Of(new[] { new ReportTarget(null, reading.Box, TargetName, reading.Dug, 0, undoMark) }, reading);
}

/// <summary>
/// write health's report: the player's parts as they read now, each part a target [id, part, damage % now -> 0] with
/// the what's undo mark; healed is left out, since nothing was healed.
/// </summary>
internal static class HealReplies
{
    internal static ReportReply Reported(HealView reading, string undoMark)
    {
        List<ReportTarget> targets = new List<ReportTarget>(reading.Parts.Count);
        foreach (HealedPartView part in reading.Parts)
        {
            targets.Add(new ReportTarget(part.Thing.ReferenceId.ToString(), null, part.Part,
                part.DamagePercentBefore, part.DamagePercentAfter, undoMark));
        }

        return Report.Of(targets, reading);
    }

    /// <summary>Whether every part reads no damage of any kind.</summary>
    internal static bool AllWhole(IReadOnlyList<HealedPartView> parts)
    {
        foreach (HealedPartView part in parts)
        {
            if (!part.Whole)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// build terrain's report: the fill's reading as its body, and the box as its one target, [box, terrain, as it is ->
/// N filled, M dug] with the what's undo mark.
/// </summary>
internal static class TerrainFillReplies
{
    internal static ReportReply Reported(TerrainFillView reading, string undoMark) =>
        Report.Of(new[]
        {
            new ReportTarget(null, reading.Box, TerrainDigReplies.TargetName, "as it is",
                $"{reading.Filled} filled, {reading.Dug} dug", undoMark)
        }, reading);
}

/// <summary>
/// build item's report: the spawn's reading as its body (placed empty, each stack's id null), and each stack a target
/// [holder id or the point, item, nothing -> quantity] with the what's undo mark.
/// </summary>
internal static class ItemSpawnReplies
{
    internal static ReportReply Reported(ItemSpawnView reading, string undoMark)
    {
        List<ReportTarget> targets = new List<ReportTarget>(reading.Items.Count);
        foreach (PlacedView stack in reading.Items)
        {
            string? holder = stack.Slot?.Id.ToString() ?? reading.To?.ReferenceId.ToString();
            object? cell = holder == null && reading.At is { } at ? new[] { at.X, at.Y, at.Z } : null;
            targets.Add(new ReportTarget(holder, cell, reading.PrefabName, null, stack.Quantity, undoMark));
        }

        return Report.Of(targets, reading);
    }
}
