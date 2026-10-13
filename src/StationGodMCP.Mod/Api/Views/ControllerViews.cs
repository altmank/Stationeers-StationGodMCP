#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Controllers;

namespace StationGodMCP.Api.Views;

/// <summary>
/// One controller of a device: kind (chip, logic_writer, batch_writer, mirror), its reference id (a chip's is the
/// device holding the program: the IC Housing, the Console), what it writes (logic_type; null for a mirror), how
/// (via), and for a chip the 0-based line. slot is true only for a slot write (ss, sbs), whose logic_type is a slot
/// logic type.
/// </summary>
internal sealed class ControllerView
{
    internal ControllerView(Controller controller)
    {
        Kind = controller.Kind;
        ReferenceId = new ThingId(controller.ReferenceId);
        PrefabName = controller.PrefabName;
        LogicType = controller.LogicType;
        Via = controller.Via;
        Line = controller.Line;
        Slot = controller.Slot ? true : (bool?)null;
    }

    public string Kind { get; }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? LogicType { get; }

    public string Via { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public int? Line { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public bool? Slot { get; }

    /// <summary>The controllers as views; null when there are none (driven_by is then left out).</summary>
    internal static List<ControllerView>? ListOrNull(List<Controller>? controllers)
    {
        if (controllers == null || controllers.Count == 0)
        {
            return null;
        }

        List<ControllerView> views = new List<ControllerView>(controllers.Count);
        foreach (Controller controller in controllers)
        {
            views.Add(new ControllerView(controller));
        }

        return views;
    }
}

/// <summary>
/// inspect controllers: one device and what can write it, one entry per controller (ControllerGroups), count the
/// entries.
/// </summary>
internal sealed class DeviceControllersView
{
    internal DeviceControllersView(ThingId referenceId, string? displayName, List<ControllerEntryView> controllers)
    {
        ReferenceId = referenceId;
        DisplayName = displayName;
        Controllers = controllers;
    }

    public ThingId ReferenceId { get; }

    public string? DisplayName { get; }

    public List<ControllerEntryView> Controllers { get; }

    public int Count => Controllers.Count;
}

/// <summary>
/// One controller: its reference id and shown name, kind (chip, logic_writer, batch_writer, mirror), how it reaches the
/// device (db, d0..d5, id, a batch instruction, output, prefab_hash, mirror), the logic types it writes, a chip's
/// 0-based line, and whether it is on (null when it has no on state).
/// </summary>
internal sealed class ControllerEntryView
{
    internal ControllerEntryView(ControllerGroup group, string? displayName, bool? on)
    {
        ReferenceId = new ThingId(group.ReferenceId);
        DisplayName = displayName;
        Kind = group.Kind;
        How = group.How;
        LogicTypes = new List<string>(group.LogicTypes);
        Line = group.Line;
        On = on;
    }

    public ThingId ReferenceId { get; }

    public string? DisplayName { get; }

    public string Kind { get; }

    public string How { get; }

    public List<string> LogicTypes { get; }

    public int? Line { get; }

    public bool? On { get; }
}
