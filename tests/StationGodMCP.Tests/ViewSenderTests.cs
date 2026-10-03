#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure.RemoteView;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Whose view a message carries: the client's registered human while it is still the client's, else the human in play
/// the client owns, so a player who rejoined an existing character (no registered human) keeps their view.
/// </summary>
public sealed class ViewSenderTests
{
    private const ulong Player = 76561198000000001UL;
    private const ulong Other = 76561198000000002UL;

    private sealed class Body
    {
    }

    private static ClientHuman<Body> Human(ulong owner, bool inPlay = true) =>
        new ClientHuman<Body>(new Body(), owner, inPlay);

    [Fact]
    public void TheRegisteredHumanCountsWhileTheClientOwnsIt()
    {
        ClientHuman<Body> registered = Human(Player);

        Assert.Same(registered.Body, ViewSender.HumanOf(Player, registered, new[] { Human(Player) }));
    }

    [Fact]
    public void ARejoinedPlayerWithoutARegisteredHumanIsFoundByOwner()
    {
        ClientHuman<Body> own = Human(Player);

        Assert.Same(own.Body, ViewSender.HumanOf(Player, null, new[] { Human(Other), own }));
    }

    [Fact]
    public void ARegisteredHumanTheClientNoLongerOwnsIsPassedOver()
    {
        ClientHuman<Body> own = Human(Player);

        Assert.Same(own.Body, ViewSender.HumanOf(Player, Human(Other), new[] { own }));
    }

    [Fact]
    public void AnOwnedHumanOutOfPlayIsNotTheSender()
    {
        Assert.Null(ViewSender.HumanOf(Player, null, new[] { Human(Player, inPlay: false), Human(Other) }));
    }

    [Fact]
    public void AClientWithoutAnIdHasNoHuman()
    {
        Assert.Null(ViewSender.HumanOf(0UL, Human(0UL), new[] { Human(0UL) }));
    }

    [Fact]
    public void TheWorldIsNotReadWhenTheRegisteredHumanAnswers()
    {
        Assert.NotNull(ViewSender.HumanOf(Player, Human(Player), Unreadable()));
    }

    private static IEnumerable<ClientHuman<Body>> Unreadable()
    {
        Assert.Fail("The humans in the world were read.");
        yield break;
    }
}
