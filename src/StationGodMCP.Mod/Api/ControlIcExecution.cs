#nullable enable

using Assets.Scripts.Objects.Electrical;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// control_ic_execution: pause a chip, resume it, step it one instruction, or restart a Lua chip. Pausing skips the
/// holder's Execute (IcExecutionController's prefixes on CircuitHousing.Execute and SuitBase.Execute); a step pauses,
/// then runs ProgrammableChip.Execute(1) once and sends the chip to clients. Pause and step are IC10 only; restart is
/// Lua only (ChipProgram). Writes.
/// </summary>
internal static class ControlIcExecutionApi
{
    internal static IcControlView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        IcTarget ic = Devices.RequireCircuitHolder(scope, args);
        ProgrammableChip chip = ic.RequireChip();
        ChipProgram program = ChipProgram.Of(chip);
        string? action = args.OptionalString("action")?.Trim().ToLowerInvariant();
        double previousLine = chip.LineNumber;
        switch (action)
        {
            case "pause":
                program.RequireSteppable("pause");
                IcExecutionController.Pause(ic.HolderId);
                break;
            case "resume":
                IcExecutionController.Resume(ic.HolderId);
                break;
            case "step":
                program.RequireSteppable("step");
                Step(ic, chip);
                break;
            case "restart":
                program.Restart(ic, chip);
                break;
            default:
                throw ApiErrors.InvalidArgument("Argument 'action' must be pause, step, resume, or restart.");
        }

        IcControlOutcome outcome = new IcControlOutcome(action!, IcExecutionController.IsPaused(ic.HolderId),
            previousLine);
        return new IcControlView(IcRuntime.PlaceOf(scope, ic), outcome, IcRuntime.State(chip),
            program.Describe(ic, chip, 0));
    }

    private static void Step(IcTarget ic, ProgrammableChip chip)
    {
        IcExecutionController.Pause(ic.HolderId);
        if (chip.CompilationError)
        {
            throw ApiErrors.Refused("ic_compile_error",
                "The IC cannot be stepped while it has a compilation error.");
        }

        if (!ic.IsOperable)
        {
            throw ApiErrors.Refused("ic_not_operable", ic.Housing != null
                ? "The IC Housing must be on, powered, and operable before stepping."
                : "The worn item needs a charged battery before its IC can be stepped.");
        }

        chip.Execute(1);
        chip.SendUpdate();
    }
}
