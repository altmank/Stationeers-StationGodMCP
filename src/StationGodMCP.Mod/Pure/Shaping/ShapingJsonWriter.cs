#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace StationGodMCP.Pure.Shaping;

/// <summary>Where the reply object to shape sits in what is written.</summary>
internal enum ShapingRoot
{
    /// <summary>The value of the root object's "result" key (the pipe's reply envelope).</summary>
    Envelope,

    /// <summary>The root value itself.</summary>
    Result
}

/// <summary>
/// A JsonWriter between the serialiser and the real writer that applies a ShapeRequest while the reply is written:
/// keys that fields leaves out and entries past a limit are not forwarded, so they are never formatted or sent. The
/// serialiser still reads every property; only formatting and sending are saved. At the end of the reply object it
/// adds fields_unmatched and shape_truncated. With nothing to leave out it forwards every token unchanged, so the
/// output is byte for byte the real writer's.
/// </summary>
internal sealed class ShapingJsonWriter : JsonWriter
{
    internal const string ResultKey = "result";
    internal const string UnmatchedKey = "fields_unmatched";
    internal const string TruncatedKey = "shape_truncated";

    private readonly JsonWriter _inner;
    private readonly ShapeRequest _shape;
    private Frame[] _frames = new Frame[16];
    private int _depth;
    private Pending _pending;

    internal ShapingJsonWriter(JsonWriter inner, ShapeRequest shape, ShapingRoot root)
    {
        _inner = inner;
        _shape = shape;
        Outcome = new ShapeOutcome(shape.Fields?.Count ?? 0);
        _pending = new Pending(root == ShapingRoot.Envelope ? Role.Envelope : Role.Result, null, null);
        AutoCompleteOnClose = false;
        CloseOutput = false;
    }

    internal ShapeOutcome Outcome { get; }

    private enum Role : byte
    {
        Pass,
        Skip,
        Envelope,
        Result,
        ResultValue,
        TopList,
        Entry,
        Projected,
        ProjectedList
    }

    private enum Token : byte
    {
        Object,
        Array,
        Scalar
    }

    public override void Flush() => _inner.Flush();

    public override void WriteStartObject()
    {
        Frame frame = Open(Token.Object);
        base.WriteStartObject();
        if (frame.Emit)
        {
            _inner.WriteStartObject();
        }
    }

    public override void WriteStartArray()
    {
        Frame frame = Open(Token.Array);
        base.WriteStartArray();
        if (frame.Emit)
        {
            _inner.WriteStartArray();
        }
    }

    public override void WriteEndObject()
    {
        Frame frame = Pop();
        base.WriteEndObject();
        if (frame.Role == Role.Result)
        {
            WriteOutcome();
        }

        if (frame.Emit)
        {
            _inner.WriteEndObject();
        }
    }

    public override void WriteEndArray()
    {
        Frame frame = Pop();
        base.WriteEndArray();
        if (frame.Role == Role.TopList)
        {
            Outcome.SawList(frame.List!, frame.Count, frame.Limit);
        }

        if (frame.Emit)
        {
            _inner.WriteEndArray();
        }
    }

    public override void WritePropertyName(string name)
    {
        bool emit = Name(name);
        base.WritePropertyName(name);
        if (emit)
        {
            _inner.WritePropertyName(name);
        }
    }

    public override void WritePropertyName(string name, bool escape)
    {
        bool emit = Name(name);
        base.WritePropertyName(name);
        if (emit)
        {
            _inner.WritePropertyName(name, escape);
        }
    }

    public override void WriteNull()
    {
        if (Scalar())
        {
            _inner.WriteNull();
        }
    }

    public override void WriteUndefined()
    {
        if (Scalar())
        {
            _inner.WriteUndefined();
        }
    }

    public override void WriteRawValue(string? json)
    {
        if (Scalar())
        {
            _inner.WriteRawValue(json);
        }
    }

    public override void WriteRaw(string? json)
    {
        if (_depth == 0 || _frames[_depth - 1].Emit)
        {
            _inner.WriteRaw(json);
        }
    }

    public override void WriteComment(string? text)
    {
        if (_depth == 0 || _frames[_depth - 1].Emit)
        {
            _inner.WriteComment(text);
        }
    }

    public override void WriteWhitespace(string ws)
    {
        if (_depth == 0 || _frames[_depth - 1].Emit)
        {
            _inner.WriteWhitespace(ws);
        }
    }

