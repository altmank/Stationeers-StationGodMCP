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

    internal IEnumerable<SelectorNode> Children =>
        _children != null ? _children.Values : (IEnumerable<SelectorNode>)Array.Empty<SelectorNode>();

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

    /// <summary>The node at first.rest..., made as needed.</summary>
    internal SelectorNode AddPath(string first, IReadOnlyList<string> rest)
    {
        SelectorNode current = Add(first);
        foreach (string key in rest)
        {
            current = current.Add(key);
        }

        return current;
    }

    /// <summary>Each single name and each whole path (its first name included) below this node, read from here.</summary>
    internal void AddEach(FieldSelector[] selectors)
    {
        for (int index = 0; index < selectors.Length; index++)
        {
            switch (selectors[index])
            {
                case FieldSelector.Name name:
                    Add(name.Key).EndHere(index);
                    break;
                case FieldSelector.Path path:
                    AddPath(path.List, path.Keys).EndHere(index);
                    break;
            }
        }
    }

    /// <summary>The selectors, repeats (by text) dropped, first one kept, as the sidecar does.</summary>
    internal static FieldSelector[] Distinct(IReadOnlyList<FieldSelector> given)
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

        return distinct.ToArray();
    }

    /// <summary>A new node holding what this node and other keep, below every key.</summary>
    internal SelectorNode With(SelectorNode other)
    {
        SelectorNode merged = new SelectorNode();
        merged.Absorb(this);
        merged.Absorb(other);
        return merged;
    }

    private void Absorb(SelectorNode other)
    {
        if (other._ends != null)
        {
            foreach (int selector in other._ends)
            {
                EndHere(selector);
            }
        }

        if (other._children != null)
        {
            foreach (KeyValuePair<string, SelectorNode> child in other._children)
            {
                Add(child.Key).Absorb(child.Value);
            }
        }
    }

    internal void EndHere(int selector)
    {
        Whole = true;
        _ends ??= new List<int>();
        _ends.Add(selector);
    }
}

/// <summary>
/// shape.fields parsed: the selectors in the order given, without repeats, the tree read from the reply's top (Top),
/// and per top-level key the node that says what to keep in each entry of that list, or inside that object. Every entry
/// of every top-level list, and every top-level object, is matched against the single names and against each path read
/// from the entry itself (occupant.prefab_name keeps prefab_name of each entry's occupant, at any depth); a path whose
/// first name is a top-level key is also read from inside that key's value (things.position.x, target.reference_id).
/// </summary>
internal sealed class FieldSelectors
{
    private readonly FieldSelector[] _selectors;
    private readonly SelectorNode _singleNames;
    private readonly Dictionary<string, SelectorNode> _byList;

    private FieldSelectors(FieldSelector[] selectors, SelectorNode singleNames, Dictionary<string, SelectorNode> byList,
        SelectorNode top)
    {
        _selectors = selectors;
        _singleNames = singleNames;
        _byList = byList;
        Top = top;
    }

    internal int Count => _selectors.Length;

    internal FieldSelector this[int index] => _selectors[index];

    /// <summary>
    /// Every selector read from the reply's top: a single name that is a top-level key keeps it whole, a path keeps
    /// only what it reaches inside it.
    /// </summary>
    internal SelectorNode Top { get; }

    /// <summary>What to keep in each object entry of the top-level list of that name, or inside the top-level object.</summary>
    internal SelectorNode EntryNodeFor(string list) =>
        _byList.TryGetValue(list, out SelectorNode node) ? node : _singleNames;

    /// <summary>The selectors, repeats (by text) dropped, first one kept, as the sidecar does.</summary>
    internal static FieldSelectors Of(IReadOnlyList<FieldSelector> given)
    {
        FieldSelector[] selectors = SelectorNode.Distinct(given);
        SelectorNode singleNames = new SelectorNode();
        singleNames.AddEach(selectors);
        Dictionary<string, SelectorNode> byList = new Dictionary<string, SelectorNode>(StringComparer.Ordinal);
        for (int index = 0; index < selectors.Length; index++)
        {
            if (selectors[index] is FieldSelector.Path path)
            {
                if (!byList.TryGetValue(path.List, out SelectorNode node))
                {
                    node = new SelectorNode();
                    node.AddEach(selectors);
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

        SelectorNode top = new SelectorNode();
        top.AddEach(selectors);
        return new FieldSelectors(selectors, singleNames, byList, top);
    }
}

/// <summary>
/// shape.omit parsed: paths read from the reply's root, each naming a key to leave out wherever the path reaches it
/// (source; runtime.registers; a list on the way applies the rest to each of its entries: members.position). A single
/// name is also left out of each top-level object and each entry of a top-level list (body leaves out target.body),
/// as fields reads single names. Selectors are kept in the order given, without repeats; Root is the tree the writer
/// walks.
/// </summary>
internal sealed class OmitSelectors
{
    private readonly FieldSelector[] _selectors;
    private readonly SelectorNode? _singleNames;
    private readonly Dictionary<string, SelectorNode> _below = new Dictionary<string, SelectorNode>(StringComparer.Ordinal);

    private OmitSelectors(FieldSelector[] selectors, SelectorNode root, SelectorNode? singleNames)
    {
        _selectors = selectors;
        Root = root;
        _singleNames = singleNames;
    }

    /// <summary>
    /// What omit leaves out inside the value of the top-level key: the paths through it and every single name; null
    /// when nothing.
    /// </summary>
    internal SelectorNode? Below(string key)
    {
        SelectorNode? paths = Root.Child(key);
        if (_singleNames == null)
        {
            return paths;
        }

        if (!_below.TryGetValue(key, out SelectorNode merged))
        {
            merged = paths != null ? paths.With(_singleNames) : _singleNames;
            _below.Add(key, merged);
        }

        return merged;
    }

    internal int Count => _selectors.Length;

    internal FieldSelector this[int index] => _selectors[index];

    internal SelectorNode Root { get; }

    internal static OmitSelectors Of(IReadOnlyList<FieldSelector> given)
    {
        FieldSelector[] selectors = SelectorNode.Distinct(given);
        SelectorNode root = new SelectorNode();
        root.AddEach(selectors);
        SelectorNode? singleNames = null;
        for (int index = 0; index < selectors.Length; index++)
        {
            if (selectors[index] is FieldSelector.Name name)
            {
                (singleNames ??= new SelectorNode()).Add(name.Key).EndHere(index);
            }
        }

        return new OmitSelectors(selectors, root, singleNames);
    }
}
