#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Rockets;
using StationGodMCP.Pure.Verbs;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// What a buildable prefab is before it stands anywhere, in its own frame (unturned, origin at a
/// snapped reference point): how the cursor places and turns it, the small cells it takes and the box its meshes fill
/// relative to its origin, its ports (joining cell offset, the way a run leaves, type, role, flow), which of its axes
/// reads as its top (VisualUp, with the source of that fact), and a flow its logic Mode reverses. Read only.
/// </summary>
internal static class PrefabDescriptions
{
    // Any point works; the cursor snaps it, and every offset is taken from the snapped origin.
    private static readonly Vector3 Reference = new Vector3(1001f, 101f, 1001f);

    private const int PortTypes = (int)(NetworkType.PowerAndData | NetworkType.Pipe | NetworkType.PipeLiquid |
                                        NetworkType.Chute);

    /// <summary>The most rows a list or a pattern answers (2.8.0): enough for any family of prefabs.</summary>
    internal const int MaximumRows = 64;

    /// <summary>
    /// inspect prefab (2.8.0) in all its forms. One whole name or hash of a buildable structure gives its placement
    /// view (Describe). One that is no buildable structure, such as an item or a kit, gives its facts. A list, or a
    /// pattern with *, gives a row of facts per loaded prefab it names, by name, at most MaximumRows. Each form adds the
    /// prefab's game data (PrefabData) and recipes (PrefabRecipes) when asked. invalid_prefab when nothing loaded
    /// matches.
    /// </summary>
    internal static object Inspect(PrefabFilter filter, bool withCells, bool withData, bool withRecipes)
    {
        if (filter.Patterns.Count == 1 && OneOf(filter.Patterns[0]) is { } reference)
        {
            Thing? thing = reference switch
            {
                PrefabRef.Named named => WorldModel.Prefabs.NamedIgnoringCase(named.Name),
                PrefabRef.Hashed hashed => Prefab.Find(hashed.Hash),
                _ => null
            };
            if (thing == null)
            {
                throw ApiErrors.Refused("invalid_prefab", $"No loaded prefab is {reference}; prefab names are as " +
                    "find thing and inspect looking_at report them (e.g. StructureWallLight), and a pattern with * " +
                    "(\"*Door*\") lists a family.");
            }

            return WorldModel.Prefabs.Build.Find(reference, out _) is { } buildable
                ? Describe(reference, withCells).With(withData ? PrefabData.Of(buildable) : null,
                    withRecipes ? PrefabRecipes.Of(buildable) : null, SlotsOf(buildable))
                : Facts(thing, withData, withRecipes);
        }

        List<Thing> matched = WorldModel.Prefabs.Matching(filter);
        if (matched.Count == 0)
        {
            throw ApiErrors.Refused("invalid_prefab",
                "No loaded prefab matches prefab; names are as find thing reports them (StructureGlassDoor).");
        }

        List<PrefabFactsView> rows = new List<PrefabFactsView>(System.Math.Min(matched.Count, MaximumRows));
        for (int index = 0; index < matched.Count && index < MaximumRows; index++)
        {
            rows.Add(Facts(matched[index], withData, withRecipes));
        }

        return new PrefabListView(rows, matched.Count);
    }

    private static PrefabRef? OneOf(PrefabPattern pattern) => pattern switch
    {
        PrefabPattern.Hash hash => new PrefabRef.Hashed(hash.Value),
        PrefabPattern.Whole whole => new PrefabRef.Named(whole.Name),
        _ => null
    };

    private static PrefabFactsView Facts(Thing prefab, bool withData, bool withRecipes) =>
        new PrefabFactsView(prefab.PrefabName, prefab.PrefabHash, ThingName.Displayed(prefab.DisplayName,
                prefab.PrefabName), prefab.GetType().Name,
            prefab is Structure && WorldModel.Prefabs.Build.Find(new PrefabRef.Hashed(prefab.PrefabHash), out _) != null,
            withData ? PrefabData.Of(prefab) : null, withRecipes ? PrefabRecipes.Of(prefab) : null,
            SlotsOf(prefab));

