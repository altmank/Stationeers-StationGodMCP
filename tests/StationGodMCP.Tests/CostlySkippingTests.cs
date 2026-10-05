#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Shaping;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Skipping costly reply parts (protocol.md, Shaping before the reply is built): ShapeRequest.Wants answers false only
/// for a key the shaping writer would leave out, so a reply built without that part, shaped, equals the full reply
/// shaped. Held for every thing_health form whose list entries carry networks.
/// </summary>
public sealed class CostlySkippingTests
{
    // ---- Wants ----

    [Fact]
    public void WithoutFieldsEveryPartIsWanted()
    {
        Assert.True(ShapeRequest.None.Wants("things", "networks"));
        Assert.True(Lenient(new JObject { ["limit"] = new JObject { ["things"] = 3 } }).Wants("things", "networks"));
        Assert.True(Lenient(new JObject { ["fields"] = new JArray() }).Wants("results", "networks"));
    }

    [Theory]
    [InlineData("networks", true, true)]
    [InlineData("  networks ", true, true)]
    [InlineData("things.networks", true, false)]
    [InlineData("things.networks.kind", true, false)]
    [InlineData("results.networks.id", false, true)]
    [InlineData("reference_id", false, false)]
    [InlineData("Networks", false, false)]
    [InlineData("networks.kind", true, true)]
    [InlineData("things", true, false)]
    [InlineData("networks-x", false, false)]
    [InlineData("things.position.x", false, false)]
    public void FieldsWantOnlyWhatTheWriterKeeps(string selector, bool inThings, bool inResults)
    {
        ShapeRequest shape = ShapingChecks.Fields(new[] { "reference_id", selector });

        Assert.Equal(inThings, shape.Wants("things", "networks"));
        Assert.Equal(inResults, shape.Wants("results", "networks"));
    }

    [Fact]
    public void ASelectorThatIsNotTextWantsNothing()
    {
        ShapeRequest shape = Lenient(new JObject { ["fields"] = new JArray(7, new JObject()) });

        Assert.False(shape.Wants("things", "networks"));
    }

    [Fact]
    public void ArgsCarryTheCallsShapeIntoDerivedArguments()
    {
        ShapeRequest shape = ShapingChecks.Fields(new[] { "reference_id" });
        Args args = new Args(new JObject { ["limit"] = 5 }, shape);

        Assert.Same(shape, args.Shape);
        Assert.Same(shape, args.With("offset", 10).Shape);
        Assert.Same(ShapeRequest.None, new Args(new JObject()).Shape);
        Assert.Same(ShapeRequest.None, new Args(new JObject(), "items[0]").Shape);
    }

    // ---- equivalence: skipped then shaped equals full then shaped ----

    private static readonly string[] SelectorPool =
    {
        "reference_id", "damage_ratio", "networks", "Networks", "things.networks", "things.networks.kind",
        "results.networks", "results.networks.id", "things.position.x", "results.index", "networks.kind", "things",
        "no_such_key", "a-b", ""
    };

