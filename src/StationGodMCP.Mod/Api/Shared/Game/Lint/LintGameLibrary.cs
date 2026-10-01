#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Pure;

using StationGodMCP.Pure.Lint;
using TerrainSystem;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Lint;

/// <summary>
/// The library rule files call in the game: the pure functions (LintLibrary.Standard) and the ones that ask the game.
/// Adding one is one Add here: a name, a typed signature, what it does, and its body.
/// </summary>
internal static class LintGameLibrary
{
    private const float SunUpSine = 0.1736f;

    private static readonly Vector3[] RayOffsets =
    {
        new Vector3(0f, 0f, 0.5f), new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f),
        new Vector3(0.5f, -0.5f, 0.5f), new Vector3(-0.5f, -0.5f, 0.5f)
    };

    private static LintLibrary? _library;

    internal static LintLibrary Library => _library ??= Build();

    private static LintLibrary Build()
    {
        LintLibrary library = LintLibrary.Standard();
        library
            .Add(new LintFunction("replaceable", "(x: thing) -> placement",
                "Whether a player could place the thing again where it stands, its neighbours present: check_replaceable's " +
                "rule (PlayerPlacement.AsItStands). replaceable is null when it could not be asked, and for a planned thing " +
                "(place_structure checks a placement itself).",
                Replaceable, cached: true))
            .Add(new LintFunction("controls_side", "(x: thing) -> controls?",
                "The world side of a device that carries its slots, buttons and switches (read from its prefab's " +
                "interactables, as describe_prefab's controls); null for a thing without one or not on quarter turns.",
                ControlsSide))
            .Add(new LintFunction("controls_blocked", "(x: thing) -> string?",
                "What stands right in front of the side its controls face (another device, a chute or small thing, a " +
                "frame's body, a wall or frame on the plane in front), as text; null when that side is clear, when it has " +
                "no control side, or when that side is a face-mounted thing's front.",
                ControlsBlocked, cached: true))
            .Add(new LintFunction("sun_blocked", "(x: thing) -> bool",
                "Something stands between the thing and the sun somewhere on the day's path, while the sun is more than " +
                "10 degrees up: the solar arm's own five rays (SolarPanelArm.CalculateSolarEfficiency, the panel's " +
                "CollisionMask; the arm turned to the sun) from each arm's cells, and the terrain (VoxelTerrain.OctreeRaycast); " +
                "from the mesh box's centre for anything else, or a planned panel. 36 sun directions over the day.",
                SunBlocked, cached: true))
            .Add(new LintFunction("weather_exposed", "(x: thing) -> bool",
                "Storms reach the thing: the air in its cell is the planet's, or within 1 kPa of it, or it has none and no " +
                "room (Structure.IsExposedToGlobal, the test SolarRadiators applies before storm damage).",
                WeatherExposed))
            .Add(new LintFunction("logic", "(x: thing, type: string) -> number?",
                "The thing's logic value of a type (On, Setting, Pressure, Ratio ...), as a Logic Reader reads it; null " +
                "when it cannot be read (not a logic device, a planned thing, a type it does not have).",
                Logic))
            .Add(new LintFunction("freezing_point", "(gas: string) -> number?",
                "Kelvin at or below which the gas or liquid freezes in a pipe (Mole.FreezingTemperature); null for a gas " +
                "that cannot freeze.",
                FreezingPoint))
            .Add(new LintFunction("auto_ignition_temperature", "(gases: map<number>) -> number?",
                "Kelvin above which the mix burns by itself (GasMixture.IsAutoIgnition): methane or hydrogen over 1 mol " +
                "573.15 K, liquid alcohol 673.15 K, each 250 K lower with over 1 mol of nitrous oxide or 150 K with ozone; " +
                "hydrazine its critical temperature. null when no fuel is over 1 mol.",
                AutoIgnition))
            .Add(new LintFunction("drill_column", "(x: thing) -> list<cell>",
                "The 2 m cells a deep miner drills through: from the cell below the thing straight down while the cell " +
                "reaches above y 0 (DeepMiner drills to y 0; CheckThingInWay looks at the 2 m cell around the bit).",
                DrillColumn));
        return library;
    }

    private static ThingSubject Thing(LintCall call) =>
        call[0].AsObject as ThingSubject ?? throw new LintEvaluationException("not a thing of the game");

    private static GameLintWorld World(LintCall call) =>
        call.World as GameLintWorld ?? throw new LintEvaluationException("not a lint call in the game");

    private static Structure PrefabOf(ThingSubject thing)
    {
        Structure? prefab = thing.IsPlanned ? null : Prefab.Find(thing.Source.PrefabHash) as Structure;
        return prefab != null ? prefab : thing.Source;
    }

    private static LintValue Replaceable(LintCall call)
    {
        ThingSubject thing = Thing(call);
        if (thing.IsPlanned)
        {
            return Placement(null, null, "a planned thing: place_structure checks the placement itself");
        }

        return PlayerPlacement.AsItStands(thing.Source, World(call).Catalogue) switch
        {
            PlacementVerdict.Refused refused => Placement(false, refused.Rule, refused.Reason),
            PlacementVerdict.NotChecked notChecked => Placement(null, notChecked.Rule, notChecked.Reason),
            _ => Placement(true, null, string.Empty)
        };
    }

    private static LintValue Placement(bool? replaceable, string? rule, string reason) =>
        LintValue.Of(new LintRecord(LintModel.Placement, "placement", new Dictionary<string, LintValue>(StringComparer.Ordinal)
        {
            ["replaceable"] = replaceable.HasValue ? LintValue.Of(replaceable.Value) : LintValue.Null,
            ["rule"] = LintValue.Of(rule),
            ["reason"] = LintValue.Of(reason)
        }));

    private static LintValue ControlsSide(LintCall call)
    {
        ThingSubject thing = Thing(call);
        ControlFace? controls = PrefabControls.Of(PrefabOf(thing));
        if (controls == null || !(thing.Cube is CubeRotation turn))
        {
            return LintValue.Null;
        }

        return LintValue.Of(new LintRecord(LintModel.Controls, thing.Key + "/controls",
            new Dictionary<string, LintValue>(StringComparer.Ordinal)
            {
                ["side"] = LintValue.Of(turn.Turn(controls.Local).Name),
                ["fallback"] = LintValue.Of(controls.Fallback),
                ["source"] = LintValue.Of(controls.Source)
            }));
    }

    private static LintValue ControlsBlocked(LintCall call)
    {
        ThingSubject thing = Thing(call);
        if (!(thing.Cube is CubeRotation turn))
        {
            return LintValue.Null;
        }

        string? blocked = PlacementLayout.ControlsBlockedBy(PrefabOf(thing), turn, thing.Mount, thing.SmallCells,
            thing.IsSmallGrid ? new List<GridCell>() : thing.LargeCells, World(call).Facts, out _, out _);
        return LintValue.Of(blocked);
    }

    private static LintValue SunBlocked(LintCall call)
    {
        ThingSubject thing = Thing(call);
        List<Vector3> origins = new List<Vector3>();
        bool single = false;
        if (!thing.IsPlanned && thing.Source is SolarPanel panel &&
            GameMembers.SolarPanelArms.GetValue(panel) is List<SolarPanelArm> arms)
        {
            foreach (SolarPanelArm arm in arms)
            {
                if (arm != null && arm.Cells != null)
                {
                    origins.Add(arm.Cells.position);
                    single |= arm.SingleRaycast;
                }
            }
        }

        if (origins.Count == 0)
        {
            origins.Add(Bodies.U(thing.RenderBox.Centre));
        }

        LayerMask mask = thing.Source is SolarPanel own ? own.CollisionMask : (LayerMask)Physics.DefaultRaycastLayers;
        RaycastHit[] hits = new RaycastHit[1];
        foreach (Vector3 sun in WorldSubject.SunPath())
        {
            if (sun.y < SunUpSine)
            {
                continue;
            }

            Quaternion facing = Quaternion.LookRotation(sun);
            foreach (Vector3 origin in origins)
            {
                if (VoxelTerrain.Instance != null && VoxelTerrain.Instance.OctreeRaycast(origin, sun))
                {
                    return LintValue.True;
                }

                for (int ray = 0; ray < (single ? 1 : RayOffsets.Length); ray++)
                {
                    if (Physics.RaycastNonAlloc(new Ray(origin + facing * RayOffsets[ray], sun), hits, float.PositiveInfinity,
                            mask) > 0 && hits[0].collider != null && !hits[0].collider.isTrigger)
                    {
                        return LintValue.True;
                    }
                }
            }
        }

        return LintValue.False;
    }

    private static LintValue WeatherExposed(LintCall call)
    {
        ThingSubject thing = Thing(call);
        if (!thing.IsPlanned)
        {
            return LintValue.Of(thing.Source.IsExposedToGlobal());
        }

        WorldGrid grid = new WorldGrid(thing.Pose);
        Atmosphere? air = AtmosphericsController.World?.GetAtmosphereLocal(grid);
        if (air == null)
        {
            return LintValue.Of(RoomController.World?.GetRoom(grid) == null);
        }

        return LintValue.Of(air.IsGlobalAtmosphere || air.IsCloseToGlobal(new PressurekPa(1.0)));
    }

    private static LintValue Logic(LintCall call)
    {
        ThingSubject thing = Thing(call);
        if (!EnumName.TryParse(call[1].AsString, out LogicType type))
        {
            throw new LintEvaluationException($"no logic type {call[1].AsString}");
        }

        if (thing.IsPlanned || !(thing.Source is ILogicable logicable) || !(ScopedTarget.From(logicable) is ScopedTarget target))
        {
            return LintValue.Null;
        }

        return LintValue.Of(LogicTypes.ReadIfReadable(target, type));
    }

    private static LintValue FreezingPoint(LintCall call)
    {
        if (!EnumName.TryParse(call[0].AsString, out Chemistry.GasType type))
        {
            throw new LintEvaluationException($"no gas {call[0].AsString}");
        }

        return Mole.CanFreeze(type) ? LintValue.Of(Mole.FreezingTemperature(type).ToDouble()) : LintValue.Null;
    }

    private static LintValue AutoIgnition(LintCall call)
    {
        IReadOnlyDictionary<string, LintValue> gases = call[0].AsMap;
        double least = GasMixture.MinCombustionMoles.ToDouble();
        double Mol(string name) => gases.TryGetValue(name, out LintValue mol) ? mol.AsNumber : 0.0;
        double offset = Math.Min(
            Mol("NitrousOxide") + Mol("LiquidNitrousOxide") > least ? Chemistry.AutoIgnitionOffsetNitrogenDioxide.ToDouble() : 0.0,
            Mol("Ozone") + Mol("LiquidOzone") > least ? Chemistry.AutoIgnitionOffsetOzone.ToDouble() : 0.0);
        double? limit = null;
        void Fuel(bool present, double kelvin) => limit = present ? Math.Min(limit ?? double.MaxValue, kelvin) : limit;
        Fuel(Mol("Methane") + Mol("LiquidMethane") > least, Chemistry.AutoIgnitionMethane.ToDouble() + offset);
        Fuel(Mol("Hydrogen") + Mol("LiquidHydrogen") > least, Chemistry.AutoIgnitionHydrogen.ToDouble() + offset);
        Fuel(Mol("LiquidAlcohol") > least, Chemistry.AutoIgnitionAlcohol.ToDouble() + offset);
        Fuel(Mol("Hydrazine") + Mol("LiquidHydrazine") > least, Chemistry.AutoIgnitionHydrazine.ToDouble());
        return LintValue.Of(limit);
    }

    private static LintValue DrillColumn(LintCall call)
    {
        ThingSubject thing = Thing(call);
        GameLintWorld world = World(call);
        GridCell own = SmallCellCode.LargeOf(new GridCell((int)Math.Round(thing.Pose.x * 10.0),
            (int)Math.Round(thing.Pose.y * 10.0), (int)Math.Round(thing.Pose.z * 10.0)));
        GridCell start = new GridCell(own.X, own.Y - SmallCellCode.Large, own.Z);
        List<LintValue> cells = new List<LintValue>();
        for (GridCell cell = start; cell.Y + SmallCellCode.Large / 2 > 0 && cells.Count < 512;
             cell = new GridCell(cell.X, cell.Y - SmallCellCode.Large, cell.Z))
        {
            cells.Add(LintValue.Of(world.LargeCell(cell)));
        }

        return LintValue.Of(cells);
    }
}
