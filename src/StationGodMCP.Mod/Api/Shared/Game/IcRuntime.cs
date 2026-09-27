#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using Assets.Scripts.Objects.Electrical;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// A chip's runtime state, read from ProgrammableChip's private fields: _Registers, _Stack, _StackPointerIndex,
/// _ReturnAddressIndex, _NextAddr, _Aliases, _Defines, _JumpTags and _LinesOfCode. A missing field is game_changed.
/// </summary>
internal static class IcRuntime
{
    /// <summary>The chip's line and error state (ProgrammableChip.LineNumber, CompilationError...).</summary>
    internal static ChipState State(ProgrammableChip chip) =>
        new ChipState(chip.LineNumber, chip.CompilationError, chip.ErrorLineNumberString, chip.ErrorTypeString,
            chip.GetErrorCode());

    internal static IcPlace PlaceOf(DeviceScope scope, IcTarget ic) =>
        new IcPlace(scope.Id, new ThingId(ic.Target.ReferenceId), ic.HolderView);

    internal static IcRuntimeView Capture(ProgrammableChip chip, long holderId, int stackStart, int stackCount)
    {
        double[] registers = GameMembers.ChipRegisters.GetValue(chip) as double[] ?? Array.Empty<double>();
        double[] stack = GameMembers.ChipStack.GetValue(chip) as double[] ?? Array.Empty<double>();
        int stackPointer = (int)GameMembers.ChipStackPointerIndex.GetValue(chip)!;
        int returnAddress = (int)GameMembers.ChipReturnAddressIndex.GetValue(chip)!;
        int nextAddress = (int)GameMembers.ChipNextAddress.GetValue(chip)!;

        IcExecutionView execution = new IcExecutionView(nextAddress, CurrentLine(chip, nextAddress),
            IcExecutionController.IsPaused(holderId));
        SpecialRegisters special = new SpecialRegisters(stackPointer, RegisterValue(registers, stackPointer),
            returnAddress, RegisterValue(registers, returnAddress));
        IcSymbols symbols = new IcSymbols(Aliases(chip), Defines(chip), JumpTags(chip));
        return new IcRuntimeView(execution, Registers(registers, stackPointer, returnAddress), special,
            StackWindow(stack, stackStart, stackCount), symbols);
    }

    private static List<RegisterView> Registers(double[] registers, int stackPointer, int returnAddress)
    {
        List<RegisterView> views = new List<RegisterView>(registers.Length);
        for (int index = 0; index < registers.Length; index++)
        {
            List<string> aliases = new List<string>();
            if (index == stackPointer)
            {
                aliases.Add("sp");
            }

            if (index == returnAddress)
            {
                aliases.Add("ra");
            }

            views.Add(new RegisterView(index, aliases, registers[index]));
        }

        return views;
    }

    private static StackWindowView StackWindow(double[] stack, int stackStart, int stackCount)
    {
        int start = Math.Min(Math.Max(stackStart, 0), stack.Length);
        int count = Math.Min(Math.Max(stackCount, 0), stack.Length - start);
        List<double> values = new List<double>(count);
        for (int offset = 0; offset < count; offset++)
        {
            values.Add(stack[start + offset]);
        }

        return new StackWindowView(stack.Length, start, values);
    }

    /// <summary>The chip's aliases (ProgrammableChip._Aliases), by name ignoring case.</summary>
    internal static List<AliasView> Aliases(ProgrammableChip chip)
    {
        List<AliasView> aliases = new List<AliasView>();
        if (!(GameMembers.ChipAliases.GetValue(chip) is IDictionary dictionary))
        {
            return aliases;
        }

        foreach (DictionaryEntry entry in dictionary)
        {
            if (entry.Value != null)
            {
                object? target = GameMembers.ChipAliasTarget.GetValue(entry.Value);
                int? index = GameMembers.ChipAliasIndex.GetValue(entry.Value) as int?;
                aliases.Add(new AliasView(entry.Key?.ToString(), target?.ToString(), index));
            }
        }

        aliases.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name ?? string.Empty,
            b.Name ?? string.Empty));
        return aliases;
    }

    private static List<DefineView> Defines(ProgrammableChip chip)
    {
        List<DefineView> defines = new List<DefineView>();
        if (GameMembers.ChipDefines.GetValue(chip) is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                defines.Add(new DefineView(entry.Key?.ToString(), entry.Value is double value ? value : 0.0));
            }
        }

        defines.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name ?? string.Empty,
            b.Name ?? string.Empty));
        return defines;
    }

    private static List<JumpTagView> JumpTags(ProgrammableChip chip)
    {
        List<JumpTagView> tags = new List<JumpTagView>();
        if (GameMembers.ChipJumpTags.GetValue(chip) is IDictionary dictionary)
        {
            foreach (DictionaryEntry entry in dictionary)
            {
                tags.Add(new JumpTagView(entry.Key?.ToString(), entry.Value is int line ? line : 0));
            }
        }

        tags.Sort(static (a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name ?? string.Empty,
            b.Name ?? string.Empty));
        return tags;
    }

    // ProgrammableChip._LinesOfCode[_NextAddr]._LineOfCode.LineOfCode: the text of the line that runs next.
    private static string? CurrentLine(ProgrammableChip chip, int nextAddress)
    {
        if (!(GameMembers.ChipLinesOfCode.GetValue(chip) is IList lines) || nextAddress < 0 ||
            nextAddress >= lines.Count)
        {
            return null;
        }

        object? line = lines[nextAddress];
        return line == null ? null : GameMembers.ChipLineOfCodeText.GetValue(line) as string;
    }

    private static double? RegisterValue(double[] registers, int index) =>
        index >= 0 && index < registers.Length ? registers[index] : null;
}
