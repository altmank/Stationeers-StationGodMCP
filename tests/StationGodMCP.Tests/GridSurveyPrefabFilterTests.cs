#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;
using Xunit.Abstractions;

namespace StationGodMCP.Tests;

/// <summary>
/// survey area prefab: only the pieces and devices of those prefabs (whole names or parts with *), with network and
/// kind. A room's 300-piece pieces section drops from about 64 KB to the insulated pipes alone.
/// </summary>
public sealed class GridSurveyPrefabFilterTests(ITestOutputHelper output)
{
    private static readonly string[] Pieces =
    {
        "StructureCableStraight", "StructureCableCorner", "StructurePipeStraight", "StructurePipeCorner",
        "StructureInsulatedPipeStraight", "StructurePipeLiquidStraight"
    };

    [Fact]
    public void NoPrefabAdmitsEveryPieceAndDevice()
    {
        SurveyFilter every = Filter("{}");

        Assert.True(every.AdmitsPiece(null, "pipe", "StructurePipeStraight", 0));
        Assert.True(every.AdmitsDevice("StructureVolumePump", 0, new List<ThingId?>(), new List<string?>()));
    }

    [Fact]
    public void APrefabPartKeepsMatchingPiecesAndDevicesAnyCase()
    {
        SurveyFilter insulated = Filter("""{"prefab":"*insulated*"}""");

        Assert.True(insulated.AdmitsPiece(null, "pipe", "StructureInsulatedPipeStraight", 0));
        Assert.False(insulated.AdmitsPiece(null, "pipe", "StructurePipeStraight", 0));
        Assert.False(insulated.AdmitsDevice("StructureVolumePump", 0, new List<ThingId?>(), new List<string?>()));
    }

    [Fact]
    public void PrefabsComposeWithKindsAndNetworks()
    {
        SurveyFilter filter = new SurveyFilter(SurveyNetworkFilter.Only(new HashSet<long> { 17 }), SurveyKinds.Every,
            Options("""{"prefab":["StructurePipeStraight","StructureVolumePump"]}""").Selector.Prefab);

        Assert.True(filter.AdmitsPiece(new ThingId(17), "pipe", "StructurePipeStraight", 0));
        Assert.False(filter.AdmitsPiece(new ThingId(18), "pipe", "StructurePipeStraight", 0));
        Assert.False(filter.AdmitsPiece(new ThingId(17), "pipe", "StructurePipeCorner", 0));
        Assert.True(filter.AdmitsDevice("StructureVolumePump", 0, new List<ThingId?> { new ThingId(17) },
            new List<string?> { "Pipe" }));
    }

    [Fact]
    public void SurveyAreaTakesThePrefabOptions()
    {
        Assert.Null(Check("""{"what":"area","room":"28","sections":["pieces"],"prefab":"*Insulated*"}"""));
        Assert.Null(Check("""{"what":"area","room":"28","prefab":["StructurePipeStraight"]}"""));
    }

    /// <summary>
    /// The 2026-10-08 call: sections ["pieces"] over a plant room of 300 cable and pipe pieces, list_limits 500; with
    /// prefab *Insulated* the 50 insulated pipes alone.
    /// </summary>
    [Fact]
    public void ARoomsPiecesSectionDropsToThePrefabsAskedFor()
    {
        SurveyFilter insulated = Filter("""{"prefab":"*Insulated*"}""");
        List<SurveyPieceView> all = new List<SurveyPieceView>();
        List<SurveyPieceView> kept = new List<SurveyPieceView>();
        for (int index = 0; index < 300; index++)
        {
            string prefab = Pieces[index % Pieces.Length];
            string kind = prefab.Contains("Cable") ? "cable" : "pipe";
            SurveyPieceView piece = Piece(index, prefab, kind);
            all.Add(piece);
            if (insulated.AdmitsPiece(new ThingId(17), kind, prefab, 0))
            {
                kept.Add(piece);
            }
        }

        int before = Survey(all).Length;
        int after = Survey(kept).Length;
        output.WriteLine($"survey area sections [pieces], 300 pieces: all {before} bytes, prefab *Insulated* " +
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
        Page<SurveyCell> page = new Page<SurveyCell>(new List<SurveyCell>(), 125, 0, 8, true);
        SurveyContents contents = new SurveyContents(pieces, new List<SurveyDeviceView>(), new List<object>(),
            new List<SurveyNetworkVisibilityView>(), new List<SurveyDoorView>());
        return StationGodMCP.Api.Shared.ApiJson.WriteShared(
            SurveyAreaReply.Of(page, contents, SurveySections.Of(new[] { "pieces" }), null));
    }

    private static SurveyFilter Filter(string json) =>
        new SurveyFilter(SurveyNetworkFilter.Every, SurveyKinds.Every, Options(json).Selector.Prefab);

    private static SurveyAreaOptions Options(string json) =>
        SurveyAreaOptions.Bind(new OptionReader(SurveyAreaOptions.Spec, JObject.Parse(json)));

    private static VerbError? Check(string options) =>
        OptionCheck.Check(VerbSurface.Verb("survey")!, JObject.Parse(options), out _);
}
