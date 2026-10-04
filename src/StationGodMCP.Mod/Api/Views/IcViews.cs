#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Api.Views;

/// <summary>
/// Which circuit holder, in which scope. ReferenceId is the device or worn item the scope reaches (an IC Housing, a
/// suit, the Console holding a Lua board, the tablet holding a Lua cartridge); Holder is the circuit holder itself,
/// the same thing for a housing or suit, the board or cartridge otherwise.
/// </summary>
internal sealed class IcPlace
{
    internal IcPlace(string gatewayId, ThingId referenceId, ThingView holder)
    {
        GatewayId = gatewayId;
        ReferenceId = referenceId;
        Holder = holder;
    }

    internal string GatewayId { get; }

    internal ThingId ReferenceId { get; }

    internal ThingView Holder { get; }
}

/// <summary>
/// The chip in a holder: its language (ic10, or lua for a StationeersLua chip), the chip itself, its source, and for a
/// Lua chip the runtime StationeersLua keeps for it.
/// </summary>
internal sealed class IcChip
{
    internal const string Ic10 = "ic10";
    internal const string Lua = "lua";

    internal IcChip(string language, ThingView chip, string? source, LuaStateView? lua)
        : this(language, chip, source, source?.Length ?? 0, lua)
    {
    }

    private IcChip(string language, ThingView chip, string? source, int sourceLength, LuaStateView? lua)
    {
        Language = language;
        Chip = chip;
        Source = source;
        SourceLength = sourceLength;
        LuaState = lua;
    }

    internal string Language { get; }

    internal ThingView Chip { get; }

    /// <summary>Null when the chip holds none, or when the caller left it out (WithoutSource).</summary>
    internal string? Source { get; }

    /// <summary>Characters in the chip's source, also when the source itself is left out.</summary>
    internal int SourceLength { get; }

    internal LuaStateView? LuaState { get; }

    /// <summary>The same chip without its source text (include_source false); source_length kept.</summary>
    internal IcChip WithoutSource() => new IcChip(Language, Chip, null, SourceLength, LuaState);
}

/// <summary>A chip's line and error state.</summary>
internal sealed class ChipState
{
    internal ChipState(double lineNumber, bool compilationError, string? errorLine, string? errorType,
        string? errorCode, CompileError? compileError = null)
    {
        LineNumber = lineNumber;
        CompilationError = compilationError;
        ErrorLine = errorLine;
        ErrorType = errorType;
        ErrorCode = Text.Plain(errorCode);
        CompileError = compileError;
    }

    internal double LineNumber { get; }

    internal bool CompilationError { get; }

    internal string? ErrorLine { get; }

    internal string? ErrorType { get; }

    /// <summary>ProgrammableChip.GetErrorCode as plain text: the game colours the error type with rich-text tags.</summary>
    internal string? ErrorCode { get; }

    /// <summary>Where compiling failed; null while the source compiles.</summary>
    internal CompileError? CompileError { get; }
}

/// <summary>
/// A compile error's line (0-based, as the chip counts lines) and type (ProgrammableChip.CompileErrorLineNumber and
/// CompileErrorType). error_line and error_type are the runtime error's; a compile error lives only here and in
/// error_code.
/// </summary>
internal sealed class CompileError
{
    internal CompileError(int line, string type)
    {
        Line = line;
        Type = type;
    }

    internal int Line { get; }

    internal string Type { get; }
}

/// <summary>get_ic_source: the chip's source and the line it is on.</summary>
internal sealed class IcSourceView
{
    internal IcSourceView(IcPlace place, IcChip chip, double lineNumber)
    {
        GatewayId = place.GatewayId;
        ReferenceId = place.ReferenceId;
        Source = chip.Source;
        LineNumber = lineNumber;
        Language = chip.Language;
        Holder = place.Holder;
        Chip = chip.Chip;
        SourceLength = chip.SourceLength;
        Lua = chip.LuaState;
    }

    public string GatewayId { get; }

