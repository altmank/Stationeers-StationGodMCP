#nullable enable

using Assets.Scripts.Objects.Electrical;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// get_ic_status: a circuit holder's chip (source, line, errors, language), the holder's power state, its pins
/// (IcPins), the chip's runtime state (IcRuntime) with a window of its stack from stack_start, and for a Lua chip
/// StationeersLua's runtime with the last log_lines of its print() log. A holder with no chip reports its pins only.
/// Read only.
/// </summary>
internal static class GetIcStatusApi
{
    private const int DefaultStackCount = 64;

    internal static object Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        IcTarget ic = Devices.RequireCircuitHolder(scope, args);
        int stackStart = args.OptionalInt("stack_start", 0, int.MaxValue) ?? 0;
        int stackCount = args.OptionalInt("stack_count", 0, DeviceMemory.MaximumValues) ?? DefaultStackCount;
        int logLines = args.OptionalInt("log_lines", 0, LuaChips.MaximumLogLines) ?? LuaChips.DefaultLogLines;
        IcPlace place = IcRuntime.PlaceOf(scope, ic);
        ProgrammableChip? chip = ic.Chip();
        if (chip == null)
        {
            return new IcNoChipView(place, IcPins.Describe(ic));
        }

        IcRuntimeParts parts = new IcRuntimeParts(IcPins.Describe(ic),
            IcRuntime.Capture(chip, ic.HolderId, stackStart, stackCount));
        return new IcStatusView(place, ChipProgram.Of(chip).Describe(ic, chip, logLines), IcRuntime.State(chip),
            ic.StateView(), parts);
    }
}
