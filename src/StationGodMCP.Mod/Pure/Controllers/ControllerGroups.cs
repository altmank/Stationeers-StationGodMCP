#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Controllers;

/// <summary>
/// One controller of a device as inspect controllers lists it: a chip, logic writer, batch writer or mirror by its
/// reference id, how it reaches the device (Controller.Via), every logic type it writes that way, in the order first
/// written (none for a mirror, which passes on whatever is written to it), and a chip's first line that writes it.
/// </summary>
internal sealed class ControllerGroup
{
    internal ControllerGroup(string kind, long referenceId, string? prefabName, string how,
        IReadOnlyList<string> logicTypes, int? line)
    {
        Kind = kind;
        ReferenceId = referenceId;
        PrefabName = prefabName;
        How = how;
        LogicTypes = logicTypes;
        Line = line;
    }

    public string Kind { get; }

    public long ReferenceId { get; }

    public string? PrefabName { get; }

    public string How { get; }

    public IReadOnlyList<string> LogicTypes { get; }

    public int? Line { get; }

    /// <summary>The same group, its first line the one given.</summary>
    internal ControllerGroup AtLine(int line) => new ControllerGroup(Kind, ReferenceId, PrefabName, How, LogicTypes, line);
}

/// <summary>A device's controllers grouped per controller: one group per (kind, reference id, how), in first-seen order.</summary>
internal static class ControllerGroups
{
    internal static List<ControllerGroup> Of(IReadOnlyList<Controller> controllers)
    {
        List<ControllerGroup> groups = new List<ControllerGroup>();
        Dictionary<(string, long, string), int> at = new Dictionary<(string, long, string), int>();
        List<List<string>> types = new List<List<string>>();
        foreach (Controller controller in controllers)
        {
            (string, long, string) key = (controller.Kind, controller.ReferenceId, controller.Via);
            if (!at.TryGetValue(key, out int index))
            {
                index = groups.Count;
                at.Add(key, index);
                types.Add(new List<string>());
                groups.Add(new ControllerGroup(controller.Kind, controller.ReferenceId, controller.PrefabName,
                    controller.Via, types[index], controller.Line));
            }
            else if (controller.Line is { } line && (groups[index].Line is not { } first || line < first))
            {
                groups[index] = groups[index].AtLine(line);
            }

            if (controller.LogicType != null && !types[index].Exists(type =>
                    string.Equals(type, controller.LogicType, StringComparison.OrdinalIgnoreCase)))
            {
                types[index].Add(controller.LogicType);
            }
        }

        return groups;
    }
}
