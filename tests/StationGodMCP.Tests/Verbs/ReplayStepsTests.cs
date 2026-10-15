#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// A call a caller runs itself as a body of its own work (VerbOutcome.Replied; review D, R-D-1 and R-D-7): the call's
/// handler takes one step in the call (the frame cap is NegativeInfinity while it starts), each later step of its
/// pending work is one step of the body, so undo's replay spreads its planners over frames; the answer is the reply as
/// typed (a report, a started job), not a JObject; a refusal is thrown. The cap nests and puts back what it found.
/// undo's own check reads the first problem of a typed report.
/// </summary>
public sealed class ReplayStepsTests
{
    [Fact]
    public void APendingCallIsSteppedOneStepPerYieldOfTheBody()
    {
        List<double> ends = new List<double>();
        Answer<object> reply = new Answer<object>();
        int yields = 0;
        foreach (object? _ in VerbOutcome.Replied(() => Counting(5, ends), reply))
        {
            yields++;
            Assert.False(reply.IsSet);
        }

        // The handler took its first step under the cap and was pending; the next three each yield, the last ends.
        Assert.Equal(3, yields);
        Assert.Equal(double.NegativeInfinity, ends[0]);
        Assert.Equal("five steps", reply.Value);
    }

    [Fact]
    public void TheAnswerIsTheTypedReplyAndARefusalIsThrown()
    {
        ReportReply report = Report.Of(Array.Empty<ReportTarget>(), null,
            new[] { new ReportProblem(0, "12", new VerbError("would_burst", "a tank bursts")) });
        Answer<object> reply = new Answer<object>();
        foreach (object? _ in VerbOutcome.Replied(() => new VerbOutcome.Answered(report), reply))
        {
        }

        Assert.Same(report, reply.Value);
        Assert.Equal("would_burst", UndoReplies.Problem(reply.Value)!.Code);
        Assert.Null(UndoReplies.Problem(Report.Of(Array.Empty<ReportTarget>())));
        Assert.Null(UndoReplies.Problem(new { job_id = "place-4" }));

        ApiException refused = Assert.Throws<ApiException>(() =>
        {
            foreach (object? _ in VerbOutcome.Replied(
                         () => new VerbOutcome.Refused(new VerbError("undo_refused", "diverged")), new Answer<object>()))
            {
            }
        });
        Assert.Equal("undo_refused", refused.Code);
    }

    [Fact]
    public void TheFrameCapNestsAndPutsBackWhatItFound()
    {
        Assert.Equal(double.PositiveInfinity, CurrentFrame.EndMs);
        using (CurrentFrame.Capped(10.0))
        {
            Assert.Equal(10.0, CurrentFrame.EndMs);
            using (CurrentFrame.Capped(20.0))
            {
                Assert.Equal(10.0, CurrentFrame.EndMs);
            }

            using (CurrentFrame.Capped(5.0))
            {
                Assert.Equal(5.0, CurrentFrame.EndMs);
            }

            Assert.Equal(10.0, CurrentFrame.EndMs);
        }

        Assert.Equal(double.PositiveInfinity, CurrentFrame.EndMs);
    }

    // A handler whose work is five steps, answered through WorkResult as the planners answer: it notes the frame end it
    // started under.
    private static VerbOutcome Counting(int steps, List<double> ends)
    {
        ends.Add(CurrentFrame.EndMs);
        return WorkResult<string>.Of(answer => Body(steps, answer), value => value);
    }

    private static IEnumerable<object?> Body(int steps, Answer<string> answer)
    {
        for (int step = 1; step < steps; step++)
        {
            yield return null;
        }

        answer.Value = "five steps";
    }
}