    public ThingId ReferenceId { get; }

    public string? Source { get; }

    public double LineNumber { get; }

    /// <summary>ic10 or lua.</summary>
    public string Language { get; }

    public ThingView Holder { get; }

    public ThingView Chip { get; }

    /// <summary>Characters in source.</summary>
    public int SourceLength { get; }

    /// <summary>A Lua chip's runtime; null for an IC10 chip.</summary>
    public LuaStateView? Lua { get; }
}

/// <summary>set_ic_source: the source as the chip now holds it, and whether it compiled.</summary>
internal sealed class IcSourceSetView
{
    internal IcSourceSetView(IcPlace place, IcChip chip, ChipState state, List<SourceNote> warnings)
    {
        Warnings = warnings;
        GatewayId = place.GatewayId;
        ReferenceId = place.ReferenceId;
        Source = chip.Source;
        LineNumber = state.LineNumber;
        CompilationError = state.CompilationError;
        ErrorLine = state.ErrorLine;
        ErrorType = state.ErrorType;
        ErrorCode = state.ErrorCode;
        CompileErrorLine = state.CompileError?.Line;
        CompileErrorType = state.CompileError?.Type;
        Language = chip.Language;
        Holder = place.Holder;
        Chip = chip.Chip;
        SourceLength = chip.SourceLength;
        Lua = chip.LuaState;
    }

    public string GatewayId { get; }

    public ThingId ReferenceId { get; }

    public string? Source { get; }

    public double LineNumber { get; }

    public bool CompilationError { get; }

    public string? ErrorLine { get; }

    public string? ErrorType { get; }

    public string? ErrorCode { get; }

    /// <summary>The compile error's line (0-based); null without one.</summary>
    public int? CompileErrorLine { get; }

    /// <summary>The compile error's type, e.g. UnrecognisedInstruction; null without one.</summary>
    public string? CompileErrorType { get; }

    /// <summary>ic10 or lua.</summary>
    public string Language { get; }

    public ThingView Holder { get; }

    public ThingView Chip { get; }

    /// <summary>Characters in source.</summary>
    public int SourceLength { get; }

    /// <summary>A Lua chip's runtime right after the write; null for an IC10 chip.</summary>
    public LuaStateView? Lua { get; }

    /// <summary>What was changed in the source on the way in, or would be lost to the in-game editor.</summary>
    public List<SourceNote> Warnings { get; }
}

/// <summary>get_ic_status for a holder with no chip: its pins only.</summary>
internal sealed class IcNoChipView
{
    internal IcNoChipView(IcPlace place, List<IcPinView> pins)
    {
        GatewayId = place.GatewayId;
        ReferenceId = place.ReferenceId;
        Pins = pins;
        Holder = place.Holder;
    }

    public string GatewayId { get; }

    public ThingId ReferenceId { get; }

    public bool HasChip => false;

    public List<IcPinView> Pins { get; }

    public ThingView Holder { get; }
}

/// <summary>get_ic_status: the chip's source, state, holder, pins and runtime.</summary>
internal sealed class IcStatusView
{
    internal IcStatusView(IcPlace place, IcChip chip, ChipState state, IcHolderView housing, IcRuntimeParts parts)
    {
        GatewayId = place.GatewayId;
        ReferenceId = place.ReferenceId;
        Source = chip.Source;
        LineNumber = state.LineNumber;
        CompilationError = state.CompilationError;
        ErrorLine = state.ErrorLine;
        ErrorType = state.ErrorType;
        ErrorCode = state.ErrorCode;
        CompileErrorLine = state.CompileError?.Line;
        CompileErrorType = state.CompileError?.Type;
        Housing = housing;
        Pins = parts.Pins;
        Runtime = parts.Runtime;
        Language = chip.Language;
        Holder = place.Holder;
        Chip = chip.Chip;
        SourceLength = chip.SourceLength;
        Lua = chip.LuaState;
    }

    public string GatewayId { get; }

