#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Each material paid source by source, and the holders named in order.</summary>
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
}