    public static IEnumerable<object[]> Forms()
    {
        yield return new object[] { "scan" };
        yield return new object[] { "network" };
        yield return new object[] { "many" };
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void ASkippedReplyShapesToTheFullReplyShaped(string form)
    {
        string list = form == "many" ? "results" : "things";
        int skipped = 0;
        foreach (string[] fields in Selections())
        {
            ShapeRequest shape = ShapingChecks.Fields(fields);
            bool wanted = shape.Wants(list, "networks");
            skipped += wanted ? 0 : 1;

            ShapedText full = ShapingChecks.Mod(Reply(form, withNetworks: true), shape);
            ShapedText built = ShapingChecks.Mod(Reply(form, withNetworks: wanted), shape);

            Assert.True(full.Json == built.Json, $"{form} fields [{string.Join(", ", fields)}]:\n{full.Json}\n{built.Json}");
        }

        Assert.True(skipped > 0, "no selection skipped networks");
    }

    [Theory]
    [MemberData(nameof(Forms))]
    public void LimitsAndMaxBytesSeeTheSameReply(string form)
    {
        string list = form == "many" ? "results" : "things";
        ShapeRequest shape = Lenient(new JObject
        {
            ["fields"] = new JArray("reference_id", "condition"),
            ["limit"] = new JObject { [list] = 1 },
            ["max_bytes"] = 2048
        });
        Assert.False(shape.Wants(list, "networks"));

        ShapedText full = ShapingChecks.Mod(Reply(form, withNetworks: true), shape);
        ShapedText built = ShapingChecks.Mod(Reply(form, withNetworks: false), shape);

        Assert.Equal(full.Json, built.Json);
        Assert.Equal(full.Outcome.Lists, built.Outcome.Lists);
        Assert.Equal(full.Outcome.Cut, built.Outcome.Cut);
    }

    [Fact]
    public void KeepingNetworksStillWritesThem()
    {
        ShapeRequest shape = ShapingChecks.Fields(new[] { "reference_id", "things.networks" });
        Assert.True(shape.Wants("things", "networks"));

        JObject written = JObject.Parse(ShapingChecks.Mod(Reply("scan", withNetworks: true), shape).Json);

        Assert.Equal("pipe", (string?)written["things"]![0]!["networks"]![0]!["kind"]);
        Assert.Null(written["things"]![1]!["networks"]);
    }

    // Every selection of up to three selectors from the pool, plus the whole pool.
    private static IEnumerable<string[]> Selections()
    {
        int count = SelectorPool.Length;
        for (int a = 0; a < count; a++)
        {
            yield return new[] { SelectorPool[a] };
            for (int b = a + 1; b < count; b++)
            {
                yield return new[] { SelectorPool[a], SelectorPool[b] };
                for (int c = b + 1; c < count; c++)
                {
                    yield return new[] { SelectorPool[a], SelectorPool[b], SelectorPool[c] };
                }
            }
        }

        yield return SelectorPool;
    }

    private static ShapeRequest Lenient(JObject shape) => ShapeRequest.Lenient(shape)!;

    // A reply of each list form as thing_health builds it: a structure with networks (unless skipped), an item without.
    private static object Reply(string form, bool withNetworks)
    {
        List<HealthView> views = new List<HealthView>
        {
            Health(700, "StructurePipeStraight", structure: true, withNetworks),
            Health(701, "ItemKitPipe", structure: false, withNetworks),
            Health(702, "StructureCableStraight", structure: true, withNetworks)
        };
        PageRequest page = PageRequest.From(new Args(new JObject { ["limit"] = 3 }), 200, 500);
        switch (form)
        {
            case "scan":
                return new HealthScanView(Slice<HealthView>.Page(views, page, 3), 2, 0, 40, 0.0,
                    new LocalPlayerView(new ThingId(1), "Player", new PositionView(0, 0, 0)));
            case "network":
                return new HealthNetworkView(new ThingId(900), "pipe", 12, false, Slice<HealthView>.Page(views, page, 3),
                    BrokenNeighbourReport.None);
            case "many":
                BatchBuilder batch = new BatchBuilder(4);
                batch.Succeeded(new HealthItemView(0, views[0]));
                batch.Failed(1, new ApiException("thing_not_found", "No thing with reference id 5."));
                batch.Succeeded(new HealthItemView(2, views[1]));
                batch.Succeeded(new HealthItemView(3, views[2]));
                return batch.Build();
            default:
                throw new ArgumentException(form);
        }
    }

    private static HealthView Health(long id, string prefab, bool structure, bool withNetworks) => new HealthView(
        new ThingView(new ThingId(id), prefab, prefab),
        structure ? "structure" : "item",
        structure ? "Pipe" : "Stackable",
        new DamageReading("destructible", "ThingDamageState", 100f, 12.5, 0.125, 88,
            new DamagePartsView(12.5f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f)),
        new HealthFlags(null, false, false, structure ? "none" : null, HealthCondition.Damaged, structure ? false : null),
        new PositionView(1.5, 2.0, -3.5),
        4.25,
        null,
        withNetworks && structure
            ? new List<NetworkRefView> { new NetworkRefView("pipe", new ThingId(900)), new NetworkRefView("cable", new ThingId(901)) }
            : null);
}
