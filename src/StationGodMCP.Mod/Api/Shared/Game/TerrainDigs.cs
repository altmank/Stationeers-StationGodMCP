#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Scheduling;
using TerrainSystem;
using TerrainSystem.Lods;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// remove terrain: every terrain voxel in a box dug out as the game's own diggers dig one.
///
/// How the game digs (CODE): a voxel is dug by VoxelTerrain.SetDensityWorldSpace(point, 0, RoomChangeSource.VoxelRemove,
/// dirtyLods: false), the call a quarry (Quarry.cs:668) and a deep miner (DeepMiner.cs:351) make for each voxel they
/// take; a mining charge lowers the density the same way (Explosion.cs:55-56, ore at :57-60). It does nothing at or under bedrock
/// (VoxelTerrain.cs:953). On a server with clients it queues a VoxelChangeEvent (VoxelTerrain.cs:956-959), which goes
/// out in the next state packet (FragmentHandler.cs:98, WriteStateImmediate); the client's replay sets the density,
/// mines a minable there when the density is under half, rebuilds its terrain mesh and applies the same world effects
/// (VoxelChangeEvent.Execute, VoxelTerrain.cs:63-76). Those effects, on both sides, give the opened cell an empty
/// atmosphere that joins its neighbours and re-check rooms (ApplyVoxelWorldEffects, VoxelTerrain.cs:1039-1057). The
/// digger then rebuilds the host's own mesh and colliders once for the whole area (LodManager.DirtyLodsBounds after the
/// loop, Explosion.cs:65); a dig does that after each slice. Ore: Vein.TryMineServer makes the ore item and marks the
/// minable mined (Vein.cs:645-663), Vein.TryRemoveServer only marks it (Vein.cs:665-675).
///
/// The state packet's voxel list (SyncList.Add) checks every event already queued before adding one, so a dig goes in
/// slices of SliceVoxels and, with clients connected, waits after each for a state packet written after it
/// (JobReplication, its own gate): each packet carries one slice, as a mining charge's at most ~4,200 voxels go in one.
/// </summary>
internal static class TerrainDigs
{
    private const int ReadsPerStep = 512;

