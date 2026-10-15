#nullable enable

using System.Collections.Generic;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Subscriptions;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Verbs;

namespace StationGodMCP.Subscriptions;

/// <summary>
/// Find queries evaluated through the find verb's own handler (cost design A6), so a find subscription answers exactly
/// what find would: its options as given, and for a what that pages by cursor the whole walk, the first page with
/// cursor "open" and the largest page its limit allows, then each later page by the cursor it answered. Each page is the
/// handler's pending work stepped as a call's is; every evaluation runs under a call context of its own (owner
/// find-subscriptions, a stream per evaluation), which owns its walk's cursor.
/// </summary>
internal sealed class FindEvaluator : IFindEvaluator
{
    internal const string Owner = "find-subscriptions";

    internal static readonly FindEvaluator Instance = new FindEvaluator();

    private long _lastStream;

    private FindEvaluator()
    {
    }

    public IFindEvaluation Start(FindQuery query) =>
        new FindEvaluation(query, new CallContext(Owner, ++_lastStream), WorldModel.WorldNews.Instance.Version(query.Topic.Channels));
}

/// <summary>One find query's evaluation, a page step at a time (FindEvaluator). Main thread.</summary>
internal sealed class FindEvaluation : IFindEvaluation
{
    private const string Verb = "find";
    private const string WhatKey = "what";
    private const string CursorKey = "cursor";
    private const string LimitKey = "limit";

    private readonly FindQuery _query;
    private readonly CallContext _context;
    private readonly long _news;
    private readonly double _asOfS;
    private readonly List<object> _pages = new List<object>();
    private JObject? _next;
    private IPendingResult? _pending;
    private string? _cursorId;
    private double _spentMs;

    internal FindEvaluation(FindQuery query, CallContext context, long news)
    {
        _query = query;
        _context = context;
        _news = news;
        _asOfS = UnityEngine.Time.time;
        _next = FirstPage(query);
    }

    public FindResult? Result { get; private set; }

    public FindFailure? Failure { get; private set; }

    public bool Step(double untilMs)
    {
        long started = Stopwatch.GetTimestamp();
        using CallContext.Scope scope = CallContext.Enter(_context);
        try
        {
            while (Result == null && Failure == null)
            {
                VerbOutcome outcome;
                if (_pending != null)
                {
                    if (!_pending.Step(untilMs))
                    {
                        return false;
                    }

                    IPendingResult done = _pending;
                    _pending = null;
                    outcome = VerbCalls.Handled(() => VerbOutcome.Of(done.Result()));
                }
                else
                {
                    JObject page = _next!;
                    _next = null;
                    outcome = VerbCalls.Handled(() => VerbHandlers.Dispatcher.Run(Verb, page, null));
                }

                Take(outcome);
                if (StopwatchClock.Instance.NowMs >= untilMs)
                {
                    break;
                }
            }

            return Result != null || Failure != null;
        }
        finally
        {
            _spentMs += (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
        }
    }

    public void Cancel()
    {
        if (_cursorId != null)
        {
            WalkCursors.Main.Close(FindEvaluator.Owner, _cursorId);
            _cursorId = null;
        }

        _pending = null;
        _next = null;
    }

    private void Take(VerbOutcome outcome)
    {
        switch (outcome)
        {
            case VerbOutcome.Pending pending:
                _pending = pending.Work;
                return;
            case VerbOutcome.Refused refused:
                Cancel();
                Failure = new FindFailure(refused.Error.Code, refused.Error.Message);
                return;
            case VerbOutcome.Answered answered:
                _pages.Add(answered.Reply);
                _cursorId = CursorOf(answered.Reply);
                if (_cursorId != null)
                {
                    _next = new JObject { [WhatKey] = _query.Topic.What, [CursorKey] = _cursorId, [LimitKey] = PageLimit(_query) };
                    return;
                }

                Result = new FindResult(_pages, Render, _asOfS, UnityEngine.Time.frameCount, _spentMs, _news);
                return;
        }
    }

    // The first page: the options as given, and for a what that pages by cursor its walk opened, as large a page as
    // its limit allows.
    private static JObject FirstPage(FindQuery query)
    {
        JObject page = (JObject)query.Options.DeepClone();
        page[WhatKey] = query.Topic.What;
        if (Spec(query)?.Takes(CursorKey) == true)
        {
            page[CursorKey] = Paging.OpenCursor;
            page[LimitKey] = PageLimit(query);
        }

        return page;
    }

    private static WhatSpec? Spec(FindQuery query) => VerbSurface.Verb(Verb)?.What(query.Topic.What);

    private static int PageLimit(FindQuery query) =>
        Spec(query)?.Option(LimitKey)?.Schema["maximum"]?.Value<int>() ?? 500;

    private static string? CursorOf(object reply) =>
        reply is ReplyObject keys && keys.Find("cursor_id") is string cursor ? cursor : null;

    // A page as JSON, by the serializer of whichever thread writes it (a connection's writer).
    private static JObject Render(object page) => JObject.FromObject(page, ApiJson.Serializer);
}
