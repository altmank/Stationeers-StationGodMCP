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
/// The edits go over frames in slices (VoxelWork), one slice per state packet when clients are connected.
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
    internal static bool Solid(VoxelPoint voxel, DigTally tally)
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

    internal static Vector3 At(VoxelPoint voxel) => new Vector3(voxel.X, voxel.Y, voxel.Z);

    // The ore item a vein's minables make; null for stone, which makes none (VoxelTerrain.OrePrefabHash).
    internal static string? OreName(Vein vein)
    {
        int hash = VoxelTerrain.OrePrefabHash(vein.Data.Type);
        if (hash == PrefabHashmap.Invalid)
        {
            return null;
        }

        Thing prefab = Prefab.Find(hash);
        return prefab != null ? prefab.PrefabName : vein.Data.Type.ToString();
    }

    internal static List<ThingView> PlayersIn(DigBox box)
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
    /// Sets one voxel's density as the game's own diggers and dirt placement do (SetDensityWorldSpace with VoxelAdd when
    /// it rises, VoxelRemove when it falls, dirtyLods false). The material is left as stored unless one is given, as
    /// the console's voxelfillnear gives the generated one (VoxelFillNearCommand.cs:84-88); a client's replay sets only
    /// the density (VoxelTerrain.cs:63-66), so clients get the material from the world they load on joining. When it
    /// takes the voxel under half density the ore there is released as ores says, and counted.
    /// </summary>
    internal static void Set(VoxelPoint voxel, byte density, bool opens, OreHandling ores, DigTally tally,
        VoxelNodeType? material = null)
    {
        Vector3 at = At(voxel);
        Vein? vein = opens ? Vein.GetVeinAtPosition(at) : null;
        byte before = VoxelTerrain.GetNodeInfoWorldSpace(at).Density;
        VoxelTerrain.SetDensityWorldSpace(at, AsFloat(density),
            density > before ? RoomChangeSource.VoxelAdd : RoomChangeSource.VoxelRemove, dirtyLods: false,
            setNodeType: material != null, voxelType: material ?? VoxelNodeType.None);
        if (vein == null || OreName(vein) is not { } ore)
        {
            return;
        }

        Vector3Int point = new Vector3Int(voxel.X, voxel.Y, voxel.Z);
        switch (ores)
        {
            case OreHandling.Drop:
                Vector3 centre = new Vector3(voxel.X + 0.5f, voxel.Y + 0.5f, voxel.Z + 0.5f);
                if (vein.TryMineServer(point, out Ore created, centre))
                {
                    tally.Ore(ore, created != null ? created.Quantity : 0);
                }

                break;
            case OreHandling.Discard:
                if (vein.TryRemoveServer(point))
                {
                    tally.Ore(ore, null);
                }

                break;
            default:
                throw new System.InvalidOperationException($"Unknown ore handling {ores.Name}.");
        }
    }

    // The float SetDensityWorldSpace turns back into this byte: it stores (byte)(Clamp01(f) * 255) (DensityToByte,
    // VoxelTerrain.cs:946-949), while DensityToFloat gives b / 256 (the DensityArray, VoxelTerrain.cs:191-449), which
    // would store one less.
    private static float AsFloat(byte density) => Mathf.Clamp01((density + 0.5f) / 255f);

    // The dig: each voxel above bedrock that is not empty goes to density 0.
    private sealed class Digging : VoxelWork
    {
        private readonly DigBox _box;
        private readonly OreHandling _ores;

        internal Digging(DigBox box, OreHandling ores)
            : base(box.TopDown())
        {
            _box = box;
            _ores = ores;
        }

        protected override bool Visit(VoxelPoint voxel)
        {
            if (!Solid(voxel, Tally))
            {
                return false;
            }

            Set(voxel, Densities.Empty, true, _ores, Tally);
            return true;
        }

        public override object Result() => new TerrainDigView(_box, Tally, _ores, PlayersIn(_box));
    }
}

/// <summary>
/// Voxel edits over frames: each step visits voxels in the given order until the slice holds SliceVoxels changes, the
/// frame's share is spent or the call must stop (WorkStop), then rebuilds the host's terrain over what changed (the
/// game's own diggers do it once for their whole area, LodManager.DirtyLodsBounds after the loop, Explosion.cs:65);
/// with clients connected the next slice waits for a state packet written after it (JobReplication, a gate of its own),
/// so each packet's voxel list (SyncList.Add checks every queued event before adding one) carries one slice.
/// </summary>
internal abstract class VoxelWork : IPendingResult
{
    private const int SliceVoxels = 512;

    private readonly IEnumerator<VoxelPoint> _voxels;
    private readonly ClientReplication _gate = new ClientReplication();
    private bool _done;

    protected VoxelWork(IEnumerable<VoxelPoint> voxels)
    {
        _voxels = voxels.GetEnumerator();
    }

    protected DigTally Tally { get; } = new DigTally();

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

        TerrainDigs.RequireHost();
        ChangedArea area = new ChangedArea();
        while (area.Count < SliceVoxels)
        {
            if (!_voxels.MoveNext())
            {
                _done = true;
                break;
            }

            VoxelPoint voxel = _voxels.Current;
            if (Visit(voxel))
            {
                area.Add(voxel);
            }

            if (StopwatchClock.Instance.NowMs >= untilMs || WorkStop.Requested)
            {
                break;
            }
        }

        if (area.Count > 0)
        {
            LodManager lods = LodManager.Instance;
            if (lods != null)
            {
                lods.DirtyLodsBounds(area.Min, area.Max);
            }

            _gate.Changed(Time.frameCount);
        }

        return _done;
    }

    public abstract object Result();

    /// <summary>Edits one voxel, counting it into Tally; true when its density changed.</summary>
    protected abstract bool Visit(VoxelPoint voxel);

    // The bounds of the voxels one slice changed.
    private sealed class ChangedArea
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
