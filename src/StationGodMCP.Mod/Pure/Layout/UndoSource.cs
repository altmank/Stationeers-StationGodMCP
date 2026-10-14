#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Pure;

/// <summary>
/// Where a place or remove job took its materials and put its refund, as its request named them: materials (null:
/// the player), free (build structure without materials), refund_to (a single word or a chain of targets) and refund
/// (the run tools' refund flag).
/// </summary>
internal sealed class JobSource
{
    internal JobSource(long? fromId, bool free, RefundRoute? refundTo, bool? refund)
    {
        FromId = fromId;
        Free = free;
        RefundTo = refundTo;
        Refund = refund;
    }

    /// <summary>A job whose request was not recorded.</summary>
    internal static JobSource Unknown => new JobSource(null, false, null, null);

    internal long? FromId { get; }

    internal bool Free { get; }

    internal RefundRoute? RefundTo { get; }

    internal bool? Refund { get; }

    /// <summary>Whether the job gave nothing back for what it took down (refund_to none, refund false).</summary>
    internal bool RefundedNothing => RefundTo is { GivesBack: false } || Refund == false;
}

/// <summary>
/// The source undo's own calls use: materials (the caller's, else the job's own recorded source, so an undo runs on
/// a dedicated server whenever the job did), and the removals' refund_to (the caller's; else none when the job placed
/// for free, since nothing was paid that could be given back; else each tool's default chain). The caller naming the
/// player is not the same as leaving materials out: only the job's own recorded source can stand in for it.
/// </summary>
internal sealed class UndoSource
{
    private UndoSource(long? fromId, RefundRoute? refundTo, List<string> notes)
    {
        FromId = fromId;
        RefundTo = refundTo;
        Notes = notes;
    }

    /// <summary>Every call's materials; null is the player.</summary>
    internal long? FromId { get; }

    /// <summary>The removals' refund_to; null leaves it out (each tool's default chain).</summary>
    internal RefundRoute? RefundTo { get; }

    internal List<string> Notes { get; }

    /// <param name="job">The job's own recorded source (JobSource).</param>
    /// <param name="callerId">The holder the caller named (materials): a thing by id, or null for the player.</param>
    /// <param name="callerGiven">Whether the caller named materials at all.</param>
    /// <param name="refundTo">The caller's refund_to; null when it gave none.</param>
    internal static UndoSource Of(JobSource job, long? callerId, bool callerGiven, RefundRoute? refundTo)
    {
        List<string> notes = new List<string>();
        long? from = callerGiven ? callerId : job.FromId;
        if (callerGiven)
        {
            notes.Add(callerId.HasValue
                ? $"materials {callerId.Value}: the caller's source pays and takes the refunds" + Instead(job)
                : "materials player: the player pays and takes the refunds" + Instead(job));
        }
        else if (job.FromId.HasValue)
        {
            notes.Add($"materials {job.FromId.Value}: the job's own source pays and takes the refunds.");
        }

        if (refundTo != null)
        {
            notes.Add($"{Vocabulary.Arg("refund_to")} {refundTo.Name}: the caller's choice for the removals' refund.");
        }

        RefundRoute? refund = refundTo;
        if (refund == null && job.Free)
        {
            refund = RefundRoute.Nothing;
            notes.Add(Vocabulary.Text("The job placed for {arg:free} ({arg:free}: true): removing what it built " +
                "gives nothing back ({arg:refund_to} none)."));
        }

        if (job.RefundedNothing)
        {
            notes.Add("The job gave nothing back for what it removed; building it again is paid as usual.");
        }

        return new UndoSource(from, refund, notes);
    }

    // What the caller's own materials choice stands in place of: the job's own recorded source, when it has one.
    private static string Instead(JobSource job) =>
        job.FromId.HasValue ? $" (instead of the job's own, {job.FromId.Value})." : string.Empty;
}
