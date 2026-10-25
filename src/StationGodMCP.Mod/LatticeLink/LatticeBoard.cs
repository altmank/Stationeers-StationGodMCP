#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.LatticeLink.Model;
using StationGodMCP.Net;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using StationGodMCP.Pure.Verbs;
using UnityEngine;
using L = Lattice.Api;

namespace StationGodMCP.LatticeLink;

/// <summary>
/// StationGod's ghost board on Lattice. Lattice keeps the ghosts (saved, synced to its clients, matched against what is
/// built, seen by Loom and every other mod); StationGod changes them only through Lattice commands and mirrors Lattice's
/// build ghosts into GhostBoardStore's board, which find ghost reads, the ghost tools pick from, and StationGod clients
/// without Lattice are sent. The agent's ghosts go into a plan of its own ("StationGod agent"), a player's into
/// Lattice's default plan. Every member here names Lattice types: reach it only after LatticePresence.ApiLoaded.
/// Main thread.
/// </summary>
internal static class LatticeBoard
{
    private static L.IGhostSystem? _system;

    // The system whose events StationGod listens to; a board reset within one world keeps it (no second subscription).
    private static L.IGhostSystem? _heard;

    private static L.PlanId? _agentPlan;

    // Change sets StationGod mirrored from a command's answer, so their events (raised later in the frame) are skipped.
    // Another mod's change of the same frame still comes as an event: only these ids are skipped.
    private static readonly HashSet<long> Answered = new HashSet<long>();

    internal static bool IsAttached => _system != null;

    // show highlight's lit ghosts, ended by the next call without keep or by clear.
    private static readonly List<System.IDisposable> Lit = new List<System.IDisposable>();

    /// <summary>Lights the ghosts for the seconds in Lattice's highlight band; null when lit, else Lattice's reason.</summary>
    internal static string? Highlight(List<long> ids, double seconds)
    {
        if (_system is not { } system)
        {
            return "Lattice runs no world";
        }

        List<L.GhostId> which = new List<L.GhostId>(ids.Count);
        foreach (long id in ids)
        {
            which.Add(new L.GhostId(id));
        }

        L.Result<L.Duration, L.Reason> duration = L.Duration.Create((float)System.Math.Max(seconds, 0.1));
        if (!duration.TryGet(out L.Duration lasting))
        {
            return "the seconds are not a duration";
        }

        L.Result<System.IDisposable, L.Reason> lit = system.Styles.Highlight(new L.HighlightRequest(
            new L.HighlightTargets.Ghosts(L.ValueList.From(which)), LatticeLooks.LookHere, lasting));
        if (lit.TryGet(out System.IDisposable handle))
        {
            Lit.Add(handle);
            return null;
        }

        return lit.TryGetError(out L.Reason why) ? LatticeText.Of(why) : "refused";
    }

    /// <summary>
    /// A board ghost's ports where it stands, as Lattice reads them (Catalogue.PortsAt, contract 0.8): null below 0.8,
    /// for a ghost Lattice does not hold, or one with no new piece; the caller reads the prefab itself then.
    /// </summary>
    internal static List<GhostPort>? Ports(long id)
    {
        if (_system is not { } system || system.Version.Minor < PortsMinor ||
            !system.Board.As(LatticeNames.Agent).Select(new L.Selector.Ids(L.ValueList.Of(new L.GhostId(id))))
                .TryGet(out L.ValueList<L.Ghost> found) || found.Count == 0)
        {
            return null;
        }

        L.Placement? placement = found[0].Intent switch
        {
            L.Intent.Build build => build.Target,
            L.Intent.Swap swap => swap.New,
            _ => null
        };
        if (placement == null || !system.Catalogue.PortsAt(placement).TryGet(out L.ValueList<L.Port> ports))
        {
            return null;
        }

        List<GhostPort> read = new List<GhostPort>(ports.Count);
        foreach (L.Port port in ports)
        {
            Vector3 joins = PieceShapes.CentreOf(PieceShapes.Cell(new Grid3(port.Joins.X, port.Joins.Y, port.Joins.Z)));
            read.Add(new GhostPort(port.Index.Value, Bodies.V(joins), LatticeGhosts.IntoPort(port.Toward), port.GameName,
                LatticeGhosts.RoleOf(port.Role)));
        }

        return read;
    }

