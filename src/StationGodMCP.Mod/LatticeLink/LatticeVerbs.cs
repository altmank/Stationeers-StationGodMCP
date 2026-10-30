#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.LatticeLink.Json;
using StationGodMCP.LatticeLink.Model;
using StationGodMCP.Pure;
using L = Lattice.Api;

namespace StationGodMCP.LatticeLink;

/// <summary>
/// find, write and show lattice: Lattice's whole contract as JSON (LatticeIn reads, LatticeOut writes; catalogue topic
/// lattice). Asks read every service (board, plans, validation, steps, bills, swaps, analysis, previews, executors,
/// inspector, catalogue, world, styles, events); commands are every Lattice command plus the agent's executor work
/// (claims, status) and the plan size limit; the view is this seat's style layers, sets, drafts, clip and highlights.
/// Everything is the agent's (Actor stationgod) unless a command names another maker. Main thread; reach it only
/// through LatticePresence.
/// </summary>
internal static class LatticeVerbs
{
    internal static readonly string[] Asks =
    {
        "ghosts", "plans", "plan", "load", "template", "validation", "steps", "bill", "swaps", "analysis", "preview",
        "executors", "story", "export", "catalogue", "ports", "facing", "families", "groups", "modules", "world",
        "explain", "matches", "pick", "clip", "events", "builders"
    };

    private static readonly string[] AgentCommands =
    {
        "claim", "renew", "release", "report_status", "limit_plan_size", "locate", "say", "offer", "answer"
    };

    // The agent's word on itself as a builder (contract 0.22): no ghost named.
    private static readonly string[] BuilderCommands = { "locate", "say", "offer", "answer" };

    private const int MaximumListed = 500;

    // ---- find lattice --------------------------------------------------------------------------------------------

    internal static LatticeAnswer Ask(string ask, JObject args)
    {
        if (LatticeBoard.Current is not { } system)
        {
            return new LatticeAnswer(new List<string> { "ask: Lattice runs no world" });
        }

        LatticeIn read = new LatticeIn(system);
        LatticeOut write = Out(system);
        JObject reply = new JObject { ["ask"] = ask, ["pending"] = false };
        List<string> notes = new List<string>();
        JToken? answer = ask switch
        {
            "ghosts" => Ghosts(system, read, write, args, notes),
            "plans" => Plans(system, write),
            "plan" => Plan(system, read, write, args, reply),
            "load" => system.Plans.LastLoad.TryGet(out L.LoadReport load)
                ? new JObject
                {
                    ["record"] = load.Record is L.SaveRecord.Found ? "found" : "missing",
                    ["ahead"] = LatticeOut.Each(load.Ahead, plan => new JObject { ["plan"] = plan.Plan.Value, ["changes"] = plan.Changes })
                }
                : JValue.CreateNull(),
            "template" => Template(system, read, args),
            "validation" => Selected(read, args) is { } checkedOnes
                ? Pending(system.Validation.Check(checkedOnes), read, "args.select", reply,
                    report => new JObject { ["at"] = report.At.Value, ["findings"] = LatticeOut.Each(report.Findings, write.Finding) })
                : null,
            "steps" => read.Ghost(args["ghost"], "args.ghost") is { } stepped
                ? Result(system.Steps.For(stepped), read, "args.ghost", write.Steps)
                : null,
            "bill" => Selected(read, args) is { } billed ? Result(system.Bills.For(billed), read, "args.select", write.Bill) : null,
            "swaps" => read.Structure(args["structure"], "args.structure") is { } swapped
                ? Result(system.Swaps.OptionsFor(swapped), read, "args.structure", options => LatticeOut.Each(options, write.Swap))
                : null,
            "analysis" => Selected(read, args) is { } analysed
                ? Pending(system.Analysis.Of(analysed), read, "args.select", reply, write.Analysis)
                : null,
            "preview" => Preview(system, read, write, args, reply),
            "executors" => read.Ghost(args["ghost"], "args.ghost") is { } claimed ? Executors(system, write, claimed) : null,
            "story" => Story(system, read, write, args),
            "export" => read.Plan(args["plan"], "args.plan") is { } exported
                ? Result(system.Inspector.Export(exported), read, "args.plan", lines => LatticeOut.Each(lines, line => new JValue(line)))
                : null,
            "catalogue" => Catalogue(system, read, write, args, notes),
            "ports" => PlacementOf(system, read, args) is { } placed
                ? Result(system.Catalogue.PortsAt(placed), read, "args", ports => LatticeOut.Each(ports, write.Port))
                : null,
            "facing" => PlacementOf(system, read, args) is { } faced
                ? Result(system.Catalogue.FacingOf(faced), read, "args",
                    axis => axis.TryGet(out L.Axis normal) ? new JValue(LatticeOut.Axis(normal)) : JValue.CreateNull())
                : null,
            "families" => read.Piece(args["piece"], "args.piece") is { } piece
                ? Result(system.Catalogue.FamiliesOf(piece), read, "args.piece", families => LatticeOut.Each(families, family => new JValue(family.Value)))
                : null,
            "groups" => LatticeOut.Each(system.Board.As(LatticeNames.Agent).Groups,
                group => new JObject { ["name"] = group.Name.Value, ["count"] = group.Count }),
            "modules" => Modules(system),
            "world" => World(system, read, write, args),
            "explain" => read.Ghost(args["ghost"], "args.ghost") is { } explained
                ? Result(system.Styles.Explain(explained), read, "args.ghost", LatticeOut.Story)
                : null,
            "matches" => read.Match(args["match"], "args.match") is { } match && read.Structure(args["structure"], "args.structure") is { } built
                ? new JValue(system.Styles.Matches(match, built))
                : null,
            "pick" => Pick(system, read, args),
            "clip" => system.Styles.InForce.TryGet(out L.ClipShape clip) ? Clip(clip) : JValue.CreateNull(),
            "events" => LatticeEvents.Since(args["since"] is JValue { Type: JTokenType.Integer } since ? (long)since : 0),
            "builders" => read.Needs(LatticeOut.BuildersMinor, "ask") ? Builders(system, read, write, args) : null,
            _ => null
        };

        if (read.Failed)
        {
            return new LatticeAnswer(read.Problems);
        }

        if (answer == null)
        {
            return new LatticeAnswer(new List<string> { $"ask: '{ask}' is one of {string.Join(", ", Asks)}" });
        }

        reply["answer"] = answer;
        reply["notes"] = new JArray(notes.ToArray());
        return new LatticeAnswer(reply);
    }