    public ThingId ReferenceId { get; }

    public bool HasChip => true;

    /// <summary>The chip's source with include_source true; null otherwise (source_length still counts it).</summary>
    public string? Source { get; }

    public double LineNumber { get; }

    public bool CompilationError { get; }

    public string? ErrorLine { get; }

    public string? ErrorType { get; }

    public string? ErrorCode { get; }

    /// <summary>The compile error's line (0-based); null without one.</summary>
    public int? CompileErrorLine { get; }

    /// <summary>The compile error's type, e.g. UnrecognisedInstruction; null without one.</summary>
    public string? CompileErrorType { get; }

    public IcHolderView Housing { get; }

    public List<IcPinView> Pins { get; }

    /// <summary>The IC10 runtime; null for a Lua chip, which has no registers, stack or line to report.</summary>
    public IcRuntimeView? Runtime { get; }

    /// <summary>ic10 or lua.</summary>
    public string Language { get; }

    public ThingView Holder { get; }

    public ThingView Chip { get; }

    /// <summary>Characters in source.</summary>
    public int SourceLength { get; }

    /// <summary>A Lua chip's runtime and log tail; null for an IC10 chip.</summary>
    public LuaStateView? Lua { get; }
}

internal sealed class IcRuntimeParts
{
    internal IcRuntimeParts(List<IcPinView> pins, IcRuntimeView? runtime)
    {
        Pins = pins;
        Runtime = runtime;
    }

    internal List<IcPinView> Pins { get; }

    internal IcRuntimeView? Runtime { get; }
}

internal sealed class IcHolderView
{
    internal IcHolderView(string kind, bool on, bool powered, bool operable)
    {
        Kind = kind;
        On = on;
        Powered = powered;
        Operable = operable;
    }

    /// <summary>
    /// ic_housing, worn_item, computer_board (a board in a Console or Computer), cartridge (in a tablet) or
    /// inserted_item (any other holder inside a device or worn item).
    /// </summary>
    public string Kind { get; }

    public bool On { get; }

    public bool Powered { get; }

    public bool Operable { get; }
}

/// <summary>A chip's registers, stack window, aliases, defines and jump tags, and where it will execute next.</summary>
internal sealed class IcRuntimeView
{
    internal IcRuntimeView(IcExecutionView execution, List<RegisterView> registers, SpecialRegisters special,
        StackWindowView stack, IcSymbols symbols)
    {
        Execution = execution;
        Registers = registers;
        RegisterCount = registers.Count;
        StackPointerRegister = special.StackPointerRegister;
        StackPointer = special.StackPointer;
        ReturnAddressRegister = special.ReturnAddressRegister;
        ReturnAddress = special.ReturnAddress;
        Stack = stack;
        Aliases = symbols.Aliases;
        Defines = symbols.Defines;
        JumpTags = symbols.JumpTags;
    }

    public IcExecutionView Execution { get; }

    public List<RegisterView> Registers { get; }

    public int RegisterCount { get; }

    public int StackPointerRegister { get; }

    public double? StackPointer { get; }

    public int ReturnAddressRegister { get; }

    public double? ReturnAddress { get; }

    public StackWindowView Stack { get; }

    public List<AliasView> Aliases { get; }

    public List<DefineView> Defines { get; }

    public List<JumpTagView> JumpTags { get; }
}

internal sealed class SpecialRegisters
{
    internal SpecialRegisters(int stackPointerRegister, double? stackPointer, int returnAddressRegister,
        double? returnAddress)
    {
        StackPointerRegister = stackPointerRegister;
        StackPointer = stackPointer;
        ReturnAddressRegister = returnAddressRegister;
        ReturnAddress = returnAddress;
    }

    internal int StackPointerRegister { get; }

    internal double? StackPointer { get; }

    internal int ReturnAddressRegister { get; }

    internal double? ReturnAddress { get; }
}