    /// <summary>What the ghosts take to build, as Lattice's bills read it; null with why when Lattice will not say.</summary>
    internal static GhostBill? Bill(List<long> ids, out string? why)
    {
        why = null;
        if (_system is not { } system)
        {
            why = "Lattice runs no world";
            return null;
        }

        List<L.GhostId> which = new List<L.GhostId>(ids.Count);
        foreach (long id in ids)
        {
            which.Add(new L.GhostId(id));
        }

        L.Result<L.Bill, L.Reason> asked = system.Bills.For(new L.Selector.Ids(L.ValueList.From(which)));
        if (!asked.TryGet(out L.Bill bill))
        {
            why = asked.TryGetError(out L.Reason reason) ? LatticeText.Of(reason) : "refused";
            return null;
        }

        List<(int Hash, int Count)> needed = new List<(int Hash, int Count)>(bill.Needed.Count);
        foreach (L.ItemQuantity item in bill.Needed)
        {
            needed.Add((item.Item.Hash, item.Count));
        }

        List<(int Hash, int Count)> back = new List<(int Hash, int Count)>(bill.GivenBack.Count);
        foreach (L.ItemQuantity item in bill.GivenBack)
        {
            back.Add((item.Item.Hash, item.Count));
        }

        List<int> tools = new List<int>();
        foreach (L.ItemKey tool in bill.Tools)
        {
            tools.Add(tool.Hash);
        }

        return new GhostBill(needed, back, tools);
    }

    // The contract minor that reads a placed piece's ports (IPieceCatalogue.PortsAt).
    private const int PortsMinor = 8;

    /// <summary>Ends every highlight show highlight lit.</summary>
    internal static void EndHighlights()
    {
        foreach (System.IDisposable handle in Lit)
        {
            handle.Dispose();
        }

        Lit.Clear();
    }

    /// <summary>Whether Lattice draws its ghosts itself (its render module runs): StationGod then only picks them.</summary>
    internal static bool Draws { get; private set; }

    /// <summary>The running Lattice's contract version, or null outside a world.</summary>
    internal static string? Version => _system is { } system ? $"{system.Version.Major}.{system.Version.Minor}" : null;

    internal static void Tick()
    {
        if (_system != null || !L.LatticeSystem.Current.TryGet(out L.IGhostSystem system))
        {
            return;
        }

        Attach(system);
    }

    internal static void Detach()
    {
        _system = null;
        _heard = null;
        _agentPlan = null;
        Answered.Clear();
        Draws = false;
        Ghosts.BoardDrawnElsewhere = false;
    }

    /// <summary>
    /// Places ghosts for an actor: the agent's into its plan, a player's (this game's, or a StationGod client's by its
    /// client id) into the default plan. On the host the ghosts Lattice added, mirrored; on a client none (Lattice sends
    /// the command to the host, and its changes arrive as events). Each placement refused names its index.
    /// </summary>
    internal static List<BoardGhost> Place(IReadOnlyList<GhostPlacement> placements, GhostSource source, ulong? client,
        List<string> refused, List<string> notes, IReadOnlyList<GhostLook>? looks = null)
    {
        List<BoardGhost> added = new List<BoardGhost>();
        if (_system is not { } system)
        {
            refused.Add("Lattice runs no world");
            return added;
        }

        bool agent = source == GhostSource.Agent;
        L.Maker maker = agent ? LatticeNames.AgentMaker : LatticeNames.PlayerMaker;
        L.Option<L.PlanId> plan = agent && AgentPlan(system) is { } id ? L.Option.Some(id) : L.Option<L.PlanId>.None;
        List<L.GhostDraft> drafts = new List<L.GhostDraft>(placements.Count);
        for (int index = 0; index < placements.Count; index++)
        {
            GhostPlacement placement = placements[index];
            if (!(Prefab.Find(placement.PrefabHash) is Structure prefab) || prefab == null)
            {
                refused.Add($"{index}: no structure prefab with hash {placement.PrefabHash}");
                continue;
            }

            GhostLook look = looks != null && index < looks.Count ? looks[index] : GhostLook.Plain;
            L.GhostDraft? draft = LatticeGhosts.ToDraft(placement, maker, plan, LastState(prefab), notes,
                out string? why, look);
            if (draft == null)
            {
                refused.Add($"{index}: {why}");
                continue;
            }

            drafts.Add(draft);
        }

        if (drafts.Count == 0)
        {
            return added;
        }

        L.Command command = agent && plan.IsNone
            ? new L.Command.CreatePlan(new L.PlanDraft(LatticeNames.AgentPlan, LatticeNames.AgentMaker,
                new L.Visibility.Everyone(), L.ValueMap<L.AttachmentKey, L.Attachment>.Empty))
            {
                Ghosts = L.ValueList.From(drafts)
            }
            : new L.Command.Place(L.ValueList.From(drafts));
        L.Actor actor = agent ? LatticeNames.Agent : new L.Actor.Player(new L.ClientId(client ?? NetworkManager.LocalClientId));
        if (Submit(system, command, actor, refused) is { } changes)
        {
            if (agent && plan.IsNone && changes.PlansOpened.Count > 0)
            {
                _agentPlan = changes.PlansOpened[0].Id;
            }

            foreach (L.Ghost ghost in changes.Added)
            {
                if (ToBoard(ghost) is { } mirrored)
                {
                    added.Add(mirrored);
                }
            }
        }

        return added;
    }

