#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>from_id as one thing or a list: parsed in order without repeats, each material paid source by source.</summary>
public sealed class PaySourcesTests
{
    [Fact]
    public void EachSourceGivesWhatItHoldsInOrder()
    {
        Assert.Equal(new[] { 3, 4, 0 }, SourceSplit.Of(new[] { 3, 10, 5 }, 7));
        Assert.Equal(new[] { 3, 10, 5 }, SourceSplit.Of(new[] { 3, 10, 5 }, 40));
        Assert.Equal(new[] { 0, 0 }, SourceSplit.Of(new[] { 3, 10 }, 0));
    }

    [Fact]
    public void HoldersReadAsAList()
    {
        Assert.Equal("Rocket Parts", SourceSplit.Joined(new[] { "Rocket Parts" }));
        Assert.Equal("Rocket Parts and Materials", SourceSplit.Joined(new[] { "Rocket Parts", "Materials" }));
        Assert.Equal("A, B and C", SourceSplit.Joined(new[] { "A", "B", "C" }));
    }

    [Fact]
    public void FromIdIsOneThingOrAListInOrder()
    {
        Assert.Empty(SourceArgs.Of(new Args(new JObject())));
        Assert.Equal(new[] { new ThingId(5) }, SourceArgs.Of(new Args(new JObject { ["from_id"] = "5" })));

        List<ThingId> listed = SourceArgs.Of(new Args(new JObject { ["from_id"] = new JArray("7", "5", "7") }));
        Assert.Equal(new[] { new ThingId(7), new ThingId(5) }, listed);
        Assert.Equal(new ThingId(7), SourceArgs.First(listed));
        Assert.Equal(new[] { new ThingId(5) }, SourceArgs.Rest(listed));
    }

    [Fact]
    public void AnEmptyOrTooLongListIsRefused()
    {
        Assert.Throws<ApiException>(() => SourceArgs.Of(new Args(new JObject { ["from_id"] = new JArray() })));
        Assert.Throws<ApiException>(() => SourceArgs.Of(new Args(new JObject
        {
            ["from_id"] = new JArray("1", "2", "3", "4", "5", "6", "7", "8", "9")
        })));
    }
}