internal sealed class IcSymbols
{
    internal IcSymbols(List<AliasView> aliases, List<DefineView> defines, List<JumpTagView> jumpTags)
    {
        Aliases = aliases;
        Defines = defines;
        JumpTags = jumpTags;
    }

    internal List<AliasView> Aliases { get; }

    internal List<DefineView> Defines { get; }

    internal List<JumpTagView> JumpTags { get; }
}

internal sealed class IcExecutionView
{
    internal IcExecutionView(int nextAddress, string? currentLine, bool paused)
    {
        NextAddress = nextAddress;
        CurrentLine = currentLine;
        Paused = paused;
    }

    public int NextAddress { get; }

    public string? CurrentLine { get; }

    public bool Paused { get; }
}

internal sealed class RegisterView
{
    internal RegisterView(int index, List<string> aliases, double value)
    {
        Index = index;
        Aliases = aliases;
        Value = value;
    }

    public int Index { get; }

    public string Name => "r" + Index.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>sp and ra where this register is the stack pointer or return address.</summary>
    public List<string> Aliases { get; }

    public double Value { get; }
}

internal sealed class StackWindowView : ITruncatingView
{
    internal StackWindowView(int size, int startAddress, List<double> values)
    {
        Size = size;
        StartAddress = startAddress;
        Values = values;
        Count = values.Count;
    }

    public int Size { get; }

    public int StartAddress { get; }

    public List<double> Values { get; }

    public int Count { get; }

    public void NoteTruncations(string path)
    {
        if (Values.Count < Size)
        {
            Truncations.Note(path + "values", Values.Count, Size,
                $"pass stack_start (now {StartAddress}) and stack_count (max {Size})");
        }
    }
}

/// <summary>An alias the chip's alias instruction made: a register or device and its index.</summary>
internal sealed class AliasView
{
    internal AliasView(string? name, string? target, int? index)
    {
        Name = name;
        Target = target;
        Index = index;
    }

    public string? Name { get; }

    /// <summary>Register, Device or Network.</summary>
    public string? Target { get; }

    public int? Index { get; }
}

internal sealed class DefineView
{
    internal DefineView(string? name, double value)
    {
        Name = name;
        Value = value;
    }

    public string? Name { get; }

    public double Value { get; }
}

internal sealed class JumpTagView
{
    internal JumpTagView(string? name, int value)
    {
        Name = name;
        Value = value;
    }

    public string? Name { get; }

    /// <summary>The line the label stands on.</summary>
    public int Value { get; }
}

/// <summary>control_ic_execution: what was done, and the chip's state afterwards.</summary>
internal sealed class IcControlView
{
    internal IcControlView(IcPlace place, IcControlOutcome outcome, ChipState state, IcChip chip)
    {
        GatewayId = place.GatewayId;
        ReferenceId = place.ReferenceId;
        Action = outcome.Action;
        Paused = outcome.Paused;
        PreviousLine = outcome.PreviousLine;
        LineNumber = state.LineNumber;
        CompilationError = state.CompilationError;
        ErrorLine = state.ErrorLine;
        ErrorType = state.ErrorType;
        ErrorCode = state.ErrorCode;
        CompileErrorLine = state.CompileError?.Line;
        CompileErrorType = state.CompileError?.Type;
        Language = chip.Language;
        Holder = place.Holder;
        Chip = chip.Chip;
        Lua = chip.LuaState;
    }

    public string GatewayId { get; }

    public ThingId ReferenceId { get; }

    /// <summary>pause, step, resume, or restart (Lua).</summary>
    public string Action { get; }

    public bool Paused { get; }

    public double PreviousLine { get; }

    public double LineNumber { get; }

    public bool CompilationError { get; }

    public string? ErrorLine { get; }

    public string? ErrorType { get; }

    public string? ErrorCode { get; }

    /// <summary>The compile error's line (0-based); null without one.</summary>
    public int? CompileErrorLine { get; }

    /// <summary>The compile error's type, e.g. UnrecognisedInstruction; null without one.</summary>
    public string? CompileErrorType { get; }

