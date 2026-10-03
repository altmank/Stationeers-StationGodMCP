#nullable enable

using System;
using System.Text;

namespace StationGodMCP.Pure.RemoteView;

/// <summary>
/// The bytes one remote-view payload is written into: little-endian integers and IEEE floats, a vector as three
/// floats, a text as a 16-bit byte count (0xFFFF for none) and its UTF-8 bytes. The game's message carries the finished
/// array as one length-prefixed block, so every layout here is read and written without a game type.
/// </summary>
internal sealed class WireWriter
{
    /// <summary>The most UTF-8 bytes a text carries; longer ones are cut at a character boundary.</summary>
    internal const int MaximumTextBytes = 512;

    internal const ushort NoText = 0xFFFF;

    private byte[] _bytes = new byte[128];
    private int _length;

    internal int Length => _length;

    internal void Byte(byte value)
    {
        Ensure(1);
        _bytes[_length++] = value;
    }

    internal void Bool(bool value) => Byte(value ? (byte)1 : (byte)0);

    internal void UInt16(ushort value)
    {
        Ensure(2);
        _bytes[_length++] = (byte)value;
        _bytes[_length++] = (byte)(value >> 8);
    }

    internal void Int32(int value)
    {
        Ensure(4);
        for (int shift = 0; shift < 32; shift += 8)
        {
            _bytes[_length++] = (byte)(value >> shift);
        }
    }

    internal void Int64(long value)
    {
        Ensure(8);
        for (int shift = 0; shift < 64; shift += 8)
        {
            _bytes[_length++] = (byte)(value >> shift);
        }
    }

    internal void Single(float value) => Int32(BitConverter.SingleToInt32Bits(value));

    internal void Vector(Vec3 value)
    {
        Single((float)value.X);
        Single((float)value.Y);
        Single((float)value.Z);
    }

    internal void Text(string? value)
    {
        if (value == null)
        {
            UInt16(NoText);
            return;
        }

        byte[] encoded = Encoding.UTF8.GetBytes(Cut(value));
        UInt16((ushort)encoded.Length);
        Ensure(encoded.Length);
        Array.Copy(encoded, 0, _bytes, _length, encoded.Length);
        _length += encoded.Length;
    }

    internal byte[] ToArray()
    {
        byte[] copy = new byte[_length];
        Array.Copy(_bytes, copy, _length);
        return copy;
    }

    // Whole characters only, so a cut text still decodes.
    private static string Cut(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= MaximumTextBytes)
        {
            return value;
        }

        int length = value.Length;
        while (length > 0 && Encoding.UTF8.GetByteCount(value.Substring(0, length)) > MaximumTextBytes)
        {
            length--;
        }

        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value.Substring(0, length);
    }

    private void Ensure(int more)
    {
        if (_length + more <= _bytes.Length)
        {
            return;
        }

        Array.Resize(ref _bytes, Math.Max(_bytes.Length * 2, _length + more));
    }
}

/// <summary>
/// Reads a payload WireWriter wrote. Reading past the end never throws: the reader turns Failed and answers zeros from
/// then on, so a decoder reads its whole layout and asks Complete once (read without failing, nothing left over).
/// </summary>
internal sealed class WireReader
{
    private readonly byte[] _bytes;
    private int _position;

    internal WireReader(byte[] bytes)
    {
        _bytes = bytes;
    }

    internal bool Failed { get; private set; }

    /// <summary>Every byte read and none missing.</summary>
    internal bool Complete => !Failed && _position == _bytes.Length;

    internal int Remaining => Failed ? 0 : _bytes.Length - _position;

    internal byte Byte() => Take(1) ? _bytes[_position - 1] : (byte)0;

    internal bool Bool() => Byte() != 0;

    internal ushort UInt16() => Take(2) ? (ushort)(_bytes[_position - 2] | (_bytes[_position - 1] << 8)) : (ushort)0;

    internal int Int32()
    {
        if (!Take(4))
        {
            return 0;
        }

        int start = _position - 4;
        return _bytes[start] | (_bytes[start + 1] << 8) | (_bytes[start + 2] << 16) | (_bytes[start + 3] << 24);
    }

    internal long Int64()
    {
        if (!Take(8))
        {
            return 0;
        }

        long value = 0;
        for (int index = 7; index >= 0; index--)
        {
            value = (value << 8) | _bytes[_position - 8 + index];
        }

        return value;
    }

    internal float Single() => BitConverter.Int32BitsToSingle(Int32());

    internal Vec3 Vector() => new Vec3(Single(), Single(), Single());

    internal string? Text()
    {
        ushort length = UInt16();
        if (length == WireWriter.NoText || Failed)
        {
            return null;
        }

        if (length > WireWriter.MaximumTextBytes || !Take(length))
        {
            Failed = true;
            return null;
        }

        return Encoding.UTF8.GetString(_bytes, _position - length, length);
    }

    /// <summary>Marks the payload unreadable (a count past its cap, an unknown kind).</summary>
    internal void Fail() => Failed = true;

    private bool Take(int count)
    {
        if (Failed || _bytes.Length - _position < count)
        {
            Failed = true;
            return false;
        }

        _position += count;
        return true;
    }
}

/// <summary>What decoding a payload came to: the value, a payload of another protocol, or bytes that do not read.</summary>
internal abstract class WireRead<T>
{
    private WireRead()
    {
    }

    internal sealed class Read : WireRead<T>
    {
        internal Read(T value)
        {
            Value = value;
        }

        internal T Value { get; }
    }

    /// <summary>A payload of another remote-view protocol: a different StationGod on the other side.</summary>
    internal sealed class OtherProtocol : WireRead<T>
    {
        internal OtherProtocol(int protocol)
        {
            Protocol = protocol;
        }

        internal int Protocol { get; }
    }

    internal sealed class Malformed : WireRead<T>
    {
        internal Malformed(string reason)
        {
            Reason = reason;
        }

        internal string Reason { get; }
    }
}

/// <summary>
/// The remote-view protocol: the first byte of every payload StationGod sends between games. A layout never changes
/// under a number; a changed layout takes the next one, and a game reading another number ignores the payload.
/// </summary>
internal static class ViewProtocol
{
    internal const byte Current = 1;
}
