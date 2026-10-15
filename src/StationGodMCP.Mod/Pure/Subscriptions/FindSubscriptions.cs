#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Subscriptions;

/// <summary>Why a find evaluation gave no list: the find's own refusal (its code and message).</summary>
internal sealed class FindFailure
{
    internal FindFailure(string code, string message)
    {
        Code = code;
        Message = message;
    }

    internal string Code { get; }

    internal string Message { get; }
}

/// <summary>
/// One evaluation of a find query in progress: the find's handler run page by page, a bounded step at a time (each
/// page its pending work, as a call's). Main thread.
/// </summary>
internal interface IFindEvaluation
{
    /// <summary>Steps on until the clock reaches untilMs (at least one step); true once Result or Failure is set.</summary>
    bool Step(double untilMs);

    FindResult? Result { get; }

    FindFailure? Failure { get; }

    /// <summary>Its subscribers are gone: whatever it keeps (a walk's cursor) is let go.</summary>
    void Cancel();
}

/// <summary>Starts evaluations of find queries (the mod runs the find verb's handler).</summary>
internal interface IFindEvaluator
{
    IFindEvaluation Start(FindQuery query);
}

/// <summary>A find subscribe's outcome: subscribed (joined a query evaluated already), refused, or a first evaluation to run.</summary>
internal abstract class FindSubscribeOutcome
{
    private FindSubscribeOutcome()
    {
    }

    internal sealed class Subscribed : FindSubscribeOutcome
    {
        internal Subscribed(SubscriptionId subscription, SamplingInterval interval, long frame, FindResult result)
        {
            Subscription = subscription;
            Interval = interval;
            Frame = frame;
            Result = result;
        }

        internal SubscriptionId Subscription { get; }

        internal SamplingInterval Interval { get; }

        internal long Frame { get; }

        /// <summary>The list the reply carries: the subscriber's first result, what later updates are compared with.</summary>
        internal FindResult Result { get; }
    }

    internal sealed class Refused : FindSubscribeOutcome
    {
        internal Refused(SubscriptionRefusal refusal)
        {
            Refusal = refusal;
        }

        internal SubscriptionRefusal Refusal { get; }
    }

    /// <summary>Nobody holds the query yet: the call runs this first evaluation, then completes the subscribe with it.</summary>
    internal sealed class Evaluate : FindSubscribeOutcome
    {
        internal Evaluate(IFindEvaluation evaluation)
        {
            Evaluation = evaluation;
        }

        internal IFindEvaluation Evaluation { get; }
    }
}

/// <summary>One subscriber of a shared find query.</summary>
internal sealed class FindSubscription
{
    internal FindSubscription(SubscriptionId id, ConnectionId connection, long stream, SamplingInterval interval,
        SharedFind shared, FindSlot slot)
    {
        Id = id;
        Connection = connection;
        Stream = stream;
        Interval = interval;
        Shared = shared;
        Slot = slot;
    }

    internal SubscriptionId Id { get; }

    internal ConnectionId Connection { get; }

    internal long Stream { get; }

    internal SamplingInterval Interval { get; }

    internal SharedFind Shared { get; }

    internal FindSlot Slot { get; }
}

/// <summary>
/// A find query and its subscribers, evaluated once for all of them (cost design A6): its latest result, what one
/// evaluation costs (measured), how often its live facts are read again (the shortest interval among its subscribers),
/// when that is next due, and the world news its latest evaluation began at.
/// </summary>
internal sealed class SharedFind
{
    private readonly List<FindSubscription> _subscribers = new List<FindSubscription>();

    internal SharedFind(FindQuery query, FindResult first, double intervalS, double nowS)
    {
        Query = query;
        Latest = first;
        Cost = new MeasuredCost(first.CostMs);
        IntervalS = intervalS;
        LastStartS = first.AsOfS;
        NextDueS = SubscriptionGrid.NextDue(double.NegativeInfinity, intervalS, nowS);
    }

    internal FindQuery Query { get; }

    internal IReadOnlyList<FindSubscription> Subscribers => _subscribers;