    public override void WriteValue(string? value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(int value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(uint value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(long value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(ulong value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(float value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(float? value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(double value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(double? value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(bool value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(short value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(ushort value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(char value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(byte value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(sbyte value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(decimal value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(DateTime value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(DateTimeOffset value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(Guid value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(TimeSpan value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(byte[]? value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    public override void WriteValue(Uri? value)
    {
        if (Scalar())
        {
            _inner.WriteValue(value);
        }
    }

    // A scalar value: decides whether it is forwarded, and keeps the base writer's state (any value token moves it
    // the same way).
    private bool Scalar()
    {
        Role role = Resolve(Token.Scalar, out _, out _);
        base.WriteNull();
        return role != Role.Skip;
    }

    private Frame Open(Token token)
    {
        Role role = Resolve(token, out SelectorNode? node, out string? list);
        Frame frame = new Frame(role, token == Token.Array, node, list, list != null ? _shape.LimitOf(list) : int.MaxValue);
        if (_depth == _frames.Length)
        {
            Array.Resize(ref _frames, _depth * 2);
        }

        _frames[_depth++] = frame;
        _pending = default;
        return frame;
    }

    private Frame Pop() => _frames[--_depth];

    /// <summary>The role of the value now starting, from the pending property (in an object) or the array's rules.</summary>
    private Role Resolve(Token token, out SelectorNode? node, out string? list)
    {
        list = null;
        if (_depth == 0 || !_frames[_depth - 1].IsArray)
        {
            Pending pending = _pending;
            _pending = default;
            node = pending.Node;
            list = pending.List;
            return pending.Role switch
            {
                Role.Envelope => token == Token.Object ? Role.Envelope : Role.Pass,
                Role.Result => token == Token.Object ? Role.Result : Role.Pass,
                Role.ResultValue => token == Token.Array ? Role.TopList : Role.Pass,
                Role.Projected => token switch
                {
                    Token.Object => Role.Entry,
                    Token.Array => Role.ProjectedList,
                    _ => Role.Pass
                },
                _ => pending.Role
            };
        }

        ref Frame array = ref _frames[_depth - 1];
        int index = array.Count++;
        switch (array.Role)
        {
            case Role.Skip:
                node = null;
                return Role.Skip;
            case Role.TopList when index >= array.Limit:
                node = null;
                return Role.Skip;
            case Role.TopList when token == Token.Object:
                Outcome.SawEntry();
                node = _shape.Fields?.EntryNodeFor(array.List!);
                return node != null ? Role.Entry : Role.Pass;
            case Role.ProjectedList when token == Token.Object:
                node = array.Node;
                return Role.Entry;
            case Role.Pass:
                node = array.Node;
                return Role.Pass;
            default:
                node = null;
                return Role.Pass;
        }
    }

    /// <summary>Decides a property: whether its name is forwarded, and the role of its value.</summary>
    private bool Name(string name)
    {
        Frame frame = _frames[_depth - 1];
        switch (frame.Role)
        {
            case Role.Skip:
                _pending = new Pending(Role.Skip, null, null);
                return false;
            case Role.Envelope:
                _pending = new Pending(name == ResultKey ? Role.Result : Role.Pass, null, null);
                return true;
            case Role.Result:
                _pending = new Pending(Role.ResultValue, null, name);
                return true;
            case Role.Entry:
            {
                SelectorNode? child = frame.Node!.Child(name);
                if (child == null)
                {
                    _pending = new Pending(Role.Skip, null, null);
                    return false;
                }

                Outcome.Match(child.Ends);
                _pending = child.Whole
                    ? new Pending(Role.Pass, child.HasChildren ? child : null, null)
                    : new Pending(Role.Projected, child, null);
                return true;
            }
            default:
            {
                SelectorNode? child = frame.Node?.Child(name);
                Outcome.Match(child?.Ends);
                _pending = new Pending(Role.Pass, child != null && child.HasChildren ? child : null, null);
                return true;
            }
        }
    }

    private void WriteOutcome()
    {
        FieldSelectors? fields = _shape.Fields;
        if (fields != null && Outcome.AnyEntry)
        {
            bool opened = false;
            for (int index = 0; index < fields.Count; index++)
            {
                if (Outcome.Matched(index))
                {
                    continue;
                }

                if (!opened)
                {
                    _inner.WritePropertyName(UnmatchedKey);
                    _inner.WriteStartArray();
                    opened = true;
                }

                _inner.WriteValue(fields[index].Text);
            }

            if (opened)
            {
                _inner.WriteEndArray();
            }
        }

        IReadOnlyList<KeyValuePair<string, int>> cut = Outcome.Cut;
        if (cut.Count > 0)
        {
            _inner.WritePropertyName(TruncatedKey);
            _inner.WriteStartObject();
            foreach (KeyValuePair<string, int> list in cut)
            {
                _inner.WritePropertyName(list.Key);
                _inner.WriteValue(list.Value);
            }

            _inner.WriteEndObject();
        }
    }

    private readonly struct Pending
    {
        internal Pending(Role role, SelectorNode? node, string? list)
        {
            Role = role;
            Node = node;
            List = list;
        }

        internal Role Role { get; }

        internal SelectorNode? Node { get; }

        internal string? List { get; }
    }

    private struct Frame
    {
        internal Frame(Role role, bool isArray, SelectorNode? node, string? list, int limit)
        {
            Role = role;
            IsArray = isArray;
            Node = node;
            List = list;
            Limit = limit;
            Count = 0;
        }

        internal Role Role { get; }

        internal SelectorNode? Node { get; }

        internal string? List { get; }

        internal int Limit { get; }

        internal bool IsArray { get; }

        internal int Count;

        internal bool Emit => Role != Role.Skip;
    }
}
