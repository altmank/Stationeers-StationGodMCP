#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure.Protocol;

/// <summary>A fixed list of Vocabulary templates a reply carries as a whole (a report's notes), worked out once.</summary>
internal sealed class TextList
{
    private readonly List<string> _plain;

    internal TextList(params string[] templates)
    {
        _plain = new List<string>(templates.Length);
        foreach (string template in templates)
        {
            _plain.Add(Vocabulary.Render(template));
        }
    }

    /// <summary>The texts.</summary>
    internal List<string> Said() => _plain;
}
