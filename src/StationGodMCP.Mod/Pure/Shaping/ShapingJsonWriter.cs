#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
/// keys that fields leaves out or omit names, and entries past a limit, are not forwarded, so they are never formatted
/// or sent. The serialiser still reads every property; only formatting and sending are saved. At the end of the reply
/// object it adds fields_mapped (selectors FieldMapping read as their one safe match), fields_unmatched (with
/// fields_closest, the near keys of each that has some, and fields_valid, the keys the reply had, sorted),
/// omit_unmatched and truncated: every list held back, by the handler (its notes) or by a limit here, with how many it carries, how many there are and how
/// to get more; an announcing writer (a call's reply) writes truncated always, empty when nothing was held back. With
/// nothing to leave out it forwards every token unchanged, so the output is byte for byte the real writer's.
/// </summary>
/// <remarks>
/// fields at the reply's top: a top-level key a single name names is kept whole and a path keeps what it reaches inside
/// it; numbers, strings, booleans and nulls are kept; every other top-level list or object is shaped as an entry (each
/// list entry, or the object itself, keeps the single names and the paths read from it) and left out when nothing in
/// it is kept. Such a value is written to a buffer first, which holds only what is kept, and copied when it ends.
/// </remarks>
internal sealed class ShapingJsonWriter : JsonWriter
{
    internal const string ResultKey = "result";
    internal const string MappedKey = "fields_mapped";
    internal const string UnmatchedKey = "fields_unmatched";
    internal const string ClosestKey = "fields_closest";
    internal const string ValidKey = "fields_valid";
    internal const string OmitUnmatchedKey = "omit_unmatched";
    internal const string TruncatedKey = "truncated";

    /// <summary>The most keys fields_valid names.</summary>
    internal const int MaximumValidKeys = 100;

    /// <summary>The most key paths below the entries one reply records for near misses.</summary>
    internal const int MaximumDeepPaths = 4000;

    private readonly JsonWriter _inner;
    private JsonWriter _target;
    private readonly ShapeRequest _shape;
    private Frame[] _frames = new Frame[16];
    private int _depth;
    private Pending _pending;

    private readonly IReadOnlyList<Truncation> _notes;
    private readonly bool _announce;
    private readonly bool _nameClosest;

    // The top-level key whose value starts next, held back until the value says whether it is kept (fields only).
    private string? _topName;
    private bool? _topEscape;
    private bool _sawEmptyList;
    private SortedSet<string>? _validKeys;

    // Key paths below the entries fields reads (atmospheres.atmosphere.total_mol), for a single name that matches no
    // entry key: null unless fields holds a single name.
    private readonly HashSet<string>? _deepPaths;
    private int _resultDepth = -1;
    private string? _lastName;

    internal ShapingJsonWriter(JsonWriter inner, ShapeRequest shape, ShapingRoot root,
        IReadOnlyList<Truncation>? notes = null, bool announce = false, bool nameClosest = true)
    {
        _nameClosest = nameClosest;
        _inner = inner;
        _target = inner;
        _shape = shape;
        _notes = notes ?? Array.Empty<Truncation>();
        _announce = announce;
        _deepPaths = HasSingleName(shape.Fields) ? new HashSet<string>(StringComparer.Ordinal) : null;
        Outcome = new ShapeOutcome(shape.Fields?.Count ?? 0, shape.Omit?.Count ?? 0);
        _pending = root == ShapingRoot.Envelope
            ? new Pending(Role.Envelope, null, null)
            : new Pending(Role.Result, null, null, shape.Omit?.Root);
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

    /// <summary>How a top-level value is kept while fields applies; None without fields and below the top.</summary>
    private enum TopKeep : byte
    {
        None,

        /// <summary>A single name named the key: its whole value.</summary>
        Whole,

        /// <summary>A path starts at the key: its value, shaped.</summary>
        Named,

        /// <summary>Nothing names the key: a scalar as it is, a list or object shaped and kept when anything in it is.</summary>
        Shaped
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
            _target.WriteStartObject();
        }
    }

    public override void WriteStartArray()
    {
        Frame frame = Open(Token.Array);
        base.WriteStartArray();
        if (frame.Emit)
        {
            _target.WriteStartArray();
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
            _target.WriteEndObject();
        }

        if (frame.Buffer != null)
        {
            FinishBuffered(frame);
        }
    }

