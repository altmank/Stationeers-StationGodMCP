#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>
/// remove_dead_ends: stubs (one connected end) and isolated pieces (none) are removed, in rounds, until removing them
/// leaves no new stub (DeadEndPruning). A stub connected to a device stays, and so does the run behind it; so does a
/// piece with a device mounted on it (the game itself refuses to deconstruct a pipe with an attached device,
/// Pipe.CanDeconstruct), an indestructible or rocket piece, one no coil or kit places, and, for pipes, the last pieces
/// of a network that still holds gas or liquid (UpgradeFamily.RemovalHolds). A removed piece gives back what
/// deconstructing it would.
/// </summary>
internal sealed class RemoveDeadEnds : ICleanOperation
{
    internal const string Operation = "remove_dead_end";
    private const int MaximumPasses = 4;

    public string Name => CleanOperationSet.RemoveDeadEnds;

    public void Plan(CleanPass pass)
    {
        List<SmallGrid> open = pass.Open;
        Dictionary<long, SmallGrid> byId = new Dictionary<long, SmallGrid>();
        Dictionary<long, Kit> kits = new Dictionary<long, Kit>();
        Dictionary<long, string> blocked = new Dictionary<long, string>();
        Dictionary<long, string> details = new Dictionary<long, string>();
        List<PieceModel> candidates = new List<PieceModel>(open.Count);
        foreach (SmallGrid piece in open)
        {
            byId[piece.ReferenceId] = piece;
            candidates.Add(pass.LiveOf(piece));
            Block(pass, piece, kits, blocked);
        }

        Surroundings around = Surroundings.Of(pass, candidates, byId);
        Dictionary<long, List<PieceEnd>> connected = new Dictionary<long, List<PieceEnd>>();
        foreach (PieceModel candidate in candidates)
        {
            connected[candidate.Id] = pass.ConnectedEnds(candidate);
        }

        PruneResult result = Prune(pass, candidates, around, byId, blocked, details);
        foreach (KeyValuePair<long, int> removal in result.Removed)
        {
            SmallGrid piece = byId[removal.Key];
            pass.Remove(piece, kits[removal.Key],
                CleanPass.Detail(Operation, pass.LiveOf(piece), connected[removal.Key], removal.Value));
        }

        foreach (KeyValuePair<long, string> stop in result.Stops)
        {
            pass.DeadEnd(byId[stop.Key], connected[stop.Key], stop.Value,
                details.TryGetValue(stop.Key, out string detail) ? detail : null);
        }
    }

    // Prunes, then holds back what would empty a network that still has contents, and prunes again.
    private static PruneResult Prune(CleanPass pass, List<PieceModel> candidates, Surroundings around,
        Dictionary<long, SmallGrid> byId, Dictionary<long, string> blocked, Dictionary<long, string> details)
    {
        PruneResult result = DeadEndPruning.Prune(candidates, around.Models, around.Devices, blocked);
        for (int attempt = 1; attempt < MaximumPasses; attempt++)
        {
            List<SmallGrid> removed = new List<SmallGrid>(result.Removed.Count);
            foreach (KeyValuePair<long, int> removal in result.Removed)
            {
                removed.Add(byId[removal.Key]);
            }

            Dictionary<long, string> holds = pass.Family.RemovalHolds(removed);
            if (holds.Count == 0)
            {
                break;
            }

            foreach (KeyValuePair<long, string> hold in holds)
            {
                blocked[hold.Key] = UpgradeFamily.HoldsContents;
                details[hold.Key] = hold.Value;
            }

            result = DeadEndPruning.Prune(candidates, around.Models, around.Devices, blocked);
        }

        return result;
    }

    /// <summary>The piece's kit, or why it may not be removed (no_kit, device_mounted, indestructible, rocket_internal).</summary>
    internal static void Block(CleanPass pass, SmallGrid piece, Dictionary<long, Kit> kits,
        Dictionary<long, string> blocked)
    {
        Kit? kit = pass.Context.KitFor(piece, false, true);
        if (kit != null)
        {
            kits[piece.ReferenceId] = kit;
        }

        string? reason = kit == null ? "no_kit"
            : pass.HasMountedDevice(pass.LiveOf(piece)) ? "device_mounted"
            : piece.Indestructable ? "indestructible"
            : IsRocketInternal(piece) ? "rocket_internal"
            : null;
        if (reason != null)
        {
            blocked[piece.ReferenceId] = reason;
        }
    }

    private static bool IsRocketInternal(SmallGrid piece) => Runs.RunPlanner.InRocket(piece);

    /// <summary>What the candidates can connect to besides each other, and which of those are devices.</summary>
    private sealed class Surroundings
    {
        private Surroundings(List<PieceModel> models, HashSet<long> devices)
        {
            Models = models;
            Devices = devices;
        }

        internal List<PieceModel> Models { get; }

        internal HashSet<long> Devices { get; }

        internal static Surroundings Of(CleanPass pass, List<PieceModel> candidates, Dictionary<long, SmallGrid> byId)
        {
            Dictionary<long, SmallGrid> things = LinkSurvey.Neighbourhood(candidates);
            Dictionary<long, SmallGrid> others = new Dictionary<long, SmallGrid>();
            HashSet<long> devices = new HashSet<long>();
            foreach (SmallGrid thing in things.Values)
            {
                if (byId.ContainsKey(thing.ReferenceId))
                {
                    continue;
                }

                others[thing.ReferenceId] = thing;
                if (thing is Device)
                {
                    devices.Add(thing.ReferenceId);
                }
            }

            return new Surroundings(pass.ModelsOf(others, 0), devices);
        }
    }
}
