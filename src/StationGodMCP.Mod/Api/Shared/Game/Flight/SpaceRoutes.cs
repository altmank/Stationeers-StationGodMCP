#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Objects;
using Assets.Scripts.Serialization;
using Objects.Rockets;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared.Game.Flight;

/// <summary>Finding rockets by any id that names them. Read only.</summary>
internal static class RocketLocator
{
    internal static List<Rocket> All()
    {
        List<Rocket> rockets = new List<Rocket>(Rocket.AllRockets.Count);
        for (int index = 0; index < Rocket.AllRockets.Count; index++)
        {
            Rocket rocket = Rocket.AllRockets[index];
            if (rocket != null && !rocket.BeingDestroyed && rocket.RocketNetwork != null)
            {
                rockets.Add(rocket);
            }
        }

        return rockets;
    }

    /// <summary>The rocket with this id, its rocket network's id, or the id of any of its parts.</summary>
    internal static Rocket Find(ThingId id)
    {
        List<Rocket> rockets = All();
        for (int index = 0; index < rockets.Count; index++)
        {
            Rocket rocket = rockets[index];
            if (rocket.ReferenceId == id.Value || rocket.RocketNetwork.ReferenceId == id.Value)
            {
                return rocket;
            }
        }

        if (GameLookup.TryFindThing(id, out Thing thing))
        {
            Networks.RocketNetwork? network = thing switch
            {
                Structure structure => Build.Rockets.NetworkOf(structure),
                IRocketInternals part => part.RocketNetwork,
                _ => null
            };
            if (network?.Rocket != null)
            {
                return network.Rocket;
            }
        }

        throw ApiErrors.Refused("rocket_not_found",
            $"No rocket has the id {id.Value}: give a rocket's, its rocket network's or any of its parts' reference id.");
    }

    /// <summary>The rocket named, or the only rocket there is; several without an id is ambiguous_rocket.</summary>
    internal static Rocket One(Args args)
    {
        ThingId? id = args.OptionalThingId("rocket_id");
        if (id.HasValue)
        {
            return Find(id.Value);
        }

        List<Rocket> rockets = All();
        if (rockets.Count == 1)
        {
            return rockets[0];
        }

        if (rockets.Count == 0)
        {
            throw ApiErrors.Refused("rocket_not_found", "There is no rocket in the world.");
        }

        List<string> names = new List<string>(rockets.Count);
        for (int index = 0; index < rockets.Count; index++)
        {
            names.Add($"{rockets[index].DisplayName} ({rockets[index].ReferenceId})");
        }

        throw ApiErrors.Refused("ambiguous_rocket",
            $"There are {rockets.Count} rockets: give rocket_id, one of {string.Join(", ", names)}.");
    }
}

/// <summary>
/// The space map as the rocket tools read it: node views, node lookup by name or code, and the route the game will fly.
/// </summary>
internal static class SpaceRoutes
{
    /// <summary>A guard against a route that never reaches its target.</summary>
    private const int MaximumHops = 64;

    internal static SpaceNodeView ViewOf(SpaceMapNode node) =>
        new SpaceNodeView(new ThingId(node.ReferenceId), NameOf(node), node.Code.String ?? string.Empty,
            node.Code.Value, node.NodeType.ToString(), node.IsCharted);

    /// <summary>
    /// The node's real name even when uncharted (SpaceMapNode.DebugName: its data's name, else its owner's); the map
    /// shows uncharted nodes as "Uncharted Location".
    /// </summary>
    internal static string NameOf(SpaceMapNode node)
    {
        string? name = node.DebugName;
        return string.IsNullOrEmpty(name) ? node.ReferenceId.ToString(CultureInfo.InvariantCulture) : name!;
    }

    internal static bool IsPad(SpaceMapNode? node) =>
        node != null && (node.NodeType == NodeType.LaunchPad || node.NodeType == NodeType.LowOrbitLaunchPad);

