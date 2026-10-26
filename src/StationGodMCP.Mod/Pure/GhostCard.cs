#nullable enable

using System;
using System.Collections.Generic;
using System.Text;

namespace StationGodMCP.Pure;

/// <summary>Where a ghost stands, as its card shows it at a glance.</summary>
internal enum GhostStanding
{
    /// <summary>A preview: drawn for a moment, on no board.</summary>
    Preview,

    /// <summary>On the board; Lattice has not judged it (or StationGod keeps the board).</summary>
    Planned,

    /// <summary>Lattice finds nothing in its way: it can be built now.</summary>
    Ready,

    /// <summary>Building has begun: part of it stands.</summary>
    Started,

    /// <summary>Waits for other ghosts to be built first.</summary>
    Waiting,

    /// <summary>Can be built, with warnings.</summary>
    Warned,

    /// <summary>Building it would fail now (the reason says why).</summary>
    Blocked,

    /// <summary>The world moved under it: what it stood for is gone or changed.</summary>
    Stale
}

/// <summary>
/// What the ghost tooltip shows of the ghost under the crosshair: its title (the label, else the piece), the piece, what
/// it asks for (build, remove, swap, finish), where it stands and why, its plan and who placed it, the next build steps,
/// the materials and tools still needed, its ports and Lattice's warnings. Made from Lattice's facts when Lattice keeps
/// the board (LatticeBoard.Card), else from StationGod's own board record alone. Lists are kept short for the panel.
/// </summary>
internal sealed class GhostCard
{
    internal const int ShownItems = 6;
    internal const int ShownSteps = 3;
    internal const int ShownWarnings = 3;

    internal GhostCard(long id, string piece, string? label)
    {
        Id = id;
        Piece = piece;
        Label = string.IsNullOrEmpty(label) ? null : label;
    }

    /// <summary>The board id; 0 for a preview.</summary>
    internal long Id { get; }

    /// <summary>The structure's shown name.</summary>
    internal string Piece { get; }

    internal string? Label { get; }

    /// <summary>The big line: the label when it has one, else the piece.</summary>
    internal string Title => Label ?? Piece;

    /// <summary>Build, Remove, Swap or Finish: what the ghost asks for.</summary>
    internal string Intent { get; set; } = "Build";

    internal GhostStanding Standing { get; set; } = GhostStanding.Planned;

    /// <summary>Why it stands so (blocked, stale, waiting for ...); null for none.</summary>
    internal string? Reason { get; set; }

    internal string? Plan { get; set; }

    internal string? PlacedBy { get; set; }

    /// <summary>The next build steps, in order, as short lines ("Place Kit (Pipe)", "Weld: 2 x Steel Sheets").</summary>
    internal List<string> Steps { get; } = new List<string>();

    /// <summary>How many steps are left in all (Steps holds the first few).</summary>
    internal int StepsLeft { get; set; }

    /// <summary>Materials still needed: shown name and count.</summary>
    internal List<(string Item, int Count)> Needed { get; } = new List<(string Item, int Count)>();

    internal List<string> Tools { get; } = new List<string>();

    /// <summary>The ports where it stands, by type (count each).</summary>
    internal List<(string Type, int Count)> Ports { get; } = new List<(string Type, int Count)>();

    internal List<string> Warnings { get; } = new List<string>();

    /// <summary>Lattice's facts could not all be read (Lattice still working them out); the card shows what it has.</summary>
    internal bool Partial { get; set; }

    /// <summary>The badge's word.</summary>
    internal static string Word(GhostStanding standing) => standing switch
    {
        GhostStanding.Preview => "PREVIEW",
        GhostStanding.Planned => "PLANNED",
        GhostStanding.Ready => "READY",
        GhostStanding.Started => "STARTED",
        GhostStanding.Waiting => "WAITING",
        GhostStanding.Warned => "WARNING",
        GhostStanding.Blocked => "BLOCKED",
        _ => "STALE"
    };

