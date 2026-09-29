#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using Xunit;

namespace StationGodMCP.Tests;

public sealed class ThingIdTests
{
    [Fact]
    public void WritesADecimalString()
    {
        Assert.Equal("\"9007199254740993\"", WireCheck.New(new ThingId(9007199254740993)));
    }

    [Theory]
    [InlineData("\"42\"", true)]
    [InlineData("42", true)]
    [InlineData("\"4x\"", false)]
    [InlineData("4.5", false)]
    [InlineData("true", false)]
    public void ReadsStringsAndIntegersOnly(string json, bool readable)
    {
        Assert.Equal(readable, ThingId.TryRead(JToken.Parse(json), out ThingId id));
        if (readable)
        {
            Assert.Equal(42, id.Value);
        }
    }
}

public sealed class ArgsTests
{
    private static Args Parse(string json) => new Args(JObject.Parse(json));

    [Fact]
    public void NullMeansAbsent()
    {
        Args args = Parse("{\"reference_id\": null, \"limit\": null}");
        Assert.False(args.Has("reference_id"));
        Assert.Null(args.OptionalThingId("reference_id"));
        Assert.Null(args.OptionalInt("limit", 1, 10));
    }

    [Fact]
    public void WrongTypeIsInvalidArgument()
    {
        ApiException error = Assert.Throws<ApiException>(() => Parse("{\"color\": 3}").OptionalString("color"));
        Assert.Equal("invalid_argument", error.Code);
        Assert.Contains("'color'", error.Message);
    }

    [Fact]
    public void MissingRequiredIdIsInvalidArgument()
    {
        Assert.Equal("invalid_argument", Assert.Throws<ApiException>(() => Parse("{}").ThingId("reference_id")).Code);
    }

    [Fact]
    public void IntegerOutsideItsRangeIsRefused()
    {
        Assert.Throws<ApiException>(() => Parse("{\"limit\": 1001}").OptionalInt("limit", 1, 1000));
        Assert.Equal(1000, Parse("{\"limit\": 1000}").OptionalInt("limit", 1, 1000));
    }

    [Fact]
    public void RejectNamesTheArgumentFromAnotherForm()
    {
        Args args = Parse("{\"kind\": \"pipe\"}");
        ApiException error = Assert.Throws<ApiException>(() => args.Reject("reference_id", "kind"));
        Assert.Contains("'kind'", error.Message);
    }

    [Fact]
    public void ANestedObjectsErrorsNameItsLine()
    {
        Args line = new Args(JObject.Parse("{\"name\": \"a\", \"quantity\": 0}"), "items[7]");
        ApiException error = Assert.Throws<ApiException>(() => line.OptionalInt("quantity", 1, int.MaxValue));
        Assert.Equal("Argument 'items[7].quantity' must be an integer from 1 to 2147483647.", error.Message);
        Assert.Contains("'items[7].name'", Assert.Throws<ApiException>(() => line.OptionalBool("name")).Message);
    }
}

public sealed class TextTests
{
    [Theory]
    [InlineData("Gas Oxygen Equal 100%%", "Gas Oxygen Equal 100%")]
    [InlineData("Gas Oxygen Equal 95%% 10mol", "Gas Oxygen Equal 95% 10mol")]
    [InlineData("<color=red>Quantity 50</color>", "Quantity 50")]
    public void ATraderConditionShowsOnePercentSign(string debugName, string shown)
    {
        Assert.Equal(shown, Text.Condition(debugName));
    }
}

public sealed class PagingTests
{
    private static readonly List<int> Ten = new List<int> { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9 };

    private static PageRequest Page(int offset, int limit) =>
        PageRequest.From(new Args(JObject.Parse($"{{\"offset\": {offset}, \"limit\": {limit}}}")), 200, 1000);

    [Fact]
    public void TakesThePageAndReportsTheRest()
    {
        Slice<int> slice = Slice<int>.Of(Ten, Page(2, 3));
        Assert.Equal(new List<int> { 2, 3, 4 }, slice.Items);
        Assert.Equal(10, slice.Total);
        Assert.True(slice.HasMore);
    }

    [Fact]
    public void OffsetPastTheEndIsEmpty()
    {
        Slice<int> slice = Slice<int>.Of(Ten, Page(50, 3));
        Assert.Empty(slice.Items);
        Assert.False(slice.HasMore);
    }

    [Fact]
    public void DefaultsApplyWhenAbsent()
    {
        PageRequest page = PageRequest.From(new Args(new JObject()), 200, 1000);
        Assert.Equal(0, page.Offset);
        Assert.Equal(200, page.Limit);
    }
}

public sealed class BatchTests
{
    private sealed class SampleView : BatchItemView
    {
        internal SampleView(int index, string name) : base(index, ok: true)
        {
            Name = name;
        }

        public string Name { get; }
    }

    [Fact]
    public void ItemsPutIndexAndOkFirstAsTheOldJObjectPatchingDid()
    {
        BatchBuilder batch = new BatchBuilder(2);
        batch.Succeeded(new SampleView(0, "a"));
        batch.Failed(1, ApiErrors.ThingNotFound(new ThingId(7)));
        var old = new
        {
            results = new object[]
            {
                new { index = 0, ok = true, name = "a" },
                new
                {
                    index = 1,
                    ok = false,
                    error = new { code = "thing_not_found", message = "No thing with reference id 7." }
                }
            },
            count = 2,
            success_count = 1,
            error_count = 1
        };
        WireCheck.Same(old, batch.Build());
    }
}

public sealed class ViewTests
{
    [Fact]
    public void CommonViewsMatchTheOldShapes()
    {
        WireCheck.Same(
            new { reference_id = "12", prefab_name = "StructureFrame", display_name = "Iron Frame" },
            new ThingView(new ThingId(12), "StructureFrame", "Iron Frame"));
        // The old StationApi.PositionJson: Math.Round(component, 1) of the float position.
        float x = 1.24f, y = -3.01f, z = 0.05f;
        WireCheck.Same(
            new { x = System.Math.Round(x, 1), y = System.Math.Round(y, 1), z = System.Math.Round(z, 1) },
            new PositionView(x, y, z));
        WireCheck.Same(new { index = (int?)null, name = (string?)null }, new ColorView(null, null));
    }
}
