#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts.Objects;
using Assets.Scripts.Serialization;
using Objects.Rockets;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Protocol;
using World = StationGodMCP.WorldModel.World;

namespace StationGodMCP.Api.Shared.Game.Flight;

/// <summary>Finding rockets by any id that names them. Read only.</summary>
internal static class RocketLocator
{
    internal static List<Rocket> All()
    {
        List<Rocket> rockets = new List<Rocket>(World.Rockets.Count);
        for (int index = 0; index < World.Rockets.Count; index++)
        {
            Rocket rocket = World.Rockets[index];
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

    /// <summary>
    /// The rocket an id option names: a rocket's, its rocket network's or any part's reference id, or its name (any
    /// case); several rockets of that name is ambiguous_rocket.
    /// </summary>
    internal static Rocket Find(string id)
    {
        if (ThingId.TryRead(new Newtonsoft.Json.Linq.JValue(id), out ThingId referenceId))
        {
            return Find(referenceId);
        }

        List<Rocket> named = All().FindAll(rocket =>
            string.Equals(rocket.DisplayName, id.Trim(), System.StringComparison.OrdinalIgnoreCase));
        return named.Count switch
        {
            1 => named[0],
            0 => throw ApiErrors.Refused("rocket_not_found",
                $"No rocket is named '{id}': give a rocket's name, or its, its rocket network's or any of its parts' " +
                "reference id."),
            _ => throw ApiErrors.Refused("ambiguous_rocket",
                $"{named.Count} rockets are named '{id}': give one's reference id, one of {Listed(named)}.")
        };
    }

    /// <summary>The rocket an id names, or the only rocket there is when id is left out.</summary>
    internal static Rocket One(string? id)
    {
        if (id != null)
        {
            return Find(id);
        }

        List<Rocket> rockets = All();
        return rockets.Count switch
        {
            1 => rockets[0],
            0 => throw ApiErrors.Refused("rocket_not_found", "There is no rocket in the world."),
            _ => throw ApiErrors.Refused("ambiguous_rocket",
                $"There are {rockets.Count} rockets: give id, one of {Listed(rockets)}.")
        };
    }

    private static string Listed(List<Rocket> rockets)
    {
        List<string> names = new List<string>(rockets.Count);
        foreach (Rocket rocket in rockets)
        {
            names.Add($"{rocket.DisplayName} ({rocket.ReferenceId})");
        }

        return string.Join(", ", names);
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
        for (int index = 0; index < World.SpaceMapNodes.Count; index++)
        {
            SpaceMapNode node = World.SpaceMapNodes[index];
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
        for (int index = 0; index < World.SpaceMapNodes.Count; index++)
        {
            SpaceMapNode node = World.SpaceMapNodes[index];
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

    /// <summary>
    /// As Resolve, but "pad"/"home" means this rocket's pad when there are several: the pad it stands on or is
    /// launching from or landing on, else the only ground (not orbital) launch mount; otherwise it is ambiguous_node
    /// and the caller names the pad.
    /// </summary>
    internal static SpaceMapNode ResolveFor(string text, Rocket rocket)
    {
        string wanted = text.Trim();
        if (!string.Equals(wanted, "pad", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(wanted, "home", StringComparison.OrdinalIgnoreCase))
        {
            return Resolve(wanted);
        }

        List<SpaceMapNode> pads = Pads();
        if (pads.Count == 1)
        {
            return pads[0];
        }

        if (IsPad(rocket.CurrentNode))
        {
            return rocket.CurrentNode!;
        }

        NodeTransit? transit = rocket.CurrentTransit;
        if (transit != null && IsPad(transit.From))
        {
            return transit.From;
        }

        if (transit != null && IsPad(transit.Destination))
        {
            return transit.Destination;
        }

        SpaceMapNode? ground = null;
        int grounds = 0;
        for (int index = 0; index < pads.Count; index++)
        {
            if (pads[index].Owner != null && !pads[index].Owner.IsOrbital)
            {
                ground = pads[index];
                grounds++;
            }
        }

        return grounds == 1 ? ground! : throw Ambiguous(wanted, pads);
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
