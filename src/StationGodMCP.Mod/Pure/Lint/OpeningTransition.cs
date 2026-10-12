#nullable enable

using System;

namespace StationGodMCP.Pure.Lint;

/// <summary>A device a logic write can open: one that lets gas out of a pipe network, or across to another.</summary>
internal enum OpeningDevice
{
    None,

    /// <summary>An active, powered or passive vent: it lets the network out into the room or the outdoors.</summary>
    Vent,

    /// <summary>A valve (thing.flow valve): it joins the networks on its two sides.</summary>
    Valve,

    /// <summary>A volume pump (thing.flow pump): it moves gas from one side to the other.</summary>
    Pump,

    /// <summary>A pressure or volume regulator (thing.flow regulator): it moves gas across.</summary>
    Regulator
}

/// <summary>The logic types a write can open a device with; None for every other type.</summary>
internal enum OpeningLogic
{
    None,
    On,
    Open,
    Mode,
    Setting
}

/// <summary>
/// Whether a logic write opens a device: its value, as the device takes it, against the device's current value turns it
/// on or opens it. The write rules lint only these writes, each time (a network that changes between two openings is
/// checked again at the second).
/// <para>
/// The device takes the value as the game's Device.SetLogicValue does: On and Open as (int)clamp(value, 0, 1), so 1
/// and above is on and anything below 1 is off; Mode as (int)clamp(value, 0, modes - 1). A write-only type (no value
/// before) counts as closed before.
/// </para>
/// <list type="table">
/// <item><term>On 0 to 1</term><description>every vent, valve, pump and regulator (an inward vent too: it pulls
/// the outdoors into the network).</description></item>
/// <item><term>Open 0 to 1</term><description>every vent, valve, pump and regulator with an Open state.
/// </description></item>
/// <item><term>Mode 1 to 0</term><description>an active or powered vent turned from inward (1) to outward (0).
/// </description></item>
/// <item><term>Mode changed</term><description>a pump with a direction (the turbo volume pump) turned to pump the other
/// way.</description></item>
/// <item><term>Setting 0 to above 0</term><description>a volume pump given a rate.</description></item>
/// </list>
/// Not openings: any write that leaves the device as it was or closes it; a valve's Mode; a regulator's Setting (a
/// back-pressure regulator opens as its setting falls, a pressure regulator as it rises, so neither side of the change
/// is an opening on its own); every other logic type, and every device that is not a vent, valve, pump or regulator.
/// </summary>
internal static class OpeningTransition
{
    /// <summary>Which device a thing is: whether it is a vent, and its thing.flow (pump, valve, regulator).</summary>
    internal static OpeningDevice DeviceOf(bool vent, string? flow) =>
        vent ? OpeningDevice.Vent
        : flow == "valve" ? OpeningDevice.Valve
        : flow == "pump" ? OpeningDevice.Pump
        : flow == "regulator" ? OpeningDevice.Regulator
        : OpeningDevice.None;

    /// <summary>The logic type by name, None for one that cannot open a device.</summary>
    internal static OpeningLogic LogicOf(string name) =>
        name == "On" ? OpeningLogic.On
        : name == "Open" ? OpeningLogic.Open
        : name == "Mode" ? OpeningLogic.Mode
        : name == "Setting" ? OpeningLogic.Setting
        : OpeningLogic.None;

    /// <summary>
    /// Whether writing requested over previous opens the device. modes: how many modes the device has (Mode is clamped
    /// to them); previous null for a type that does not read.
    /// </summary>
    internal static bool Opens(OpeningDevice device, OpeningLogic logic, double? previous, double requested,
        int modes = 2)
    {
        if (device == OpeningDevice.None || double.IsNaN(requested))
        {
            return false;
        }

        switch (logic)
        {
            case OpeningLogic.On:
            case OpeningLogic.Open:
                return Switch(requested) == 1 && (previous is double before ? Switch(before) == 0 : true);
            case OpeningLogic.Mode when device == OpeningDevice.Vent:
                // VentDirection: Outward 0, Inward 1.
                return ModeOf(requested, modes) == 0 && (previous is double was ? ModeOf(was, modes) != 0 : true);
            case OpeningLogic.Mode when device == OpeningDevice.Pump:
                return previous is double direction ? ModeOf(requested, modes) != ModeOf(direction, modes) : true;
            case OpeningLogic.Setting when device == OpeningDevice.Pump:
                return requested > 0 && (previous is double rate ? rate <= 0 : true);
            default:
                return false;
        }
    }

    /// <summary>Whether a thing of the lint model is a vent: a passive vent piece, an active or powered vent.</summary>
    internal static bool IsVent(ILintObject thing)
    {
        LintValue kind = thing.Get(LintModel.Thing["kind"]);
        if (kind.Kind == LintKind.String && kind.AsString == "passive_vent")
        {
            return true;
        }

        if (kind.Kind == LintKind.String && kind.AsString != "device")
        {
            return false;
        }

        foreach (LintValue name in thing.Get(LintModel.Thing["runtime_types"]).AsList)
        {
            if (name.Kind == LintKind.String &&
                (name.AsString == "ActiveVent" || name.AsString == "PoweredVent" || name.AsString == "PassiveVent"))
            {
                return true;
            }
        }

        return false;
    }

    // Device.SetLogicValue: (int)Mathf.Clamp((float)value, 0f, 1f).
    private static int Switch(double value) => (int)Math.Max(0.0, Math.Min(1.0, value));

    private static int ModeOf(double value, int modes) => (int)Math.Max(0.0, Math.Min(Math.Max(0, modes - 1), value));
}