    /// <summary>ic10 or lua.</summary>
    public string Language { get; }

    public ThingView Holder { get; }

    public ThingView Chip { get; }

    /// <summary>A Lua chip's runtime after the action; null for an IC10 chip.</summary>
    public LuaStateView? Lua { get; }
}

/// <summary>What control_ic_execution did: the action, the pause state after it, and the line before it.</summary>
internal readonly struct IcControlOutcome
{
    internal IcControlOutcome(string action, bool paused, double previousLine)
    {
        Action = action;
        Paused = paused;
        PreviousLine = previousLine;
    }

    internal string Action { get; }

    internal bool Paused { get; }

    internal double PreviousLine { get; }
}

/// <summary>resolve_ic_selectors: what the chip's pins reach, its aliases, and batch selectors for devices.</summary>
internal sealed class IcSelectorsView
{
    internal IcSelectorsView(IcPlace place, DeviceView db, List<PinTargetView> pins, List<AliasView> aliases,
        List<StableSelectorView> stableSelectors, int? batchDeviceCount)
    {
        BatchDeviceCount = batchDeviceCount;
        GatewayId = place.GatewayId;
        ReferenceId = place.ReferenceId;
        Db = db;
        Pins = pins;
        Aliases = aliases;
        StableSelectors = stableSelectors;
        StableSelectorCount = stableSelectors.Count;
    }

    public string GatewayId { get; }

    public ThingId ReferenceId { get; }

    /// <summary>The holder itself, which the chip reaches as db.</summary>
    public DeviceView Db { get; }

    public List<PinTargetView> Pins { get; }

    public List<AliasView> Aliases { get; }

    public List<StableSelectorView> StableSelectors { get; }

    public int StableSelectorCount { get; }

    /// <summary>How many devices the chip's batch instructions walk; null when the holder has no data network.</summary>
    public int? BatchDeviceCount { get; }
}

internal sealed class PinTargetView
{
    internal PinTargetView(int index, bool connected, DeviceView? target)
    {
        Selector = "d" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Connected = connected;
        Target = target;
    }

    public string Selector { get; }

    public bool Connected { get; }

    public DeviceView? Target { get; }
}

/// <summary>A device's prefab and name hash, the pair a chip's batch instructions (lbn, sbn) select by, counted over the
/// devices those instructions reach.</summary>
internal sealed class StableSelectorView
{
    internal StableSelectorView(ThingView device, int prefabHash, int? nameHash, int collisionCount, bool reachable)
    {
        Reachable = reachable;
        ReferenceId = device.ReferenceId;
        DisplayName = device.DisplayName;
        PrefabName = device.PrefabName;
        PrefabHash = prefabHash;
        NameHash = nameHash;
        CollisionCount = collisionCount;
        // The chip cannot read a device off its network: an lbn with the pair reads other devices or none.
        Ic10Example = reachable && nameHash.HasValue
            ? "lbn r0 " + prefabHash.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " +
              nameHash.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + " <LogicType> Average"
            : null;
    }

    public ThingId ReferenceId { get; }

    public string? DisplayName { get; }

    public string? PrefabName { get; }

    public int PrefabHash { get; }

    public int? NameHash { get; }

    public string SelectorKind => "prefab_and_name_hash";

    /// <summary>Whether the chip's batch instructions reach the device (it is on the holder's data network).</summary>
    public bool Reachable { get; }

    /// <summary>A batch instruction with this pair selects this device and no other.</summary>
    public bool Unique => Reachable && CollisionCount == 1;

    /// <summary>
    /// How many devices the chip reaches (on the holder's data network) have this pair. An unreachable device is not
    /// among them, so its count is of the other devices an lbn with its pair would read.
    /// </summary>
    public int CollisionCount { get; }

    /// <summary>An lbn with the pair; null when the device is unreachable or has no name hash.</summary>
    public string? Ic10Example { get; }
}
