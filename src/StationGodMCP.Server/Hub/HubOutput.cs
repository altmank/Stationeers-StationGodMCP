using System.Text;
using System.Threading.Channels;

namespace StationGodMCP.Server;

/// <summary>
/// A session's outgoing lines, written by one loop in the order they were queued: replies come from many calls at once
/// and events from the link's reader, and a queue keeps a subscription's events in order where a lock would not. A
/// line queued after Complete, or after the stream broke, is dropped: nobody is reading any more.
/// </summary>
internal sealed class HubOutput
{
    private static readonly byte[] NewLine = "\n"u8.ToArray();

    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true
    });
    private readonly Stream _stream;
    private readonly Task _writing;

    internal HubOutput(Stream stream)
    {
        _stream = stream;
        _writing = Task.Run(WriteAllAsync);
    }

    internal void Send(string line) => _lines.Writer.TryWrite(line);

    /// <summary>Lets the queued lines out, then stops.</summary>
    internal Task CompleteAsync()
    {
        _lines.Writer.TryComplete();
        return _writing;
    }

    private async Task WriteAllAsync()
    {
        ChannelReader<string> reader = _lines.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                // A burst goes out with one flush.
                while (reader.TryRead(out string? line))
                {
                    await _stream.WriteAsync(Encoding.UTF8.GetBytes(line)).ConfigureAwait(false);
                    await _stream.WriteAsync(NewLine).ConfigureAwait(false);
                }

                await _stream.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // The caller went away; what is left is dropped.
            _lines.Writer.TryComplete();
        }
    }
}
