#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Subscriptions;

namespace StationGodMCP.Pure.Sampling;

/// <summary>One frame on the real clock: the mod's frame counter, real seconds (running while paused), and the UTC time.</summary>
internal readonly struct RealTimeTick
{
    internal RealTimeTick(long frame, double realTimeS, DateTimeOffset utcNow)
    {
        Frame = frame;
        RealTimeS = realTimeS;
        UtcNow = utcNow;
    }

    internal long Frame { get; }

    internal double RealTimeS { get; }

    internal DateTimeOffset UtcNow { get; }
}

/// <summary>One sample's read: the gateway the reads went through, or the error that failed the whole read.</summary>
internal readonly struct LogicSampleRead
{
    private LogicSampleRead(string? gatewayId, string? code, string? message)
    {
        GatewayId = gatewayId;
        Code = code;
        Message = message;
    }

    internal string? GatewayId { get; }

    /// <summary>The error code when the read failed as a whole (gateway_not_found, ...); null when it read.</summary>
    internal string? Code { get; }

    internal string? Message { get; }

    internal bool Ok => Code == null;

    internal static LogicSampleRead Read(string gatewayId) => new LogicSampleRead(gatewayId, null, null);

    internal static LogicSampleRead Failed(string code, string message) => new LogicSampleRead(null, code, message);
}

/// <summary>Reads every target once, in the calling frame, as read_logic_many reads them, one result per target in order.</summary>
internal interface ILogicSampleReader<TItem> where TItem : class
{
    LogicSampleRead Read(SampleLogicArguments arguments, List<TItem> into);
}

/// <summary>Told when a run has its outcome, so the call's reply can be sent. Main thread.</summary>
internal interface ISampleLogicEvents<TItem> where TItem : class
{
    void Finished(SampleLogicRun<TItem> run, SampleLogicOutcome<TItem> outcome);
}

/// <summary>A run's end: the reply, or the error a sample's read failed with (the call's error, as the sidecar's was).</summary>
internal abstract class SampleLogicOutcome<TItem> where TItem : class
{
    private SampleLogicOutcome()
    {
    }

    internal sealed class Completed : SampleLogicOutcome<TItem>
    {
        internal Completed(SampleLogicResult<TItem> result)
        {
            Result = result;
        }

        internal SampleLogicResult<TItem> Result { get; }
    }

    internal sealed class Failed : SampleLogicOutcome<TItem>
    {
        internal Failed(string code, string message)
        {
            Code = code;
            Message = message;
        }

        internal string Code { get; }

        internal string Message { get; }
    }
}

/// <summary>
/// sample_logic's reply, as the sidecar loop gave it: {gateway_id, started_at_utc, duration_seconds, interval_seconds,
/// sample_count, change_count, changes: [{elapsed_seconds, readings}]}.
/// </summary>
internal sealed class SampleLogicResult<TItem> where TItem : class
{
    internal SampleLogicResult(string? gatewayId, DateTimeOffset startedAtUtc, double durationSeconds,
        double intervalSeconds, int sampleCount, List<SampleLogicChange<TItem>> changes)
    {
        GatewayId = gatewayId;
        StartedAtUtc = startedAtUtc;
        DurationSeconds = durationSeconds;
        IntervalSeconds = intervalSeconds;
        SampleCount = sampleCount;
        Changes = changes;
    }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public string? GatewayId { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public double DurationSeconds { get; }

    public double IntervalSeconds { get; }

    public int SampleCount { get; }

    public int ChangeCount => Changes.Count;

    public List<SampleLogicChange<TItem>> Changes { get; }
}

/// <summary>One sample that changed something: when (0 for the first sample) and every reading that changed.</summary>
internal sealed class SampleLogicChange<TItem> where TItem : class
{
    internal SampleLogicChange(double elapsedSeconds, List<TItem> readings)
    {
        ElapsedSeconds = elapsedSeconds;
        Readings = readings;
    }

    public double ElapsedSeconds { get; }

