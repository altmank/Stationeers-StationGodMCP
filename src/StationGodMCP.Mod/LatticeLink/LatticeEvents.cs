#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using StationGodMCP.LatticeLink.Json;
using StationGodMCP.LatticeLink.Model;
using L = Lattice.Api;

namespace StationGodMCP.LatticeLink;

/// <summary>
/// The last of Lattice's events in this world, numbered, for find lattice {ask: events, args: {since}}: change sets (in
/// short), completions, executor status, a sent command's answer, plan states, blocked ghosts, moved structures, answers
/// ready, loads and board resets. Heard once per system (LatticeBoard.Attach); the newest Kept are kept.
/// </summary>
internal static class LatticeEvents
{
    private const int Kept = 300;

    private static readonly LinkedList<JObject> Heard = new LinkedList<JObject>();

    private static long _next = 1;

    internal static void Listen(L.IGhostSystem system)
    {
        L.IGhostEvents events = system.Events;
        events.Changed += changes => Add("changed", new JObject
        {
            ["change_set"] = changes.Id.Value,
            ["by"] = LatticeOut.Actor(changes.By),
            ["revision"] = changes.Revision.Value,
            ["added"] = LatticeOut.Ids(Each(changes.Added, ghost => ghost.Id)),
            ["changed"] = LatticeOut.Ids(Each(changes.Changed, change => change.After.Id)),
            ["removed"] = LatticeOut.Each(changes.Removed, removal => new JObject
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
            }),
            ["plans_opened"] = LatticeOut.Each(changes.PlansOpened, plan => new JValue(plan.Id.Value)),
            ["plans_finished"] = LatticeOut.Each(changes.PlansFinished, plan => new JValue(plan.Value)),
        });
        events.Completed += (ghost, completion) => Add("completed", new JObject
        {
            ["ghost"] = ghost.Value,
            ["built"] = completion.Built.TryGet(out L.StructureRef built) ? built.Id.Value.ToString(CultureInfo.InvariantCulture) : null,
        });
        events.StatusChanged += ghost => Add("status_changed", new JObject { ["ghost"] = ghost.Value });
        events.Answered += (ticket, outcome) => Add("answered", new JObject
        {
            ["ticket"] = ticket.Command.Value.ToString(),
            ["outcome"] = outcome is L.Outcome.Applied applied ? "applied" : "refused",
            ["change_set"] = outcome is L.Outcome.Applied done ? done.Changes.Id.Value : null,
            ["refusals"] = outcome is L.Outcome.Refused refused ? LatticeText.Of(refused.Why) : null,
        });
        events.PlanStateChanged += plan => Add("plan_state_changed", new JObject { ["plan"] = plan.Value });
        events.Blocked += (maker, blocked) => Add("blocked", new JObject
        {
            ["maker"] = maker.Value,
            ["ghosts"] = LatticeOut.Each(blocked, ghost => new JObject
            {
                ["ghost"] = ghost.Ghost.Value, ["why"] = LatticeOut.Reason(ghost.Why)
            }),
        });
        events.StructuresMoved += moves => Add("structures_moved", new JObject
        {
            ["moves"] = LatticeOut.Each(moves, move => new JObject
            {
                ["id"] = move.From.Id.Value.ToString(CultureInfo.InvariantCulture),
                ["from"] = LatticeOut.Cell(move.From.At),
                ["to"] = LatticeOut.Cell(move.To),
                ["turn"] = LatticeOut.Turn(move.Turn),
            }),
        });
        events.AnswersReady += revision => Add("answers_ready", new JObject { ["revision"] = revision.Value });
        events.Loaded += load => Add("loaded", new JObject
        {
            ["record"] = load.Record is L.SaveRecord.Found ? "found" : "missing",
            ["plans_ahead"] = load.Ahead.Count,
        });
        events.BoardReset += () => Add("board_reset", new JObject());
    }

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
