#nullable enable

using System;
using System.Collections.Generic;
using System.Text;

namespace StationGodMCP.Pure.Protocol;

/// <summary>
/// Splits a byte stream into lines: each ends at a line feed, a carriage return before it is dropped, a UTF-8 byte
/// order mark at the very start of the stream is dropped, and bytes are decoded as UTF-8 (invalid ones become U+FFFD,
/// as StreamReader does). A line longer than MaximumLineBytes (when set) is reported once, as Overflow, and the
/// stream is unusable after it.
/// </summary>
internal sealed class LineFramer
{
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, false);

    private byte[] _pending = new byte[4096];
    private int _pendingCount;
    private bool _started;

    /// <summary>The longest line accepted, in bytes without its line end; null for no limit.</summary>
    internal int? MaximumLineBytes { get; set; }

    /// <summary>True once a line passed MaximumLineBytes.</summary>
    internal bool Overflowed { get; private set; }

    /// <summary>
    /// Takes the next bytes read and adds every line they complete to lines. False when a line passed the limit (the
    /// lines before it are still added).
    /// </summary>
    internal bool Feed(byte[] buffer, int count, List<string> lines)
    {
        if (Overflowed)
        {
            return false;
        }

        int start = 0;
        if (!_started && count > 0)
        {
            start = SkipByteOrderMark(buffer, count);
        }

        for (int index = start; index < count; index++)
        {
            if (buffer[index] != (byte)'\n')
            {
                continue;
            }

            Append(buffer, start, index - start);
            string line = Overflowed ? string.Empty : TakeLine();
            if (Overflowed)
            {
                return false;
            }

            lines.Add(line);
            start = index + 1;
        }

        Append(buffer, start, count - start);
        return !Overflowed;
    }

    // A byte order mark may arrive split across reads; until three bytes are in, hold them.
    private int SkipByteOrderMark(byte[] buffer, int count)
    {
        int needed = 3 - _pendingCount;
        if (_pendingCount + count < 3)
        {
            bool stillMark = true;
            for (int index = 0; index < count && stillMark; index++)
            {
                stillMark = buffer[index] == ByteOrderMarkAt(_pendingCount + index);
            }

            if (stillMark)
            {
                Append(buffer, 0, count);
                return count;
            }

            _started = true;
            return 0;
        }

        _started = true;
        bool mark = true;
        for (int index = 0; index < 3; index++)
        {
            byte value = index < _pendingCount ? _pending[index] : buffer[index - _pendingCount];
            mark &= value == ByteOrderMarkAt(index);
        }

        if (!mark)
        {
            return 0;
        }

        _pendingCount = 0;
        return needed;
    }

    private static byte ByteOrderMarkAt(int index) => index switch
    {
        0 => 0xEF,
        1 => 0xBB,
        _ => 0xBF
    };

    private void Append(byte[] buffer, int offset, int count)
    {
        if (count <= 0)
        {
            return;
        }

        if (MaximumLineBytes is int maximum && _pendingCount + count > maximum + 1)
        {
            // One byte over is allowed for a carriage return the line end will drop.
            Overflowed = true;
            return;
        }

        if (_pendingCount + count > _pending.Length)
        {
            Array.Resize(ref _pending, Math.Max(_pending.Length * 2, _pendingCount + count));
        }

        Buffer.BlockCopy(buffer, offset, _pending, _pendingCount, count);
        _pendingCount += count;
    }

    private string TakeLine()
    {
        int length = _pendingCount;
        if (length > 0 && _pending[length - 1] == (byte)'\r')
        {
            length--;
        }

        if (MaximumLineBytes is int maximum && length > maximum)
        {
            Overflowed = true;
            return string.Empty;
        }

        _started = true;
        string line = Utf8.GetString(_pending, 0, length);
        _pendingCount = 0;
        return line;
    }
}
