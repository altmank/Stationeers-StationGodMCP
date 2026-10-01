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
        bool writes, int? pin, long? referenceId)
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
    private static readonly Dictionary<string, List<ChipOperation>> Parsed = new Dictionary<string, List<ChipOperation>>(StringComparer.Ordinal);

    private static readonly Regex Hash = new Regex("^HASH\\(\\s*\"([^\"]*)\"\\s*\\)$", RegexOptions.CultureInvariant);

    // StationeersLua: batch_read(ph, lt, mode), batch_write(ph, lt, v), their _slot (slot, lst) and _name (name hash
    // second) forms, as globals or under ic.logic; prefab and name as hash("...") / ic.hash("...") or numbers.
    private static readonly Regex LuaBatch = new Regex(
        "\\b(batch_(?:read|write)(?:_slot)?(?:_name)?)\\s*\\(([^()]*(?:\\([^()]*\\)[^()]*)*)\\)",
        RegexOptions.CultureInvariant);

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
                "prefab (sb; sbn with its label), through a pin set to it (s dN), or by its id (sd).",
                ChipWrites, cached: true))
            .Add(new LintFunction("hash", "(s: string) -> number",
                "The game's HASH(\"...\"): the hash IC10 gives a prefab name or a label.",
                call => LintValue.Of(HashOf(call[0].AsString))));
    }

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
        LintValue label = thing.Get(LintModel.Thing["label"]);
        int nameHash = HashOf(label.IsNull ? thing.Get(LintModel.Thing["display_name"]).AsString : label.AsString);
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

    private static List<ChipOperation> OperationsOf(ILintObject chip)
    {
        string language = chip.Get(LintModel.Chip["language"]).AsString;
        string source = chip.Get(LintModel.Chip["source"]).AsString;
        string key = language + "\n" + source;
        lock (Parsed)
        {
            if (Parsed.TryGetValue(key, out List<ChipOperation> known))
            {
                return known;
            }
        }

        List<ChipOperation> operations = language == "lua" ? Lua(source) : Ic10(source);
        lock (Parsed)
        {
            if (Parsed.Count > 256)
            {
                Parsed.Clear();
            }

            Parsed[key] = operations;
        }

        return operations;
    }

    /// <summary>The device operations of an IC10 program, defines and aliases resolved.</summary>
    internal static List<ChipOperation> Ic10(string source)
    {
        List<ChipOperation> operations = new List<ChipOperation>();
        Dictionary<string, string> defines = new Dictionary<string, string>(StringComparer.Ordinal);
        Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        List<List<string>> lines = new List<List<string>>();
        foreach (string raw in source.Replace("\r\n", "\n").Split('\n'))
        {
            List<string> tokens = Ic10Tokens(raw);
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
                aliases[tokens[1]] = tokens[2];
            }

            lines.Add(tokens);
        }

        foreach (List<string> tokens in lines)
        {
            ChipOperation? op = Ic10Operation(tokens, defines, aliases);
            if (op != null)
            {
                operations.Add(op);
            }
        }

        return operations;
    }

    private static ChipOperation? Ic10Operation(List<string> t, Dictionary<string, string> defines,
        Dictionary<string, string> aliases)
    {
        string op = t[0];
        string Arg(int index) => index + 1 < t.Count ? t[index + 1] : string.Empty;
        switch (op)
        {
            case "lb":
            case "lbs":
                return Batch(op, Arg(1), null, op == "lb" ? Arg(2) : Arg(3), false, defines);
            case "lbn":
            case "lbns":
                return Batch(op, Arg(1), Arg(2), op == "lbn" ? Arg(3) : Arg(4), false, defines);
            case "sb":
            case "sbs":
                return Batch(op, Arg(0), null, op == "sb" ? Arg(1) : Arg(2), true, defines);
            case "sbn":
            case "sbns":
                return Batch(op, Arg(0), Arg(1), op == "sbn" ? Arg(2) : Arg(3), true, defines);
            case "s":
            case "l":
            case "ls":
            case "ss":
                bool writes = op == "s" || op == "ss";
                string device = writes ? Arg(0) : Arg(1);
                device = aliases.TryGetValue(device, out string pinned) ? pinned : device;
                int? pin = device == "db" ? -1
                    : device.Length == 2 && device[0] == 'd' && char.IsDigit(device[1]) ? device[1] - '0'
                    : (int?)null;
                string logic = op == "s" ? Arg(1) : op == "l" ? Arg(2) : op == "ss" ? Arg(2) : Arg(3);
                return pin.HasValue ? new ChipOperation(op, null, null, null, null, logic, writes, pin, null) : null;
            case "sd":
            case "ld":
                bool sets = op == "sd";
                (double? id, string? _) = Resolve(sets ? Arg(0) : Arg(1), defines);
                return id.HasValue
                    ? new ChipOperation(op, null, null, null, null, sets ? Arg(1) : Arg(2), sets, null, (long)id.Value)
                    : null;
            default:
                return null;
        }
    }

    private static ChipOperation Batch(string op, string type, string? name, string logic, bool writes,
        Dictionary<string, string> defines)
    {
        (double? prefabHash, string? prefab) = Resolve(type, defines);
        (double? nameHash, string? nameText) = name != null ? Resolve(name, defines) : (null, null);
        return new ChipOperation(op, prefabHash.HasValue ? (int)prefabHash.Value : (int?)null, prefab,
            nameHash.HasValue ? (int)nameHash.Value : (int?)null, nameText, logic, writes, null, null);
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

    /// <summary>The batch calls of a Lua program: batch_read / batch_write and their _slot and _name forms.</summary>
    internal static List<ChipOperation> Lua(string source)
    {
        List<ChipOperation> operations = new List<ChipOperation>();
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
                op.IndexOf("write", StringComparison.Ordinal) >= 0, null, null));
        }

        return operations;
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
