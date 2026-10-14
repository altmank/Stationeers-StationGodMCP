#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure.Protocol;

/// <summary>A fixed list of texts a reply carries as a whole (a report's notes).</summary>
internal sealed class TextList
{
    private readonly List<string> _plain;

    internal TextList(params string[] texts)
    {
        _plain = new List<string>(texts.Length);
        foreach (string text in texts)
        {
            _plain.Add(text);
        }
    }

    /// <summary>The texts.</summary>
    internal List<string> Said() => _plain;
}
