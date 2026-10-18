#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace StationGodMCP.Pure.Lint;

/// <summary>One device operation in a chip's program: a batch read or write, or a write to a pin or an id.</summary>
internal sealed class ChipOperation
{
    internal ChipOperation(string op, int? prefabHash, string? prefab, int? nameHash, string? name, string? logic,
        bool writes, int? pin, long? referenceId, int? line = null)
    {
        Op = op;
        PrefabHash = prefabHash;
        Prefab = prefab;
        NameHash = nameHash;
        Name = name;
        Logic = logic;
        Writes = writes;
        Pin = pin;
        ReferenceId = referenceId;
        Line = line;
    }

    internal string Op { get; }

    internal int? PrefabHash { get; }

    internal string? Prefab { get; }

    internal int? NameHash { get; }

    internal string? Name { get; }

    internal string? Logic { get; }

    internal bool Writes { get; }

    /// <summary>d0..d5 for l and s; -1 for db (the housing itself).</summary>
    internal int? Pin { get; }

    /// <summary>The id ld and sd name.</summary>
    internal long? ReferenceId { get; }

    /// <summary>The source line it is on, 0-based as the chip counts lines.</summary>
    internal int? Line { get; }

    /// <summary>A slot read or write (ls, ss, lbs, sbs, lbns, sbns; Lua's _slot forms): Logic is a slot logic type.</summary>
    internal bool IsSlot => Op == "ls" || Op == "ss" || Op == "lbs" || Op == "sbs" || Op == "lbns" || Op == "sbns" ||
                            Op.IndexOf("_slot", StringComparison.Ordinal) >= 0;

    internal bool IsBatch => PrefabHash.HasValue || Prefab != null || (Op.StartsWith("lb", StringComparison.Ordinal) ||
                                                                         Op.StartsWith("sb", StringComparison.Ordinal) ||
                                                                         Op.StartsWith("batch", StringComparison.Ordinal));
}

/// <summary>
/// Reads the device operations out of IC10 and Lua chip programs, and the library functions built on them:
/// chip_batch_names (the batch operations a data network's chips make, with the prefab and name they name) and
/// chip_writes (some chip on a device's data networks writes a logic type to it).
/// </summary>
internal static class LintChipPrograms
{
    /// <summary>The parsed programs, kept by language and source until the source changes (ChipProgramCache).</summary>
    internal static readonly ChipProgramCache Programs = new ChipProgramCache();

    private static readonly Regex Hash = new Regex("^HASH\\(\\s*\"([^\"]*)\"\\s*\\)$", RegexOptions.CultureInvariant);

    // StationeersLua: batch_read(ph, lt, mode), batch_write(ph, lt, v), their _slot (slot, lst) and _name (name hash
    // second) forms, as globals (with or without logic_), under ic or ic.logic; prefab and name as hash("...") /
    // ic.hash("...") or numbers.
    private static readonly Regex LuaBatch = new Regex(
        "\\b(?:logic_)?(batch_(?:read|write)(?:_slot)?(?:_name)?)\\s*\\(([^()]*(?:\\([^()]*\\)[^()]*)*)\\)",
        RegexOptions.CultureInvariant);

    // StationeersLua's device writes: write(index, lt, v), write_id(id, lt, v) and their _slot forms (slot, lst), as
    // ic.write, ic.logic_write, ic.logic.write or the global logic_write. Never io.write or ic.disk.write.
    private static readonly Regex LuaWrite = new Regex(
        "(?<![\\w.])(?:ic\\.(?:logic\\.)?(?:logic_)?|logic_)(write(?:_slot)?(?:_id)?)\\s*\\(([^()]*(?:\\([^()]*\\)[^()]*)*)\\)",
        RegexOptions.CultureInvariant);

    /// <summary>Chip programs held parsed (server info runtime.lint_chip_programs).</summary>
    internal static int ParsedCount => Programs.Count;

    internal static void Register(LintLibrary library)
    {
        library
            .Add(new LintFunction("chip_batch_names", "(n: network) -> list<batch>",
                "The batch operations (IC10 lb, lbn, lbs, lbns, sb, sbn, sbs; Lua batch_read/batch_write and their _slot " +
                "and _name forms) the program chips in housings on this data network make, each with the prefab and name hashes " +
                "it names (and the names, where written as HASH(\"...\")). A device a by-name batch is meant to reach " +
                "must carry that name as its label.",
                ChipBatchNames, cached: true))
            .Add(new LintFunction("chip_writes", "(x: thing, logic: string) -> bool",
                "Some program chip on one of the thing's data networks writes the logic type to it: by batch to its " +
                "prefab (sb; sbn with its name hash), through a pin set to it (s dN), or by its id (sd).",
                ChipWrites, cached: true))
            .Add(new LintFunction("hash", "(s: string) -> number",
                "The game's HASH(\"...\"): the hash IC10 gives a prefab name or a label.",
                call => LintValue.Of(HashOf(call[0].AsString))));
    }

