#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// Where a place or remove job took its materials and put its refund, as its request named them: from_id (null: the
/// local player), free (place_structure without materials), refund_to (remove_structure: source, ground or none) and
/// refund (the run tools' refund flag).
/// </summary>
internal sealed class JobSource
{
    internal JobSource(long? fromId, bool free, string? refundTo, bool? refund)
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

    internal string? RefundTo { get; }

    internal bool? Refund { get; }

    /// <summary>Whether the job gave nothing back for what it took down (refund_to none, refund false).</summary>
    internal bool RefundedNothing => RefundTo == "none" || Refund == false;
}

/// <summary>
/// The source undo_job's own calls use: from_id (the caller's, else the job's own, so an undo runs on a dedicated
/// server whenever the job did), and the removal's refund_to (the caller's; else none when the job placed for free,
/// since nothing was paid that could be given back; else the removal's default, the source).
/// </summary>
internal sealed class UndoSource
{
    private UndoSource(long? fromId, string? refundTo, List<string> notes)
    {
        FromId = fromId;
        RefundTo = refundTo;
        Notes = notes;
    }

    /// <summary>Every call's from_id; null leaves it out (the local player).</summary>
    internal long? FromId { get; }

    /// <summary>The removal's refund_to; null leaves it out (source).</summary>
    internal string? RefundTo { get; }

    internal List<string> Notes { get; }

    internal static UndoSource Of(JobSource job, long? fromId, string? refundTo)
    {
        List<string> notes = new List<string>();
        long? from = fromId ?? job.FromId;
        if (!fromId.HasValue && job.FromId.HasValue)
        {
            notes.Add($"from_id {job.FromId.Value}: the job's own source pays and takes the refunds.");
        }

        string? refund = refundTo;
        if (refund == null && job.Free)
        {
            refund = "none";
            notes.Add("The job placed for free (free: true): removing what it built gives nothing back " +
                      "(refund_to none).");
        }

        if (job.RefundedNothing)
        {
            notes.Add("The job gave nothing back for what it removed; building it again is paid as usual.");
        }

        return new UndoSource(from, refund, notes);
    }
}
