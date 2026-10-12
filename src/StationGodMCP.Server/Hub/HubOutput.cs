using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace StationGodMCP.Server;

/// <summary>
/// A session's outgoing lines, written by one loop in the order they were queued: replies come from many calls at once
/// and events from the link's reader, and a queue keeps a subscription's events in order where a lock would not.
///
/// The mod's slow-client rule holds here too (protocol.md, Outbound queue and slow clients): when the oldest line still
/// queued has waited longer than slowAfter, the caller is not reading, so the queue is dropped and the output reports
/// itself lost (SlowClient) instead of growing without end. A write that fails reports OutputBroken. Lines queued after
/// either, or after Complete, are dropped: nobody is reading any more.
/// </summary>
internal sealed class HubOutput
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly Channel<Queued> _lines = Channel.CreateUnbounded<Queued>(new UnboundedChannelOptions
    {
        SingleReader = true
    });
    private readonly Stream _stream;
    private readonly long _slowAfterTicks;
    private readonly Action<HubSessionEnd> _lost;
    private readonly Timer _watch;
    private readonly Task _writing;
    private int _ended;

    internal HubOutput(Stream stream, TimeSpan slowAfter, Action<HubSessionEnd> lost)
    {
        _stream = stream;
        _slowAfterTicks = (long)(slowAfter.TotalSeconds * Stopwatch.Frequency);
        _lost = lost;
        TimeSpan every = slowAfter / 4 < TimeSpan.FromSeconds(1) ? slowAfter / 4 : TimeSpan.FromSeconds(1);
        _watch = new Timer(_ => Watch(), null, every, every);
        _writing = Task.Run(WriteAllAsync);
    }

    internal void Send(string line) => _lines.Writer.TryWrite(new Queued(line, Stopwatch.GetTimestamp()));

    /// <summary>
    /// Lets the queued lines out for at most wait, then stops; a writer stuck on a peer that does not read is left
    /// behind with its stream.
    /// </summary>
    internal async Task CompleteAsync(TimeSpan wait)
    {
        _lines.Writer.TryComplete();
        await Task.WhenAny(_writing, Task.Delay(wait)).ConfigureAwait(false);
        await _watch.DisposeAsync().ConfigureAwait(false);
    }

    private void Watch()
    {
        if (_lines.Reader.TryPeek(out Queued oldest) && Stopwatch.GetTimestamp() - oldest.QueuedAt > _slowAfterTicks)
        {
            Lose(HubSessionEnd.SlowClient);
        }
    }

    private void Lose(HubSessionEnd why)
    {
        if (Interlocked.Exchange(ref _ended, 1) != 0)
        {
            return;
        }

        _lines.Writer.TryComplete();
        while (_lines.Reader.TryRead(out _))
        {
            // Dropped: nobody reads them.
        }

        _watch.Change(Timeout.Infinite, Timeout.Infinite);
        _lost(why);
    }

    private async Task WriteAllAsync()
    {
        ChannelReader<Queued> reader = _lines.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                // A burst goes out with one flush.
                while (Volatile.Read(ref _ended) == 0 && reader.TryRead(out Queued queued))
                {
                    await _stream.WriteAsync(Encoding.UTF8.GetBytes(queued.Line)).ConfigureAwait(false);
                    await _stream.WriteAsync(NewLine).ConfigureAwait(false);
                }

                await _stream.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // Any fault of the stream (a broken pipe, a stream that throws) ends the output, never the hub.
            Lose(HubSessionEnd.OutputBroken);
        }
    }

    /// <summary>A line and when it was queued (Stopwatch ticks).</summary>
    private readonly struct Queued(string line, long queuedAt)
    {
        internal string Line { get; } = line;

        internal long QueuedAt { get; } = queuedAt;
    }
}
