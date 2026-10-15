#nullable enable

using System.Collections.Generic;
using Assets.Scripts.GridSystem;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.WorldModel;

/// <summary>
/// A frame's exposed faces, followed (cost design A2): per large cell holding a frame, the faces rules (1) to (3) of
/// OuterFrames give and those rule (4) decides (CellAir.Measure), kept until something they read changes. They read
/// the cell's neighbours' rooms, centre structures and terrain and the face structures on either side, so a change at a
/// cell (WorldEvents.Cells: a structure's air or gravity, a room's cells, an edited voxel's cells) drops the masks at
/// that cell and its six neighbours; a mask is measured again when next read. Rule (4)'s air test is live: a few faces,
/// read each time.
/// <para>
/// Verify mode ([Performance] VerifyFacts): every read also measures again and logs a difference.
/// </para>
/// </summary>
[ReplyFact(FactRule.Followed, "a frame's exposed faces (rules 1-3; rule 4's air is live)")]
internal static class FrameFaces
{
    private static readonly Dictionary<GridCell, Mask> Masks = new Dictionary<GridCell, Mask>();
    private static readonly List<GridCell> Changed = new List<GridCell>();
    private static readonly List<GridCell> Around = new List<GridCell>(7);
    private static long _measured;
    private static long _reused;
    private static long _dropped;
    private static long _verified;
    private static long _differences;

    /// <summary>[Performance] VerifyFacts: every read also measures again and logs a difference.</summary>
    internal static bool Verify { get; set; }

    internal static bool Ready => WorldEvents.CellsMissing.Length == 0;

    /// <summary>Each server frame: drops the masks the changes told since the last frame reach.</summary>
    internal static void Tick()
    {
        if (Ready)
        {
            using ProfScope scope = Prof.Scope(ProfId.WorldFacts);
            Drop();
        }
    }

    /// <summary>
    /// The exposed faces of the frame at the cell (bit n is face n of CellAir.Steps): the kept mask, measured when there
    /// is none, with rule (4)'s faces read now. Refuses game_changed when a change point is missing.
    /// </summary>
    internal static int Exposed(Grid3 cell, CellAir air)
    {
        if (!Ready)
        {
            throw new GameChangedException(WorldEvents.CellsMissing);
        }

        Drop();
        GridCell key = new GridCell(cell.x, cell.y, cell.z);
        if (Masks.TryGetValue(key, out Mask mask))
        {
            _reused++;
            if (Verify)
            {
                Compare(cell, air, mask);
            }
        }
        else
        {
            mask = Measure(cell, air);
            Masks[key] = mask;
        }

        return mask.Open | (mask.WorldAirOnly != 0 ? CellAir.WithWorldAir(cell, mask.WorldAirOnly) : 0);
    }

    /// <summary>server info runtime.facts.frame_faces.</summary>
    internal static FrameFacesFactView View() =>
        new FrameFacesFactView(Ready, Verify, Masks.Count, _measured, _reused, _dropped, WorldEvents.Cells.Told,
            _verified, _differences);

    /// <summary>A world left.</summary>
    internal static void Clear()
    {
        Masks.Clear();
        WorldEvents.Clear();
    }

    private static Mask Measure(Grid3 cell, CellAir air)
    {
        _measured++;
        air.Measure(cell, out int open, out int worldAirOnly);
        return new Mask(open, worldAirOnly);
    }

    // The masks the changes told since the last Drop reach: each changed cell's and its neighbours'.
    private static void Drop()
    {
        if (!WorldEvents.Cells.Take(Changed) || Masks.Count == 0)
        {
            // Everything changed, or nothing is kept (a load tells every structure's cells): drop all at once.
            _dropped += Masks.Count;
            Masks.Clear();
            Changed.Clear();
            return;
        }

        for (int index = 0; index < Changed.Count; index++)
        {
            Around.Clear();
            ChangedCells.Around(Changed[index], Around);
            for (int near = 0; near < Around.Count; near++)
            {
                if (Masks.Remove(Around[near]))
                {
                    _dropped++;
                }
            }
        }

        Changed.Clear();
    }

    private static void Compare(Grid3 cell, CellAir air, Mask kept)
    {
        air.Measure(cell, out int open, out int worldAirOnly);
        _verified++;
        if (open == kept.Open && worldAirOnly == kept.WorldAirOnly)
        {
            return;
        }

        _differences++;
        StationGodMod.LogWarning($"Frame faces differ at {cell}: kept open {kept.Open} rule-4 {kept.WorldAirOnly}, " +
                                 $"measured open {open} rule-4 {worldAirOnly}.");
    }

    private readonly struct Mask
    {
        internal Mask(int open, int worldAirOnly)
        {
            Open = (byte)open;
            WorldAirOnly = (byte)worldAirOnly;
        }

        internal byte Open { get; }

        internal byte WorldAirOnly { get; }
    }
}