    public override void WriteEndArray()
    {
        Frame frame = Pop();
        base.WriteEndArray();
        if (frame.Emit)
        {
            _target.WriteEndArray();
        }

        bool kept = frame.Buffer == null || FinishBuffered(frame);
        if (frame.Role == Role.TopList)
        {
            // A list fields left out is not reported cut: the reply does not carry it at all.
            Outcome.SawList(frame.List!, frame.Count, kept ? frame.Limit : int.MaxValue);
            _sawEmptyList |= Math.Min(frame.Count, frame.Limit) == 0;
        }
    }

    public override void WritePropertyName(string name)
    {
        bool emit = Name(name, null);
        base.WritePropertyName(name);
        if (emit)
        {
            _target.WritePropertyName(name);
        }
    }

    public override void WritePropertyName(string name, bool escape)
    {
        bool emit = Name(name, escape);
        base.WritePropertyName(name);
        if (emit)
        {
            _target.WritePropertyName(name, escape);
        }
    }

    public override void WriteNull()
    {
        if (Scalar())
        {
            _target.WriteNull();
        }
    }

    public override void WriteUndefined()
    {
        if (Scalar())
        {
            _target.WriteUndefined();
        }
    }

    public override void WriteRawValue(string? json)
    {
        if (Scalar())
        {
            _target.WriteRawValue(json);
        }
    }

    public override void WriteRaw(string? json)
    {
        if (_depth == 0 || _frames[_depth - 1].Emit)
        {
            _target.WriteRaw(json);
        }
    }

    public override void WriteComment(string? text)
    {
        if (_depth == 0 || _frames[_depth - 1].Emit)
        {
            _target.WriteComment(text);
        }
    }

    public override void WriteWhitespace(string ws)
    {
        if (_depth == 0 || _frames[_depth - 1].Emit)
        {
            _target.WriteWhitespace(ws);
        }
    }

