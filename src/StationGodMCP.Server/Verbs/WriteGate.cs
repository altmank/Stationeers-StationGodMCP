using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// Asked before every write, with the call's resolved targets (docs/architecture/hub.md, C# seams). The direct face's
/// gate is the direct write rule; a run's gate is its route and the seen set.
/// </summary>
public interface IWriteGate
{
    WriteDecision Decide(VerbCall call, WriteTargets targets);
}

/// <summary>The cap calls it with the text actually sent, so the seen set learns the ids shown.</summary>
public interface IShownText
{
    void Shown(string text);
}

/// <summary>What the gate decided: act, report instead, refuse with a row, or stop the run with rows and a fix.</summary>
public abstract class WriteDecision
{
    private WriteDecision()
    {
    }

    public sealed class Act : WriteDecision
    {
    }

    public sealed class Report : WriteDecision
    {
    }

    public sealed class Refuse : WriteDecision
    {
        public Refuse(JsonNode row)
        {
            Row = row;
        }

        public JsonNode Row { get; }
    }

    public sealed class Stop : WriteDecision
    {
        public Stop(JsonArray rows, string fix, string code = "unseen_target", string? message = null)
        {
            Rows = rows;
            Fix = fix;
            Code = code;
            Message = message;
        }

        public JsonArray Rows { get; }

        public string Fix { get; }

        /// <summary>The error code the stopped call answers (unseen_target unless the gate says otherwise).</summary>
        public string Code { get; }

        /// <summary>The gate's own message; null for the pipeline's.</summary>
        public string? Message { get; }
    }
}

/// <summary>A write call's targets: each x-target path given, with the ids or selector found there.</summary>
public sealed class WriteTargets
{
    public WriteTargets(IReadOnlyList<WriteTarget> targets)
    {
        Targets = targets;
    }

    public IReadOnlyList<WriteTarget> Targets { get; }
}

/// <summary>Where a target came from: ids the caller typed out, a ref's shown ids, or a selector (a spread).</summary>
public enum TargetSource
{
    Typed,
    Ref,
    Selector
}

/// <summary>
/// One target: its x-target path (or the selector option it came from) and role (acts-on, pays-from), the ids given
/// there (strings; a number id as its text, words such as "player" as written), the selector value when the path is a
/// selector (network, room, near, box) or holds an object, where it came from, and for a ref target the ref ("r7").
/// </summary>
public sealed class WriteTarget
{
    public WriteTarget(string path, string role, IReadOnlyList<string> ids, JsonNode? selector,
        TargetSource source = TargetSource.Typed, string? from = null)
    {
        Path = path;
        Role = role;
        Ids = ids;
        Selector = selector;
        Source = selector != null && source == TargetSource.Typed ? TargetSource.Selector : source;
        From = from;
    }

    public string Path { get; }

    public string Role { get; }

    public IReadOnlyList<string> Ids { get; }

    public JsonNode? Selector { get; }

    public TargetSource Source { get; }

    /// <summary>The ref a Ref target's ids came from.</summary>
    public string? From { get; }
}

/// <summary>A gate that lets every write act as sent.</summary>
internal sealed class PassThroughGate : IWriteGate
{
    internal static readonly PassThroughGate Instance = new();

    private static readonly WriteDecision.Act Acts = new();

    public WriteDecision Decide(VerbCall call, WriteTargets targets) => Acts;
}

/// <summary>
/// Reads a write what's x-target paths out of the options, marking the ids a ref gave (refs: option name to ref). A
/// spread path (role spread: a filter or selector that chooses what the write acts on) is a selector target with its
/// value, for the handler forms its when names; network, room, near and box are spreads wherever a what takes them.
/// </summary>
internal static class WriteTargetPaths
{
    internal static readonly string[] Selectors = ["network", "room", "near", "box"];

    internal static WriteTargets Resolve(WhatEntry what, JsonObject options, IReadOnlyList<RefUse>? refs = null)
    {
        WriteRules rules = what.Write!;
        List<WriteTarget> targets = [];
        foreach (TargetPath path in rules.Targets)
        {
            if (path.Role == "spread")
            {
                if (options[path.Path] is { } chosen && WhenApplies(what, path, options))
                {
                    targets.Add(new WriteTarget(path.Path, path.Role, [], chosen.DeepClone(), TargetSource.Selector));
                }

                continue;
            }

            List<string> ids = [];
            List<JsonNode> selectors = [];
            Collect(options, path.Path, ids, selectors);
            if (ids.Count == 0 && selectors.Count == 0)
            {
                continue;
            }

            JsonNode? selector = selectors.Count switch
            {
                0 => null,
                1 => selectors[0],
                _ => new JsonArray(selectors.ToArray())
            };
            // Ids a ref gave keep their ref, one target per ref; the rest of the path's ids were typed.
            foreach (RefUse use in refs ?? [])
            {
                if (use.Path != path.Path)
                {
                    continue;
                }

                List<string> fromRef = [];
                foreach (string id in use.Ids)
                {
                    if (ids.Remove(id))
                    {
                        fromRef.Add(id);
                    }
                }

                targets.Add(new WriteTarget(path.Path, path.Role, fromRef, null, TargetSource.Ref, use.Ref));
            }

            if (ids.Count > 0 || selector != null)
            {
                targets.Add(new WriteTarget(path.Path, path.Role, ids, selector));
            }
        }

        // The spreads every what names alike, where its x-target does not declare them.
        foreach (string spread in Selectors)
        {
            if (options[spread] is { } value && what.Options.ContainsKey(spread) && !Has(targets, spread) &&
                !Declares(rules, spread))
            {
                targets.Add(new WriteTarget(spread, "spread", [], value.DeepClone(), TargetSource.Selector));
            }
        }

        return new WriteTargets(targets);
    }

