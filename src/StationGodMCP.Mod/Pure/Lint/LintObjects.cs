#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Lint;

/// <summary>A thing, port, network, cell, room or other object of the lint model, as expressions read it.</summary>
internal interface ILintObject
{
    ObjectType Type { get; }

    /// <summary>Identity: two objects with the same key are the same object (== and in compare keys).</summary>
    string Key { get; }

    /// <summary>The game's reference id (a thing, network or room id), when it has one.</summary>
    long? ReferenceId { get; }

    /// <summary>Where it is, when it has a place: a finding's at, distance and pairs read it.</summary>
    Vec3? Position { get; }

    /// <summary>How a message or rule_error names it.</summary>
    string Describe { get; }

    LintValue Get(LintField field);
}

/// <summary>
/// An object whose fields are worked out on first read and kept for the rest of the lint call, so a rule set that
/// reads one field from many rules pays for it once.
/// </summary>
internal abstract class LintSubject : ILintObject
{
    private LintValue[]? _values;
    private bool[]? _known;

    protected LintSubject(ObjectType type)
    {
        Type = type;
    }

    public ObjectType Type { get; }

    public abstract string Key { get; }

    public virtual long? ReferenceId => null;

    public virtual Vec3? Position => null;

    public abstract string Describe { get; }

    public LintValue Get(LintField field)
    {
        if (!ReferenceEquals(field.Owner, Type))
        {
            throw new LintEvaluationException($"{Type.Name} has no field {field.Name}");
        }

        _values ??= new LintValue[Type.Fields.Count];
        _known ??= new bool[Type.Fields.Count];
        if (!_known[field.Index])
        {
            _values[field.Index] = Compute(field.Name);
            _known[field.Index] = true;
        }

        return _values[field.Index];
    }

    /// <summary>The field's value; an unknown field name throws LintEvaluationException.</summary>
    protected abstract LintValue Compute(string field);

    protected LintEvaluationException NoField(string field) =>
        new LintEvaluationException($"{Type.Name} has no field {field}");
}

/// <summary>An object whose field values are given up front: rule examples, function results and tests.</summary>
internal sealed class LintRecord : ILintObject
{
    private readonly Dictionary<string, LintValue> _values;
    private readonly Dictionary<string, LintValue> _stubs;

    internal LintRecord(ObjectType type, string key, Dictionary<string, LintValue> values,
        Dictionary<string, LintValue>? stubs = null, string? describe = null)
    {
        Type = type;
        Key = key;
        _values = values;
        _stubs = stubs ?? new Dictionary<string, LintValue>(StringComparer.Ordinal);
        Describe = describe ?? key;
    }

    public ObjectType Type { get; }

    public string Key { get; }

    public long? ReferenceId =>
        _values.TryGetValue("reference_id", out LintValue id) && id.Kind == LintKind.Number ? (long)id.AsNumber
        : _values.TryGetValue("id", out LintValue other) && other.Kind == LintKind.Number ? (long)other.AsNumber
        : (long?)null;

    public Vec3? Position =>
        _values.TryGetValue("position", out LintValue at) && at.Kind == LintKind.Vec ? at.AsVec : (Vec3?)null;

    public string Describe { get; }

    public LintValue Get(LintField field)
    {
        if (_values.TryGetValue(field.Name, out LintValue value))
        {
            return value;
        }

        if (field.Type.IsNullable)
        {
            return LintValue.Null;
        }

        if (field.Type is ListType)
        {
            return LintValue.EmptyList;
        }

        throw new LintEvaluationException($"{Describe} gives no {field.Name}");
    }

    /// <summary>A function result an example fixes for this object: "sun_blocked" or "logic(On)".</summary>
    internal bool TryStub(string call, out LintValue value) => _stubs.TryGetValue(call, out value);
}

/// <summary>The sets a rule selects from, the world object, and cell lookups: one lint call's view of the game.</summary>
internal interface ILintWorld
{
    /// <summary>pieces, devices, structures, things, ports, cells, networks or rooms of the region checked.</summary>
    IReadOnlyList<ILintObject> Subjects(string set);

    /// <summary>The world object: sun, day length, outdoor atmosphere.</summary>
    ILintObject World { get; }

    /// <summary>The cell of a size (0.5: small, 2: a 2 m cell) holding a point; null where the world has none.</summary>
    ILintObject? CellAt(Vec3 point, double size);

    /// <summary>Whether the subject belongs to the plan a dry run checks (a planned thing, or one of its ports).</summary>
    bool IsPlanned(ILintObject subject);
}

/// <summary>The subject sets a select may name.</summary>
internal static class LintSets
{
    internal static readonly string[] All = { "pieces", "devices", "structures", "things", "ports", "cells", "networks", "rooms" };

    internal static readonly Dictionary<string, string> Docs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["pieces"] = "thing: cable, pipe and chute pieces, and what stands in their slots (in-line tanks, passive vents)",
        ["devices"] = "thing: small-grid devices and other small-grid things (consoles, sensors, lights, vents, panels)",
        ["structures"] = "thing: 2 m structures (frames, walls, windows, doors, large devices) and plates on the cells' faces",
        ["things"] = "thing: pieces, devices and structures together",
        ["ports"] = "port: the cable, pipe and chute ports of the devices",
        ["cells"] = "cell: the small cells a piece or device stands in",
        ["networks"] = "network: the cable, pipe and chute networks of the pieces and ports",
        ["rooms"] = "room: the rooms the cells lie in"
    };

    internal static string ElementTypeOf(string set) =>
        set == "ports" ? "port" : set == "cells" ? "cell" : set == "networks" ? "network" : set == "rooms" ? "room" : "thing";
}
