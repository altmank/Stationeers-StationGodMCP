#nullable enable

using System;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Scheduling;
using Xunit;
using Connection = StationGodMCP.Pure.Scheduling.FrameScheduler<StationGodMCP.Tests.Scheduling.FakeCall>.Connection;

namespace StationGodMCP.Tests.Scheduling;

/// <summary>
/// A client with one call at a time beside a hub that keeps 16 calls in flight on each of three streams, refilled as
/// they run: the one-call client has its turn within a frame or two whenever it joins, in either lane, whether the hub
/// holds one connection with three streams or three connections under one name. Waits are counted in frames after the
/// one the call arrived in (0: it ran in the next frame).
/// </summary>
public sealed class FrameSchedulerFairnessTests
{
    private const int HubStreams = 3;
    private const int PerStream = 16;
    private const int Frames = 1500;

    [Theory]
    [InlineData(true, 0, 75)]
    [InlineData(true, 1, 75)]
    [InlineData(true, 2, 3)]
    [InlineData(true, 3, 1)]
    [InlineData(true, 4, 120)]
    [InlineData(false, 0, 75)]
    [InlineData(false, 5, 40)]
    public void AOneCallClientWaitsAtMostTwoFramesInTheLightLaneBesideABusyHub(bool oneConnection, int seed,
        int joinFrame)
    {
        // seed 0: every hub call costs 1 ms, so the 4 ms budget stops each frame after about four; otherwise 0.4-2 ms,
        // which moves inspect atmosphere between the lanes as its average crosses the threshold.
        Random? random = seed == 0 ? null : new Random(seed);
        Load load = new Load(oneConnection, random, SchedulerRig.Read("inspect.atmosphere"), lightFiller: false);

        int worst = load.WorstWait(SchedulerRig.Read("read.snapshot"), joinFrame);

        Assert.InRange(worst, 0, 2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AOneCallClientsHeavyCallRunsInTheNextRoundBesideAHubWithAHeavyBacklog(bool oneConnection)
    {
        Load load = new Load(oneConnection, new Random(7), SchedulerRig.Heavy("inspect.health"), lightFiller: false);

        int worst = load.WorstWait(SchedulerRig.Heavy("deep_survey"), joinFrame: 60);

        Assert.InRange(worst, 0, 1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnderAlwaysBusyLightWorkAOneCallClientsHeavyCallWaitsOnlyTheHeavyBound(bool oneConnection)
    {
        Load load = new Load(oneConnection, new Random(8), SchedulerRig.Heavy("inspect.health"), lightFiller: true);

        int worst = load.WorstWait(SchedulerRig.Heavy("deep_survey"), joinFrame: 60);

        Assert.InRange(worst, 0, SchedulerSettings.DefaultHeavyMaxWaitFrames);
    }

    /// <summary>The hub's load, and optionally light work over the whole budget every frame from a client of its own.</summary>
    private sealed class Load
    {
        private readonly SchedulerRig _rig = new SchedulerRig(SchedulerRig.WithBudget(4.0));
        private readonly Connection[] _hub = new Connection[HubStreams];
        private readonly Random? _random;
        private readonly CallProfile _hubProfile;
        private readonly bool _oneConnection;
        private readonly Connection? _filler;

        internal Load(bool oneConnection, Random? random, CallProfile hubProfile, bool lightFiller)
        {
            _oneConnection = oneConnection;
            _random = random;
            _hubProfile = hubProfile;
            for (int index = 0; index < HubStreams; index++)
            {
                _hub[index] = oneConnection && index > 0 ? _hub[0] : _rig.Scheduler.Open("hub");
            }

            _filler = lightFiller ? _rig.Scheduler.Open("filler") : null;
            for (long stream = 1; stream <= HubStreams; stream++)
            {
                for (int index = 0; index < PerStream; index++)
                {
                    HubCall(stream);
                }
            }
        }

        /// <summary>
        /// Runs Frames frames; from joinFrame a new client keeps one call of the profile waiting, sending the next once
        /// the last ran. Returns the most frames one of its calls waited.
        /// </summary>
        internal int WorstWait(CallProfile profile, int joinFrame)
        {
            Connection? client = null;
            FakeCall? waiting = null;
            int worst = 0;
            for (int frame = 1; frame <= Frames; frame++)
            {
                if (frame == joinFrame)
                {
                    client = _rig.Scheduler.Open("dashboard");
                }

                if (client != null && waiting == null)
                {
                    waiting = new FakeCall("dashboard", 0.2) { ArrivedAfterFrame = _rig.Scheduler.Frame };
                    Assert.True(_rig.Scheduler.Enqueue(client, waiting, profile, double.PositiveInfinity).IsQueued);
                }

                if (_filler != null)
                {
                    _rig.Busy(_filler, frame, 5.0);
                }

                int ranBefore = _rig.Runner.Ran.Count;
                _rig.Frame();
                for (int index = ranBefore; index < _rig.Runner.Ran.Count; index++)
                {
                    FakeCall ran = _rig.Runner.Ran[index];
                    if (ran.Name == "hub")
                    {
                        HubCall(ran.Stream);
                    }
                }

                if (waiting != null)
                {
                    bool ran = waiting.RanInFrame >= 0;
                    long waited = (ran ? waiting.RanInFrame : _rig.Scheduler.Frame + 1) - waiting.ArrivedAfterFrame - 1;
                    worst = Math.Max(worst, (int)waited);
                    if (ran)
                    {
                        waiting = null;
                    }
                }
            }

            return worst;
        }

        private void HubCall(long stream)
        {
            double cost = _random == null ? 1.0 : 0.4 + 1.6 * _random.NextDouble();
            // One connection: streams 1-3. Three connections: each its own default stream.
            long on = _oneConnection ? stream : 0;
            FakeCall call = new FakeCall("hub", cost) { Stream = stream };
            Assert.True(_rig.Scheduler.Enqueue(_hub[stream - 1], on, call, _hubProfile, double.PositiveInfinity).IsQueued);
        }
    }
}
