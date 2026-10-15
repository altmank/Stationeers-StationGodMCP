#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Pure.Verbs;

/// <summary>A blueprint piece as build blueprint's report lists it: the prefab it creates and where it lands.</summary>
internal sealed class PastePiece
{
    internal PastePiece(string prefab, Vec3 cell)
    {
        Prefab = prefab;
        Cell = cell;
    }

    /// <summary>BlueprintEntry.PrefabName.</summary>
    public string Prefab { get; }

    /// <summary>Where the piece lands, in metres (BlueprintMod's own paste geometry).</summary>
    public Vec3 Cell { get; }
}

/// <summary>
/// build blueprint's report (Report.Of): one target per piece of the blueprint file, [cell, prefab, ? -> prefab] with
/// the what's undo mark (the newest paste only), the call's own body (the file, the anchor, the turn, the copied angle,
/// how long the paste takes and what a report cannot show), and no problems: a paste whose checks refused answers its
/// refusal as the report's one problem (WriteCheck.Outcome).
/// </summary>
internal static class PasteReplies
{
    internal static ReportReply Reported(IReadOnlyList<PastePiece> pieces, BlueprintPasteReportView body, string mark)
    {
        List<ReportTarget> targets = new List<ReportTarget>(pieces.Count);
        foreach (PastePiece piece in pieces)
        {
            targets.Add(new ReportTarget(null, Cell(piece.Cell), piece.Prefab, JValue.CreateNull(),
                new JValue(piece.Prefab), mark));
        }

        return Report.Of(targets, JObject.FromObject(body, ApiJson.Serializer), new List<ReportProblem>());
    }

    // A piece's cell, in metres and hundredths, as a waypoint is given: [x, y, z].
    private static JArray Cell(Vec3 cell) =>
        new JArray(Decimal(cell.X), Decimal(cell.Y), Decimal(cell.Z));

    private static double Decimal(double metres) => Math.Round(metres * 100.0) / 100.0;
}