    /// <summary>Takes ghosts off by id for an actor; the ids Lattice took off (none on a client: they arrive as events).</summary>
    internal static List<long> Remove(IEnumerable<long> ids, GhostSource source, ulong? client, List<string> refused)
    {
        List<long> removed = new List<long>();
        List<L.GhostId> which = new List<L.GhostId>();
        foreach (long id in ids)
        {
            which.Add(new L.GhostId(id));
        }

        if (_system is not { } system || which.Count == 0)
        {
            return removed;
        }

        L.Actor actor = source == GhostSource.Agent
            ? LatticeNames.Agent
            : new L.Actor.Player(new L.ClientId(client ?? NetworkManager.LocalClientId));
        if (Submit(system, new L.Command.Remove(new L.Selector.Ids(L.ValueList.From(which))), actor, refused) is
            { } changes)
        {
            foreach (L.GhostRemoval removal in changes.Removed)
            {
                removed.Add(removal.Ghost.Id.Value);
            }
        }

        return removed;
    }

    /// <summary>
    /// Lattice's validation of ghosts, one check per ghost; null with the reason in note while Lattice is still
    /// working it out (ask again) or when it refuses.
    /// </summary>
    internal static Dictionary<long, GhostCheck>? Check(IReadOnlyList<long> ids, out string? note)
    {
        note = null;
        if (_system is not { } system)
        {
            note = "Lattice runs no world.";
            return null;
        }

        List<L.GhostId> which = new List<L.GhostId>(ids.Count);
        foreach (long id in ids)
        {
            which.Add(new L.GhostId(id));
        }

        L.Result<L.Answer<L.ValidationReport>, L.Reason> asked =
            system.Validation.Check(new L.Selector.Ids(L.ValueList.From(which)));
        if (asked.TryGetError(out L.Reason why))
        {
            note = $"Lattice refused the check: {LatticeText.Of(why)}.";
            return null;
        }

        asked.TryGet(out L.Answer<L.ValidationReport> answer);
        if (answer is L.Answer<L.ValidationReport>.Ready ready)
        {
            return GhostCheck.PerGhost(ready.Value.Findings);
        }

        note = "Lattice is still checking these ghosts: ask again in a moment.";
        return null;
    }

