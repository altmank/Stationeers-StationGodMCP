#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using StationGodMCP.LatticeLink.Json;
using StationGodMCP.LatticeLink.Model;
using L = Lattice.Api;

namespace StationGodMCP.LatticeLink;

/// <summary>
/// The last of Lattice's events in this world, numbered, for find lattice {ask: events, args: {since}}: change sets (in
/// short), completions, executor status, a sent command's answer, plan states, blocked ghosts, moved structures, answers
/// ready, loads, board resets and builders (0.22). Heard once per system (LatticeBoard.Attach); the newest Kept are kept.
/// </summary>
internal static class LatticeEvents
{
    private const int Kept = 300;

    private static readonly LinkedList<JObject> Heard = new LinkedList<JObject>();

    private static long _next = 1;

    // Every handler and mapping below is a static method, never a lambda: a lambda that names a Lattice type is cached
    // in a field of a compiler-made class, which a game without Lattice lays out and fails on (LatticeFieldsTests).
    internal static void Listen(L.IGhostSystem system)
    {
        L.IGhostEvents events = system.Events;
        events.Changed += OnChanged;
        events.Completed += OnCompleted;
        events.StatusChanged += OnStatusChanged;
        events.Answered += OnAnswered;
        events.PlanStateChanged += OnPlanStateChanged;
        events.Blocked += OnBlocked;
        events.StructuresMoved += OnStructuresMoved;
        events.AnswersReady += OnAnswersReady;
        events.Loaded += OnLoaded;
        events.BoardReset += OnBoardReset;
        if (system.Version.Major == L.ApiVersion.ContractMajor && system.Version.Minor >= LatticeOut.BuildersMinor)
        {
            ListenToBuilders(events);
        }
    }

    private static void OnChanged(L.ChangeSet changes) => Add("changed", new JObject
    {
        ["change_set"] = changes.Id.Value,
        ["by"] = LatticeOut.Actor(changes.By),
        ["revision"] = changes.Revision.Value,
        ["added"] = LatticeOut.Ids(Each(changes.Added, GhostIdOf)),
        ["changed"] = LatticeOut.Ids(Each(changes.Changed, ChangedIdOf)),
        ["removed"] = LatticeOut.Each(changes.Removed, Removal),
        ["plans_opened"] = LatticeOut.Each(changes.PlansOpened, OpenedPlan),
        ["plans_finished"] = LatticeOut.Each(changes.PlansFinished, FinishedPlan),
    });

    private static L.GhostId GhostIdOf(L.Ghost ghost) => ghost.Id;

    private static L.GhostId ChangedIdOf(L.GhostChange change) => change.After.Id;

    private static JToken Removal(L.GhostRemoval removal) => new JObject
    {
        ["ghost"] = removal.Ghost.Id.Value,
        ["cause"] = removal.Cause switch
        {
            L.RemovalCause.Removed => "removed",
            L.RemovalCause.Replaced => "replaced",
            L.RemovalCause.Done => "done",
            L.RemovalCause.TargetGone => "target_gone",
            _ => removal.Cause.GetType().Name
        }
    };

    private static JToken OpenedPlan(L.Plan plan) => new JValue(plan.Id.Value);

    private static JToken FinishedPlan(L.PlanId plan) => new JValue(plan.Value);

    private static void OnCompleted(L.GhostId ghost, L.Completion completion) => Add("completed", new JObject
    {
        ["ghost"] = ghost.Value,
        ["built"] = completion.Built.TryGet(out L.StructureRef built) ? built.Id.Value.ToString(CultureInfo.InvariantCulture) : null,
    });

    private static void OnStatusChanged(L.GhostId ghost) => Add("status_changed", new JObject { ["ghost"] = ghost.Value });

    private static void OnAnswered(L.Ticket ticket, L.Outcome outcome) => Add("answered", new JObject
    {
        ["ticket"] = ticket.Command.Value.ToString(),
        ["outcome"] = outcome is L.Outcome.Applied applied ? "applied" : "refused",
        ["change_set"] = outcome is L.Outcome.Applied done ? done.Changes.Id.Value : null,
        ["refusals"] = outcome is L.Outcome.Refused refused ? LatticeText.Of(refused.Why) : null,
    });

    private static void OnPlanStateChanged(L.PlanId plan) => Add("plan_state_changed", new JObject { ["plan"] = plan.Value });

    private static void OnBlocked(L.Maker maker, L.ValueList<L.BlockedGhost> blocked) => Add("blocked", new JObject
    {
        ["maker"] = maker.Value,
        ["ghosts"] = LatticeOut.Each(blocked, BlockedGhost),
    });

    private static JToken BlockedGhost(L.BlockedGhost ghost) => new JObject
    {
        ["ghost"] = ghost.Ghost.Value, ["why"] = LatticeOut.Reason(ghost.Why)
    };

    private static void OnStructuresMoved(L.ValueList<L.StructureMove> moves) => Add("structures_moved", new JObject
    {
        ["moves"] = LatticeOut.Each(moves, StructureMove),
    });

    private static JToken StructureMove(L.StructureMove move) => new JObject
    {
        ["id"] = move.From.Id.Value.ToString(CultureInfo.InvariantCulture),
        ["from"] = LatticeOut.Cell(move.From.At),
        ["to"] = LatticeOut.Cell(move.To),
        ["turn"] = LatticeOut.Turn(move.Turn),
    };

    private static void OnAnswersReady(L.Revision revision) => Add("answers_ready", new JObject { ["revision"] = revision.Value });

    private static void OnLoaded(L.LoadReport load) => Add("loaded", new JObject
    {
        ["record"] = load.Record is L.SaveRecord.Found ? "found" : "missing",
        ["plans_ahead"] = load.Ahead.Count,
    });

    private static void OnBoardReset() => Add("board_reset", new JObject());

    // Contract 0.22: a builder registered, went, or changed what it says.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ListenToBuilders(L.IGhostEvents events) => events.BuilderChanged += OnBuilderChanged;

    private static void OnBuilderChanged(L.ExecutorId builder) =>
        Add("builder_changed", new JObject { ["builder"] = builder.Value });

    /// <summary>The events numbered after since, oldest first, and the number to ask from next.</summary>
    internal static JObject Since(long since)
    {
        JArray events = new JArray();
        foreach (JObject heard in Heard)
        {
            if ((long)heard["seq"]! > since)
            {
                events.Add(heard.DeepClone());
            }
        }

        return new JObject { ["events"] = events, ["next"] = _next - 1, ["kept"] = Kept };
    }

    private static void Add(string kind, JObject body)
    {
        JObject heard = new JObject
        {
            ["seq"] = _next++,
            ["kind"] = kind,
            ["at"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        };
        foreach (KeyValuePair<string, JToken?> field in body)
        {
            heard[field.Key] = field.Value;
        }

        Heard.AddLast(heard);
        while (Heard.Count > Kept)
        {
            Heard.RemoveFirst();
        }
    }

    private static IEnumerable<TOut> Each<TIn, TOut>(IEnumerable<TIn> values, Func<TIn, TOut> map)
    {
        foreach (TIn value in values)
        {
            yield return map(value);
        }
    }
}
