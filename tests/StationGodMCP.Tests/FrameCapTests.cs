#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>[Rig] MaxFps and -sgfps: no cap unless the value is above 0; the argument overrides the key.</summary>
public sealed class FrameCapTests
{
    [Fact]
    public void ZeroOrMissingIsNoCap()
    {
        Assert.Null(FrameCap.Choose(FrameCap.DefaultMaxFps, null));
        Assert.Null(FrameCap.Choose(0, new[] { "-load", "x" }));
    }

    [Fact]
    public void NegativeIsNoCap() => Assert.Null(FrameCap.Choose(-5, null));

    [Fact]
    public void APositiveKeyIsTheCap() => Assert.Equal(30, FrameCap.Choose(30, new string[0]));

    [Fact]
    public void TheArgumentOverridesTheKey()
    {
        Assert.Equal(15, FrameCap.Choose(30, new[] { "-SGFPS", "15" }));
        Assert.Equal(20, FrameCap.Choose(0, new[] { "-sgfps", "20" }));
        Assert.Null(FrameCap.Choose(30, new[] { "-sgfps", "0" }));
    }

    [Fact]
    public void AnUnreadableArgumentLeavesTheKey()
    {
        Assert.Equal(30, FrameCap.Choose(30, new[] { "-sgfps", "fast" }));
        Assert.Equal(30, FrameCap.Choose(30, new[] { "-sgfps" }));
    }
}
