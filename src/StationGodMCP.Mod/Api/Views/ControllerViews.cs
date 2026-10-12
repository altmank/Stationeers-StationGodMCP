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

/// <summary>inspect_controllers: one device and what can write it; error instead when a listed id names no device.</summary>
internal sealed class ControlledDeviceView
{
    internal ControlledDeviceView(ThingId referenceId, string? prefabName, List<ControllerView> controllers)
    {
        ReferenceId = referenceId;
        PrefabName = prefabName;
        Controllers = controllers;
    }

    internal ControlledDeviceView(ThingId referenceId, ApiException error)
    {
        ReferenceId = referenceId;
        Controllers = new List<ControllerView>();
        Error = new ErrorView(error.Code, error.Message);
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public List<ControllerView> Controllers { get; }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ErrorView? Error { get; }
}

/// <summary>
/// inspect_controllers' result: devices, each asked id in order (with an empty controllers list when nothing writes
/// it), or for a network only its data devices that have a controller; checked counts the devices looked at.
/// </summary>
internal sealed class ControllersView
{
    internal ControllersView(ThingId? networkId, List<ControlledDeviceView> devices, int checkedCount)
    {
        NetworkId = networkId;
        Devices = devices;
        Checked = checkedCount;
    }

    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public ThingId? NetworkId { get; }

    public List<ControlledDeviceView> Devices { get; }

    public int Checked { get; }
}