    internal FindResult Latest { get; private set; }

    internal MeasuredCost Cost { get; }

    internal double IntervalS { get; private set; }

    internal double NextDueS { get; set; }

    /// <summary>When the latest evaluation began (game seconds).</summary>
    internal double LastStartS { get; set; }

    internal IFindEvaluation? Running { get; set; }

    internal double ProjectedMsPerSecond => Cost.PerSecond(IntervalS);

    /// <summary>The projected load with a subscriber of this interval joined, against now.</summary>
    internal double AddedMsPerSecond(double intervalS) =>
        Cost.PerSecond(Math.Min(IntervalS, intervalS)) - Cost.PerSecond(IntervalS);

    internal void Join(FindSubscription subscription)
    {
        _subscribers.Add(subscription);
        IntervalS = Math.Min(IntervalS, subscription.Interval.Seconds);
    }

    internal void Leave(FindSubscription subscription)
    {
        _subscribers.Remove(subscription);
        if (_subscribers.Count == 0)
        {
            return;
        }

        double shortest = double.MaxValue;
        foreach (FindSubscription left in _subscribers)
        {
            shortest = Math.Min(shortest, left.Interval.Seconds);
        }

        IntervalS = shortest;
    }

    internal void Evaluated(FindResult result, double nowS)
    {
        Latest = result;
        Cost.Add(result.CostMs);
        if (NextDueS <= nowS)
        {
            NextDueS = SubscriptionGrid.NextDue(NextDueS, IntervalS, nowS);
        }
    }
}

/// <summary>What one turn of the find lane did.</summary>
internal enum FindStep
{
    /// <summary>Nothing was due.</summary>
    Idle,

    /// <summary>An evaluation took a step and goes on.</summary>
    Worked,

    /// <summary>An evaluation ended with a result (FindQueries.Finished).</summary>
    Finished,

    /// <summary>An evaluation ended with a refusal or threw (FindQueries.Finished, FindQueries.Failure).</summary>
    Failed,
}

/// <summary>
/// The shared find queries and their evaluations (cost design A6). A query is due when its interval has passed since
/// its grid's last point (its live facts), or earlier when one of its channels' news moved since its latest evaluation
/// began, at most once per EventGapS. One evaluation runs at a time, a step per turn of the subscription lane, so the
/// lane's share bounds what finds cost a frame; the query due longest goes first. Main thread.
/// </summary>
internal sealed class FindQueries
{
    /// <summary>The shortest time between two evaluations of one query brought forward by world news.</summary>
    internal const double EventGapS = 1.0;

    private readonly IFindEvaluator _evaluator;
    private readonly IWorldNews _news;
    private readonly Dictionary<string, SharedFind> _byKey = new Dictionary<string, SharedFind>(StringComparer.Ordinal);
    private readonly List<SharedFind> _all = new List<SharedFind>();
    private SharedFind? _evaluating;

    internal FindQueries(IFindEvaluator evaluator, IWorldNews news)
    {
        _evaluator = evaluator;
        _news = news;
    }

    /// <summary>Shared queries held.</summary>
    internal int Count => _all.Count;

    /// <summary>Evaluations ended since the mod loaded (first ones not counted: those are their subscribe calls').</summary>
    internal long Evaluations { get; private set; }

    /// <summary>The evaluation the last Finished or Failed turn ended, and its query.</summary>
    internal SharedFind? Finished { get; private set; }

    /// <summary>Why the last Failed turn's evaluation ended.</summary>
    internal SubscriptionEnd? Failure { get; private set; }

    internal SharedFind? Of(FindQuery query) => _byKey.TryGetValue(query.Key, out SharedFind? shared) ? shared : null;

    /// <summary>A first evaluation, for a subscribe that found no query to join.</summary>
    internal IFindEvaluation StartFirst(FindQuery query) => _evaluator.Start(query);

    /// <summary>The query's channels' news now, for a first evaluation to begin at.</summary>
    internal long NewsOf(FindQuery query) => _news.Version(query.Topic.Channels);

