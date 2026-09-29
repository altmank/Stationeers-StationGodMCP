#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects.Electrical;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The language a chip runs, and how the IC tools write, restart and step it. IC10 is the game's interpreter; Lua is
/// a StationeersLua chip (ItemIntegratedCircuitLua), which StationeersLua runs in place of IC10 by patching
/// ProgrammableChip.SetSourceCode, GetSourceCode and Execute.
/// </summary>
internal abstract class ChipProgram
{
    private static readonly ChipProgram Ic10Program = new Ic10();
    private static readonly ChipProgram LuaProgram = new Lua();

    internal abstract string Language { get; }

    internal static ChipProgram Of(ProgrammableChip chip) => LuaChips.IsLua(chip) ? LuaProgram : Ic10Program;

    /// <summary>The chip as the IC tools report it; logLines is how much of a Lua chip's print() log to include.</summary>
    internal IcChip Describe(IcTarget ic, ProgrammableChip chip, int logLines) =>
        new IcChip(Language, new ThingView(new ThingId(chip.ReferenceId), chip.PrefabName, chip.DisplayName),
            ic.Holder.GetSourceCode(), LuaState(chip, logLines));

    /// <summary>
    /// Replace the chip's source the way the IC editor's export does (ICircuitHolder.SetSourceCode); returns what was
    /// changed in the source on the way, or would be lost to the in-game editor.
    /// </summary>
    internal abstract List<SourceNote> Write(IcTarget ic, ProgrammableChip chip, string source);

    /// <summary>Run the chip's current source again from the start.</summary>
    internal abstract void Restart(IcTarget ic, ProgrammableChip chip);

    /// <summary>Refuses pausing and stepping where they mean nothing.</summary>
    internal abstract void RequireSteppable(string action);

    protected abstract LuaStateView? LuaState(ProgrammableChip chip, int logLines);

    /// <summary>
    /// IC10: the holder compiles the source line by line (ProgrammableChip.SetSourceCode); execution restarts at line 0
    /// and the chip is sent to clients. The chip gets the text it stores (Ic10Source): LF line ends, and the game's own
    /// ASCII conversion (AsciiString) applied first, so the program it runs is the one a save keeps.
    /// </summary>
    private sealed class Ic10 : ChipProgram
    {
        internal override string Language => IcChip.Ic10;

        internal override List<SourceNote> Write(IcTarget ic, ProgrammableChip chip, string source)
        {
            string stored = new AsciiString(Ic10Source.WithUnixLineEnds(source)).ToString();
            ic.Holder.SetSourceCode(stored);
            chip.LineNumber = 0d;
            chip.SendUpdate();
            return Ic10Source.Notes(source, stored);
        }

        internal override void Restart(IcTarget ic, ProgrammableChip chip) =>
            throw ApiErrors.Refused("not_a_lua_chip",
                "restart is for Lua chips; an IC10 chip restarts at line 0 when set_ic_source writes it.");

        internal override void RequireSteppable(string action)
        {
        }

        protected override LuaStateView? LuaState(ProgrammableChip chip, int logLines) => null;
    }

    /// <summary>
    /// Lua: StationeersLua's SetSourceCode prefix stores the source (gzip and base64 when that is shorter or it holds
    /// non-ASCII text, prefix "LZ:"), clears the chip's errors, and compiles it on a worker thread
    /// (LuaChipRuntimeManager.UpdateSourceServerSideAsync): the module-level code runs once, then tick(dt) every game
    /// tick. A source that failed before is not compiled again while it is unchanged (FailedSourceHashes), so a write
    /// and a restart first forget that failure (ClearFailedSourceHash), as the Lua debugger's restart does. There is
    /// no line to step or pause at.
    /// </summary>
    private sealed class Lua : ChipProgram
    {
        internal override string Language => IcChip.Lua;

        internal override List<SourceNote> Write(IcTarget ic, ProgrammableChip chip, string source)
        {
            LuaChips.RequireSourceSize(source);
            LuaChips.ForgetFailedSource(chip, required: false);
            ic.Holder.SetSourceCode(source);
            chip.SendUpdate();
            return new List<SourceNote>();
        }

        internal override void Restart(IcTarget ic, ProgrammableChip chip)
        {
            LuaChips.ForgetFailedSource(chip, required: true);
            ic.Holder.SetSourceCode(ic.Holder.GetSourceCode());
            chip.SendUpdate();
        }

        internal override void RequireSteppable(string action) =>
            throw ApiErrors.Refused("lua_chip_unsupported",
                $"A Lua chip cannot {action}: it runs a whole tick(dt) per game tick, with no line to stop at. Use " +
                "restart to run it again from the start, or set_ic_source to change it.");

        protected override LuaStateView? LuaState(ProgrammableChip chip, int logLines) =>
            LuaChips.State(chip, logLines);
    }
}
