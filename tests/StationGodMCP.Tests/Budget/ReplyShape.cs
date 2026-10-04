#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Tests.Budget;

/// <summary>
/// What a method's default reply holds on the large world, member by member (Type.Member, as ReplySynth names
/// them): how many entries each list or dictionary has, which members a default reply leaves out, the text of a
/// member whose length matters (a chip's source), and which view a reply is built from. Each declaration states a
/// bound the handler keeps (a page limit, a fixed count) or the large world's size where nothing bounds it.
/// </summary>
internal sealed class ReplyShape
{
    private readonly Dictionary<string, int> _lengths = new Dictionary<string, int>(StringComparer.Ordinal);
    private readonly HashSet<string> _absent = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _texts = new Dictionary<string, string>(StringComparer.Ordinal);
    private readonly Dictionary<Type, Func<object>> _factories = new Dictionary<Type, Func<object>>();
    private readonly Dictionary<string, Type> _types = new Dictionary<string, Type>(StringComparer.Ordinal);

    internal ReplyShape(Type view) => View = view;

    /// <summary>The method's x-default-limits: entries the mod keeps of each top-level list, by its JSON key.</summary>
    internal IReadOnlyDictionary<string, int> TopLimits { get; private set; } = new Dictionary<string, int>();

    internal ReplyShape WithTopLimits(IReadOnlyDictionary<string, int> limits)
    {
        TopLimits = limits;
        return this;
    }

    /// <summary>The view the default reply is.</summary>
    internal Type View { get; }

    internal ReplyShape List(string member, int length)
    {
        _lengths[member] = length;
        return this;
    }

    /// <summary>A member the default reply leaves out (null, or never set): an opt-in part.</summary>
    internal ReplyShape Absent(params string[] members)
    {
        foreach (string member in members)
        {
            _absent.Add(member);
        }

        return this;
    }

    internal ReplyShape Text(string member, string text)
    {
        _texts[member] = text;
        return this;
    }

    /// <summary>The type a member declared object (or a list of object) holds on the wire.</summary>
    internal ReplyShape Holds(string member, Type type)
    {
        _types[member] = type;
        return this;
    }

    /// <summary>A view built by hand where filling its fields would not give what the handler writes.</summary>
    internal ReplyShape Build(Type type, Func<object> factory)
    {
        _factories[type] = factory;
        return this;
    }

    /// <summary>This shape's declarations over the common ones: a member declared in both takes this shape's.</summary>
    internal ReplyShape Over(ReplyShape common)
    {
        ReplyShape merged = new ReplyShape(View) { TopLimits = TopLimits };
        foreach (ReplyShape source in new[] { common, this })
        {
            foreach (KeyValuePair<string, int> length in source._lengths)
            {
                merged._lengths[length.Key] = length.Value;
                merged._absent.Remove(length.Key);
            }

            foreach (string absent in source._absent)
            {
                merged._absent.Add(absent);
                merged._lengths.Remove(absent);
            }

            foreach (KeyValuePair<string, string> text in source._texts)
            {
                merged._texts[text.Key] = text.Value;
            }

            foreach (KeyValuePair<Type, Func<object>> factory in source._factories)
            {
                merged._factories[factory.Key] = factory.Value;
            }

            foreach (KeyValuePair<string, Type> held in source._types)
            {
                merged._types[held.Key] = held.Value;
            }
        }

        return merged;
    }

    internal int? LengthOf(string member) =>
        _lengths.TryGetValue(member, out int length) || _lengths.TryGetValue(AnyType(member), out length) ? length : null;

    internal bool IsAbsent(string member) => _absent.Contains(member) || _absent.Contains(AnyType(member));

    internal string? TextOf(string member) =>
        _texts.TryGetValue(member, out string? text) || _texts.TryGetValue(AnyType(member), out text) ? text : null;

    internal Type? HeldBy(string member) => _types.TryGetValue(member, out Type? type) ? type : null;

    // Type.Member as *.Member: a declaration that holds for that member of every view.
    private static string AnyType(string member)
    {
        int dot = member.IndexOf('.');
        return dot < 0 ? member : "*" + member.Substring(dot);
    }

    internal Func<object>? Factory(Type type) => _factories.TryGetValue(type, out Func<object>? factory) ? factory : null;
}
