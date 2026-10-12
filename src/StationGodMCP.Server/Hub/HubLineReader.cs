using System.Text;

namespace StationGodMCP.Server;

/// <summary>
/// Reads a caller's lines with a bound (protocol.md, Framing): a line longer than Limit bytes is not kept whole but
/// answered TooLong as soon as it passes the bound, so one caller cannot fill the hub's memory. A carriage return
/// before the line feed is dropped; a last line without a line feed still counts.
/// </summary>
internal sealed class HubLineReader
{
    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[8192];
    private readonly MemoryStream _partial = new();
    private int _start;
    private int _end;

    internal HubLineReader(Stream stream, int limit)
    {
        _stream = stream;
        Limit = limit;
    }

    /// <summary>The longest line taken, in bytes, line feed not counted.</summary>
    internal int Limit { get; set; }

    internal async Task<LineRead> ReadAsync(CancellationToken cancellation)
    {
        while (true)
        {
            int newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                int length = newline - _start;
                if (_partial.Length + length > Limit)
                {
                    return TooLong();
                }

                _partial.Write(_buffer, _start, length);
                _start = newline + 1;
                return Taken();
            }

            if (_partial.Length + (_end - _start) > Limit)
            {
                return TooLong();
            }

            _partial.Write(_buffer, _start, _end - _start);
            _start = 0;
            _end = await _stream.ReadAsync(_buffer, cancellation).ConfigureAwait(false);
            if (_end == 0)
            {
                return _partial.Length > 0 ? Taken() : LineRead.End.Instance;
            }
        }
    }

    private LineRead Taken()
    {
        int length = (int)_partial.Length;
        byte[] bytes = _partial.GetBuffer();
        if (length > 0 && bytes[length - 1] == (byte)'\r')
        {
            length--;
        }

        string line = Encoding.UTF8.GetString(bytes, 0, length);
        _partial.SetLength(0);
        return new LineRead.Text(line);
    }

    // The session ends after a line too long, so what is left of it is dropped with the buffer.
    private LineRead TooLong()
    {
        _partial.SetLength(0);
        _start = _end = 0;
        return LineRead.TooLong.Instance;
    }
}

/// <summary>One read: a line, a line over the bound, or the end of the input.</summary>
internal abstract class LineRead
{
    private LineRead()
    {
    }

    internal sealed class Text(string line) : LineRead
    {
        internal string Line { get; } = line;
    }

    internal sealed class TooLong : LineRead
    {
        internal static readonly TooLong Instance = new();
    }

    internal sealed class End : LineRead
    {
        internal static readonly End Instance = new();
    }
}