    public List<TItem> Readings { get; }
}

/// <summary>One sample_logic call in progress: its schedule, the last reading of each target, and the changes so far.</summary>
internal sealed class SampleLogicRun<TItem> where TItem : class
{
    private readonly List<TItem> _previous;
    private readonly List<SampleLogicChange<TItem>> _changes = new List<SampleLogicChange<TItem>>();
    private string? _gatewayId;
    private double _startS;
    private DateTimeOffset _startedAtUtc;
    private int _sampleCount;

    internal SampleLogicRun(ConnectionId connection, SampleLogicArguments arguments, double startedS)
    {
        Connection = connection;
        Arguments = arguments;
        _gatewayId = arguments.GatewayId;
        _previous = new List<TItem>(arguments.Targets.Count);
        DueS = startedS;
        LastSampledFrame = long.MinValue;
    }

    internal ConnectionId Connection { get; }

    internal SampleLogicArguments Arguments { get; }

    /// <summary>Real time the next sample is due; the first is due when the call started.</summary>
    internal double DueS { get; private set; }

    internal long LastSampledFrame { get; private set; }

    internal int SampleCount => _sampleCount;

    /// <summary>One sample: null while the run goes on, its outcome once it has ended.</summary>
    internal SampleLogicOutcome<TItem>? Sample(RealTimeTick tick, ILogicSampleReader<TItem> reader,
        IReadingComparer<TItem> comparer, List<TItem> buffer)
    {
        LastSampledFrame = tick.Frame;
        buffer.Clear();
        LogicSampleRead read;
        try
        {
            read = reader.Read(Arguments, buffer);
        }
        catch (Exception failure)
        {
            // The game tick's boundary: a read that throws fails this call, never the frame.
            return new SampleLogicOutcome<TItem>.Failed("internal_error", failure.Message);
        }

        if (!read.Ok)
        {
            return new SampleLogicOutcome<TItem>.Failed(read.Code!, read.Message ?? read.Code!);
        }

        bool first = _sampleCount == 0;
        if (first)
        {
            _startS = tick.RealTimeS;
            _startedAtUtc = tick.UtcNow;
        }

        double elapsed = first ? 0.0 : tick.RealTimeS - _startS;
        _gatewayId = read.GatewayId ?? _gatewayId;
        List<TItem>? changed = null;
        for (int index = 0; index < buffer.Count; index++)
        {
            TItem reading = buffer[index];
            if (index < _previous.Count && comparer.SameValues(_previous[index], reading))
            {
                continue;
            }

            changed ??= new List<TItem>();
            changed.Add(reading);
            if (index < _previous.Count)
            {
                _previous[index] = reading;
            }
            else
            {
                _previous.Add(reading);
            }
        }

        if (changed != null)
        {
            _changes.Add(new SampleLogicChange<TItem>(first ? 0.0 : SampleSchedule.Round(elapsed), changed));
        }

        _sampleCount++;
        if (SampleSchedule.Ends(elapsed, Arguments.DurationSeconds))
        {
            return new SampleLogicOutcome<TItem>.Completed(new SampleLogicResult<TItem>(_gatewayId, _startedAtUtc,
                SampleSchedule.Round(elapsed), Arguments.IntervalSeconds, _sampleCount, _changes));
        }

        DueS = _startS + SampleSchedule.NextDue(elapsed, Arguments.IntervalSeconds, Arguments.DurationSeconds);
        return null;
    }
}

/// <summary>
/// sample_logic in the mod: every call's run, sampled on the real clock in the subscription lane, so a paused game
/// keeps sampling. Each frame the due runs are sampled, the most overdue first, at most once per run, while the lane's
/// budget allows; a run whose last sample is taken hands its outcome to the events. Pure: the reads, the comparison
/// and the clock come in from outside.
/// </summary>
internal sealed class LogicSampler<TItem> where TItem : class
{
    private readonly ILogicSampleReader<TItem> _reader;
    private readonly IReadingComparer<TItem> _comparer;
    private readonly List<SampleLogicRun<TItem>> _runs = new List<SampleLogicRun<TItem>>();
    private readonly List<TItem> _buffer = new List<TItem>(SampleLogicArguments.MaximumTargets);

