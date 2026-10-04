#nullable enable

using Assets.Scripts.Objects.Electrical;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// get_ic_status: a circuit holder's chip (line, errors, language; its source only with include_source true), the
/// holder's power state, its pins (IcPins), for an IC10 chip its runtime state (IcRuntime) with a window of its stack
/// from stack_start, and for a Lua chip StationeersLua's runtime with the last log_lines of its print() log (an IC10
/// runtime means nothing there, so runtime is null). A holder with no chip reports its pins only. Read only.
/// </summary>
internal static class GetIcStatusApi
{
    private const int DefaultStackCount = ReplyDefaults.IcStackWindow;

    internal static object Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        IcTarget ic = Devices.RequireCircuitHolder(scope, args);
        int stackStart = args.OptionalInt("stack_start", 0, int.MaxValue) ?? 0;
        int stackCount = args.OptionalInt("stack_count", 0, DeviceMemory.MaximumValues) ?? DefaultStackCount;
        int logLines = args.OptionalInt("log_lines", 0, LuaChips.MaximumLogLines) ?? LuaChips.DefaultLogLines;
        bool includeSource = args.OptionalBool("include_source") ?? false;
        IcPlace place = IcRuntime.PlaceOf(scope, ic);
        ProgrammableChip? chip = ic.Chip();
        if (chip == null)
        {
            return new IcNoChipView(place, IcPins.Describe(ic));
        }

        ChipProgram program = ChipProgram.Of(chip);
        IcRuntimeParts parts = new IcRuntimeParts(IcPins.Describe(ic),
            program.Language == IcChip.Ic10 ? IcRuntime.Capture(chip, ic.HolderId, stackStart, stackCount) : null);
        IcChip described = program.Describe(ic, chip, logLines);
        return new IcStatusView(place, includeSource ? described : described.WithoutSource(), IcRuntime.State(chip),
            ic.StateView(), parts);
    }
}
