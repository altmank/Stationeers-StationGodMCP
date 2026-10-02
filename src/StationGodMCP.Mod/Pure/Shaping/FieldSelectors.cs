#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Shaping;

/// <summary>
/// One selector from shape.fields: a single name (a key of every entry of every top-level list), a path (a top-level
/// list, then keys inside its entries), or text that follows neither, which matches nothing. Text is the selector as
/// fields_unmatched reports it: trimmed.
/// </summary>
internal abstract class FieldSelector
{
    private FieldSelector(string text) => Text = text;

    internal string Text { get; }

    /// <summary>A selector from its text, trimmed as the sidecar trims it; text outside the grammar is Unparsed.</summary>
    internal static FieldSelector Parse(string given)
    {
        string text = given.Trim();
        string[] names = text.Split('.');
        foreach (string name in names)
        {
            if (!IsName(name))
            {
                return new Unparsed(text);
            }
        }

        if (names.Length == 1)
        {
            return new Name(text, names[0]);
        }

        string[] rest = new string[names.Length - 1];
        Array.Copy(names, 1, rest, 0, rest.Length);
        return new Path(text, names[0], rest);
    }

    /// <summary>A selector that is not a string at all: never matches, reported by its JSON text.</summary>
    internal static FieldSelector NotText(string json) => new Unparsed(json);

    /// <summary>name = 1*( ALPHA / DIGIT / "_" ), ASCII only.</summary>
    internal static bool IsName(string name)
    {
        if (name.Length == 0)
        {
            return false;
        }

        foreach (char character in name)
        {
            bool allowed = character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_';
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>reference_id: that key in every object entry of every top-level list.</summary>
    internal sealed class Name : FieldSelector
    {
        internal Name(string text, string key) : base(text) => Key = key;

        internal string Key { get; }
    }

    /// <summary>things.position.x: in the top-level list things, the key path position.x of each object entry.</summary>
    internal sealed class Path : FieldSelector
    {
        internal Path(string text, string list, string[] keys) : base(text)
        {
            List = list;
            Keys = keys;
        }

        internal string List { get; }

        internal IReadOnlyList<string> Keys { get; }
    }

    /// <summary>Not a selector under the grammar: kept only so fields_unmatched names it.</summary>
    internal sealed class Unparsed : FieldSelector
    {
        internal Unparsed(string text) : base(text)
        {
        }
    }
}

/// <summary>
/// What to keep below one key, from every selector that reaches it: the whole value, or only some keys inside it.
/// Ends lists the selectors (by index) that are satisfied when this key is present.
/// </summary>
internal sealed class SelectorNode
{
    private Dictionary<string, SelectorNode>? _children;
    private List<int>? _ends;

    /// <summary>Keep the key's whole value (a selector stopped here); children then only record matches.</summary>
    internal bool Whole { get; private set; }

    internal bool HasChildren => _children != null;

    internal IReadOnlyList<int>? Ends => _ends;

    internal SelectorNode? Child(string key) =>
        _children != null && _children.TryGetValue(key, out SelectorNode child) ? child : null;

    internal SelectorNode Add(string key)
    {
        _children ??= new Dictionary<string, SelectorNode>(StringComparer.Ordinal);
        if (!_children.TryGetValue(key, out SelectorNode child))
        {
            child = new SelectorNode();
            _children.Add(key, child);
        }

        return child;
    }

    internal void EndHere(int selector)
    {
        Whole = true;
        _ends ??= new List<int>();
        _ends.Add(selector);
    }
}

/// <summary>
/// shape.fields parsed: the selectors in the order given, without repeats, and per top-level list the node that
/// says what to keep in each of its entries. A list named by no path keeps the single names only.
/// </summary>
internal sealed class FieldSelectors
{
    private readonly FieldSelector[] _selectors;
    private readonly SelectorNode _singleNames;
    private readonly Dictionary<string, SelectorNode> _byList;

    private FieldSelectors(FieldSelector[] selectors, SelectorNode singleNames, Dictionary<string, SelectorNode> byList)
    {
        _selectors = selectors;
        _singleNames = singleNames;
        _byList = byList;
    }

    internal int Count => _selectors.Length;

    internal FieldSelector this[int index] => _selectors[index];

    /// <summary>What to keep in each object entry of the top-level list of that name.</summary>
    internal SelectorNode EntryNodeFor(string list) =>
        _byList.TryGetValue(list, out SelectorNode node) ? node : _singleNames;

    /// <summary>The selectors, repeats (by text) dropped, first one kept, as the sidecar does.</summary>
    internal static FieldSelectors Of(IReadOnlyList<FieldSelector> given)
    {
        List<FieldSelector> distinct = new List<FieldSelector>(given.Count);
        HashSet<string> texts = new HashSet<string>(StringComparer.Ordinal);
        foreach (FieldSelector selector in given)
        {
            if (texts.Add(selector.Text))
            {
                distinct.Add(selector);
            }
        }

        FieldSelector[] selectors = distinct.ToArray();
        SelectorNode singleNames = new SelectorNode();
        AddSingleNames(singleNames, selectors);
        Dictionary<string, SelectorNode> byList = new Dictionary<string, SelectorNode>(StringComparer.Ordinal);
        for (int index = 0; index < selectors.Length; index++)
        {
            if (selectors[index] is FieldSelector.Path path)
            {
                if (!byList.TryGetValue(path.List, out SelectorNode node))
                {
                    node = new SelectorNode();
                    AddSingleNames(node, selectors);
                    byList.Add(path.List, node);
                }

                SelectorNode current = node;
                foreach (string key in path.Keys)
                {
                    current = current.Add(key);
                }

                current.EndHere(index);
            }
        }

        return new FieldSelectors(selectors, singleNames, byList);
    }

    private static void AddSingleNames(SelectorNode node, FieldSelector[] selectors)
    {
        for (int index = 0; index < selectors.Length; index++)
        {
            if (selectors[index] is FieldSelector.Name name)
            {
                node.Add(name.Key).EndHere(index);
            }
        }
    }
}