    private static LatticeOut Out(L.IGhostSystem system) =>
        new LatticeOut(system, static hash => Prefab.Find(hash) is Thing prefab && prefab != null ? prefab.PrefabName : null);

    private static L.Selector? Selected(LatticeIn read, JObject args) =>
        LatticeIn.Given(args, "select") ? read.Selector(args["select"], "args.select") : new L.Selector.Everything();

    private static JToken? Result<T>(L.Result<T, L.Reason> asked, LatticeIn read, string path, Func<T, JToken> write)
    {
        if (asked.TryGet(out T value))
        {
            return write(value);
        }

        asked.TryGetError(out L.Reason why);
        return new JObject { ["refused"] = LatticeOut.Reason(why) };
    }

    private static JToken? Pending<T>(L.Result<L.Answer<T>, L.Reason> asked, LatticeIn read, string path, JObject reply,
        Func<T, JToken> write) where T : notnull
    {
        if (asked.TryGetError(out L.Reason why))
        {
            return new JObject { ["refused"] = LatticeOut.Reason(why) };
        }

        asked.TryGet(out L.Answer<T> answer);
        if (answer is L.Answer<T>.Ready ready)
        {
            return write(ready.Value);
        }

        reply["pending"] = true;
        return new JObject { ["pending_at"] = answer.At.Value };
    }

    private static JToken Ghosts(L.IGhostSystem system, LatticeIn read, LatticeOut write, JObject args, List<string> notes)
    {
        L.Selector? which = Selected(read, args);
        L.IGhostBoard board = system.Board.As(LatticeIn.Given(args, "as") && read.Actor(args["as"], "args.as") is { } viewer
            ? viewer
            : LatticeNames.Agent);
        if (which == null)
        {
            return JValue.CreateNull();
        }

        L.Result<L.ValueList<L.Ghost>, L.Reason> picked = board.Select(which);
        if (!picked.TryGet(out L.ValueList<L.Ghost> ghosts))
        {
            picked.TryGetError(out L.Reason why);
            return new JObject { ["refused"] = LatticeOut.Reason(why) };
        }

        int offset = args["offset"] is JValue { Type: JTokenType.Integer } from ? Math.Max(0, (int)from) : 0;
        int limit = args["limit"] is JValue { Type: JTokenType.Integer } most ? Math.Max(0, Math.Min(MaximumListed, (int)most)) : 100;
        JArray listed = new JArray();
        for (int index = offset; index < ghosts.Count && listed.Count < limit; index++)
        {
            listed.Add(write.Ghost(ghosts[index]));
        }

        if (offset + listed.Count < ghosts.Count)
        {
            notes.Add($"{ghosts.Count - offset - listed.Count} more: offset {offset + listed.Count}.");
        }

        return new JObject { ["revision"] = board.Revision.Value, ["total"] = ghosts.Count, ["ghosts"] = listed };
    }

    private static JToken Plans(L.IGhostSystem system, LatticeOut write) => new JObject
    {
        ["default"] = system.Plans.Default.Value,
        ["active"] = LatticeOut.Each(system.Plans.All, write.Plan),
        ["finished"] = LatticeOut.Each(system.Plans.Finished, write.Finished),
    };

    private static JToken? Plan(L.IGhostSystem system, LatticeIn read, LatticeOut write, JObject args, JObject reply)
    {
        if (read.Plan(args["plan"], "args.plan") is not { } id)
        {
            return null;
        }

        JObject answer = new JObject();
        if (system.Plans.Find(id).TryGet(out L.Plan plan))
        {
            answer["plan"] = write.Plan(plan);
        }

        answer["facts"] = Pending(system.Plans.Facts(id), read, "args.plan", reply, write.Facts);
        answer["state"] = Pending(system.Plans.State(id), read, "args.plan", reply, state => new JValue(LatticeOut.PlanState(state)));
        answer["ahead_of_save"] = Result(system.Plans.AheadOfSave(id), read, "args.plan", ahead => new JValue(ahead));
        return answer;
    }

    private static JToken? Template(L.IGhostSystem system, LatticeIn read, JObject args)
    {
        L.Template? template = LatticeIn.Given(args, "template")
            ? read.Template(args["template"], "args.template")
            : read.Template(args, "args");
        return template != null
            ? new JObject { ["name"] = template.Name.Value, ["ghosts"] = template.Ghosts.Count, ["text"] = system.Plans.WriteTemplate(template) }
            : null;
    }

    private static JToken? Preview(L.IGhostSystem system, LatticeIn read, LatticeOut write, JObject args, JObject reply)
    {
        LatticeLookIn looks = new LatticeLookIn(read);
        if (read.Command(args["command"], "args.command", looks) is not { } command)
        {
            return null;
        }

        foreach (string look in looks.Looks)
        {
            LatticeLooks.Ensure(system, look, null);
        }

        L.Answer<L.Result<L.Preview, L.ValueList<L.Refusal>>> answer = system.Preview.Of(command, LatticeNames.Agent);
        if (answer is not L.Answer<L.Result<L.Preview, L.ValueList<L.Refusal>>>.Ready ready)
        {
            reply["pending"] = true;
            return new JObject { ["pending_at"] = answer.At.Value };
        }

        return ready.Value.TryGet(out L.Preview preview)
            ? write.Preview(preview)
            : ready.Value.TryGetError(out L.ValueList<L.Refusal> refusals)
                ? new JObject { ["refusals"] = write.Refusals(refusals) }
                : JValue.CreateNull();
    }

    private static JToken Executors(L.IGhostSystem system, LatticeOut write, L.GhostId ghost)
    {
        JObject answer = new JObject();
        if (system.Executors.ClaimOn(ghost).TryGet(out L.Claim claim))
        {
            answer["claim"] = new JObject { ["by"] = claim.By.Value, ["until_s"] = claim.Until.Seconds };
        }

        answer["status"] = LatticeOut.Each(system.Executors.StatusOf(ghost), report => new JObject
        {
            ["by"] = report.By.Id.Value,
            ["name"] = report.By.Name.Value,
            ["status"] = report.Status switch
            {
                L.ExecutorStatus.Waiting waiting => new JObject { ["waiting"] = LatticeOut.Reason(waiting.Why) },
                L.ExecutorStatus.Working working => new JObject { ["working"] = new JObject { ["step"] = working.Step, ["of"] = working.Of } },
                L.ExecutorStatus.Paused => new JValue("paused"),
                _ => new JValue(report.Status.GetType().Name)
            }
        });
        return answer;
    }