    /// <summary>Forgets every parsed program.</summary>
    internal static void Clear() => Programs.Clear();

    /// <summary>The game's HASH("..."): Unity's Animator.StringToHash, a CRC-32 of the UTF-8 bytes, as a signed int.</summary>
    internal static int HashOf(string text)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte value in Encoding.UTF8.GetBytes(text))
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return unchecked((int)~crc);
    }

    private static LintValue ChipBatchNames(LintCall call)
    {
        List<LintValue> batches = new List<LintValue>();
        int index = 0;
        foreach (LintValue chipValue in call[0].AsObject.Get(LintModel.Network["chips"]).AsList)
        {
            ILintObject chip = chipValue.AsObject;
            foreach (ChipOperation op in OperationsOf(chip))
            {
                if (!op.IsBatch)
                {
                    continue;
                }

                Dictionary<string, LintValue> values = new Dictionary<string, LintValue>(StringComparer.Ordinal)
                {
                    ["chip"] = chipValue,
                    ["op"] = LintValue.Of(op.Op),
                    ["prefab_hash"] = op.PrefabHash.HasValue ? LintValue.Of(op.PrefabHash.Value) : LintValue.Null,
                    ["prefab"] = LintValue.Of(op.Prefab),
                    ["name_hash"] = op.NameHash.HasValue ? LintValue.Of(op.NameHash.Value) : LintValue.Null,
                    ["name"] = LintValue.Of(op.Name),
                    ["logic"] = LintValue.Of(op.Logic),
                    ["writes"] = LintValue.Of(op.Writes)
                };
                batches.Add(LintValue.Of(new LintRecord(LintModel.Batch, $"{chip.Key}/batch{index++}", values,
                    describe: $"{op.Op} {op.Prefab ?? op.PrefabHash?.ToString(CultureInfo.InvariantCulture)} {op.Name}".Trim())));
            }
        }

        return LintValue.Of(batches);
    }

    private static LintValue ChipWrites(LintCall call)
    {
        ILintObject thing = call[0].AsObject;
        string logic = call[1].AsString;
        int prefabHash = (int)thing.Get(LintModel.Thing["prefab_hash"]).AsNumber;
        int nameHash = (int)thing.Get(LintModel.Thing["name_hash"]).AsNumber;
        long id = (long)thing.Get(LintModel.Thing["reference_id"]).AsNumber;
        foreach (LintValue network in thing.Get(LintModel.Thing["networks"]).AsList)
        {
            if (network.AsObject.Get(LintModel.Network["kind"]).AsString != "cable")
            {
                continue;
            }

            foreach (LintValue chipValue in network.AsObject.Get(LintModel.Network["chips"]).AsList)
            {
                ILintObject chip = chipValue.AsObject;
                IReadOnlyList<LintValue> pins = chip.Get(LintModel.Chip["pins"]).AsList;
                foreach (ChipOperation op in OperationsOf(chip))
                {
                    if (!op.Writes || !string.Equals(op.Logic, logic, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    bool hits = op.ReferenceId == id ||
                                (op.Pin is int pin && pin >= 0 && pin < pins.Count && !pins[pin].IsNull &&
                                 pins[pin].AsObject.Key == thing.Key) ||
                                (op.PrefabHash == prefabHash && (!op.NameHash.HasValue || op.NameHash == nameHash));
                    if (hits)
                    {
                        return LintValue.True;
                    }
                }
            }
        }

        return LintValue.False;
    }

    private static List<ChipOperation> OperationsOf(ILintObject chip) =>
        Programs.Of(chip.Get(LintModel.Chip["language"]).AsString, chip.Get(LintModel.Chip["source"]).AsString);

    /// <summary>
    /// The device operations of an IC10 program, each with its 0-based line. Defines resolve to their constants, and a
    /// pin through an alias to every device the alias is ever set to (an alias set twice yields an operation for
    /// each), following an alias of an alias. A device register (dr0) or an id held in a register is not resolved.
    /// </summary>
    internal static List<ChipOperation> Ic10(string source)
    {
        List<ChipOperation> operations = new List<ChipOperation>();
        Dictionary<string, string> defines = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, List<string>> aliases = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        List<List<string>> lines = new List<List<string>>();
        List<int> numbers = new List<int>();
        string[] raw = source.Replace("\r\n", "\n").Split('\n');
        for (int number = 0; number < raw.Length; number++)
        {
            List<string> tokens = Ic10Tokens(raw[number]);
            if (tokens.Count == 0)
            {
                continue;
            }

            if (tokens[0] == "define" && tokens.Count >= 3)
            {
                defines[tokens[1]] = tokens[2];
            }
            else if (tokens[0] == "alias" && tokens.Count >= 3)
            {
                if (!aliases.TryGetValue(tokens[1], out List<string> targets))
                {
                    aliases[tokens[1]] = targets = new List<string>(1);
                }

                if (!targets.Contains(tokens[2]))
                {
                    targets.Add(tokens[2]);
                }
            }

            lines.Add(tokens);
            numbers.Add(number);
        }

        for (int index = 0; index < lines.Count; index++)
        {
            Ic10Operations(lines[index], numbers[index], defines, aliases, operations);
        }

        return operations;
    }

    private static void Ic10Operations(List<string> t, int line, Dictionary<string, string> defines,
        Dictionary<string, List<string>> aliases, List<ChipOperation> into)
    {
        string op = t[0];
        string Arg(int index) => index + 1 < t.Count ? t[index + 1] : string.Empty;
        switch (op)
        {
            case "lb":
            case "lbs":
                into.Add(Batch(op, Arg(1), null, op == "lb" ? Arg(2) : Arg(3), false, defines, line));
                return;
            case "lbn":
            case "lbns":
                into.Add(Batch(op, Arg(1), Arg(2), op == "lbn" ? Arg(3) : Arg(4), false, defines, line));
                return;
            case "sb":
            case "sbs":
                into.Add(Batch(op, Arg(0), null, op == "sb" ? Arg(1) : Arg(2), true, defines, line));
                return;
            case "sbn":
            case "sbns":
                into.Add(Batch(op, Arg(0), Arg(1), op == "sbn" ? Arg(2) : Arg(3), true, defines, line));
                return;
            case "s":
            case "l":
            case "ls":
            case "ss":
                bool writes = op == "s" || op == "ss";
                string logic = op == "s" ? Arg(1) : op == "l" ? Arg(2) : op == "ss" ? Arg(2) : Arg(3);
                foreach (int pin in Pins(writes ? Arg(0) : Arg(1), aliases))
                {
                    into.Add(new ChipOperation(op, null, null, null, null, logic, writes, pin, null, line));
                }

                return;
            case "sd":
            case "ld":
                bool sets = op == "sd";
                (double? id, string? _) = Resolve(sets ? Arg(0) : Arg(1), defines);
                if (id.HasValue)
                {
                    into.Add(new ChipOperation(op, null, null, null, null, sets ? Arg(1) : Arg(2), sets, null,
                        (long)id.Value, line));
                }

                return;
        }
    }

    // The pins a device argument can name: db as -1, d0..d5 as 0..5, through aliases (every value each takes, an alias
    // of an alias followed a few levels); none for a device register or anything else.
    private static List<int> Pins(string device, Dictionary<string, List<string>> aliases)
    {
        List<int> pins = new List<int>(1);
        AddPins(device, aliases, pins, 0);
        return pins;
    }

    private static void AddPins(string device, Dictionary<string, List<string>> aliases, List<int> pins, int depth)
    {
        int? pin = device == "db" ? -1
            : device.Length == 2 && device[0] == 'd' && char.IsDigit(device[1]) ? device[1] - '0'
            : (int?)null;
        if (pin.HasValue)
        {
            if (!pins.Contains(pin.Value))
            {
                pins.Add(pin.Value);
            }

            return;
        }

        if (depth < 4 && aliases.TryGetValue(device, out List<string> targets))
        {
            foreach (string target in targets)
            {
                AddPins(target, aliases, pins, depth + 1);
            }
        }
    }

    private static ChipOperation Batch(string op, string type, string? name, string logic, bool writes,
        Dictionary<string, string> defines, int? line)
    {
        (double? prefabHash, string? prefab) = Resolve(type, defines);
        (double? nameHash, string? nameText) = name != null ? Resolve(name, defines) : (null, null);
        return new ChipOperation(op, prefabHash.HasValue ? (int)prefabHash.Value : (int?)null, prefab,
            nameHash.HasValue ? (int)nameHash.Value : (int?)null, nameText, logic, writes, null, null, line);
    }

    // A constant argument: HASH("..."), a number ($hex, %binary) or a define naming one; else (null, null).
    private static (double?, string?) Resolve(string token, Dictionary<string, string> defines)
    {
        for (int depth = 0; depth < 8 && defines.TryGetValue(token, out string defined); depth++)
        {
            token = defined;
        }

        Match hash = Hash.Match(token);
        if (hash.Success)
        {
            return (HashOf(hash.Groups[1].Value), hash.Groups[1].Value);
        }

        if (token.StartsWith("$", StringComparison.Ordinal) &&
            long.TryParse(token.Substring(1).Replace("_", string.Empty), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex))
        {
            return (unchecked((int)hex), null);
        }

        if (token.StartsWith("%", StringComparison.Ordinal))
        {
            try
            {
                return (unchecked((int)Convert.ToInt64(token.Substring(1).Replace("_", string.Empty), 2)), null);
            }
            catch (FormatException)
            {
                return (null, null);
            }
        }

        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            ? (number, null)
            : (null, null);
    }

    // Splits one IC10 line at white space, keeping HASH("a b") whole and dropping the comment.
    private static List<string> Ic10Tokens(string line)
    {
        List<string> tokens = new List<string>();
        StringBuilder current = new StringBuilder();
        bool quoted = false;
        foreach (char c in line)
        {
            if (!quoted && c == '#')
            {
                break;
            }

            if (c == '"')
            {
                quoted = !quoted;
            }

            if (!quoted && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    tokens.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            tokens.Add(current.ToString());
        }

        return tokens;
    }

    /// <summary>
    /// The device calls of a Lua program, each with its 0-based line: batch_read / batch_write and their _slot and
    /// _name forms, and write / write_id (and _slot) with a constant device index (a pin, 0 for d0) or id. A device
    /// index or id held in a variable is not resolved; nor is a logic type given as a number. A call inside a comment
    /// (-- to the line's end, or a --[[ ]] / --[==[ ]==] block) is no call.
    /// </summary>
    internal static List<ChipOperation> Lua(string source)
    {
        source = WithoutLuaComments(source);
        List<ChipOperation> operations = new List<ChipOperation>();
        List<int> lineStarts = LineStarts(source);
        foreach (Match match in LuaBatch.Matches(source))
        {
            string op = match.Groups[1].Value;
            List<string> args = LuaArguments(match.Groups[2].Value);
            bool named = op.IndexOf("_name", StringComparison.Ordinal) >= 0;
            bool slot = op.IndexOf("_slot", StringComparison.Ordinal) >= 0;
            (double? prefabHash, string? prefab) = LuaConstant(args.Count > 0 ? args[0] : string.Empty);
            (double? nameHash, string? name) = named && args.Count > 1 ? LuaConstant(args[1]) : (null, null);
            int logicAt = (named ? 2 : 1) + (slot ? 1 : 0);
            string? logic = args.Count > logicAt ? LuaLogic(args[logicAt]) : null;
            operations.Add(new ChipOperation(op, prefabHash.HasValue ? (int)prefabHash.Value : (int?)null, prefab,
                nameHash.HasValue ? (int)nameHash.Value : (int?)null, name, logic,
                op.IndexOf("write", StringComparison.Ordinal) >= 0, null, null, LineOf(lineStarts, match.Index)));
        }

        foreach (Match match in LuaWrite.Matches(source))
        {
            string op = match.Groups[1].Value;
            List<string> args = LuaArguments(match.Groups[2].Value);
            bool byId = op.EndsWith("_id", StringComparison.Ordinal);
            int logicAt = op.IndexOf("_slot", StringComparison.Ordinal) >= 0 ? 2 : 1;
            (double? device, string? _) = LuaConstant(args.Count > 0 ? args[0] : string.Empty);
            if (!device.HasValue || args.Count <= logicAt)
            {
                continue;
            }

            int? pin = !byId && device.Value >= 0 && device.Value <= 5 && device.Value == Math.Floor(device.Value)
                ? (int)device.Value
                : (int?)null;
            long? id = byId ? (long)device.Value : (long?)null;
            if (pin.HasValue || id.HasValue)
            {
                operations.Add(new ChipOperation(op, null, null, null, null, LuaLogic(args[logicAt]), true, pin, id,
                    LineOf(lineStarts, match.Index)));
            }
        }

        return operations;
    }

    // The source with every comment's characters but its line breaks turned to spaces, so indexes and lines stay put;
    // strings (quoted or long brackets) are kept whole, so a "--" inside one starts nothing.
    private static string WithoutLuaComments(string source)
    {
        char[] text = source.ToCharArray();
        int index = 0;
        while (index < text.Length)
        {
            char c = text[index];
            if (c == '"' || c == '\'')
            {
                index = QuotedEnd(text, index);
            }
            else if (c == '[' && LongBracketLevel(text, index) is int level)
            {
                index = LongBracketEnd(text, index, level);
            }
            else if (c == '-' && index + 1 < text.Length && text[index + 1] == '-')
            {
                int end = index + 2 < text.Length && text[index + 2] == '[' &&
                          LongBracketLevel(text, index + 2) is int block
                    ? LongBracketEnd(text, index + 2, block)
                    : LineEnd(text, index);
                for (int blank = index; blank < end; blank++)
                {
                    if (text[blank] != '\n' && text[blank] != '\r')
                    {
                        text[blank] = ' ';
                    }
                }

                index = end;
            }
            else
            {
                index++;
            }
        }

        return new string(text);
    }

    // Just past a quoted string's closing quote (a backslash escapes the next character); at its line's end when open.
    private static int QuotedEnd(char[] text, int open)
    {
        for (int index = open + 1; index < text.Length; index++)
        {
            if (text[index] == '\\')
            {
                index++;
            }
            else if (text[index] == text[open])
            {
                return index + 1;
            }
            else if (text[index] == '\n')
            {
                return index;
            }
        }

        return text.Length;
    }

    // The number of '=' in a long bracket opening at index ([[, [=[, [==[ ...); null when none opens there.
    private static int? LongBracketLevel(char[] text, int index)
    {
        int level = 0;
        int at = index + 1;
        while (at < text.Length && text[at] == '=')
        {
            level++;
            at++;
        }

        return at < text.Length && text[at] == '[' ? level : null;
    }

    // Just past the long bracket's close (], level '=', ]); the source's end when it never closes.
    private static int LongBracketEnd(char[] text, int open, int level)
    {
        for (int index = open + level + 2; index < text.Length; index++)
        {
            if (text[index] != ']')
            {
                continue;
            }

            int at = index + 1;
            int equals = 0;
            while (at < text.Length && text[at] == '=')
            {
                equals++;
                at++;
            }

            if (equals == level && at < text.Length && text[at] == ']')
            {
                return at + 1;
            }
        }

        return text.Length;
    }

    private static int LineEnd(char[] text, int index)
    {
        while (index < text.Length && text[index] != '\n')
        {
            index++;
        }

        return index;
    }

    // The index each line starts at, for LineOf.
    private static List<int> LineStarts(string source)
    {
        List<int> starts = new List<int> { 0 };
        for (int index = source.IndexOf('\n'); index >= 0; index = source.IndexOf('\n', index + 1))
        {
            starts.Add(index + 1);
        }

        return starts;
    }

    // The 0-based line holding the character at index.
    private static int LineOf(List<int> lineStarts, int index)
    {
        int found = lineStarts.BinarySearch(index);
        return found >= 0 ? found : ~found - 1;
    }

    private static List<string> LuaArguments(string text)
    {
        List<string> args = new List<string>();
        int depth = 0, start = 0;
        bool quoted = false;
        for (int index = 0; index < text.Length; index++)
        {
            char c = text[index];
            quoted ^= c == '"' || c == '\'';
            depth += quoted ? 0 : c == '(' ? 1 : c == ')' ? -1 : 0;
            if (c == ',' && depth == 0 && !quoted)
            {
                args.Add(text.Substring(start, index - start).Trim());
                start = index + 1;
            }
        }

        args.Add(text.Substring(start).Trim());
        return args;
    }

    private static readonly Regex LuaHash = new Regex("^(?:ic\\.)?hash\\(\\s*[\"']([^\"']*)[\"']\\s*\\)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static (double?, string?) LuaConstant(string text)
    {
        Match match = LuaHash.Match(text);
        if (match.Success)
        {
            return (HashOf(match.Groups[1].Value), match.Groups[1].Value);
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            ? (number, null)
            : (null, null);
    }

    // A logic type argument: "On", LogicType.On, ic.enums.LogicType.On ... reduced to its last name.
    private static string LuaLogic(string text)
    {
        string trimmed = text.Trim().Trim('"', '\'');
        int dot = trimmed.LastIndexOf('.');
        return dot >= 0 ? trimmed.Substring(dot + 1) : trimmed;
    }
}
