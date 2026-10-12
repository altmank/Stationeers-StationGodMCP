#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Controllers;

/// <summary>The device a controller query asks about: what a chip's pin, id or batch write matches it by.</summary>
internal sealed class ControlledDevice
{
    internal ControlledDevice(long referenceId, int prefabHash, int nameHash)
    {
        ReferenceId = referenceId;
        PrefabHash = prefabHash;
        NameHash = nameHash;
    }

    internal long ReferenceId { get; }

    internal int PrefabHash { get; }

    /// <summary>HASH of the device's name (ILogicable.GetNameHash), what sbn and lbn match.</summary>
    internal int NameHash { get; }
}

/// <summary>
/// Something that can write a device: a program chip (by a pin, an id or a batch), a logic writer, a batch writer or a
/// mirror. LogicType is the logic type it writes (a slot logic type when Slot), null for a mirror, which passes on
/// whatever is written to it. Via says how it reaches the device: db, d0..d5, id, the batch instruction or Lua call
/// (sb, sbn, batch_write_name...), output (a logic writer's output), prefab_hash (a batch writer), mirror.
/// </summary>
internal sealed class Controller
{
    internal Controller(string kind, long referenceId, string? prefabName, string? logicType, string via, int? line,
        bool slot)
    {
        Kind = kind;
        ReferenceId = referenceId;
        PrefabName = prefabName;
        LogicType = logicType;
        Via = via;
        Line = line;
        Slot = slot;
    }

    internal string Kind { get; }

    internal long ReferenceId { get; }

    internal string? PrefabName { get; }

    internal string? LogicType { get; }

    internal string Via { get; }

    /// <summary>The program line (0-based) of a chip's first such write; null for the other kinds.</summary>
    internal int? Line { get; }

    internal bool Slot { get; }

    /// <summary>The same controller writing the same thing the same way (another line of it, or another network).</summary>
    internal bool SameAs(Controller other) =>
        Kind == other.Kind && ReferenceId == other.ReferenceId && Via == other.Via && Slot == other.Slot &&
        string.Equals(LogicType, other.LogicType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Writes this logic type itself (not a slot logic type of that name; a mirror names none).</summary>
    internal bool Writes(string logicType) =>
        !Slot && LogicType != null && string.Equals(LogicType, logicType, StringComparison.OrdinalIgnoreCase);
}

/// <summary>The kinds of controller.</summary>
internal static class ControllerKinds
{
    internal const string Chip = "chip";
    internal const string LogicWriter = "logic_writer";
    internal const string BatchWriter = "batch_writer";
    internal const string Mirror = "mirror";
}

/// <summary>driven_by: the controllers that write the same logic type a write sets.</summary>
internal static class DrivenByMatch
{
    /// <summary>Those of the controllers that write this logic type; null when none does.</summary>
    internal static List<Controller>? Writing(List<Controller> controllers, string logicType)
    {
        List<Controller>? writing = null;
        foreach (Controller controller in controllers)
        {
            if (controller.Writes(logicType))
            {
                (writing ??= new List<Controller>(1)).Add(controller);
            }
        }

        return writing;
    }
}