    /// <summary>Refused on a multiplayer client: only the host's terrain is the world's.</summary>
    internal static void RequireHost()
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host digs terrain.");
        }
    }

    /// <summary>The report's reading, a body over frames: each voxel read as the dig would find it.</summary>
    internal static IEnumerable<object?> Read(DigBox box, OreHandling ores, Answer<TerrainDigView> answer)
    {
        RequireHost();
        DigTally tally = new DigTally();
        int read = 0;
        foreach (VoxelPoint voxel in box.TopDown())
        {
            if (Solid(voxel, tally))
            {
                Vein? vein = Vein.GetVeinAtPosition(At(voxel));
                if (vein != null && OreName(vein) is { } ore)
                {
                    tally.Ore(ore, null);
                }
            }

            if (++read % ReadsPerStep == 0)
            {
                yield return null;
            }
        }

        answer.Value = new TerrainDigView(box, tally, ores, PlayersIn(box));
    }

    /// <summary>The dig itself: work the slice lane steps until every voxel is dug.</summary>
    internal static IPendingResult Dig(DigBox box, OreHandling ores) => new Digging(box, ores);

    // Counts the voxel as empty, kept at bedrock, or solid (to dig); true when solid.
    private static bool Solid(VoxelPoint voxel, DigTally tally)
    {
        if (VoxelTerrain.GetDensityWorldSpace(new Vector3Int(voxel.X, voxel.Y, voxel.Z)) <= 0f)
        {
            tally.Empty();
            return false;
        }

        if (!DigBox.AboveBedrock(voxel))
        {
            tally.Bedrock();
            return false;
        }

        tally.Solid();
        return true;
    }

    private static Vector3 At(VoxelPoint voxel) => new Vector3(voxel.X, voxel.Y, voxel.Z);

    // The ore item a vein's minables make; null for stone, which makes none (VoxelTerrain.OrePrefabHash).
    private static string? OreName(Vein vein)
    {
        int hash = VoxelTerrain.OrePrefabHash(vein.Data.Type);
        if (hash == PrefabHashmap.Invalid)
        {
            return null;
        }

        Thing prefab = Prefab.Find(hash);
        return prefab != null ? prefab.PrefabName : vein.Data.Type.ToString();
    }

    private static List<ThingView> PlayersIn(DigBox box)
    {
        List<ThingView> inside = new List<ThingView>();
        foreach (NamedPlayer<Human> player in PlayerOrigin.NamedPlayers())
        {
            if (box.Holds(Bodies.V(player.Body.Position)))
            {
                inside.Add(GameLookup.ViewOf(player.Body));
            }
        }

        return inside;
    }

    /// <summary>
    /// The dig over frames: each step digs voxels top down until the slice is full, the frame's share is spent or the
    /// call must stop (WorkStop), then rebuilds the host's terrain over what it dug; with clients the next slice waits
    /// for the state packet that carries this one.
    /// </summary>
    private sealed class Digging : IPendingResult
    {
        private const int SliceVoxels = 512;

        private readonly DigBox _box;
        private readonly OreHandling _ores;
        private readonly IEnumerator<VoxelPoint> _voxels;
        private readonly DigTally _tally = new DigTally();
        private readonly ClientReplication _gate = new ClientReplication();
        private bool _done;

        internal Digging(DigBox box, OreHandling ores)
        {
            _box = box;
            _ores = ores;
            _voxels = box.TopDown().GetEnumerator();
        }

        public bool Step(double untilMs)
        {
            if (_done)
            {
                return true;
            }

            if (!JobReplication.MayStep(_gate))
            {
                return false;
            }

            RequireHost();
            DugArea area = new DugArea();
            while (area.Count < SliceVoxels)
            {
                if (!_voxels.MoveNext())
                {
                    _done = true;
                    break;
                }

                VoxelPoint voxel = _voxels.Current;
                if (Solid(voxel, _tally))
                {
                    DigOut(voxel);
                    area.Add(voxel);
                }

                if (StopwatchClock.Instance.NowMs >= untilMs || WorkStop.Requested)
                {
                    break;
                }
            }

            if (area.Count > 0)
            {
                Rebuild(area);
                _gate.Changed(Time.frameCount);
            }

            return _done;
        }

        public object Result() => new TerrainDigView(_box, _tally, _ores, PlayersIn(_box));

        private void DigOut(VoxelPoint voxel)
        {
            Vector3 at = At(voxel);
            Vein? vein = Vein.GetVeinAtPosition(at);
            VoxelTerrain.SetDensityWorldSpace(at, 0f, RoomChangeSource.VoxelRemove, dirtyLods: false);
            if (vein == null || OreName(vein) is not { } ore)
            {
                return;
            }

            Vector3Int point = new Vector3Int(voxel.X, voxel.Y, voxel.Z);
            switch (_ores)
            {
                case OreHandling.Drop:
                    Vector3 centre = new Vector3(voxel.X + 0.5f, voxel.Y + 0.5f, voxel.Z + 0.5f);
                    if (vein.TryMineServer(point, out Ore created, centre))
                    {
                        _tally.Ore(ore, created != null ? created.Quantity : 0);
                    }

                    break;
                case OreHandling.Discard:
                    if (vein.TryRemoveServer(point))
                    {
                        _tally.Ore(ore, null);
                    }

                    break;
                default:
                    throw new System.InvalidOperationException($"Unknown ore handling {_ores.Name}.");
            }
        }

        // The host's terrain mesh and colliders over the slice, rebuilt once (LodManager.DirtyLodsBounds).
        private static void Rebuild(DugArea area)
        {
            LodManager lods = LodManager.Instance;
            if (lods != null)
            {
                lods.DirtyLodsBounds(area.Min, area.Max);
            }
        }
    }

    // The bounds of the voxels one slice dug.
    private sealed class DugArea
    {
        private Vector3Int _min;
        private Vector3Int _max;

        internal int Count { get; private set; }

        internal Vector3 Min => _min;

        internal Vector3 Max => _max + Vector3Int.one;

        internal void Add(VoxelPoint voxel)
        {
            Vector3Int point = new Vector3Int(voxel.X, voxel.Y, voxel.Z);
            _min = Count == 0 ? point : Vector3Int.Min(_min, point);
            _max = Count == 0 ? point : Vector3Int.Max(_max, point);
            Count++;
        }
    }
}
