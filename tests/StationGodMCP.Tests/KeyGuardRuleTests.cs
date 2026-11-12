#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>The promise: any text focus blocks every StationGod key; our own menu's Escape still closes it.</summary>
public class KeyGuardRuleTests
{
    public static IEnumerable<object[]> EachFact() => new[]
    {
        new object[] { "GameTyping", new KeyFacts(true, false, false, false, false) },
        new object[] { "ConsoleOpen", new KeyFacts(false, true, false, false, false) },
        new object[] { "InputWindowOpen", new KeyFacts(false, false, true, false, false) },
        new object[] { "UiFieldFocused", new KeyFacts(false, false, false, true, false) },
        new object[] { "OwnSearchFocused", new KeyFacts(false, false, false, false, true) },
    };

    [Theory]
    [MemberData(nameof(EachFact))]
    public void AnyOneFactBlocksKeys(string name, KeyFacts facts)
    {
        Assert.False(KeyGuardRule.KeysAllowed(facts), name);
    }

    [Fact]
    public void NoFactAllowsKeys() => Assert.True(KeyGuardRule.KeysAllowed(default));

    [Fact]
    public void EscapeClosesOurMenuWithOnlyOurOwnTypingState()
    {
        KeyFacts ours = new KeyFacts(GameTyping: true, false, false, false, OwnSearchFocused: true);
        Assert.True(KeyGuardRule.MenuEscapeAllowed(true, ours));
        Assert.True(KeyGuardRule.MenuEscapeAllowed(true, default));
    }

    [Theory]
    [InlineData("ConsoleOpen")]
    [InlineData("InputWindowOpen")]
    [InlineData("UiFieldFocused")]
    public void EscapeLeavesOurMenuAloneWhenAnotherWindowTypes(string which)
    {
        KeyFacts facts = which switch
        {
            "ConsoleOpen" => new KeyFacts(true, true, false, false, false),
            "InputWindowOpen" => new KeyFacts(true, false, true, false, false),
            _ => new KeyFacts(true, false, false, true, false),
        };
        Assert.False(KeyGuardRule.MenuEscapeAllowed(true, facts));
    }

    [Fact]
    public void EscapeDoesNothingWhenTheMenuIsClosed() =>
        Assert.False(KeyGuardRule.MenuEscapeAllowed(false, default));
}
