#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>The promise: any text focus blocks every StationGod key; our own menu's Escape still closes it.</summary>
public class KeyGuardRuleTests
{
    [Theory]
    [InlineData("GameTyping")]
    [InlineData("ConsoleOpen")]
    [InlineData("InputWindowOpen")]
    [InlineData("UiFieldFocused")]
    [InlineData("OwnSearchFocused")]
    public void AnyOneFactBlocksKeys(string name)
    {
        KeyFacts facts = name switch
        {
            "GameTyping" => new KeyFacts(true, false, false, false, false),
            "ConsoleOpen" => new KeyFacts(false, true, false, false, false),
            "InputWindowOpen" => new KeyFacts(false, false, true, false, false),
            "UiFieldFocused" => new KeyFacts(false, false, false, true, false),
            _ => new KeyFacts(false, false, false, false, true),
        };
        Assert.False(KeyGuardRule.KeysAllowed(facts), name);
    }

    [Fact]
    public void NoFactAllowsKeys() => Assert.True(KeyGuardRule.KeysAllowed(default));

    [Fact]
    public void OurOpenMenuAloneDoesNotBlockKeys() =>
        Assert.True(KeyGuardRule.KeysAllowed(new KeyFacts(true, false, false, false, false, OwnMenuOpen: true)));

    [Fact]
    public void OurOpenMenuWithItsSearchFocusedBlocksKeys() =>
        Assert.False(KeyGuardRule.KeysAllowed(new KeyFacts(true, false, false, false, true, OwnMenuOpen: true)));

    [Fact]
    public void OurOpenMenuWithTheConsoleOpenBlocksKeys() =>
        Assert.False(KeyGuardRule.KeysAllowed(new KeyFacts(true, true, false, false, false, OwnMenuOpen: true)));

    [Fact]
    public void OurOpenMenuWithAFocusedGameFieldBlocksKeys() =>
        Assert.False(KeyGuardRule.KeysAllowed(new KeyFacts(true, false, false, true, false, OwnMenuOpen: true)));

    [Fact]
    public void TypingWithOurMenuClosedBlocksKeys() =>
        Assert.False(KeyGuardRule.KeysAllowed(new KeyFacts(true, false, false, false, false, OwnMenuOpen: false)));

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
