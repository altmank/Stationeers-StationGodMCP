#nullable enable

using Assets.Scripts.Objects.Electrical;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// get_ic_source: the source of the chip in a circuit holder (ICircuitHolder.GetSourceCode, which StationeersLua
/// returns decompressed for a Lua chip), the line it is on (ProgrammableChip.LineNumber, IC10 only) and its language.
/// Read only.
/// </summary>
internal static class GetIcSourceApi
{
    internal static IcSourceView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        IcTarget ic = Devices.RequireCircuitHolder(scope, args);
        ProgrammableChip chip = ic.RequireChip();
        IcChip described = ChipProgram.Of(chip).Describe(ic, chip, 0);
        return new IcSourceView(IcRuntime.PlaceOf(scope, ic), described, chip.LineNumber);
    }
}
