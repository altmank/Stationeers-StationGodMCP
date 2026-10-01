#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Networks;
using Objects.Rockets;
using StationGodMCP.Api.Views;
using TerrainSystem;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Flight;

/// <summary>
/// What a landing (or launch) would hit on the way down: Rocket.CheckForOverlap (Rocket.cs:2320-2372) runs every
/// physics step while launching or landing, with two 0.9 m spheres, 1 m below the lowest hull piece and 1 m above the
/// highest, against the Default layer, and terrain density above 0.9 at either centre. IsCollision (2374-2407) ignores
/// the launch mount and the rocket's own parts; any other structure (another rocket's parts too) blows the rocket up.
/// The rocket's parent transform never goes above mount + 1000 m (MoveToTarget, 2266-2316), so that is the column swept.
/// This replays the spheres up the column at the target pad, read only. RocketCollisionDetector's trigger is not
/// replayed.
/// </summary>
internal static class RocketColumn
{
    private const float Radius = 0.9f;
    private const float Step = 0.5f;
    private const float Ceiling = 1000f;
    private const int Listed = 10;

    private static readonly Collider[] Hits = new Collider[512];

    internal static ColumnView? Scan(Rocket rocket, SpaceMapNode pad)
    {
        RocketNetwork network = rocket.RocketNetwork;
        if (pad.Owner == null || !Extremes(rocket, out Vector3 low, out Vector3 high))
        {
            return null;
        }

        Vector3 mount = pad.Owner.RocketTransformPosition;
        int mask = 1 << LayerMask.NameToLayer("Default");
        List<ColumnBlockerView> blockers = new List<ColumnBlockerView>(Listed);
        HashSet<long> seen = new HashSet<long>();
        int more = 0;
        bool terrainNoted = false;
        for (float height = 0f; height <= Ceiling; height += Step)
        {
            Probe(mount + low + Vector3.down + new Vector3(0f, height, 0f), "engine");
            Probe(mount + high + Vector3.up + new Vector3(0f, height, 0f), "nose");
        }

        return new ColumnView(blockers.Count == 0 && more == 0, mount.y + low.y - 1f, mount.y + Ceiling + high.y + 1f,
            blockers, more);

        void Probe(Vector3 centre, string sweptBy)
        {
            if (!terrainNoted && VoxelTerrain.GetDensityWorldSpace(centre) > 0.9f)
            {
                terrainNoted = true;
                Add(null, "terrain (density above 0.9)", centre.y, sweptBy);
            }

            int count = Physics.OverlapSphereNonAlloc(centre, Radius, Hits, mask, QueryTriggerInteraction.Ignore);
            for (int index = 0; index < count; index++)
            {
                if (Thing.Find(Hits[index]) is Structure structure && Blocks(structure, network) &&
                    seen.Add(structure.ReferenceId))
                {
                    Add(new ThingId(structure.ReferenceId), $"{Names.Of(structure)} ({structure.PrefabName})", centre.y,
                        sweptBy);
                }
            }
        }

        void Add(ThingId? id, string what, float y, string sweptBy)
        {
            if (blockers.Count < Listed)
            {
                blockers.Add(new ColumnBlockerView(id, what, y, sweptBy));
            }
            else
            {
                more++;
            }
        }
    }

    // Rocket.IsCollision without its side effects (it damages or explodes what it finds).
    private static bool Blocks(Structure structure, RocketNetwork own) => structure switch
    {
        LaunchMount => false,
        StructureFuselage fuselage => fuselage.RocketNetwork != null && fuselage.RocketNetwork != own,
        IRocketInternals part => part.RocketNetwork != null && part.RocketNetwork != own,
        _ => true
    };

    // The lowest and highest hull pieces relative to the rocket's reference point: the mount it stands on, or its
    // parent transform in flight.
    private static bool Extremes(Rocket rocket, out Vector3 low, out Vector3 high)
    {
        low = Vector3.zero;
        high = Vector3.zero;
        Vector3 reference;
        if (rocket.RocketParentTransform != null)
        {
            reference = rocket.RocketParentTransform.position;
        }
        else if (rocket.CurrentNode?.Owner != null)
        {
            reference = rocket.CurrentNode.Owner.RocketTransformPosition;
        }
        else
        {
            return false;
        }

        bool any = false;
        float lowest = float.MaxValue;
        float highest = float.MinValue;
        foreach (INetworkedStructure piece in rocket.RocketNetwork.StructureList)
        {
            if (!(piece is StructureFuselage fuselage))
            {
                continue;
            }

            Vector3 offset = fuselage.Position - reference;
            if (offset.y < lowest)
            {
                lowest = offset.y;
                low = offset;
            }

            if (offset.y > highest)
            {
                highest = offset.y;
                high = offset;
            }

            any = true;
        }

        return any;
    }
}