    // The template's own slot list (Thing.Slots on the prefab), read without spawning. A slot whose SlotIndex is not
    // set on the template takes its list position.
    private static PrefabSlotsView? SlotsOf(Thing prefab)
    {
        if (prefab.Slots == null || prefab.Slots.Count == 0)
        {
            return null;
        }

        List<PrefabSlotView> rows = new List<PrefabSlotView>(prefab.Slots.Count);
        List<PrefabSlotFacts> facts = new List<PrefabSlotFacts>(prefab.Slots.Count);
        for (int position = 0; position < prefab.Slots.Count; position++)
        {
            Slot slot = prefab.Slots[position];
            PrefabSlotFacts slotFacts = new PrefabSlotFacts(slot.Type == Slot.Class.None,
                slot.SpecificTypePrefabHashes ?? System.Array.Empty<int>());
            facts.Add(slotFacts);
            rows.Add(new PrefabSlotView(position, ThingName.Displayed(slot.DisplayName, null), slot.StringKey,
                new EnumValueView((int)slot.Type, EnumNames<Slot.Class>.Of(slot.Type)),
                PrefabSlotRule.IsGeneral(slotFacts)));
        }

        return new PrefabSlotsView(rows, PrefabSlotRule.GeneralCount(facts));
    }

    /// <summary>The prefab named, every small cell's offset with withCells; invalid_prefab when none is loaded.</summary>
    internal static DescribePrefabView Describe(PrefabRef reference, bool withCells)
    {
        BuildCatalogue catalogue = WorldModel.Prefabs.Build;
        Structure? found = catalogue.Find(reference, out string? issue);
        if (found == null)
        {
            throw ApiErrors.Refused("invalid_prefab", issue ?? "No such prefab.");
        }

        Structure prefab = found;

        Structure? cursor = catalogue.CursorOf(prefab);
        Vector3 origin = cursor != null ? CursorCheck.Snap(cursor, Reference, Quaternion.identity) : Reference;
        List<GridCell> cells = Bodies.SmallCells(prefab, origin, Quaternion.identity);
        List<PointView> offsets = cells.ConvertAll(cell => PointView.Of(Bodies.V(PieceShapes.CentreOf(cell) - origin)));
        Box3 render = Bodies.RenderBox(prefab, Vector3.zero, Quaternion.identity);
        Box3? grid = cells.Count > 0 ? Box3.OfSmallCells(cells) : (Box3?)null;
        List<PrefabPortView> ports = new List<PrefabPortView>();
        foreach (OrientPort port in Orienter.Ports(prefab, origin, Quaternion.identity, PortTypes))
        {
            ports.Add(new PrefabPortView(port.Index, port.Type, port.Role, port.Flow,
                PointView.Of(port.Cell - Bodies.V(origin)), port.Outward.Name));
        }

        VisualUp up = PlacePlanner.VisualUpOf(prefab);
        return new DescribePrefabView(
            new PlacementPrefabView(prefab.PrefabName, prefab.PrefabHash, prefab.DisplayName,
                prefab.BuildStates.Count), prefab.GetType().Name,
            BuildReports.SnapName(prefab.PlacementType), prefab.GridSize, prefab is SmallGrid,
            prefab.RotationAxis.ToString(), AllowedRotations(prefab), offsets.Count, withCells ? offsets : null,
            new BoxView(render),
            grid.HasValue ? new BoxView(new Box3(grid.Value.Min - Bodies.V(origin), grid.Value.Max - Bodies.V(origin)))
                : null,
            ports, new VisualUpView(up.LocalUp.Name, up.LyingAllowed, up.Source, up.Verified),
            Orienter.ReversibleFlow(prefab)
                ? new ModeFlipView("Mode", 1, "Mode 0 (Right) and 1 (Left) move gas opposite ways through it")
                : null,
            cursor != null,
            PrefabControls.Of(prefab) is ControlFace controls ? new ControlFaceView(controls) : null,
            EngineOf(prefab));
    }

    // A rocket engine's numbers as the game filled them at load (OnPrefabLoad runs CalculateMaxThrust on the prefab).
    private static PrefabEngineView? EngineOf(Structure prefab)
    {
        if (!(prefab is RocketEngineBase engine))
        {
            return null;
        }

        string className = engine.GetType().Name;
        return new PrefabEngineView(EngineSpecs.NameOf(className),
            new EnginePerformance(engine.MaxThrust, engine.MaxExhaustVelocity, engine.SpecificImpulse,
                engine.MaxFuelFlowRate, engine.EngineEfficiency, engine.EfficiencyPercent, engine.MassContribution,
                engine.internalVolume),
            EngineDesign.Of(className));
    }

    // The turns the cursor can give a grid-placed prefab; every turn for the others (the cursor check decides).
    private static List<OrientationView> AllowedRotations(Structure prefab)
    {
        List<OrientationView> turns = new List<OrientationView>();
        foreach (CubeRotation turn in CubeRotation.All)
        {
            if (PlacePlanner.CursorAllows(prefab, turn))
            {
                turns.Add(OrientationView.Of(turn));
            }
        }

        return turns;
    }
}

