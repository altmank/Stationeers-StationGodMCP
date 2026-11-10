#nullable enable

using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

public sealed class OnceSetTests
{
    [Fact]
    public void ASameKeyLogsOnce()
    {
        OnceSet set = new OnceSet(4);
        Assert.Equal(OnceSet.Verdict.Log, set.Offer("a"));
        Assert.Equal(OnceSet.Verdict.Repeat, set.Offer("a"));
    }

    [Fact]
    public void ThePastTheCapKeySaysSoOnceThenStaysQuiet()
    {
        OnceSet set = new OnceSet(256);
        for (int index = 0; index < 256; index++)
        {
            Assert.Equal(OnceSet.Verdict.Log, set.Offer("k" + index));
        }

        Assert.Equal(OnceSet.Verdict.CapReached, set.Offer("k256"));
        Assert.Equal(OnceSet.Verdict.Suppressed, set.Offer("k257"));
        Assert.Equal(OnceSet.Verdict.Repeat, set.Offer("k0"));
    }

    [Fact]
    public void ClearMakesTheSameKeyLogAgain()
    {
        OnceSet set = new OnceSet(1);
        set.Offer("a");
        Assert.Equal(OnceSet.Verdict.CapReached, set.Offer("b"));
        set.Clear();
        Assert.Equal(OnceSet.Verdict.Log, set.Offer("a"));
        Assert.Equal(OnceSet.Verdict.CapReached, set.Offer("b"));
    }
}
