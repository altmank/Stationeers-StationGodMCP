#nullable enable

using System;
using StationGodMCP.Pure.Scheduling;
using Xunit;

namespace StationGodMCP.Tests.Scheduling;

/// <summary>The frame's shares and the settings' limits (scheduling.md, Settings).</summary>
public sealed class SchedulerSettingsTests
{
    [Theory]
    [InlineData(4.0, 1.5, false, 4.0, 1.5)]
    [InlineData(4.0, 1.5, true, 2.0, 1.0)]
    [InlineData(2.0, 1.5, false, 2.0, 1.0)]
    [InlineData(0.0, 1.5, true, 2.0, 1.0)]
    [InlineData(1.0, 1.5, true, 1.0, 0.5)]
    [InlineData(4.0, 0.0, false, 4.0, 0.0)]
    public void TheSubscriptionShareIsCappedAtHalfTheFramesBudget(double request, double subscription, bool job,
        double budget, double share)
    {
        FrameShares shares = FrameShares.For(request, subscription, job);

        Assert.Equal(budget, shares.BudgetMs);
        Assert.Equal(share, shares.SubscriptionMs);
    }

    [Fact]
    public void AZeroRequestBudgetIsUnlimitedAndLeavesTheSubscriptionShareAsSet()
    {
        FrameShares shares = FrameShares.For(0.0, 1.5, false);

        Assert.True(double.IsPositiveInfinity(shares.BudgetMs));
        Assert.Equal(1.5, shares.SubscriptionMs);
    }

    [Fact]
    public void TheDefaultsAreTheSpecs()
    {
        SchedulerSettings settings = SchedulerSettings.Default;

        Assert.Equal(4.0, settings.RequestBudgetMs);
        Assert.Equal(1.5, settings.SubscriptionBudgetMs);
        Assert.Equal(1.0, settings.HeavyThresholdMs);
        Assert.Equal(10, settings.HeavyMaxWaitFrames);
        Assert.Equal(16, settings.MaxInFlight);
    }

    [Fact]
    public void SubscriptionAdmissionAllowsHalfTheLaneAtThirtyFramesASecond()
    {
        Assert.Equal(22.5, SchedulerSettings.Default.SubscriptionAdmissionCapMsPerSecond, 9);
    }

    [Theory]
    [InlineData(-1.0, 1.5, 1.0, 10, 16)]
    [InlineData(double.NaN, 1.5, 1.0, 10, 16)]
    [InlineData(4.0, double.PositiveInfinity, 1.0, 10, 16)]
    [InlineData(4.0, 1.5, -0.5, 10, 16)]
    [InlineData(4.0, 1.5, 1.0, -1, 16)]
    [InlineData(4.0, 1.5, 1.0, 10, 0)]
    public void ValuesOutOfRangeAreRefused(double request, double subscription, double threshold, int wait, int inFlight)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new SchedulerSettings(request, subscription, threshold, wait, inFlight));
    }
}
