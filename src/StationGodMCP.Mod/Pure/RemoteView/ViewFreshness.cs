#nullable enable

using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure.RemoteView;

/// <summary>
/// Whether a remote view still shows what the player sees. Age alone never makes it stale: a player who alt-tabs or
/// stands still keeps looking at the same thing. It is stale once the player stands more than 0.3 m from where the view
/// was taken, or once it is older than 5 s and the player moved at all since (the client sends a moving view within
/// 0.1 s, so an old view of a moved player means its updates stopped).
/// </summary>
internal abstract class ViewFreshness
{
    internal const double MovedM = 0.3;
    internal const double OldS = 5.0;
    internal const double StillM = 0.05;

    private ViewFreshness()
    {
    }

    internal sealed class Fresh : ViewFreshness
    {
        internal static readonly Fresh Instance = new Fresh();

        private Fresh()
        {
        }
    }

    internal sealed class Stale : ViewFreshness
    {
        internal Stale(string reason)
        {
            Reason = reason;
        }

        /// <summary>Why, a clause ("the player is 1.2 m from where it was taken").</summary>
        internal string Reason { get; }
    }

    /// <summary>Judges a view taken with the player at reported, the player now at current, ageS seconds ago.</summary>
    internal static ViewFreshness Judge(Vec3 reported, Vec3 current, double ageS)
    {
        double moved = (current - reported).Length;
        if (moved > MovedM)
        {
            return new Stale(string.Format(CultureInfo.InvariantCulture,
                "the player is {0:0.0#} m from where it was taken", moved));
        }

        if (ageS > OldS && moved > StillM)
        {
            return new Stale(string.Format(CultureInfo.InvariantCulture,
                "it is {0:0.0} s old and the player moved {1:0.00} m since", ageS, moved));
        }

        return Fresh.Instance;
    }
}

/// <summary>
/// The views a server holds, one per key (a player), each with the time it arrived. A client's reports are ordered by
/// session and sequence: within a session only a higher sequence replaces the view held; a new session (the client's
/// game restarted) always does.
/// </summary>
internal sealed class ViewBook<TKey>
    where TKey : notnull
{
    private readonly Dictionary<TKey, ReceivedView> _views = new Dictionary<TKey, ReceivedView>();

    internal int Count => _views.Count;

    internal IEnumerable<TKey> Keys => _views.Keys;

    /// <summary>Keeps the report unless it is older than the one held; false when it was ignored.</summary>
    internal bool Offer(TKey key, ViewReport report, double now)
    {
        if (_views.TryGetValue(key, out ReceivedView? held) &&
            held.Report.Session == report.Session && report.Sequence <= held.Report.Sequence)
        {
            return false;
        }

        _views[key] = new ReceivedView(report, now);
        return true;
    }

    internal ReceivedView? Find(TKey key) => _views.TryGetValue(key, out ReceivedView? view) ? view : null;

    internal bool Remove(TKey key) => _views.Remove(key);

    internal void Clear() => _views.Clear();
}

/// <summary>A view as the server holds it: the report and the time (seconds) it arrived.</summary>
internal sealed class ReceivedView
{
    internal ReceivedView(ViewReport report, double receivedAt)
    {
        Report = report;
        ReceivedAt = receivedAt;
    }

    internal ViewReport Report { get; }

    internal double ReceivedAt { get; }

    internal double AgeAt(double now) => System.Math.Max(0.0, now - ReceivedAt);
}
