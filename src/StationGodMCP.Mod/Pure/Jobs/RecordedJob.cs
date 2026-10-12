#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure.Jobs;

/// <summary>What JobSnapshots keeps of one job: the tool, the snapshots of what it removes, and its source.</summary>
internal sealed class RecordedJob
{
    internal RecordedJob(string tool, Dictionary<long, ThingSnapshot> removed, JobSource source)
    {
        Tool = tool;
        Removed = removed;
        Source = source;
    }

    internal string Tool { get; }

    internal Dictionary<long, ThingSnapshot> Removed { get; }

    internal JobSource Source { get; }
}

/// <summary>
/// The estimated bytes of what KeptJobs holds per job, taken once as an entry is stored: deterministic, growing with
/// the entry's size, and cheap. Not exact: each constant rounds up the managed objects behind one part on a 64-bit
/// runtime.
/// <list type="bullet">
/// <item>A snapshot (RecordedJob): JobBytes, plus ThingBytes for each thing it removes (the ThingSnapshot and its
/// dictionary slot; prefab names, tools, grades and turns are shared, so not counted), plus TextBytes and two bytes a
/// character for a label, plus PieceBytes and CellBytes a cell for a cable, pipe or chute piece.</item>
/// <item>A final status (the view its polls answer): FinalBytes, plus two bytes for each character of the view as
/// polls write it (ApiJson.Length): its ids, names and lists sit in memory at about one to two bytes a character
/// of their JSON.</item>
/// </list>
/// So a removal of 50 things costs about 7 KB, and one of 2,000 about 260 KB (550 KB when they are network pieces); a
/// typical final status 2 to 8 KB, and one of a 500-piece run (about 44 KB of JSON, most of it placed) about 90 KB.
/// </summary>
internal static class KeptJobSizes
{
    /// <summary>The RecordedJob, its dictionary, its source and the store's entry, key and node.</summary>
    internal const long JobBytes = 512;

    internal const long ThingBytes = 128;

    /// <summary>A string's header and length, beside two bytes a character.</summary>
    internal const long TextBytes = 24;

    /// <summary>The NetworkPiece and its list of cells.</summary>
    internal const long PieceBytes = 96;

    internal const long CellBytes = 48;

    /// <summary>The store's entry, key and node, beside the view's characters.</summary>
    internal const long FinalBytes = 256;

    internal static long Snapshot(RecordedJob job)
    {
        long bytes = JobBytes;
        foreach (ThingSnapshot thing in job.Removed.Values)
        {
            bytes += ThingBytes;
            if (thing.Label != null)
            {
                bytes += TextBytes + 2L * thing.Label.Length;
            }

            if (thing.Piece != null)
            {
                bytes += PieceBytes + CellBytes * thing.Piece.Cells.Count;
            }
        }

        return bytes;
    }

    /// <summary>A final status whose view polls write in jsonCharacters characters.</summary>
    internal static long Final(long jsonCharacters) => FinalBytes + 2L * jsonCharacters;
}
