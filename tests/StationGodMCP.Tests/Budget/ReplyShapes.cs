#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Tests.Budget;

/// <summary>
/// Every what's default reply on the large world (LargeWorld), as ReplySynth builds it: which view, how long each
/// list is and why, which parts a default reply leaves out. This table is the heavy-payload inventory: a list that
/// grows with the world, a page or a request says so here, with the bound that keeps it in the budget. A method the
/// table does not cover must be in Exemptions with its reason, so a new tool fails ReplyBudgetTests until it is placed.
/// </summary>
internal static class ReplyShapes
{
    /// <summary>Members whose length is fixed by what they are, whatever the tool.</summary>
    internal static readonly ReplyShape Common = new ReplyShape(typeof(object))
        // Vectors and pairs written as arrays.
        .List("*.PadSizeTiles", 2).List("*.Direction", 3).List("DishView.Forward", 3).List("DishNowView.Forward", 3)
        .List("DishView.TransformUp", 3).List("DishAimView.Pointing", 3).List("DishAimView.Target", 3)
        .List("MountView.Axes", 2).List("MountView.Min", 2).List("MountView.Max", 2).List("MinerBeaconView.At", 3)
        .List("MinerProfileView.Quantity", 2).List("MinerProfileView.TimeS", 2)
        .List("BlueprintPasteStartedView.Anchor", 3).List("BandView.Ideal", 2).List("BandView.Survivable", 2)
        .List("PartPoseView.Offset", 3)
        // A piece's ends (at most 6, mostly 2) and an end's flow.
        .List("*.Ends", 2).List("*.ConnectedEnds", 2).List("RunFlowView.In", 1).List("RunFlowView.Out", 1)
        .List("RunCellView.Joins", 1)
        // sp and ra name two of 18 registers.
        .List("RegisterView.Aliases", 1)
        // A thing's holders from its own slot outwards: a suit in a locker, an ore in a backpack on a player.
        .List("*.HeldIn", 2)
        // A gas mixture's gases: the large world's tanks hold four or five.
        .List("HeldAtmosphereView.Contents", 5).List("RoomView.Gases", 5).List("RunPipeNetworkView.Gases", 5)
        .List("ReservoirView.Contents", 3).List("StartingAirView.Gases", 5).List("BurstView.Gases", 4)
        .List("PlanetView.Gases", 9).List("PlantAirView.Ratios", 8).List("FuelLineView.Mix", 3)
        // Material lines: a piece costs or gives back one or two kinds of item.
        // A build step takes one or two items and uses a tool.
        .List("BuildStateView.Next", 3)
        .List("*.Refund", 2).List("*.Cost", 2).List("*.Needed", 2).List("*.Stacks", 2).List("*.Used", 2)
        .List("PlacementView.Ports", 2).List("SurveyDeviceView.Ports", 3)
        // Problems and warnings a report carries on a typical large-world request.
        .List("*.Problems", 2).List("*.Warnings", 2)
        // The controllers writing a written logic type on a device: one chip or writer, rarely two.
        .List("*.DrivenBy", 2)
        .Build(typeof(JObject), () => new JObject { ["prefab"] = "StructureWallLight", ["at"] = new JArray(1, 2, 3) });

    internal static IReadOnlyDictionary<string, ReplyShape[]> ByMethod { get; } = Build();

    /// <summary>Whats held to no budget, each with why: none while every what's reply is still to be shaped here.</summary>
    internal static IReadOnlyDictionary<string, string> Exemptions { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    private static ReplyShape S<T>() => new ReplyShape(typeof(T));

    private static Dictionary<string, ReplyShape[]> Build()
    {
        Dictionary<string, ReplyShape[]> shapes = new Dictionary<string, ReplyShape[]>(StringComparer.Ordinal);
        return shapes;
    }

    // clean_chutes' dry run at the default limit: removals and dead pieces kept each listed up to it. kept_pieces holds
    // only pieces on a path left with an open end (a piece serving a path is not listed). riding: an item rides in a
    // dead piece only on its way to the floor, and in a junction for one tick, so two at once is already rare. It
    // builds nothing, so no warning (materials_from_player) is listed.
}