    internal LogicSampler(ILogicSampleReader<TItem> reader, IReadingComparer<TItem> comparer)
    {
        _reader = reader;
        _comparer = comparer;
    }

    internal int Active => _runs.Count;

    /// <summary>A new run, started at real time nowS; its first sample is due then (the next Poll takes it).</summary>
    internal SampleLogicRun<TItem> Start(ConnectionId connection, SampleLogicArguments arguments, double nowS)
    {
        SampleLogicRun<TItem> run = new SampleLogicRun<TItem>(connection, arguments, nowS);
        _runs.Add(run);
        return run;
    }

    /// <summary>Drops a run without an outcome (its call was cancelled); false when it is not running.</summary>
    internal bool Cancel(SampleLogicRun<TItem> run) => _runs.Remove(run);

    /// <summary>The connection closed: its runs are dropped.</summary>
    internal void CancelConnection(ConnectionId connection)
    {
        for (int index = _runs.Count - 1; index >= 0; index--)
        {
            if (_runs[index].Connection.Equals(connection))
            {
                _runs.RemoveAt(index);
            }
        }
    }

    /// <summary>One frame: due samples, most overdue first, while the budget allows. Returns the samples taken.</summary>
    internal int Poll(RealTimeTick tick, ISamplingBudget budget, ISampleLogicEvents<TItem> events)
    {
        int taken = 0;
        while (MostOverdue(tick) is SampleLogicRun<TItem> run && budget.MayTakeAnother())
        {
            taken++;
            if (run.Sample(tick, _reader, _comparer, _buffer) is SampleLogicOutcome<TItem> outcome)
            {
                _runs.Remove(run);
                events.Finished(run, outcome);
            }
        }

        return taken;
    }

    private SampleLogicRun<TItem>? MostOverdue(RealTimeTick tick)
    {
        SampleLogicRun<TItem>? most = null;
        foreach (SampleLogicRun<TItem> run in _runs)
        {
            if (run.LastSampledFrame != tick.Frame && SampleSchedule.IsDue(run.DueS, tick.RealTimeS) &&
                (most == null || run.DueS < most.DueS))
            {
                most = run;
            }
        }

        return most;
    }
}

/// <summary>
/// Compares two of read_logic_many's results as the sidecar loop compared their JSON text: index, ok, the device and
/// logic type, the value (equal doubles write equal text, but 0 and -0 do not) or the error's code and message.
/// </summary>
internal sealed class LogicResultComparer : IReadingComparer<BatchItemView>
{
    internal static readonly LogicResultComparer Instance = new LogicResultComparer();

    private LogicResultComparer()
    {
    }

    public bool SameValues(BatchItemView previous, BatchItemView next)
    {
        if (previous.Index != next.Index || previous.Ok != next.Ok)
        {
            return false;
        }

        return (previous, next) switch
        {
            (LogicReadItemView a, LogicReadItemView b) =>
                a.ReferenceId.Equals(b.ReferenceId) && SameType(a.LogicType, b.LogicType) &&
                SameAsWritten(a.Value, b.Value),
            (LogicFailedItemView a, LogicFailedItemView b) =>
                a.ReferenceId.HasValue == b.ReferenceId.HasValue &&
                (!a.ReferenceId.HasValue || a.ReferenceId.GetValueOrDefault().Equals(b.ReferenceId.GetValueOrDefault())) &&
                SameType(a.LogicType, b.LogicType) && SameError(a.Error, b.Error),
            (BatchErrorView a, BatchErrorView b) => SameError(a.Error, b.Error),
            _ => false,
        };
    }

    private static bool SameType(LogicTypeView? a, LogicTypeView? b) =>
        a == null || b == null
            ? a == b
            : a.Id == b.Id && string.Equals(a.Name, b.Name, StringComparison.Ordinal);

    private static bool SameError(ErrorView a, ErrorView b) =>
        string.Equals(a.Code, b.Code, StringComparison.Ordinal) &&
        string.Equals(a.Message, b.Message, StringComparison.Ordinal);

    internal static bool SameAsWritten(double a, double b) =>
        a.Equals(b) && (a != 0.0 || BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b));
}
