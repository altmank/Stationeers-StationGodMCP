#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// One handler per (verb, what): it binds the what's generated options class from a checked call and runs on it.
/// </summary>
internal sealed class VerbHandler
{
    private readonly Func<OptionReader, VerbOutcome> _run;

    private VerbHandler(WhatSpec what, Func<OptionReader, VerbOutcome> run)
    {
        What = what;
        _run = run;
    }

    internal WhatSpec What { get; }

    /// <summary>
    /// The handler for a what: bind is the what's generated Bind, run the verb-native handler. A value an option kind
    /// cannot use refuses the call with every such problem, before run.
    /// </summary>
    internal static VerbHandler For<TOptions>(WhatSpec what, Func<OptionReader, TOptions> bind,
        Func<TOptions, VerbOutcome> run) =>
        new VerbHandler(what, reader =>
        {
            TOptions options = bind(reader);
            return reader.Problems.Count > 0
                ? new VerbOutcome.Refused(OptionProblem.Refusal(what.Label, reader.Problems))
                : run(options);
        });

    internal VerbOutcome Run(OptionReader reader) => _run(reader);
}

/// <summary>
/// Verb calls (PLAN 2.0, wire form {"method": "&lt;verb&gt;", "params": {"what", ...options}}): the verb, then the what
/// and options checked (OptionCheck), the what's options bound, and the handler the (verb, what) table holds run. That
/// table is the only one: a verb or what with no handler is an unknown method, like any name that is no verb.
/// </summary>
internal sealed class VerbDispatcher
{
    private readonly Dictionary<string, Dictionary<string, VerbHandler>> _handlers =
        new Dictionary<string, Dictionary<string, VerbHandler>>(StringComparer.Ordinal);

    internal VerbDispatcher(IEnumerable<VerbHandler> handlers)
    {
        foreach (VerbHandler handler in handlers)
        {
            if (!_handlers.TryGetValue(handler.What.Verb, out Dictionary<string, VerbHandler>? whats))
            {
                whats = new Dictionary<string, VerbHandler>(StringComparer.Ordinal);
                _handlers.Add(handler.What.Verb, whats);
            }

            whats.Add(handler.What.Name, handler);
        }
    }

    /// <summary>A table with no handlers: every verb is an unknown method.</summary>
    internal static VerbDispatcher None { get; } = new VerbDispatcher(Array.Empty<VerbHandler>());

    /// <summary>True when the verb has a handler for at least one of its whats.</summary>
    internal bool Answers(string verb) => _handlers.ContainsKey(verb);

    /// <summary>True when the (verb, what) has a handler.</summary>
    internal bool Answers(WhatSpec what) =>
        _handlers.TryGetValue(what.Verb, out Dictionary<string, VerbHandler>? whats) && whats.ContainsKey(what.Name);

    /// <summary>
    /// The call's what when the call can run: a verb with a handler, its what and options checked and that what's
    /// handler held. Otherwise the refusal: method_not_found for no handler, invalid_argument with every problem.
    /// </summary>
    internal VerbError? Admit(string method, JObject? parameters, out WhatSpec? what)
    {
        what = null;
        VerbSpec? verb = VerbSurface.Verb(method);
        if (verb == null || !Answers(verb.Name))
        {
            return VerbError.UnknownMethod(method);
        }

        if (OptionCheck.Check(verb, parameters, out what) is { } refused)
        {
            return refused;
        }

        return Answers(what!) ? null : VerbError.UnknownMethod(method, WhatOf(what!));
    }

    /// <summary>
    /// Runs a verb call: admitted again unless checked says the reader thread did, then bound and handled. shape is the
    /// call's shape (Fields.ShapeOf).
    /// </summary>
    internal VerbOutcome Run(string method, JObject? parameters, ShapeRequest? shape, bool checkedAlready = false)
    {
        WhatSpec? what;
        if (checkedAlready)
        {
            what = VerbSurface.Verb(method)?.What(parameters?[OptionCheck.WhatKey]?.Type == JTokenType.String
                ? (string)parameters[OptionCheck.WhatKey]!
                : null);
        }
        else if (Admit(method, parameters, out what) is { } refused)
        {
            return new VerbOutcome.Refused(refused);
        }

        if (what == null || !_handlers.TryGetValue(what.Verb, out Dictionary<string, VerbHandler>? whats) ||
            !whats.TryGetValue(what.Name, out VerbHandler? handler))
        {
            return new VerbOutcome.Refused(VerbError.UnknownMethod(method, what == null ? null : WhatOf(what)));
        }

        return handler.Run(new OptionReader(what, parameters, shape));
    }

    private static string? WhatOf(WhatSpec what) => what.Name == VerbSurface.WhatlessKey ? null : what.Name;
}
