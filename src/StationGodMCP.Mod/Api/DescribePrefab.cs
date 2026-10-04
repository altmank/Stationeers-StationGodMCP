#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Rockets;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// describe_prefab: what a buildable prefab is before it stands anywhere, in its own frame (unturned, origin at a
/// snapped reference point): how the cursor places and turns it, the small cells it takes and the box its meshes fill
/// relative to its origin, its ports (joining cell offset, the way a run leaves, type, role, flow), which of its axes
/// reads as its top (VisualUp, with the source of that fact), and a flow its logic Mode reverses. Read only.
/// </summary>
internal static class DescribePrefabApi
{
    // Any point works; the cursor snaps it, and every offset is taken from the snapped origin.
    private static readonly Vector3 Reference = new Vector3(1001f, 101f, 1001f);

    private const int PortTypes = (int)(NetworkType.PowerAndData | NetworkType.Pipe | NetworkType.PipeLiquid |
                                        NetworkType.Chute);

    internal static DescribePrefabView Handle(Args args)
    {
        PrefabRef reference = BuildArgs.PrefabOf(args.Optional("prefab"), "prefab");
        BuildCatalogue catalogue = BuildCatalogue.Load();
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
            prefab.RotationAxis.ToString(), AllowedRotations(prefab), offsets, new BoxView(render),
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

