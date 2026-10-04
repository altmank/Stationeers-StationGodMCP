#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>thing_health damage_record: the game's flags as the causes its tooltip names, in its order.</summary>
public sealed class PipeDamageRecordTests
{
    [Theory]
    [InlineData(0, new string[0])]
    [InlineData(1, new[] { "pressure" })]
    [InlineData(2, new[] { "liquid" })]
    [InlineData(4, new[] { "solid" })]
    [InlineData(6, new[] { "liquid", "solid" })]
    [InlineData(7, new[] { "pressure", "liquid", "solid" })]
    public void FlagsBecomeCauses(byte record, string[] causes)
    {
        Assert.Equal(causes, PipeDamageRecord.Causes(record));
    }
}
