#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Lint;

/// <summary>
/// A type of the lint expression language: bool, number, string, vec, any, null, list&lt;T&gt;, map&lt;T&gt; (string keys),
/// T? (T or null) and the object types of the lint model (thing, port, network, cell, ...).
/// </summary>
internal abstract class LintType
{
    internal static readonly LintType Bool = new PrimitiveType("bool");
    internal static readonly LintType Number = new PrimitiveType("number");
    internal static readonly LintType String = new PrimitiveType("string");
    internal static readonly LintType Vec = new PrimitiveType("vec");
    internal static readonly LintType Any = new PrimitiveType("any");
    internal static readonly LintType Null = new PrimitiveType("null");

    internal abstract string Name { get; }

    internal virtual bool IsNullable => false;

    /// <summary>The type without its null: T for T?, itself otherwise.</summary>
    internal virtual LintType NonNull => this;

    internal static LintType ListOf(LintType element) => new ListType(element);

    internal static LintType MapOf(LintType value) => new MapType(value);

    internal static LintType Nullable(LintType inner) =>
        inner.IsNullable || ReferenceEquals(inner, Any) || ReferenceEquals(inner, Null) ? inner : new NullableType(inner);

    /// <summary>Whether a value of type <paramref name="other"/> may stand where this type is expected.</summary>
    internal bool Accepts(LintType other)
    {
        if (ReferenceEquals(this, Any) || ReferenceEquals(other, Any))
        {
            return true;
        }

        if (ReferenceEquals(other, Null))
        {
            return IsNullable;
        }

        if (other.IsNullable && !IsNullable)
        {
            return false;
        }

        return NonNull.AcceptsNonNull(other.NonNull);
    }

    protected virtual bool AcceptsNonNull(LintType other) => SameAs(other);

    internal virtual bool SameAs(LintType other) => ReferenceEquals(NonNull, other.NonNull);

    public override string ToString() => Name;
}

internal sealed class PrimitiveType : LintType
{
    internal PrimitiveType(string name)
    {
        Name = name;
    }

    internal override string Name { get; }
}

internal sealed class ListType : LintType
{
    internal ListType(LintType element)
    {
        Element = element;
    }

    internal LintType Element { get; }

    internal override string Name => $"list<{Element.Name}>";

    protected override bool AcceptsNonNull(LintType other) => other is ListType list && Element.Accepts(list.Element);

    internal override bool SameAs(LintType other) => other.NonNull is ListType list && Element.SameAs(list.Element);
}

internal sealed class MapType : LintType
{
    internal MapType(LintType value)
    {
        Value = value;
    }

    internal LintType Value { get; }

    internal override string Name => $"map<{Value.Name}>";

    protected override bool AcceptsNonNull(LintType other) => other is MapType map && Value.Accepts(map.Value);

    internal override bool SameAs(LintType other) => other.NonNull is MapType map && Value.SameAs(map.Value);
}

internal sealed class NullableType : LintType
{
    internal NullableType(LintType inner)
    {
        Inner = inner;
    }

    internal LintType Inner { get; }

    internal override string Name => Inner.Name + "?";

    internal override bool IsNullable => true;

    internal override LintType NonNull => Inner;
}

/// <summary>One field of an object type: its name, type and what it means.</summary>
internal sealed class LintField
{
    internal LintField(ObjectType owner, string name, LintType type, string doc, int index)
    {
        Owner = owner;
        Name = name;
        Type = type;
        Doc = doc;
        Index = index;
    }

    internal ObjectType Owner { get; }

    internal string Name { get; }

    internal LintType Type { get; }

    internal string Doc { get; }

    /// <summary>Position in the owner's field list: the slot a subject caches the value in.</summary>
    internal int Index { get; }
}

/// <summary>An object type of the lint model: a named set of typed fields.</summary>
internal sealed class ObjectType : LintType
{
    private readonly List<LintField> _fields = new List<LintField>();
    private readonly Dictionary<string, LintField> _byName = new Dictionary<string, LintField>(StringComparer.Ordinal);

    internal ObjectType(string name, string doc)
    {
        Name = name;
        Doc = doc;
    }

    internal override string Name { get; }

    internal string Doc { get; }

    internal IReadOnlyList<LintField> Fields => _fields;

    internal ObjectType Field(string name, LintType type, string doc)
    {
        if (_byName.ContainsKey(name))
        {
            throw new ArgumentException($"{Name} already has a field {name}.", nameof(name));
        }

        LintField field = new LintField(this, name, type, doc, _fields.Count);
        _fields.Add(field);
        _byName[name] = field;
        return this;
    }

    internal LintField? Find(string name) => _byName.TryGetValue(name, out LintField field) ? field : null;

    internal LintField this[string name] =>
        Find(name) ?? throw new ArgumentException($"{Name} has no field {name}.", nameof(name));
}