    /// <summary>
    /// The hops the rocket will fly from a node to the target. The game asks SpaceMapPathFinder.GetNextConnection
    /// afresh at every node it reaches (Rocket.PhysicsUpdate, Rocket.cs:2020-2038: CurrentTransit is cleared on
    /// arrival and set from the next call), so this does the same, one hop at a time. The pathfinder is a
    /// breadth-first search over child connections and then the parent (SpaceMapPathFinder.cs:47-93): fewest hops
    /// found first, not the shortest distance, and it does not look at whether a node is charted. Null when the
    /// target cannot be reached.
    /// </summary>
    internal static List<NodeTransit>? Plan(SpaceMapNode from, SpaceMapNode target)
    {
        List<NodeTransit> hops = new List<NodeTransit>(4);
        SpaceMapNode node = from;
        while (node != target)
        {
            if (hops.Count >= MaximumHops ||
                !SpaceMapPathFinder.GetNextConnection(node, target, out NodeTransit next, out _))
            {
                return null;
            }

            hops.Add(next);
            node = next.Destination;
        }

        return hops;
    }

    /// <summary>Launch pads with a mount (the nodes a rocket lands on).</summary>
    internal static List<SpaceMapNode> Pads()
    {
        List<SpaceMapNode> pads = new List<SpaceMapNode>(2);
        for (int index = 0; index < SpaceMapNode.AllSpaceMapNodes.Count; index++)
        {
            SpaceMapNode node = SpaceMapNode.AllSpaceMapNodes[index];
            if (IsPad(node) && node.Owner != null && !node.BeingDestroyed)
            {
                pads.Add(node);
            }
        }

        return pads;
    }

    /// <summary>
    /// The node a text names: "pad"/"home" (the only launch pad), a map code as shown ("01-02-...") or its number, a
    /// reference id, the node's data id, or its name (exact, case ignored). More than one match is ambiguous_node.
    /// </summary>
    internal static SpaceMapNode Resolve(string text)
    {
        string wanted = text.Trim();
        if (string.Equals(wanted, "pad", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(wanted, "home", StringComparison.OrdinalIgnoreCase))
        {
            List<SpaceMapNode> pads = Pads();
            return pads.Count == 1 ? pads[0] : throw Ambiguous(wanted, pads);
        }

        List<SpaceMapNode> found = new List<SpaceMapNode>(2);
        bool number = ulong.TryParse(wanted, NumberStyles.None, CultureInfo.InvariantCulture, out ulong value);
        for (int index = 0; index < SpaceMapNode.AllSpaceMapNodes.Count; index++)
        {
            SpaceMapNode node = SpaceMapNode.AllSpaceMapNodes[index];
            if (node == null || node.BeingDestroyed)
            {
                continue;
            }

            bool matches = string.Equals(node.Code.String, wanted, StringComparison.Ordinal) ||
                           (number && (node.Code.Value == value || (ulong)node.ReferenceId == value)) ||
                           string.Equals(node.Id, wanted, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(NameOf(node), wanted, StringComparison.OrdinalIgnoreCase);
            if (matches && !found.Contains(node))
            {
                found.Add(node);
            }
        }

        return found.Count == 1
            ? found[0]
            : found.Count == 0
                ? throw ApiErrors.Refused("node_not_found",
                    $"No space-map node is called '{wanted}': give its name, its map code, or 'pad'.")
                : throw Ambiguous(wanted, found);
    }

    private static ApiException Ambiguous(string wanted, List<SpaceMapNode> nodes)
    {
        List<string> names = new List<string>(nodes.Count);
        for (int index = 0; index < nodes.Count; index++)
        {
            names.Add($"{NameOf(nodes[index])} (code {nodes[index].Code.String})");
        }

        return nodes.Count == 0
            ? ApiErrors.Refused("node_not_found", "There is no launch pad on the map.")
            : ApiErrors.Refused("ambiguous_node",
                $"'{wanted}' names {nodes.Count} nodes: {string.Join(", ", names)}. Give the code.");
    }
}