    private static bool Declares(WriteRules rules, string path)
    {
        foreach (TargetPath target in rules.Targets)
        {
            if (target.Path == path)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A target with when belongs to those handler forms: it applies unless the call gives an option that only other
    /// forms take (move item's prefab chooses items into a store; out of one, with source, it names what to take).
    /// </summary>
    internal static bool WhenApplies(WhatEntry what, TargetPath path, JsonObject options)
    {
        if (path.When.Count == 0)
        {
            return true;
        }

        foreach ((string name, JsonNode? value) in options)
        {
            if (value == null || name == path.Path)
            {
                continue;
            }

            bool inForms = false;
            bool elsewhere = false;
            foreach (HandlerEntry handler in what.Handlers)
            {
                bool takes = false;
                foreach (ArgEntry entry in handler.Args)
                {
                    takes |= entry.Option == name;
                }

                if (takes && path.When.Contains(handler.When ?? string.Empty))
                {
                    inForms = true;
                }
                else if (takes)
                {
                    elsewhere = true;
                }
            }

            if (elsewhere && !inForms)
            {
                return false;
            }
        }

        return true;
    }

    private static bool Has(List<WriteTarget> targets, string path)
    {
        foreach (WriteTarget target in targets)
        {
            if (target.Path == path)
            {
                return true;
            }
        }

        return false;
    }

    // A path is dotted names, where name[] walks every entry of a list ("writes[].id").
    private static void Collect(JsonNode? node, string path, List<string> ids, List<JsonNode> selectors)
    {
        if (node == null)
        {
            return;
        }

        if (path.Length == 0)
        {
            Leaf(node, ids, selectors);
            return;
        }

        int dot = path.IndexOf('.');
        string step = dot < 0 ? path : path[..dot];
        string rest = dot < 0 ? string.Empty : path[(dot + 1)..];
        bool each = step.EndsWith("[]", StringComparison.Ordinal);
        string name = each ? step[..^2] : step;
        if (node is not JsonObject container || container[name] is not { } child)
        {
            return;
        }

        if (rest.Length == 0 && Array.IndexOf(Selectors, name) >= 0)
        {
            selectors.Add(child.DeepClone());
            return;
        }

        if (each && child is JsonArray entries)
        {
            foreach (JsonNode? entry in entries)
            {
                Collect(entry, rest, ids, selectors);
            }

            return;
        }

        Collect(child, rest, ids, selectors);
    }

    private static void Leaf(JsonNode node, List<string> ids, List<JsonNode> selectors)
    {
        switch (node)
        {
            case JsonArray list:
                foreach (JsonNode? item in list)
                {
                    if (item != null)
                    {
                        Leaf(item, ids, selectors);
                    }
                }

                break;
            case JsonObject:
                selectors.Add(node.DeepClone());
                break;
            case JsonValue value when value.TryGetValue(out string? text):
                ids.Add(text);
                break;
            default:
                ids.Add(node.ToJsonString());
                break;
        }
    }
}

/// <summary>
/// The direct write rule (PLAN v9 2.0), for the direct and program faces: confirm: true acts. Without it, build, swap,
/// remove, trade, undo and every cheat report; so does any write whose targets the caller did not type out (a ref, a
/// network, a spread: near, box, room). Every other write acts on typed ids, move item included. Where a what's
/// catalogue route says otherwise, this rule wins.
/// </summary>
internal sealed class DirectWriteGate(VerbCatalogue catalogue) : IWriteGate
{
    private static readonly string[] ReportingVerbs = ["build", "swap", "remove", "trade", "undo"];

    private static readonly WriteDecision.Act Acts = new();

    private static readonly WriteDecision.Report Reports = new();

    public WriteDecision Decide(VerbCall call, WriteTargets targets)
    {
        if (JsonNodes.IsTrue(call.Args["confirm"]))
        {
            return Acts;
        }

        WhatEntry? what = catalogue.Verb(call.Verb)?.What(call.What);
        if (what?.Write != null && call.Args["confirm"] is { } confirm && !JsonNodes.IsTrue(confirm))
        {
            // confirm: false asks for the report, whatever the write.
            return Reports;
        }

        return what?.Write != null && ReportsUntilConfirm(what, call.Args, targets) ? Reports : Acts;
    }

    /// <summary>True when the rule makes this write report until confirm: true.</summary>
    internal static bool ReportsUntilConfirm(WhatEntry what, JsonObject args, WriteTargets targets)
    {
        if (Array.IndexOf(ReportingVerbs, what.Verb) >= 0 || IsCheat(what, args))
        {
            return true;
        }

        foreach (WriteTarget target in targets.Targets)
        {
            if (target.Source != TargetSource.Typed)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>A cheat: always, or when its option is given true (free: true, force: true).</summary>
    internal static bool IsCheat(WhatEntry what, JsonObject args)
    {
        string? cheat = what.Write?.Cheat;
        if (cheat == null)
        {
            return false;
        }

        if (cheat == "always")
        {
            return true;
        }

        int colon = cheat.IndexOf(':');
        return colon > 0 && JsonNodes.IsTrue(args[cheat[..colon].Trim()]);
    }
}

/// <summary>The gate for a run's call when its run gave none: every write reports.</summary>
internal sealed class ReportingGate : IWriteGate
{
    internal static readonly ReportingGate Instance = new();

    private static readonly WriteDecision.Report Reports = new();

    public WriteDecision Decide(VerbCall call, WriteTargets targets) => Reports;
}
