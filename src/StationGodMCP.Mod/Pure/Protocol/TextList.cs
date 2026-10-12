#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure.Protocol;

/// <summary>
/// A fixed list of Vocabulary templates a reply carries as a whole (a report's notes). Without a map it is the same list
/// every time, worked out once; a call that sent a map gets a new list in its own words.
/// </summary>
internal sealed class TextList
{
    private readonly string[] _templates;
    private readonly List<string> _plain;

    internal TextList(params string[] templates)
    {
        _templates = templates;
        _plain = new List<string>(templates.Length);
        foreach (string template in templates)
        {
            _plain.Add(Vocabulary.Render(template, null));
        }
    }

    /// <summary>The texts as the running call says its names.</summary>
    internal List<string> Said()
    {
        RenameMap? map = Vocabulary.Current;
        if (map == null)
        {
            return _plain;
        }

        List<string> said = new List<string>(_templates.Length);
        foreach (string template in _templates)
        {
            said.Add(Vocabulary.Render(template, map));
        }

        return said;
    }
}
