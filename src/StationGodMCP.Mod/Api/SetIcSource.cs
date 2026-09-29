#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects.Electrical;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// set_ic_source: replace the source of the chip in a circuit holder as the IC editor's export does
/// (ICircuitHolder.SetSourceCode). An IC10 chip compiles at once and restarts at line 0; a Lua chip compiles on a
/// worker thread (ChipProgram), so the reply may still say compiling. An IC10 source is stored with LF line ends and
/// as ASCII, and the reply's warnings say so, and when it passes the in-game editor's limits (Ic10Source). Sends the
/// chip to clients. Writes.
/// </summary>
internal static class SetIcSourceApi
{
    internal static IcSourceSetView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        IcTarget ic = Devices.RequireCircuitHolder(scope, args);
        string source = args.String("source");
        ProgrammableChip chip = ic.RequireChip();
        ChipProgram program = ChipProgram.Of(chip);
        List<SourceNote> notes = program.Write(ic, chip, source);
        return new IcSourceSetView(IcRuntime.PlaceOf(scope, ic), program.Describe(ic, chip, 0),
            IcRuntime.State(chip), notes);
    }
}
