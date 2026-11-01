#nullable enable

using System;
using System.Reflection;
using StationGodMCP.LatticeLink.Model;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// In a game without the optional Lattice mod, StationGod 2.16.0 answered no call: the load's compile warm-up, run
/// before the request dispatcher each frame, asked Assembly.GetTypes(), which threw ReflectionTypeLoadException every
/// frame; once that was caught, reading the members of a LatticeLink type crashed the game's Mono natively (rig sgt5).
/// LoadableTypes keeps the types that loaded and, without Lattice, leaves out every type that may name Lattice's.
/// </summary>
public sealed class LoadableTypesTests
{
    [Fact]
    public void ReturnsEveryTypeWhenAllLoad()
    {
        Type[] types = LoadableTypes.Of(() => new[] { typeof(int), typeof(string) });

        Assert.Equal(new[] { typeof(int), typeof(string) }, types);
    }

    [Fact]
    public void KeepsTheLoadedTypesWhenSomeCannotLoad()
    {
        Type?[] partial = { typeof(int), null, typeof(string), null };
        Exception?[] errors = { new System.IO.FileNotFoundException("the optional assembly") };

        Type[] types = LoadableTypes.Of(() => throw new ReflectionTypeLoadException(partial!, errors!));

        Assert.Equal(new[] { typeof(int), typeof(string) }, types);
    }

    [Fact]
    public void LeavesOtherFailuresToTheCaller()
    {
        Assert.Throws<InvalidOperationException>(() => LoadableTypes.Of(() => throw new InvalidOperationException()));
    }

    [Fact]
    public void WithoutLatticeLeavesOutLatticeLinkTypesAndTheirNestedOnes()
    {
        Type nested = typeof(Outer.Inner);
        Type[] all = { typeof(int), typeof(LatticeNames), typeof(LoadableTypes), nested };

        Assert.Equal(new[] { typeof(int), typeof(LoadableTypes), nested },
            LoadableTypes.Readable(() => all, latticeLoaded: false));
        Assert.Equal(all, LoadableTypes.Readable(() => all, latticeLoaded: true));
    }

    [Fact]
    public void WithoutLatticeAPartialLoadIsFilteredToo()
    {
        Type?[] partial = { typeof(LatticeNames), null, typeof(LoadableTypes) };

        Type[] types = LoadableTypes.Readable(() => throw new ReflectionTypeLoadException(partial!, new Exception?[0]!),
            latticeLoaded: false);

        Assert.Equal(new[] { typeof(LoadableTypes) }, types);
    }

    [Theory]
    [InlineData("StationGodMCP.LatticeLink", true)]
    [InlineData("StationGodMCP.LatticeLink.Json", true)]
    [InlineData("StationGodMCP.LatticeLink.Model", true)]
    [InlineData("StationGodMCP.LatticeLinkage", false)]
    [InlineData("StationGodMCP.Verbs", false)]
    [InlineData(null, false)]
    public void TheLatticeNamespaceIsLatticeLinkAndBelow(string? ns, bool inside)
    {
        Assert.Equal(inside, LoadableTypes.InLatticeNamespace(ns));
    }

    [Fact]
    public void ANestedTypeCountsByItsOutermostType()
    {
        Assert.False(LoadableTypes.NamesLattice(typeof(Outer.Inner)));
        Assert.True(LoadableTypes.NamesLattice(typeof(LatticeNames)));
    }

    [Fact]
    public void ReadsARealAssembly()
    {
        Assert.Contains(typeof(LoadableTypes), LoadableTypes.Readable(typeof(LoadableTypes).Assembly, latticeLoaded: false));
    }

    private static class Outer
    {
        internal static class Inner
        {
        }
    }
}
