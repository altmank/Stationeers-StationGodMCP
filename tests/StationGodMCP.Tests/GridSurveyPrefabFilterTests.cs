#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;
using Xunit.Abstractions;

namespace StationGodMCP.Tests;

/// <summary>
/// grid_survey prefab, prefabs and prefab_contains: only the pieces and devices of those prefabs, with network_ids and
/// kinds. A room's 300-piece pieces section drops from about 64 KB to the insulated pipes alone.
/// </summary>
public sealed class GridSurveyPrefabFilterTests(ITestOutputHelper output)
{
    private static readonly string[] Pieces =
    {
        "StructureCableStraight", "StructureCableCorner", "StructurePipeStraight", "StructurePipeCorner",
        "StructureInsulatedPipeStraight", "StructurePipeLiquidStraight"
    };

    [Fact]
    public void NoPrefabArgumentAdmitsEveryPieceAndDevice()
    {
        SurveyFilter every = Filter("{}");

        Assert.True(every.AdmitsPiece(null, "pipe", "StructurePipeStraight"));
        Assert.True(every.AdmitsDevice("StructureVolumePump", new List<ThingId?>(), new List<string?>()));
    }

    [Fact]
    public void PrefabContainsKeepsMatchingPiecesAndDevicesAnyCase()
    {
        SurveyFilter insulated = Filter("""{"prefab_contains":"insulated"}""");

        Assert.True(insulated.AdmitsPiece(null, "pipe", "StructureInsulatedPipeStraight"));
        Assert.False(insulated.AdmitsPiece(null, "pipe", "StructurePipeStraight"));
        Assert.False(insulated.AdmitsDevice("StructureVolumePump", new List<ThingId?>(), new List<string?>()));
    }

    [Fact]
    public void PrefabsComposeWithKindsAndNetworks()
    {
        SurveyFilter filter = new SurveyFilter(SurveyNetworkFilter.Only(new HashSet<long> { 17 }), SurveyKinds.Every,
            PrefabMatches.Parse(Args("""{"prefabs":["StructurePipeStraight","StructureVolumePump"]}""")));

        Assert.True(filter.AdmitsPiece(new ThingId(17), "pipe", "StructurePipeStraight"));
        Assert.False(filter.AdmitsPiece(new ThingId(18), "pipe", "StructurePipeStraight"));
        Assert.False(filter.AdmitsPiece(new ThingId(17), "pipe", "StructurePipeCorner"));
        Assert.True(filter.AdmitsDevice("StructureVolumePump", new List<ThingId?> { new ThingId(17) },
            new List<string?> { "Pipe" }));
    }

    [Fact]
    public void TheSidecarTakesThePrefabArguments()
    {
        Assert.Empty(Problems("""{"room_id":"28","sections":["pieces"],"prefab_contains":"Insulated"}"""));
        Assert.Empty(Problems("""{"room_id":"28","prefabs":["StructurePipeStraight"]}"""));
    }

    /// <summary>
    /// The 2026-10-08 call: sections ["pieces"] over a plant room of 300 cable and pipe pieces, list_limits 500; with
    /// prefab_contains Insulated the 50 insulated pipes alone.
    /// </summary>
    [Fact]
    public void ARoomsPiecesSectionDropsToThePrefabsAskedFor()
    {
        SurveyFilter insulated = Filter("""{"prefab_contains":"Insulated"}""");
        List<SurveyPieceView> all = new List<SurveyPieceView>();
        List<SurveyPieceView> kept = new List<SurveyPieceView>();
        for (int index = 0; index < 300; index++)
        {
            string prefab = Pieces[index % Pieces.Length];
            string kind = prefab.Contains("Cable") ? "cable" : "pipe";
            SurveyPieceView piece = Piece(index, prefab, kind);
            all.Add(piece);
            if (insulated.AdmitsPiece(new ThingId(17), kind, prefab))
            {
                kept.Add(piece);
            }
        }

        int before = Survey(all).Length;
        int after = Survey(kept).Length;
        output.WriteLine($"grid_survey sections [pieces], 300 pieces: all {before} bytes, prefab_contains Insulated " +
                         $"{after} bytes ({kept.Count} pieces)");

        Assert.Equal(50, kept.Count);
        Assert.InRange(before, 50_000, 80_000);
        Assert.True(after * 5 <= before, $"filtered {after} bytes against {before}");
    }

    private static SurveyPieceView Piece(int index, string prefab, string kind) =>
        new SurveyPieceView(new ThingView(new ThingId(2_300_000 + index), prefab, null), kind,
            new PositionView(593.5 + index % 10 * 0.5, 211.25, 627 + index / 10 * 0.5), null, null,
            new List<string> { "+x", "-x" }, new ThingId(17), kind == "pipe" ? "gas" : "normal");

    private static string Survey(List<SurveyPieceView> pieces)
    {
        Slice<SurveyCell> page = Slice<SurveyCell>.Page(new List<SurveyCell>(),
            PageRequest.From(Args("{}"), 8, 125), 125);
        SurveyContents contents = new SurveyContents(pieces, new List<SurveyDeviceView>(), new List<object>(),
            new List<SurveyNetworkVisibilityView>(), new List<SurveyDoorView>());
        return WireCheck.New(new GridSurveyView(page, contents, SurveySections.Parse(Args("""{"sections":["pieces"]}""")),
            null));
    }

    private static SurveyFilter Filter(string json) =>
        new SurveyFilter(SurveyNetworkFilter.Every, SurveyKinds.Every, PrefabMatches.Parse(Args(json)));

    private static Args Args(string json) => new Args(JObject.Parse(json));

    private static IReadOnlyList<string> Problems(string arguments)
    {
        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(arguments);
        return StationGodMCP.Server.ArgumentCheck.Problems(StationGodMCP.Server.Program.InputSchemas["grid_survey"],
            document.RootElement);
    }
}