    public override void WriteValue(string? value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(int value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(uint value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(long value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(ulong value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(float value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(float? value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(double value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(double? value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(bool value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(short value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(ushort value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(char value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(byte value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(sbyte value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(decimal value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(DateTime value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(DateTimeOffset value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(Guid value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(TimeSpan value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(byte[]? value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    public override void WriteValue(Uri? value)
    {
        if (Scalar())
        {
            _target.WriteValue(value);
        }
    }

    // A scalar value: decides whether it is forwarded, and keeps the base writer's state (any value token moves it
    // the same way).
    private bool Scalar()
    {
        Role role = Resolve(Token.Scalar, out _, out _, out _, out TopKeep keep);
        base.WriteNull();
        if (role == Role.Skip)
        {
            return false;
        }

        if (keep != TopKeep.None)
        {
            WriteTopName();
        }

        return true;
    }

    private Frame Open(Token token)
    {
        Role role = Resolve(token, out SelectorNode? node, out string? list, out SelectorNode? omit, out TopKeep keep);
        bool inTopList = _depth > 0 && _frames[_depth - 1].Role == Role.TopList;
        Frame frame = new Frame(role, token == Token.Array, node, list, list != null ? _shape.LimitOf(list) : int.MaxValue,
            omit, keep, role == Role.Entry && (inTopList || keep != TopKeep.None));
        frame.Segment = _depth > 0 && !_frames[_depth - 1].IsArray ? _lastName : null;
        if (role == Role.Result)
        {
            _resultDepth = _depth;
        }
        if (keep != TopKeep.None && role != Role.Skip)
        {
            if (keep == TopKeep.Shaped)
            {
                frame.Buffer = new JTokenWriter();
                frame.TopName = _topName;
                frame.TopEscape = _topEscape;
                _topName = null;
                _target = frame.Buffer;
            }
            else
            {
                WriteTopName();
            }
        }

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
    private Role Resolve(Token token, out SelectorNode? node, out string? list, out SelectorNode? omit,
        out TopKeep keep)
    {
        list = null;
        keep = TopKeep.None;
        if (_depth == 0 || !_frames[_depth - 1].IsArray)
        {
            Pending pending = _pending;
            _pending = default;
            node = pending.Node;
            list = pending.List;
            omit = pending.Omit;
            keep = pending.Keep;
            return pending.Role switch
            {
                Role.Envelope => token == Token.Object ? Role.Envelope : Role.Pass,
                Role.Result => token == Token.Object ? Role.Result : Role.Pass,
                Role.ResultValue => token switch
                {
                    Token.Array => Role.TopList,
                    Token.Object when pending.Keep is TopKeep.Named or TopKeep.Shaped => Role.Entry,
                    _ => Role.Pass
                },
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
        omit = array.Omit;
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
                if (array.Keep == TopKeep.Whole)
                {
                    node = array.Node;
                    return Role.Pass;
                }

                node = _shape.Fields?.EntryNodeFor(array.List!);
                return node != null ? Role.Entry : Role.Pass;
            case Role.TopList when array.Keep == TopKeep.Shaped:
                // A list nothing names keeps only object entries: a single name can match nothing else.
                node = null;
                return Role.Skip;
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
    private bool Name(string name, bool? escape)
    {
        _lastName = name;
        NoteDeepPath(name);
        Frame frame = _frames[_depth - 1];
        if (frame.Buffer != null)
        {
            _frames[_depth - 1].Count++;
        }

        SelectorNode? omit = frame.Role == Role.Skip ? null : frame.Omit?.Child(name);
        if (omit is { Whole: true })
        {
            Outcome.OmitBelow(omit);
            if (frame.Role == Role.Entry)
            {
                Outcome.Match(frame.Node!.Child(name)?.Ends);
            }

            _pending = new Pending(Role.Skip, null, null);
            return false;
        }

        if (frame.Role == Role.Result && _shape.Omit != null)
        {
            // Below a top-level key: the paths through it and, one level down, every single name.
            omit = _shape.Omit.Below(name);
        }

        bool emit = Decide(frame, name, escape);
        _pending = _pending.WithOmit(omit);
        return emit;
    }

    private bool Decide(Frame frame, string name, bool? escape)
    {
        switch (frame.Role)
        {
            case Role.Skip:
                _pending = new Pending(Role.Skip, null, null);
                return false;
            case Role.Envelope:
                _pending = name == ResultKey
                    ? new Pending(Role.Result, null, null, _shape.Omit?.Root)
                    : new Pending(Role.Pass, null, null);
                return true;
            case Role.Result:
                return DecideTop(name, escape);
            case Role.Entry:
            {
                if (frame.CollectKeys)
                {
                    NoteKey(name);
                }

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

    // A top-level key: without fields it is written at once; with fields its name waits for its value (Open, Scalar).
    private bool DecideTop(string name, bool? escape)
    {
        FieldSelectors? fields = _shape.Fields;
        if (fields == null)
        {
            _pending = new Pending(Role.ResultValue, null, name);
            return true;
        }

        NoteKey(name);
        _topName = name;
        _topEscape = escape;
        SelectorNode? top = fields.Top.Child(name);
        if (top is { Whole: true })
        {
            Outcome.Match(top.Ends);
            _pending = new Pending(Role.ResultValue, top.HasChildren ? top : null, name, keep: TopKeep.Whole);
        }
        else
        {
            _pending = new Pending(Role.ResultValue, fields.EntryNodeFor(name), name,
                keep: top != null ? TopKeep.Named : TopKeep.Shaped);
        }

        return false;
    }

    private void WriteTopName()
    {
        string? name = _topName;
        _topName = null;
        if (name != null)
        {
            WriteName(name, _topEscape);
        }
    }

    private void WriteName(string name, bool? escape)
    {
        if (escape is bool given)
        {
            _inner.WritePropertyName(name, given);
        }
        else
        {
            _inner.WritePropertyName(name);
        }
    }

    // A top-level list or object nothing named, now complete in its buffer: copied out when anything in it was kept,
    // or when it had nothing to keep (an empty list, a list a limit cut to nothing, an empty object).
    private bool FinishBuffered(Frame frame)
    {
        _target = _inner;
        JToken? value = frame.Buffer!.Token;
        int considered = frame.IsArray ? Math.Min(frame.Count, frame.Limit) : frame.Count;
        if (considered > 0 && !Holds(value))
        {
            return false;
        }

        WriteName(frame.TopName!, frame.TopEscape);
        value!.WriteTo(_inner);
        return true;
    }

    private static bool Holds(JToken? value)
    {
        switch (value)
        {
            case JObject entry:
                return entry.HasValues;
            case JArray list:
                foreach (JToken entry in list)
                {
                    if (entry is JObject { HasValues: true })
                    {
                        return true;
                    }
                }

                return false;
            default:
                return false;
        }
    }

    // The path from the reply's top to this key, when it lies below a top-level entry's own keys: three names or more.
    private void NoteDeepPath(string name)
    {
        if (_deepPaths == null || _resultDepth < 0 || _depth - _resultDepth < 2 || _deepPaths.Count >= MaximumDeepPaths)
        {
            return;
        }

        List<string> path = new List<string>(_depth - _resultDepth + 1);
        for (int index = _resultDepth + 1; index < _depth; index++)
        {
            if (_frames[index].Segment is string segment)
            {
                path.Add(segment);
            }
        }

        if (path.Count >= 2)
        {
            path.Add(name);
            _deepPaths.Add(string.Join(".", path));
        }
    }

    private static bool HasSingleName(FieldSelectors? fields)
    {
        if (fields == null)
        {
            return false;
        }

        for (int index = 0; index < fields.Count; index++)
        {
            if (fields[index] is FieldSelector.Name)
            {
                return true;
            }
        }

        return false;
    }

    private void NoteKey(string name) => (_validKeys ??= new SortedSet<string>(StringComparer.Ordinal)).Add(name);

    private void WriteOutcome()
    {
        FieldSelectors? fields = _shape.Fields;
        WriteMapped(fields);
        // A reply whose lists are all empty may lack a key only because it has no entries: nothing is reported then.
        if (fields != null && (Outcome.AnyEntry || !_sawEmptyList))
        {
            List<int> unmatched = new List<int>();
            for (int index = 0; index < fields.Count; index++)
            {
                if (!Outcome.Matched(index))
                {
                    unmatched.Add(index);
                }
            }

            if (unmatched.Count > 0)
            {
                SortedSet<string> seen = new SortedSet<string>(_validKeys ?? new SortedSet<string>(StringComparer.Ordinal),
                    StringComparer.Ordinal);
                _inner.WritePropertyName(UnmatchedKey);
                _inner.WriteStartArray();
                foreach (int index in unmatched)
                {
                    _inner.WriteValue(fields[index].Text);
                }

                _inner.WriteEndArray();
                foreach (string skipped in _shape.SkippedKeys)
                {
                    NoteKey(skipped);
                }

                Outcome.Unmatch(unmatched, seen, _validKeys ?? seen, _deepPaths);
                if (_nameClosest)
                {
                    WriteClosest(fields, unmatched);
                }

                _inner.WritePropertyName(ValidKey);
                _inner.WriteStartArray();
                int written = 0;
                foreach (string key in _validKeys ?? new SortedSet<string>(StringComparer.Ordinal))
                {
                    if (written++ == MaximumValidKeys)
                    {
                        break;
                    }

                    _inner.WriteValue(key);
                }

                _inner.WriteEndArray();
            }
        }

        OmitSelectors? omit = _shape.Omit;
        if (omit != null)
        {
            bool opened = false;
            for (int index = 0; index < omit.Count; index++)
            {
                if (Outcome.Omitted(index))
                {
                    continue;
                }

                if (!opened)
                {
                    _inner.WritePropertyName(OmitUnmatchedKey);
                    _inner.WriteStartArray();
                    opened = true;
                }

                _inner.WriteValue(omit[index].Text);
            }

            if (opened)
            {
                _inner.WriteEndArray();
            }
        }

        List<Truncation> truncated = Truncations.Merge(_notes, Outcome.Cut, _shape);
        if (truncated.Count == 0 && !_announce)
        {
            return;
        }

        _inner.WritePropertyName(TruncatedKey);
        _inner.WriteStartArray();
        foreach (Truncation entry in truncated)
        {
            _inner.WriteStartObject();
            _inner.WritePropertyName("list");
            _inner.WriteValue(entry.List);
            _inner.WritePropertyName("returned");
            _inner.WriteValue(entry.Returned);
            _inner.WritePropertyName("total");
            _inner.WriteValue(entry.Total);
            if (entry.AtLeast)
            {
                _inner.WritePropertyName("at_least");
                _inner.WriteValue(true);
            }

            _inner.WritePropertyName("more");
            _inner.WriteValue(entry.More);
            _inner.WriteEndObject();
        }

        _inner.WriteEndArray();
    }

    // The selectors read as another key that matched it: {given: key}.
    private void WriteMapped(FieldSelectors? fields)
    {
        IReadOnlyDictionary<string, string>? mapped = _shape.Mapped;
        if (fields == null || mapped == null)
        {
            return;
        }

        bool opened = false;
        for (int index = 0; index < fields.Count; index++)
        {
            string text = fields[index].Text;
            if (!Outcome.Matched(index) || !mapped.TryGetValue(text, out string? key))
            {
                continue;
            }

            if (!opened)
            {
                _inner.WritePropertyName(MappedKey);
                _inner.WriteStartObject();
                opened = true;
            }

            _inner.WritePropertyName(text);
            _inner.WriteValue(key);
        }

        if (opened)
        {
            _inner.WriteEndObject();
        }
    }

    // Each unmatched selector's close keys among those the reply had (costly keys skipped included): {given: [keys]}.
    private void WriteClosest(FieldSelectors fields, List<int> unmatched)
    {
        bool opened = false;
        foreach (int index in unmatched)
        {
            string? key = FieldMapping.KeyOf(fields[index]);
            List<string> closest = key != null && _validKeys != null
                ? FieldMatch.Closest(key, _validKeys)
                : new List<string>();
            if (fields[index] is FieldSelector.Name name && _deepPaths != null)
            {
                foreach (string path in DeepPaths.Closest(name.Key, _deepPaths))
                {
                    if (closest.Count < FieldMatch.MaximumCandidates)
                    {
                        closest.Add(path);
                    }
                }
            }
            if (closest.Count == 0)
            {
                continue;
            }

            if (!opened)
            {
                _inner.WritePropertyName(ClosestKey);
                _inner.WriteStartObject();
                opened = true;
            }

            _inner.WritePropertyName(fields[index].Text);
            _inner.WriteStartArray();
            foreach (string close in closest)
            {
                _inner.WriteValue(close);
            }

            _inner.WriteEndArray();
        }

        if (opened)
        {
            _inner.WriteEndObject();
        }
    }

    private readonly struct Pending
    {
        internal Pending(Role role, SelectorNode? node, string? list, SelectorNode? omit = null,
            TopKeep keep = TopKeep.None)
        {
            Role = role;
            Node = node;
            List = list;
            Omit = omit;
            Keep = keep;
        }

        /// <summary>What omit still leaves out below the value now starting; null when nothing.</summary>
        internal SelectorNode? Omit { get; }

        internal TopKeep Keep { get; }

        internal Pending WithOmit(SelectorNode? omit) => omit == null ? this : new Pending(Role, Node, List, omit, Keep);

        internal Role Role { get; }

        internal SelectorNode? Node { get; }

        internal string? List { get; }
    }

    private struct Frame
    {
        internal Frame(Role role, bool isArray, SelectorNode? node, string? list, int limit, SelectorNode? omit,
            TopKeep keep = TopKeep.None, bool collectKeys = false)
        {
            Role = role;
            IsArray = isArray;
            Node = node;
            List = list;
            Limit = limit;
            Omit = omit;
            Keep = keep;
            CollectKeys = collectKeys;
            Count = 0;
            Buffer = null;
            TopName = null;
            TopEscape = null;
            Segment = null;
        }

        /// <summary>The key this value is written under; null for a list entry and the reply's own root.</summary>
        internal string? Segment;

        internal TopKeep Keep { get; }

        /// <summary>An entry whose keys fields_valid lists: a top-level list's entry or a top-level object.</summary>
        internal bool CollectKeys { get; }

        /// <summary>Where a top-level value nothing named is written until it ends; null otherwise.</summary>
        internal JTokenWriter? Buffer;

        internal string? TopName;

        internal bool? TopEscape;

        /// <summary>What omit leaves out below this object, or in each entry of this array.</summary>
        internal SelectorNode? Omit { get; }

        internal Role Role { get; }

        internal SelectorNode? Node { get; }

        internal string? List { get; }

        internal int Limit { get; }

        internal bool IsArray { get; }

        internal int Count;

        internal bool Emit => Role != Role.Skip;
    }
}
