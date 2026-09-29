#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// What a build or remove report shows as refunded: with refund off nothing comes back, so every refund list is empty
/// and every refund count 0, dry run or not, and a client summing refunds counts only what will be delivered. A swap
/// (replace_walls, replace_frames) is the exception in part: the old piece's materials still pay toward the new one,
/// as a merge placement does, so its refund stays listed as that offset; only its give-back (a negative net) goes.
/// </summary>
internal static class RefundShown
{
    internal static List<T> Items<T>(bool refundEnabled, List<T> refund) =>
        refundEnabled ? refund : new List<T>();

    internal static int Count(bool refundEnabled, int refund) => refundEnabled ? refund : 0;

    /// <summary>A swap's cost less its refund: negative (given back) only with refund on.</summary>
    internal static int Net(bool refundEnabled, int cost, int refund) =>
        refundEnabled ? cost - refund : Math.Max(0, cost - refund);
}
