using System.Text;

namespace StationGodMCP.Client;

/// <summary>
/// The protocol's framing over one stream (protocol.md, Framing): a message is one line of UTF-8 JSON ended by a line
/// feed, a carriage return before it is dropped, blank lines are skipped. Reading and writing may run at the same time;
/// writes are serialised so two lines never interleave.
/// </summary>
internal sealed class LineChannel(Stream stream) : IAsyncDisposable
{
    private const int InitialBuffer = 64 * 1024;

    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private byte[] _buffer = new byte[InitialBuffer];
    private int _start;
    private int _scanned;
    private int _end;

    /// <summary>The next non-blank line without its ending, or null when the stream ended.</summary>
    internal async Task<byte[]?> ReadLineAsync(CancellationToken cancellation)
    {
        while (true)
        {
            int newline = Array.IndexOf(_buffer, (byte)'\n', _scanned, _end - _scanned);
            if (newline >= 0)
            {
                int length = newline - _start;
                if (length > 0 && _buffer[newline - 1] == (byte)'\r')
                {
                    length--;
                }

                byte[] line = _buffer.AsSpan(_start, length).ToArray();
                _start = _scanned = newline + 1;
                if (!IsBlank(line))
                {
                    return line;
                }

                continue;
            }

            _scanned = _end;
            MakeRoom();
            int read = await stream.ReadAsync(_buffer.AsMemory(_end), cancellation).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            _end += read;
        }
    }

    /// <summary>Writes one message and its line feed.</summary>
    internal async Task WriteLineAsync(string json, CancellationToken cancellation)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(json + "\n");
        await _writeLock.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(bytes, cancellation).ConfigureAwait(false);
            await stream.FlushAsync(cancellation).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync() => stream.DisposeAsync();

    private void MakeRoom()
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _scanned -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
        {
            Array.Resize(ref _buffer, _buffer.Length * 2);
        }
    }

    private static bool IsBlank(byte[] line)
    {
        foreach (byte character in line)
        {
            if (character is not ((byte)' ' or (byte)'\t' or (byte)'\r'))
            {
                return false;
            }
        }

        return true;
    }
}