    private static void Attach(L.IGhostSystem system)
    {
        // Turn.Axes, Turn.Of and Selector.Everything came with contract 0.2.
        if (system.Version.Major != L.ApiVersion.ContractMajor || system.Version.Minor < 2)
        {
            OnceLog.Warning("lattice_version",
                $"Lattice {system.Version.Major}.{system.Version.Minor} runs; StationGod needs contract " +
                $"{L.ApiVersion.ContractMajor}.2 or later (minor). Ghosts stay off until Lattice is updated.");
            return;
        }

        _system = system;
        _agentPlan = null;
        Answered.Clear();
        if (!ReferenceEquals(_heard, system))
        {
            _heard = system;
            system.Events.Changed += OnChanged;
            system.Events.BoardReset += OnBoardReset;
            if (system.Styles.Add(LatticeLooks.Layer).TryGetError(out L.Reason why))
            {
                OnceLog.Warning("lattice_look", $"Lattice refused StationGod's ghost look ({LatticeText.Of(why)}): " +
                                                "its ghosts draw in Lattice's default look.");
            }
        }

        Draws = false;
        foreach (L.ModuleStatus module in system.Modules)
        {
            if (module.Name == LatticeNames.Render && module.State is L.ModuleState.On)
            {
                Draws = true;
            }
        }

        Ghosts.BoardDrawnElsewhere = Draws;
        GhostBoardStore.FromLattice(true, AllBuildGhosts(system), new List<long>());
        StationGodMod.Log($"Ghosts are Lattice's (contract {system.Version.Major}.{system.Version.Minor}" +
                          $"{(Draws ? ", Lattice draws them" : ", StationGod draws them")}): " +
                          $"{GhostBoardStore.Board.Count} build ghosts on the board.");
        if (NetworkManager.IsServer)
        {
            MoveBoardFile(system);
        }
    }

    private static void OnChanged(L.ChangeSet changes)
    {
        if (!ReferenceEquals(_system, _heard))
        {
            return;
        }

        if (!Answered.Remove(changes.Id.Value))
        {
            Mirror(changes);
        }
    }

    private static void OnBoardReset()
    {
        // The world ended (Current is cleared) or its board was replaced: the next Tick attaches again if one runs.
        _system = null;
        _agentPlan = null;
        Draws = false;
        Ghosts.BoardDrawnElsewhere = false;
        GhostBoardStore.FromLattice(true, new List<BoardGhost>(), new List<long>());
    }

    private static L.ChangeSet? Submit(L.IGhostSystem system, L.Command command, L.Actor actor, List<string> refused)
    {
        switch (system.Commands.Submit(L.CommandId.New(), command, actor))
        {
            case L.Submission.Answered { Outcome: L.Outcome.Applied applied }:
                Answered.Add(applied.Changes.Id.Value);
                Mirror(applied.Changes);
                return applied.Changes;
            case L.Submission.Answered { Outcome: L.Outcome.Refused why }:
                refused.Add($"Lattice refused it: {LatticeText.Of(why.Why)}");
                return null;
            default:
                return null;
        }
    }

    private static void Mirror(L.ChangeSet changes)
    {
        List<BoardGhost> added = new List<BoardGhost>();
        List<long> removed = new List<long>();
        foreach (L.Ghost ghost in changes.Added)
        {
            if (ToBoard(ghost) is { } mirrored)
            {
                added.Add(mirrored);
            }
        }

        foreach (L.GhostChange change in changes.Changed)
        {
            if (ToBoard(change.After) is { } mirrored)
            {
                added.Add(mirrored);
            }
            else
            {
                removed.Add(change.After.Id.Value);
            }
        }

        foreach (L.GhostRemoval removal in changes.Removed)
        {
            removed.Add(removal.Ghost.Id.Value);
        }

        if (_agentPlan is { } plan)
        {
            foreach (L.PlanId finished in changes.PlansFinished)
            {
                if (finished == plan)
                {
                    _agentPlan = null;
                }
            }
        }

        GhostBoardStore.FromLattice(false, added, removed);
    }

    private static List<BoardGhost> AllBuildGhosts(L.IGhostSystem system)
    {
        List<BoardGhost> all = new List<BoardGhost>();
        L.Result<L.ValueList<L.Ghost>, L.Reason> picked =
            system.Board.As(LatticeNames.Agent).Select(new L.Selector.Everything());
        if (picked.TryGet(out L.ValueList<L.Ghost> ghosts))
        {
            foreach (L.Ghost ghost in ghosts)
            {
                if (ToBoard(ghost) is { } mirrored)
                {
                    all.Add(mirrored);
                }
            }
        }
        else if (picked.TryGetError(out L.Reason why))
        {
            StationGodMod.LogWarning($"Lattice's ghosts could not be read: {LatticeText.Of(why)}.");
        }

        return all;
    }