    internal SharedFind Add(FindQuery query, FindResult first, double intervalS, double nowS)
    {
        SharedFind shared = new SharedFind(query, first, intervalS, nowS);
        _byKey.Add(query.Key, shared);
        _all.Add(shared);
        return shared;
    }

    internal double ProjectedMsPerSecond()
    {
        double total = 0.0;
        foreach (SharedFind shared in _all)
        {
            total += shared.ProjectedMsPerSecond;
        }

        return total;
    }

    /// <summary>
    /// One turn: steps the running evaluation once, or starts the query due longest when none runs (paused tells a
    /// subscriber that takes no updates now; a query all of whose subscribers are paused is not evaluated).
    /// </summary>
    internal FindStep Step(SamplingTick tick, double untilMs, Func<FindSubscription, bool> paused)
    {
        Finished = null;
        Failure = null;
        if (_evaluating == null)
        {
            _evaluating = NextDue(tick, paused);
            if (_evaluating == null)
            {
                return FindStep.Idle;
            }

            _evaluating.LastStartS = tick.GameTimeS;
            _evaluating.Running = _evaluator.Start(_evaluating.Query);
        }

        SharedFind shared = _evaluating;
        IFindEvaluation running = shared.Running!;
        try
        {
            if (!running.Step(untilMs))
            {
                return FindStep.Worked;
            }
        }
        catch (Exception failure)
        {
            // The game tick's boundary: an evaluation that throws ends its own query's subscriptions, never the frame.
            return Ended(shared, new SubscriptionEnd.ReadFailed(failure));
        }

        if (running.Failure is FindFailure refused)
        {
            return Ended(shared, new SubscriptionEnd.ReadFailed(
                new InvalidOperationException($"{refused.Code}: {refused.Message}")));
        }

        _evaluating = null;
        shared.Running = null;
        shared.Evaluated(running.Result!, tick.GameTimeS);
        Evaluations++;
        Finished = shared;
        return FindStep.Finished;
    }

    /// <summary>Drops the queries no subscriber holds any more, letting go of a running evaluation.</summary>
    internal void Prune()
    {
        for (int index = _all.Count - 1; index >= 0; index--)
        {
            SharedFind shared = _all[index];
            if (shared.Subscribers.Count == 0)
            {
                Forget(shared);
            }
        }
    }

    /// <summary>A world left: every query goes (their subscriptions are ended by the engine).</summary>
    internal void Clear()
    {
        for (int index = _all.Count - 1; index >= 0; index--)
        {
            Forget(_all[index]);
        }
    }

    private FindStep Ended(SharedFind shared, SubscriptionEnd end)
    {
        _evaluating = null;
        shared.Running = null;
        Finished = shared;
        Failure = end;
        return FindStep.Failed;
    }

    private SharedFind? NextDue(SamplingTick tick, Func<FindSubscription, bool> paused)
    {
        SharedFind? due = null;
        foreach (SharedFind shared in _all)
        {
            if (shared.Subscribers.Count == 0 || AllPaused(shared, paused))
            {
                continue;
            }

            bool intervalDue = shared.NextDueS <= tick.GameTimeS;
            bool newsDue = tick.GameTimeS >= shared.LastStartS + EventGapS &&
                           _news.Version(shared.Query.Topic.Channels) != shared.Latest.News;
            if ((intervalDue || newsDue) && (due == null || shared.NextDueS < due.NextDueS))
            {
                due = shared;
            }
        }

        return due;
    }

    private static bool AllPaused(SharedFind shared, Func<FindSubscription, bool> paused)
    {
        foreach (FindSubscription subscription in shared.Subscribers)
        {
            if (!paused(subscription))
            {
                return false;
            }
        }

        return true;
    }

    private void Forget(SharedFind shared)
    {
        shared.Running?.Cancel();
        shared.Running = null;
        if (ReferenceEquals(_evaluating, shared))
        {
            _evaluating = null;
        }

        _byKey.Remove(shared.Query.Key);
        _all.Remove(shared);
    }
}
