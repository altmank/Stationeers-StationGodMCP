#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using Assets.Scripts;
using Assets.Scripts.Atmospherics;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Lint;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Lint;

/// <summary>The small value objects of the lint model, read from the game: rotation, box, slots, gases.</summary>
internal static class LintRecords
{
    internal static LintRecord Rotation(CubeRotation turn) =>
        new LintRecord(LintModel.Rotation, $"rotation:{turn.Forward.Name}/{turn.Up.Name}",
            new Dictionary<string, LintValue>(StringComparer.Ordinal)
            {
                ["facing"] = LintValue.Of(turn.Forward.Name),
                ["up"] = LintValue.Of(turn.Up.Name)
            });

    internal static LintRecord Box(Box3 box, string owner) =>
        new LintRecord(LintModel.Box, owner + "/box", new Dictionary<string, LintValue>(StringComparer.Ordinal)
        {
            ["min"] = LintValue.Of(box.Min),
            ["max"] = LintValue.Of(box.Max),
            ["centre"] = LintValue.Of(box.Centre),
            ["size"] = LintValue.Of(box.Size)
        });

    internal static LintValue Slots(Thing thing, string owner)
    {
        List<LintValue> slots = new List<LintValue>();
        if (thing.Slots == null)
        {
            return LintValue.Of(slots);
        }

        for (int index = 0; index < thing.Slots.Count; index++)
        {
            Slot slot = thing.Slots[index];
            if (slot == null)
            {
                continue;
            }

            DynamicThing? occupant = slot.Get();
            double quantity = occupant == null ? 0.0 : occupant is Item item ? WorldItems.QuantityOf(item) : 1.0;
            slots.Add(LintValue.Of(new LintRecord(LintModel.Slot, $"{owner}/slot{index.ToString(CultureInfo.InvariantCulture)}",
                new Dictionary<string, LintValue>(StringComparer.Ordinal)
                {
                    ["index"] = LintValue.Of(index),
                    ["name"] = LintValue.Of(Text.Plain(slot.DisplayName ?? string.Empty)),
                    ["type"] = LintValue.Of(slot.Type.ToString()),
                    ["occupant"] = LintValue.Of(occupant != null ? occupant.PrefabName : null),
                    ["occupant_name"] = LintValue.Of(occupant != null ? Names.Of(occupant) : null),
                    ["quantity"] = LintValue.Of(quantity)
                }, describe: $"slot {index}")));
        }

        return LintValue.Of(slots);
    }

    /// <summary>Moles by gas name (Chemistry.GasType names) above the game's minimum quantity.</summary>
    internal static LintValue Gases(GasMixture mixture)
    {
        double minimum = Chemistry.MINIMUM_QUANTITY_MOLES.ToDouble();
        Dictionary<string, LintValue> gases = new Dictionary<string, LintValue>(StringComparer.Ordinal);
        foreach (Chemistry.GasType type in GasTypes.All)
        {
            double moles = mixture.GetMoleValue(type).Quantity.ToDouble();
            if (moles > minimum)
            {
                gases[type.ToString()] = LintValue.Of(moles);
            }
        }

        return LintValue.Of(gases);
    }
}

/// <summary>The world object: the sun now and over the day, the day's length, the outdoor air.</summary>
internal sealed class WorldSubject : LintSubject
{
    /// <summary>Directions sampled over one day for sun.path: every 10 degrees of the planet's turn.</summary>
    internal const int PathSamples = 36;

    private readonly GameLintWorld _world;

    internal WorldSubject(GameLintWorld world)
        : base(LintModel.World)
    {
        _world = world;
    }

    public override string Key => "world";

    public override string Describe => "the world";

    protected override LintValue Compute(string field)
    {
        switch (field)
        {
            case "sun":
                Vector3 now = OrbitalSimulation.WorldSunVector.normalized;
                return LintValue.Of(new LintRecord(LintModel.Sun, "sun", new Dictionary<string, LintValue>(StringComparer.Ordinal)
                {
                    ["direction"] = LintValue.Of(Bodies.V(now)),
                    ["up"] = LintValue.Of(now.y > 0f && !OrbitalSimulation.IsEclipse),
                    ["path"] = Path()
                }));
            case "day_length":
                return LintValue.Of(OrbitalSimulation.GetDayLengthSeconds());
            case "outdoor":
                return Outdoor();
            default:
                throw NoField(field);
        }
    }

    /// <summary>
    /// Unit vectors toward the sun over one day: the world sun vector turned about the planet's axis as the scene
    /// sees it (OrbitalSimulation.GetSceneRotation applied to PlayerBody.RotationAxis), every 10 degrees. The orbit's
    /// own drift over a day (a few degrees) is left out.
    /// </summary>
    internal static List<Vector3> SunPath()
    {
        List<Vector3> path = new List<Vector3>(PathSamples);
        Vector3 now = OrbitalSimulation.WorldSunVector.normalized;
        OrbitalSimulation? system = OrbitalSimulation.System;
        if (system?.PlayerBody == null)
        {
            path.Add(now);
            return path;
        }

        Vector3 axis = (system.GetSceneRotation() * system.PlayerBody.RotationAxis).normalized;
        for (int step = 0; step < PathSamples; step++)
        {
            path.Add(Quaternion.AngleAxis(step * 360f / PathSamples, axis) * now);
        }

        return path;
    }

    private static LintValue Path()
    {
        List<LintValue> values = new List<LintValue>(PathSamples);
        foreach (Vector3 direction in SunPath())
        {
            values.Add(LintValue.Of(Bodies.V(direction)));
        }

        return LintValue.Of(values);
    }

    private LintValue Outdoor()
    {
        Atmosphere? air = null;
        foreach (ILintObject cell in _world.Subjects("cells"))
        {
            if (cell is CellSubject subject && _world.Facts.RoomAt(subject.Large) == null)
            {
                air = AtmosphericsController.World?.SampleGlobalAtmosphere(new WorldGrid(PieceShapes.Grid(subject.Large)));
                break;
            }
        }

        Dictionary<string, LintValue> values = new Dictionary<string, LintValue>(StringComparer.Ordinal)
        {
            ["gases"] = air != null ? LintRecords.Gases(air.GasMixture) : LintValue.Of(new Dictionary<string, LintValue>()),
            ["pressure"] = LintValue.Of(air != null ? air.PressureGassesAndLiquids.ToDouble() : 0.0),
            ["temperature"] = LintValue.Of(air != null ? air.Temperature.ToDouble() : 0.0)
        };
        return LintValue.Of(new LintRecord(LintModel.Atmosphere, "outdoor", values));
    }
}