    private static L.PlanId? AgentPlan(L.IGhostSystem system)
    {
        if (_agentPlan is { } known && system.Plans.Find(known).IsSome)
        {
            return known;
        }

        _agentPlan = null;
        foreach (L.Plan plan in system.Plans.All)
        {
            if (plan.Owner == LatticeNames.Agent && plan.Name == LatticeNames.AgentPlan)
            {
                _agentPlan = plan.Id;
                break;
            }
        }

        return _agentPlan;
    }

    // The save's old stationgod-ghosts.json (StationGod's own board before Lattice) placed into Lattice once, then
    // renamed so it is not read again.
    private static void MoveBoardFile(L.IGhostSystem system)
    {
        string? file = GhostBoardStore.SaveFile;
        if (file == null || !File.Exists(file))
        {
            return;
        }

        try
        {
            GhostBoard old = GhostBoard.FromJson(JObject.Parse(File.ReadAllText(file)), out int skipped);
            List<L.GhostDraft> drafts = new List<L.GhostDraft>();
            List<string> notes = new List<string>();
            foreach (BoardGhost ghost in old.All)
            {
                if (Prefab.Find(ghost.PrefabHash) is Structure prefab && prefab != null &&
                    LatticeGhosts.ToDraft(new GhostPlacement(ghost.PrefabHash, ghost.BuildState, ghost.Position,
                        ghost.Euler, ghost.Tint, ghost.Label), LatticeNames.MovedMaker, L.Option<L.PlanId>.None,
                        LastState(prefab), notes, out _) is { } draft)
                {
                    drafts.Add(draft);
                }
                else
                {
                    skipped++;
                }
            }

            List<string> refused = new List<string>();
            int placed = 0;
            if (drafts.Count > 0 &&
                Submit(system, new L.Command.Place(L.ValueList.From(drafts)), LatticeNames.Self, refused) is { } changes)
            {
                placed = changes.Added.Count;
            }

            if (refused.Count > 0)
            {
                StationGodMod.LogWarning($"The ghosts of {file} were not moved into Lattice: {refused[0]}. The file is kept.");
                return;
            }

            File.Move(file, file + ".moved-to-lattice");
            StationGodMod.Log($"Ghost board: {placed} ghosts of {file} moved into Lattice" +
                              $"{(skipped > 0 ? $" ({skipped} left out)" : "")}; the file is now {Path.GetFileName(file)}.moved-to-lattice.");
        }
        catch (Exception exception) when (exception is IOException || exception is Newtonsoft.Json.JsonException ||
                                          exception is UnauthorizedAccessException)
        {
            StationGodMod.LogWarning($"The ghost board {file} could not be moved into Lattice: {exception.Message}");
        }
    }

    private static BoardGhost? ToBoard(L.Ghost ghost) =>
        LatticeGhosts.ToBoard(ghost, PrefabName, LastStateOf, OwnerName, GhostTints.Default);

    private static string PrefabName(int hash) =>
        Prefab.Find(hash) is Thing prefab && prefab != null ? prefab.PrefabName : "";

    private static int LastStateOf(int hash) =>
        Prefab.Find(hash) is Structure prefab && prefab != null ? LastState(prefab) : 0;

    private static int LastState(Structure prefab) =>
        prefab.BuildStates != null && prefab.BuildStates.Count > 0 ? prefab.BuildStates.Count - 1 : 0;

    private static string OwnerName(L.Actor actor)
    {
        switch (actor)
        {
            case L.Actor.Agent agent:
                return agent == LatticeNames.Agent ? "agent" : $"agent {agent.Id.Value}";
            case L.Actor.Mod mod:
                return mod.Id == LatticeNames.Mod ? "stationgod" : mod.Id.Value;
            case L.Actor.Player player:
                ulong id = player.Client.Value;
                foreach (Client client in NetworkBase.Clients)
                {
                    if (client != null && client.ClientId == id && !string.IsNullOrEmpty(client.name))
                    {
                        return client.name;
                    }
                }

                Human human = Human.LocalHuman;
                return id == NetworkManager.LocalClientId && human != null && !string.IsNullOrEmpty(human.DisplayName)
                    ? human.DisplayName
                    : $"player {id}";
            default:
                return actor.GetType().Name;
        }
    }
}