    /// <summary>The badge's colour (red, green, blue), each 0 to 1; each standing its own, readable on a dark panel.</summary>
    internal static (float Red, float Green, float Blue) Colour(GhostStanding standing) => standing switch
    {
        GhostStanding.Preview => (0.62f, 0.66f, 0.72f),
        GhostStanding.Planned => (0.36f, 0.68f, 1f),
        GhostStanding.Ready => (0.30f, 0.85f, 0.45f),
        GhostStanding.Started => (0.25f, 0.82f, 0.85f),
        GhostStanding.Waiting => (0.95f, 0.78f, 0.25f),
        GhostStanding.Warned => (1f, 0.62f, 0.20f),
        GhostStanding.Blocked => (1f, 0.35f, 0.35f),
        _ => (0.72f, 0.55f, 0.95f)
    };

    /// <summary>
    /// The standing from Lattice's findings for the ghost: any fail blocks, then waits, then warns; none (or only passes)
    /// is ready. A started or stale ghost keeps that standing (findings then go to Reason or Warnings).
    /// </summary>
    internal static GhostStanding FromFindings(GhostStanding state, bool fails, bool waits, bool warns, bool judged)
    {
        if (state is GhostStanding.Stale or GhostStanding.Preview)
        {
            return state;
        }

        if (fails)
        {
            return GhostStanding.Blocked;
        }

        if (state == GhostStanding.Started)
        {
            return state;
        }

        if (waits)
        {
            return GhostStanding.Waiting;
        }

        if (warns)
        {
            return GhostStanding.Warned;
        }

        return judged ? GhostStanding.Ready : state;
    }

    /// <summary>Adds an item to the needed list, adding up the same item.</summary>
    internal void Need(string item, int count)
    {
        for (int index = 0; index < Needed.Count; index++)
        {
            if (Needed[index].Item == item)
            {
                Needed[index] = (item, Needed[index].Count + count);
                return;
            }
        }

        Needed.Add((item, count));
    }

    /// <summary>Counts the ports by type, in the order first seen; a type is spelt as words ("PowerAndData" -> "Power and data").</summary>
    internal void CountPorts(IEnumerable<GhostPort> ports)
    {
        foreach (GhostPort port in ports)
        {
            string type = Words(port.Type);
            int at = Ports.FindIndex(entry => entry.Type == type);
            if (at < 0)
            {
                Ports.Add((type, 1));
            }
            else
            {
                Ports[at] = (type, Ports[at].Count + 1);
            }
        }
    }

    /// <summary>The ports line: "2 Pipe, 1 Power".</summary>
    internal string PortsLine()
    {
        List<string> parts = new List<string>(Ports.Count);
        foreach ((string type, int count) in Ports)
        {
            parts.Add($"{count} {type}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>The needed list as lines ("3 x Steel Sheets"), the rest folded into "and N more".</summary>
    internal List<string> NeededLines()
    {
        List<string> lines = new List<string>();
        for (int index = 0; index < Needed.Count && index < ShownItems; index++)
        {
            lines.Add($"{Needed[index].Count} x {Needed[index].Item}");
        }

        if (Needed.Count > ShownItems)
        {
            lines.Add($"and {Needed.Count - ShownItems} more");
        }

        return lines;
    }

    /// <summary>A game name in words: split before capitals and digits, first word kept, the rest lower case.</summary>
    internal static string Words(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return name;
        }

        StringBuilder words = new StringBuilder(name.Length + 8);
        for (int index = 0; index < name.Length; index++)
        {
            char letter = name[index];
            bool starts = index > 0 && char.IsUpper(letter) && !char.IsUpper(name[index - 1]);
            if (letter == '_')
            {
                words.Append(' ');
                continue;
            }

            if (starts)
            {
                words.Append(' ');
                words.Append(char.ToLowerInvariant(letter));
            }
            else
            {
                words.Append(letter);
            }
        }

        string text = words.ToString().Trim();
        return text.Length > 0 ? char.ToUpperInvariant(text[0]) + text.Substring(1) : text;
    }

    /// <summary>Cuts a long line to most characters with an ellipsis ("...").</summary>
    internal static string Short(string text, int most) =>
        text.Length <= most ? text : text.Substring(0, Math.Max(0, most - 3)).TrimEnd() + "...";
}