    // Contract 0.22. No args: every registered builder. builder: that one and its plans. plan: its builders and where
    // each stands, progress, ghosts no claim holds, and what its builders set aside for it.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JToken? Builders(L.IGhostSystem system, LatticeIn read, LatticeOut write, JObject args)
    {
        L.IExecutors executors = system.Executors;
        if (LatticeIn.Given(args, "plan"))
        {
            if (read.Plan(args["plan"], "args.plan") is not { } plan)
            {
                return null;
            }

            return new JObject
            {
                ["plan"] = plan.Value,
                ["builders"] = LatticeOut.Each(executors.AssignedTo(plan), assignment =>
                {
                    JObject written = LatticeOut.Assignment(assignment);
                    if (BuilderOf(assignment) is { } by)
                    {
                        written["progress"] = LatticeOut.Progress(executors.ProgressOf(plan, by));
                    }

                    return written;
                }),
                ["progress"] = LatticeOut.Progress(executors.ProgressOf(plan)),
                ["unclaimed"] = executors.UnclaimedIn(plan),
                ["supply_toward"] = write.Quantities(executors.SupplyToward(plan)),
            };
        }

        if (LatticeIn.Given(args, "builder"))
        {
            if (read.Builder(args["builder"], "args.builder") is not { } id)
            {
                return null;
            }

            return new JObject
            {
                ["builder"] = executors.Find(id).TryGet(out L.Builder found) ? write.Builder(found) : null,
                ["plans"] = LatticeOut.Each(executors.PlansOf(id), plan => new JObject
                {
                    ["plan"] = plan.Value, ["progress"] = LatticeOut.Progress(executors.ProgressOf(plan, id))
                }),
            };
        }

        return LatticeOut.Each(executors.All, write.Builder);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static L.ExecutorId? BuilderOf(L.Assignment assignment) => assignment switch
    {
        L.Assignment.Asked asked => asked.By,
        L.Assignment.Accepted accepted => accepted.By,
        L.Assignment.Refused refused => refused.By,
        L.Assignment.Absent absent => absent.By,
        _ => null
    };

    private static JToken? Story(L.IGhostSystem system, LatticeIn read, LatticeOut write, JObject args)
    {
        if (LatticeIn.Given(args, "ghost"))
        {
            return read.Ghost(args["ghost"], "args.ghost") is { } ghost
                ? Result(system.Inspector.Story(ghost), read, "args.ghost", write.Story)
                : null;
        }

        return read.Plan(args["plan"], "args.plan") is { } plan
            ? Result(system.Inspector.Story(plan), read, "args.plan", write.Story)
            : null;
    }

    private static JToken? Catalogue(L.IGhostSystem system, LatticeIn read, LatticeOut write, JObject args, List<string> notes)
    {
        if (LatticeIn.Given(args, "piece"))
        {
            return read.Piece(args["piece"], "args.piece") is { } piece
                ? Result(system.Catalogue.Piece(piece), read, "args.piece", entry =>
                {
                    JObject written = write.PieceEntry(entry);
                    written["family"] = system.Catalogue.FamilyOf(piece).TryGet(out L.PieceFamily family) ? family.Value : null;
                    return written;
                })
                : null;
        }

        if (LatticeIn.Given(args, "kit"))
        {
            return read.Kit(args["kit"], "args.kit") is { } kit ? Result(system.Catalogue.Kit(kit), read, "args.kit", write.KitEntry) : null;
        }

        bool kits = args["list"] is JValue { Type: JTokenType.String } list && (string)list! == "kits";
        string? contains = args["name_contains"] is JValue { Type: JTokenType.String } text ? (string)text! : null;
        JArray listed = new JArray();
        int total = 0;
        if (kits)
        {
            foreach (L.KitEntry kit in system.Catalogue.Kits)
            {
                if (Matches(kit.PrefabName, kit.Name, contains) && total++ < MaximumListed)
                {
                    listed.Add(write.KitEntry(kit));
                }
            }
        }
        else
        {
            foreach (L.PieceEntry piece in system.Catalogue.Pieces)
            {
                if (Matches(piece.PrefabName, piece.Name, contains) && total++ < MaximumListed)
                {
                    listed.Add(write.PieceEntry(piece));
                }
            }
        }

        if (total > MaximumListed)
        {
            notes.Add($"{total - MaximumListed} more: narrow with name_contains.");
        }

        return new JObject { ["total"] = total, [kits ? "kits" : "pieces"] = listed };
    }

    private static bool Matches(string prefabName, string name, string? contains) =>
        contains == null || prefabName.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0 ||
        name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0;

    // A placement {piece, at, turn}, or a ghost's new piece where it stands ({ghost}).
    private static L.Placement? PlacementOf(L.IGhostSystem system, LatticeIn read, JObject args)
    {
        if (!LatticeIn.Given(args, "ghost"))
        {
            return read.Placement(args, "args");
        }

        if (read.Ghost(args["ghost"], "args.ghost") is not { } id)
        {
            return null;
        }

        if (system.Board.As(LatticeNames.Agent).Find(id).TryGet(out L.Ghost ghost))
        {
            switch (ghost.Intent)
            {
                case L.Intent.Build build:
                    return build.Target;
                case L.Intent.Swap swap:
                    return swap.New;
            }

            read.Problem("args.ghost", "is a ghost with no new piece (a removal or a finish)");
            return null;
        }

        read.Problem("args.ghost", $"no ghost {id.Value} on the board");
        return null;
    }

    private static JToken Modules(L.IGhostSystem system)
    {
        JObject intents = new JObject();
        foreach (L.IntentKind kind in new[] { L.IntentKind.Build, L.IntentKind.Remove, L.IntentKind.Swap, L.IntentKind.FinishOnly })
        {
            intents[LatticeOut.Snake(kind.ToString())] = system.Has(kind);
        }

        return new JObject
        {
            ["contract"] = $"{system.Version.Major}.{system.Version.Minor}",
            ["built_against"] = $"{L.ApiVersion.ContractMajor}.{L.ApiVersion.ContractMinor}",
            ["intents"] = intents,
            ["modules"] = LatticeOut.Each(system.Modules, module => new JObject
            {
                ["name"] = module.Name.Value,
                ["on"] = module.State is L.ModuleState.On,
                ["why_off"] = module.State is L.ModuleState.Off off ? LatticeOut.Reason(off.Why) : null
            }),
        };
    }

    private static JToken? World(L.IGhostSystem system, LatticeIn read, LatticeOut write, JObject args)
    {
        if (LatticeIn.Given(args, "structure"))
        {
            return read.Id(args["structure"], "args.structure") is { } id
                ? system.World.Structure(new L.ReferenceId(id)).TryGet(out L.StructureInfo info) ? write.StructureInfo(info) : JValue.CreateNull()
                : null;
        }

        if (LatticeIn.Given(args, "at"))
        {
            return read.Cell(args["at"], "args.at") is { } cell
                ? new JObject
                {
                    ["structures"] = LatticeOut.Each(system.World.At(cell), write.StructureInfo),
                    ["room"] = system.World.RoomAt(cell).TryGet(out L.RoomRef room) ? room.Value : null,
                    ["ghosts"] = LatticeOut.Each(system.Board.As(LatticeNames.Agent).At(cell), write.Ghost),
                }
                : null;
        }

        if (LatticeIn.Given(args, "select"))
        {
            return read.Structures(args["select"], "args.select") is { } which
                ? Result(system.World.Select(which), read, "args.select", refs => LatticeOut.Each(refs, write.Structure))
                : null;
        }

        if (LatticeIn.Given(args, "network_of"))
        {
            return read.Id(args["network_of"], "args.network_of") is { } piece
                ? system.World.NetworkOf(new L.ReferenceId(piece)).TryGet(out L.NetworkRef network) ? new JValue(network.Value) : JValue.CreateNull()
                : null;
        }

        if (LatticeIn.Given(args, "on"))
        {
            // The ghosts that point at a built structure (remove, swap, finish, or started on it).
            return read.Structure(args["on"], "args.on") is { } on
                ? LatticeOut.Each(system.Board.As(LatticeNames.Agent).On(on), write.Ghost)
                : null;
        }

        if (LatticeIn.Given(args, "assumed_from"))
        {
            return read.Structure(args["assumed_from"], "args.assumed_from") is { } assumed
                ? system.World.AssumedFrom(assumed).TryGet(out L.GhostId ghost) ? new JValue(ghost.Value) : JValue.CreateNull()
                : null;
        }

        read.Problem("args", "names structure, at, select, network_of, on or assumed_from");
        return null;
    }

    private static JToken? Pick(L.IGhostSystem system, LatticeIn read, JObject args)
    {
        L.WorldPoint? from = read.Point(args["from"], "args.from");
        L.WorldVector? direction = read.Vector(args["direction"], "args.direction");
        double? reach = LatticeIn.Given(args, "max_m") ? read.Number(args["max_m"], "args.max_m") : 100;
        if (from is not { } origin || direction is not { } way || reach is not { } metres)
        {
            return null;
        }

        if (read.MadeRef(Box(L.PickRay.Create(origin, way, (float)metres)), "args") is not { } boxed)
        {
            return null;
        }

        L.PickOptions options = new L.PickOptions
        {
            Drafts = args["drafts"] is JValue { Type: JTokenType.String } drafts && (string)drafts! == "exclude"
                ? L.DraftPicks.Exclude
                : L.DraftPicks.Include,
            BeyondClip = args["beyond_clip"] is JValue { Type: JTokenType.Boolean } beyond && (bool)beyond
                ? L.BeyondClipPicks.Include
                : L.BeyondClipPicks.Exclude,
        };
        if (LatticeIn.Given(args, "only") && read.Match(args["only"], "args.only") is { } only)
        {
            options = options with { Only = L.Option.Some(only) };
        }

        if (!system.Styles.Pick(boxed.Ray, options).TryGet(out L.PickHit hit))
        {
            return JValue.CreateNull();
        }

        return hit switch
        {
            L.PickHit.OnGhost ghost => new JObject
            {
                ["ghost"] = ghost.Ghost.Value, ["part"] = ghost.Part.ToString().ToLowerInvariant(), ["cell"] = LatticeOut.Cell(ghost.Cell),
                ["face"] = LatticeOut.Face(ghost.Face), ["point"] = LatticeOut.Point(ghost.Point), ["distance_m"] = Math.Round(ghost.Distance, 3)
            },
            L.PickHit.OnDraft draft => new JObject
            {
                ["drafts"] = draft.Drafts.Value, ["index"] = draft.Index, ["part"] = draft.Part.ToString().ToLowerInvariant(),
                ["cell"] = LatticeOut.Cell(draft.Cell), ["face"] = LatticeOut.Face(draft.Face), ["point"] = LatticeOut.Point(draft.Point),
                ["distance_m"] = Math.Round(draft.Distance, 3)
            },
            _ => new JValue(hit.GetType().Name)
        };
    }

    // PickRay is a struct; MadeRef wants a class, so it travels boxed.
    private static L.Result<RayBox, L.Reason> Box(L.Result<L.PickRay, L.Reason> made) =>
        made.TryGet(out L.PickRay ray)
            ? L.Result<RayBox, L.Reason>.Ok(new RayBox(ray))
            : made.TryGetError(out L.Reason why)
                ? L.Result<RayBox, L.Reason>.Error(why)
                : throw new InvalidOperationException("a result is a value or a reason");

    private sealed class RayBox
    {
        internal RayBox(L.PickRay ray) => Ray = ray;

        internal L.PickRay Ray { get; }
    }

    private static JToken Clip(L.ClipShape clip) => clip switch
    {
        L.ClipShape.Plane plane => new JObject
        {
            ["plane"] = new JObject { ["point"] = LatticeOut.Point(plane.Point), ["normal"] = LatticeOut.Vector(plane.Normal) }
        },
        L.ClipShape.Box box => new JObject { ["box"] = new JArray(LatticeOut.Point(box.Min), LatticeOut.Point(box.Max)) },
        _ => new JValue(clip.GetType().Name)
    };

    // ---- write lattice -------------------------------------------------------------------------------------------

    /// <summary>
    /// A Lattice command as the agent: without acts, Lattice's preview of it (nothing changes); with acts, submitted, and
    /// the change set it made (or that it went to the host, or Lattice's refusals). The agent's executor commands and the
    /// plan size limit act only with acts too.
    /// </summary>
    internal static LatticeAnswer Command(JToken command, bool acts)
    {
        if (LatticeBoard.Current is not { } system)
        {
            return new LatticeAnswer(new List<string> { "command: Lattice runs no world" });
        }

        LatticeIn read = new LatticeIn(system);
        LatticeOut write = Out(system);
        if (command is JObject { Count: 1 } named && Array.IndexOf(AgentCommands, First(named)) >= 0)
        {
            return AgentCommand(system, read, First(named), named[First(named)], acts);
        }

        LatticeLookIn looks = new LatticeLookIn(read);
        L.Command? parsed = read.Command(command, "command", looks);
        if (parsed == null || read.Failed)
        {
            return new LatticeAnswer(read.Problems);
        }

        List<string> notes = new List<string>();
        foreach (string look in looks.Looks)
        {
            LatticeLooks.Ensure(system, look, notes);
        }

        JObject reply = new JObject();
        if (!acts)
        {
            JObject asked = new JObject { ["command"] = command.DeepClone() };
            reply["outcome"] = "report";
            reply["preview"] = Preview(system, read, write, asked, reply);
            if (reply["pending"]?.Type == JTokenType.Boolean && (bool)reply["pending"]!)
            {
                reply.Remove("pending");
                notes.Add("Lattice is still working the preview out: ask again in a moment.");
            }

            reply["notes"] = new JArray(notes.ToArray());
            return new LatticeAnswer(reply);
        }

        switch (LatticeBoard.SubmitAny(system, parsed, LatticeNames.Agent))
        {
            case L.Submission.Answered { Outcome: L.Outcome.Applied applied }:
                reply["outcome"] = "applied";
                reply["change_set"] = write.ChangeSet(applied.Changes);
                break;
            case L.Submission.Answered { Outcome: L.Outcome.Refused refused }:
                reply["outcome"] = "refused";
                reply["refusals"] = write.Refusals(refused.Why);
                break;
            case L.Submission.Sent sent:
                reply["outcome"] = "sent";
                reply["ticket"] = sent.Ticket.Command.Value.ToString();
                notes.Add("Lattice is working the command out (or sent it to the host): its outcome comes as an event " +
                          "(find lattice {ask: events}).");
                break;
        }

        reply["notes"] = new JArray(notes.ToArray());
        return new LatticeAnswer(reply);
    }

    private static string First(JObject named)
    {
        foreach (KeyValuePair<string, JToken?> entry in named)
        {
            return entry.Key;
        }

        return "";
    }

    // The executor StationGod's agent works ghosts as, registered once per world on first use (host only).
    private static L.IExecutorHandle? _executor;
    private static L.IGhostSystem? _executorFor;
    private static readonly Dictionary<long, L.Claim> Claims = new Dictionary<long, L.Claim>();

    private static LatticeAnswer AgentCommand(L.IGhostSystem system, LatticeIn read, string kind, JToken? payload, bool acts)
    {
        string path = "command." + kind;
        JObject reply = new JObject { ["outcome"] = acts ? "applied" : "report" };
        if (kind == "limit_plan_size")
        {
            if (read.Whole(payload, path) is not { } most)
            {
                return new LatticeAnswer(read.Problems);
            }

            if (acts)
            {
                L.Result<IDisposable, L.Reason> limited = system.Registries.LimitPlanSize(most);
                if (limited.TryGetError(out L.Reason refusedWhy))
                {
                    reply["outcome"] = "refused";
                    reply["refusals"] = new JArray(LatticeOut.Reason(refusedWhy));
                }
            }

            reply["notes"] = new JArray($"Plans hold at most {most} ghosts for this world.");
            return new LatticeAnswer(reply);
        }

        if (Array.IndexOf(BuilderCommands, kind) >= 0)
        {
            return read.Needs(LatticeOut.BuildersMinor, path)
                ? BuilderCommand(system, read, kind, payload, acts)
                : new LatticeAnswer(read.Problems);
        }

        JObject? fields = read.Object(payload, path);
        L.GhostId? ghost = fields != null ? read.Ghost(fields["ghost"], path + ".ghost") : null;
        if (ghost is not { } id || read.Failed)
        {
            return new LatticeAnswer(read.Problems);
        }

        L.Result<L.Claim, L.ClaimRefused>? claimed = null;
        L.Result<L.Unit, L.Reason>? reported = null;
        L.ExecutorStatus? status = null;
        if (kind == "report_status")
        {
            status = Status(read, fields!["status"], path + ".status");
            if (status == null)
            {
                return new LatticeAnswer(read.Problems);
            }
        }

        double lease = 60;
        if (kind == "claim" && LatticeIn.Given(fields, "lease_s") && read.Number(fields!["lease_s"], path + ".lease_s") is { } seconds)
        {
            lease = seconds;
        }

        if (read.Failed)
        {
            return new LatticeAnswer(read.Problems);
        }

        if (!acts)
        {
            reply["notes"] = new JArray($"{kind} ghost {id.Value} as StationGod's agent executor: nothing done until confirm.");
            return new LatticeAnswer(reply);
        }

        if (Executor(system, out string? why) is not { } executor)
        {
            reply["outcome"] = "refused";
            reply["refusals"] = new JArray(why ?? "");
            return new LatticeAnswer(reply);
        }

        switch (kind)
        {
            case "claim":
                if (read.Made(L.Lease.Create(lease), path + ".lease_s") is not { } length)
                {
                    return new LatticeAnswer(read.Problems);
                }

                claimed = executor.Claim(id, length);
                break;
            case "renew":
                claimed = Claims.TryGetValue(id.Value, out L.Claim held)
                    ? executor.Renew(held)
                    : L.Result<L.Claim, L.ClaimRefused>.Error(new L.ClaimRefused(Code("not-claimed", id), L.Option<L.ExecutorId>.None));
                break;
            case "release":
                if (Claims.TryGetValue(id.Value, out L.Claim released))
                {
                    executor.Release(released);
                    Claims.Remove(id.Value);
                }

                break;
            default:
                reported = executor.Report(id, status!);
                break;
        }

        if (claimed is { } claim)
        {
            if (claim.TryGet(out L.Claim made))
            {
                Claims[id.Value] = made;
                reply["claim"] = new JObject { ["ghost"] = id.Value, ["until_s"] = made.Until.Seconds };
            }
            else if (claim.TryGetError(out L.ClaimRefused refusal))
            {
                reply["outcome"] = "refused";
                reply["refusals"] = new JArray(LatticeOut.Reason(refusal.Why));
                if (refusal.HeldBy.TryGet(out L.ExecutorId holder))
                {
                    reply["held_by"] = holder.Value;
                }
            }
        }

        if (reported is { } done && done.TryGetError(out L.Reason failed))
        {
            reply["outcome"] = "refused";
            reply["refusals"] = new JArray(LatticeOut.Reason(failed));
        }

        return new LatticeAnswer(reply);
    }

    // Contract 0.22: the agent's word on itself as a builder. locate {sphere: {at, build_reach, source_range}} or null;
    // say "ready" or {stuck: why}; offer {reaches, toward}; answer {plan, answer: "accepted" or {refused: why}}.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static LatticeAnswer BuilderCommand(L.IGhostSystem system, LatticeIn read, string kind, JToken? payload, bool acts)
    {
        string path = "command." + kind;
        L.Option<L.BuilderArea>? area = null;
        L.BuilderWord? word = null;
        L.SupplyReport? supply = null;
        L.PlanId? plan = null;
        L.AssignmentAnswer? answer = null;
        switch (kind)
        {
            case "locate":
                area = read.Area(payload, path);
                break;
            case "say":
                if (read.Case(payload, path, "ready", "stuck") is { } said)
                {
                    word = said.Kind == "ready"
                        ? new L.BuilderWord.Ready()
                        : read.Text(said.Payload, path + ".stuck") is { } stuck ? new L.BuilderWord.Stuck(Code("stuck", null, stuck)) : null;
                }

                break;
            case "offer":
                supply = read.Supply(payload, path);
                break;
            default:
                if (read.Object(payload, path) is { } answered)
                {
                    plan = read.Plan(answered["plan"], path + ".plan");
                    if (read.Case(answered["answer"], path + ".answer", "accepted", "refused") is { } given)
                    {
                        answer = given.Kind == "accepted"
                            ? new L.AssignmentAnswer.Accepted()
                            : read.Text(given.Payload, path + ".answer.refused") is { } refused
                                ? new L.AssignmentAnswer.Refused(Code("refused", null, refused))
                                : null;
                    }
                }

                break;
        }

        if (read.Failed)
        {
            return new LatticeAnswer(read.Problems);
        }

        JObject reply = new JObject { ["outcome"] = acts ? "applied" : "report" };
        if (!acts)
        {
            reply["notes"] = new JArray($"{kind} as StationGod's agent builder: nothing done until confirm.");
            return new LatticeAnswer(reply);
        }

        if (Executor(system, out string? why) is not { } executor)
        {
            reply["outcome"] = "refused";
            reply["refusals"] = new JArray(why);
            return new LatticeAnswer(reply);
        }

        L.Result<L.Unit, L.Reason>? done = null;
        switch (kind)
        {
            case "locate":
                executor.Locate(area!.Value);
                break;
            case "say":
                executor.Say(word!);
                break;
            case "offer":
                done = executor.Offer(supply!);
                break;
            default:
                done = executor.Answer(plan!.Value, answer!);
                break;
        }

        if (done is { } result && result.TryGetError(out L.Reason failed))
        {
            reply["outcome"] = "refused";
            reply["refusals"] = new JArray(LatticeOut.Reason(failed));
        }

        return new LatticeAnswer(reply);
    }

    private static L.IExecutorHandle? Executor(L.IGhostSystem system, out string? why)
    {
        why = null;
        if (ReferenceEquals(_executorFor, system) && _executor != null)
        {
            return _executor;
        }

        Claims.Clear();
        _executorFor = system;
        L.ExecutorInfo info = new L.ExecutorInfo(LatticeNames.Must(L.ExecutorId.Create(LatticeNames.Mod, "agent")),
            LatticeNames.Must(L.DisplayName.Create("StationGod agent")));
        if (system.Version.Major == L.ApiVersion.ContractMajor && system.Version.Minor >= LatticeOut.BuildersMinor)
        {
            info = BuildsForTheAgent(info);
        }
        L.Result<L.IExecutorHandle, L.Reason> registered = system.Executors.Register(info);
        if (registered.TryGet(out L.IExecutorHandle handle))
        {
            _executor = handle;
            return handle;
        }

        registered.TryGetError(out L.Reason reason);
        why = LatticeText.Of(reason);
        _executor = null;
        return null;
    }

    // Contract 0.22: the agent builds for itself (its own plans), any piece.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static L.ExecutorInfo BuildsForTheAgent(L.ExecutorInfo info) => info with { Owner = L.Option.Some(LatticeNames.Agent) };

    private static L.ExecutorStatus? Status(LatticeIn read, JToken? token, string path)
    {
        if (read.Case(token, path, "working", "waiting", "paused") is not { } status)
        {
            return null;
        }

        switch (status.Kind)
        {
            case "paused":
                return new L.ExecutorStatus.Paused();
            case "working":
            {
                if (read.Object(status.Payload, path + ".working") is not { } working)
                {
                    return null;
                }

                int? step = read.Whole(working["step"], path + ".working.step");
                int? of = read.Whole(working["of"], path + ".working.of");
                return step is { } s && of is { } o ? new L.ExecutorStatus.Working(s, o) : null;
            }
            default:
                return read.Text(status.Payload, path + ".waiting") is { } why
                    ? new L.ExecutorStatus.Waiting(Code("waiting", null, why))
                    : null;
        }
    }

    private static L.Reason Code(string code, L.GhostId? ghost, string? why = null)
    {
        L.ValueMap<string, string> facts = L.ValueMap<string, string>.Empty;
        if (ghost is { } id)
        {
            facts = facts.With("ghost", id.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (why != null)
        {
            facts = facts.With("why", why);
        }

        return L.Reason.Of(LatticeNames.Must(L.ReasonCode.Create(LatticeNames.Mod, code)),
            LatticeNames.Must(L.RuleId.Create(LatticeNames.Mod, "agent")), facts);
    }

    // ---- show lattice --------------------------------------------------------------------------------------------

    private static readonly Dictionary<string, L.IStyleLayer> Layers = new Dictionary<string, L.IStyleLayer>(StringComparer.Ordinal);
    private static readonly List<L.IClip> Clips = new List<L.IClip>();
    private static readonly List<IDisposable> Highlights = new List<IDisposable>();
    private static L.IGhostSystem? _viewFor;

    private static readonly Dictionary<string, Func<L.StyleLayerDescriptor>> LibraryLayers =
        new Dictionary<string, Func<L.StyleLayerDescriptor>>(StringComparer.Ordinal)
        {
            ["blueprint"] = static () => L.LibraryLayers.Blueprint,
            ["step_numbers"] = static () => L.LibraryLayers.StepNumbers,
            ["bill_counts"] = static () => L.LibraryLayers.BillCounts,
            ["reasons"] = static () => L.LibraryLayers.Reasons,
            ["plan_names"] = static () => L.LibraryLayers.PlanNames,
        };

    /// <summary>One part of this seat's Lattice view (layer, set, drafts, clip, highlight); never synced or saved.</summary>
    internal static LatticeAnswer View(string part, JObject given)
    {
        if (LatticeBoard.Current is not { } system)
        {
            return new LatticeAnswer(new List<string> { part + ": Lattice runs no world" });
        }

        if (!ReferenceEquals(_viewFor, system))
        {
            // Registrations end with their world.
            Layers.Clear();
            Clips.Clear();
            Highlights.Clear();
            _viewFor = system;
        }

        LatticeIn read = new LatticeIn(system);
        List<string> notes = new List<string>();
        string? done = part switch
        {
            "layer" => Layer(system, read, given, notes),
            "set" => Set(system, read, given),
            "drafts" => Drafts(system, read, given, notes),
            "clip" => ClipView(system, read, given),
            "highlight" => Highlight(system, read, given),
            _ => null
        };

        if (read.Failed || done == null)
        {
            return new LatticeAnswer(read.Failed ? read.Problems : new List<string> { $"{part}: is layer, set, drafts, clip or highlight" });
        }

        JArray layers = new JArray();
        foreach (KeyValuePair<string, L.IStyleLayer> layer in Layers)
        {
            layers.Add(layer.Value.Enabled ? layer.Key : layer.Key + " (off)");
        }

        return new LatticeAnswer(new JObject { ["done"] = done, ["layers"] = layers, ["notes"] = new JArray(notes.ToArray()) });
    }

    private static string? Layer(L.IGhostSystem system, LatticeIn read, JObject given, List<string> notes)
    {
        if (LatticeIn.Given(given, "add"))
        {
            if (read.Layer(given["add"], "layer.add") is not { } layer)
            {
                return null;
            }

            string name = layer.Id.Value;
            if (Layers.TryGetValue(name, out L.IStyleLayer? old))
            {
                old.Dispose();
                Layers.Remove(name);
                notes.Add($"The layer {name} was replaced.");
            }

            L.Result<L.IStyleLayer, L.Reason> added = system.Styles.Add(layer);
            if (!added.TryGet(out L.IStyleLayer handle))
            {
                added.TryGetError(out L.Reason why);
                read.Problem("layer.add", $"Lattice refused it ({LatticeText.Of(why)})");
                return null;
            }

            Layers[name] = handle;
            return $"added {name}";
        }

        if (LatticeIn.Given(given, "library"))
        {
            if (read.Text(given["library"], "layer.library") is not { } library)
            {
                return null;
            }

            if (!LibraryLayers.TryGetValue(library, out Func<L.StyleLayerDescriptor>? descriptor))
            {
                read.Problem("layer.library", $"is one of {string.Join(", ", LibraryLayers.Keys)}");
                return null;
            }

            bool enabled = !LatticeIn.Given(given, "enabled") || (read.Flag(given["enabled"], "layer.enabled") ?? true);
            string key = "library " + library;
            if (!Layers.TryGetValue(key, out L.IStyleLayer? handle))
            {
                L.Result<L.IStyleLayer, L.Reason> added = system.Styles.Add(descriptor());
                if (!added.TryGet(out handle))
                {
                    added.TryGetError(out L.Reason why);
                    read.Problem("layer.library", $"Lattice refused it ({LatticeText.Of(why)})");
                    return null;
                }

                Layers[key] = handle;
            }

            if (enabled)
            {
                handle.Enable();
            }
            else
            {
                handle.Disable();
            }

            return $"{key} {(enabled ? "on" : "off")}";
        }

        foreach (string action in new[] { "enable", "disable", "remove" })
        {
            if (!LatticeIn.Given(given, action))
            {
                continue;
            }

            if (read.Text(given[action], "layer." + action) is not { } name)
            {
                return null;
            }

            string key = Layers.ContainsKey(name) ? name : "stationgod/agent-" + name;
            if (!Layers.TryGetValue(key, out L.IStyleLayer? handle))
            {
                read.Problem("layer." + action, $"no layer {name} added here: {string.Join(", ", Layers.Keys)}");
                return null;
            }

            switch (action)
            {
                case "enable":
                    handle.Enable();
                    break;
                case "disable":
                    handle.Disable();
                    break;
                default:
                    handle.Dispose();
                    Layers.Remove(key);
                    break;
            }

            return $"{action}d {key}";
        }

        read.Problem("layer", "is {add: layer}, {enable|disable|remove: name} or {library: name, enabled}");
        return null;
    }

    private static string? Set(L.IGhostSystem system, LatticeIn read, JObject given)
    {
        L.StyleSetId? id = read.SetId(given["id"], "set.id");
        if (id is not { } setId)
        {
            return null;
        }

        bool structures = LatticeIn.Given(given, "structures") && (read.Flag(given["structures"], "set.structures") ?? false);
        string[] actions = { "put", "replace", "add", "remove", "clear" };
        foreach (string action in actions)
        {
            if (!LatticeIn.Given(given, action))
            {
                continue;
            }

            string path = "set." + action;
            if (structures)
            {
                if (read.MadeRef(system.Styles.StructureSet(setId), "set.id") is not { } set)
                {
                    return null;
                }

                if (action == "clear")
                {
                    set.Clear();
                }
                else if (action == "put")
                {
                    if (read.Structure(given[action], path) is not { } one)
                    {
                        return null;
                    }

                    set.Put(one);
                }
                else
                {
                    L.ValueList<L.StructureRef> refs = read.EachValue(given[action], path, read.Structure);
                    if (read.Failed)
                    {
                        return null;
                    }

                    if (action == "replace") set.Replace(refs);
                    else if (action == "add") set.Add(refs);
                    else set.Remove(refs);
                }

                return $"structure set {setId.Value}: {action}, {set.Count} in it";
            }

            if (read.MadeRef(system.Styles.Set(setId), "set.id") is not { } ghosts)
            {
                return null;
            }

            if (action == "clear")
            {
                ghosts.Clear();
            }
            else if (action == "put")
            {
                if (read.Ghost(given[action], path) is not { } one)
                {
                    return null;
                }

                ghosts.Put(one);
            }
            else
            {
                L.ValueList<L.GhostId> ids = read.EachValue(given[action], path, read.Ghost);
                if (read.Failed)
                {
                    return null;
                }

                if (action == "replace") ghosts.Replace(ids);
                else if (action == "add") ghosts.Add(ids);
                else ghosts.Remove(ids);
            }

            return $"set {setId.Value}: {action}, {ghosts.Count} in it";
        }

        read.Problem("set", "names one of put, replace, add, remove or clear");
        return null;
    }

    private static string? Drafts(L.IGhostSystem system, LatticeIn read, JObject given, List<string> notes)
    {
        L.DraftsId? id = read.Text(given["id"], "drafts.id") is { } name
            ? read.Made(L.DraftsId.Create(LatticeNames.Mod, name), "drafts.id")
            : null;
        if (id is not { } draftsId || read.MadeRef(system.Styles.Drafts(draftsId), "drafts.id") is not { } drafts)
        {
            return null;
        }

        LatticeLookIn looks = new LatticeLookIn(read);
        if (LatticeIn.Given(given, "show"))
        {
            L.ValueList<L.GhostDraft> shown = read.Each(given["show"], "drafts.show", (draft, path) => read.Draft(draft, path, looks));
            if (read.Failed)
            {
                return null;
            }

            foreach (string look in looks.Looks)
            {
                LatticeLooks.Ensure(system, look, notes);
            }

            drafts.Show(shown);
            return $"drafts {draftsId.Value}: {shown.Count} shown";
        }

        if (LatticeIn.Given(given, "preview"))
        {
            if (read.Command(given["preview"], "drafts.preview", looks) is not { } command)
            {
                return null;
            }

            foreach (string look in looks.Looks)
            {
                LatticeLooks.Ensure(system, look, notes);
            }

            L.Answer<L.Result<L.Preview, L.ValueList<L.Refusal>>> answer = system.Preview.Of(command, LatticeNames.Agent);
            if (answer is not L.Answer<L.Result<L.Preview, L.ValueList<L.Refusal>>>.Ready ready)
            {
                notes.Add("Lattice is still working the preview out: call again in a moment to draw it.");
                return $"drafts {draftsId.Value}: preview pending";
            }

            if (!ready.Value.TryGet(out L.Preview preview))
            {
                ready.Value.TryGetError(out L.ValueList<L.Refusal> refusals);
                notes.Add($"Lattice refused the command: {LatticeText.Of(refusals)}.");
                return $"drafts {draftsId.Value}: refused";
            }

            drafts.Show(preview);
            return $"drafts {draftsId.Value}: preview shown";
        }

        if (LatticeIn.Given(given, "clear"))
        {
            drafts.Clear();
            return $"drafts {draftsId.Value}: cleared";
        }

        read.Problem("drafts", "is {id, show: [drafts]}, {id, preview: command} or {id, clear: true}");
        return null;
    }

    private static string? ClipView(L.IGhostSystem system, LatticeIn read, JObject given)
    {
        if (LatticeIn.Given(given, "clear"))
        {
            foreach (L.IClip clip in Clips)
            {
                clip.Dispose();
            }

            int count = Clips.Count;
            Clips.Clear();
            return $"{count} clip(s) removed";
        }

        if (LatticeIn.Given(given, "move"))
        {
            if (read.Clip(given["move"], "clip.move") is not { } shape)
            {
                return null;
            }

            if (Clips.Count == 0)
            {
                read.Problem("clip.move", "no clip of StationGod's to move: give a shape to install one");
                return null;
            }

            Clips[Clips.Count - 1].Move(shape);
            return "clip moved";
        }

        if (read.Clip(given, "clip") is not { } installed)
        {
            return null;
        }

        L.Result<L.IClip, L.Reason> made = system.Styles.Clip(installed);
        if (!made.TryGet(out L.IClip handle))
        {
            made.TryGetError(out L.Reason why);
            read.Problem("clip", $"Lattice refused it ({LatticeText.Of(why)})");
            return null;
        }

        Clips.Add(handle);
        return "clip installed";
    }

    private static string? Highlight(L.IGhostSystem system, LatticeIn read, JObject given)
    {
        L.HighlightTargets? targets = read.Highlighted(given["targets"], "highlight.targets");
        L.Style? style = LatticeIn.Given(given, "style") ? read.Style(given["style"], "highlight.style") : LatticeLooks.LookHere;
        L.Duration? lasting = read.Duration(LatticeIn.Given(given, "seconds") ? given["seconds"] : new JValue(10), "highlight.seconds");
        if (targets == null || style == null || lasting is not { } seconds)
        {
            return null;
        }

        L.Result<IDisposable, L.Reason> lit = system.Styles.Highlight(new L.HighlightRequest(targets, style, seconds));
        if (!lit.TryGet(out IDisposable handle))
        {
            lit.TryGetError(out L.Reason why);
            read.Problem("highlight", $"Lattice refused it ({LatticeText.Of(why)})");
            return null;
        }

        Highlights.Add(handle);
        return $"highlighted for {seconds.Seconds:0.#} s";
    }
}
