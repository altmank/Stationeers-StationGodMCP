using System.Text.Json.Nodes;

namespace StationGodMCP.Server;

/// <summary>
/// Refs and the cap on the direct face (hub.md Faces and Recording, refs and shaping topics): a reply that lists things,
/// a report or a cut reply mints rN; a reply over 12 KB is cut to about 4 KB; a ref names the ids its reply showed;
/// repeat: "rN" sends that call again with the given options replacing its own; cuts and re-asks are recorded.
/// </summary>
public sealed partial class VerbPipeline
{
    private const int ReAskCalls = 3;

    private readonly object _reAskGate = new();
    private readonly List<PendingCut> _pendingCuts = [];

    /// <summary>What a repeat gives: an answer of its own (from the store, or a refusal), or the call to send again.</summary>
    private abstract class RepeatOf
    {
        private RepeatOf()
        {
        }

        internal sealed class Answer(VerbReply reply) : RepeatOf
        {
            internal VerbReply Reply { get; } = reply;
        }

        internal sealed class Call(VerbCall repeated) : RepeatOf
        {
            internal VerbCall Repeated { get; } = repeated;
        }
    }

    // A cut reply watched for being asked again in the next three direct calls.
    private sealed class PendingCut(string reference, string verb, string? what, JsonObject options)
    {
        internal string Ref { get; } = reference;

        internal string Verb { get; } = verb;

        internal string? What { get; } = what;

        internal JsonObject Options { get; } = options;

        internal int Calls { get; set; }
    }

    private RepeatOf Repeat(VerbCall call)
    {
        string given = JsonNodes.Text(call.Args["repeat"]) ?? call.Args["repeat"]!.ToJsonString();
        StoredRef? stored = RefStore.Parse(given) is { Path.Length: 0 } parsed ? _refs.Get(parsed.Id) : null;
        if (stored == null)
        {
            return new RepeatOf.Answer(OptionProblem.Refusal(call.Verb, [new OptionProblem("repeat",
                $"no ref '{given}' (refs last until the world changes)")]));
        }

        if (stored.Verb != call.Verb)
        {
            return new RepeatOf.Answer(OptionProblem.Refusal(call.Verb, [new OptionProblem("repeat",
                $"{stored.Id} is a {stored.Verb} call: repeat it with {stored.Verb}")]));
        }

        JsonObject options = (JsonObject)stored.Options.DeepClone();
        bool onlyWhole = true;
        foreach ((string name, JsonNode? value) in call.Args)
        {
            if (name == "repeat")
            {
                continue;
            }

            onlyWhole &= name == "whole";
            options[name] = value?.DeepClone();
        }

        // whole alone on a reply that is not a live value is answered from the store; all of it is shown now.
        if (onlyWhole && JsonNodes.IsTrue(call.Args["whole"]) && stored.Verb != "read" && stored.Whole is JsonObject whole)
        {
            JsonObject kept = (JsonObject)whole.DeepClone();
            kept["ref"] = stored.Id;
            kept["from_store"] = true;
            stored.Shown = kept.DeepClone();
            return new RepeatOf.Answer(new VerbReply.Done(kept));
        }

        // A report repeated with confirm: true acts on what it listed: its options with refs as their ids, an expanded
        // spread as its ids (paths and roles as they were), a write-N undo as that undo; a spread the handler selects is
        // sent again, saying the selection may differ.
        string? note = null;
        if (stored.Report && JsonNodes.IsTrue(call.Args["confirm"]) && stored.Replay != null)
        {
            options = (JsonObject)stored.Replay.DeepClone();
            foreach ((string name, JsonNode? value) in call.Args)
            {
                if (name != "repeat")
                {
                    options[name] = value?.DeepClone();
                }
            }

            note = stored.ReplayNote;
        }

        options.Remove("repeat");
        return new RepeatOf.Call(new VerbCall(call.Verb, call.What ?? stored.What, options, call.Face, call.Stream, call.Line)
        {
            Gate = call.Gate,
            Note = note
        });
    }

    /// <summary>
    /// Refs as targets are the ids their replies showed. id takes refs and ids mixed, several refs among them, each id
    /// keeping its source (refs: one use per ref). A path that takes one id (to, source, within...) takes a ref that
    /// shows exactly one; a ref inside list entries (writes[].id, moves[].to) is refused, naming id as the way.
    /// </summary>
    private VerbReply.Failed? ResolveRefs(WhatEntry what, JsonObject args, List<RefUse> refs, out JsonObject resolved)
    {
        resolved = args;
        List<OptionProblem> problems = [];
        JsonObject? copy = null;
        foreach ((string name, JsonNode? value) in args)
        {
            if (value == null || !what.Options.ContainsKey(name) || !HoldsRef(value))
            {
                continue;
            }

            if (name != "id" && !IsTargetPath(what, name))
            {
                continue;
            }

            copy ??= (JsonObject)args.DeepClone();
            JsonArray ids = [];
            foreach (JsonNode? entry in value is JsonArray list ? list : new JsonArray(value.DeepClone()))
            {
                if (RefStore.Parse(JsonNodes.Text(entry)) is not { } parsed)
                {
                    ids.Add(entry?.DeepClone());
                    continue;
                }

                if (_refs.Get(parsed.Id) is not { } stored)
                {
                    problems.Add(new OptionProblem(name,
                        $"no ref '{parsed.Id}' (refs last until the world changes; a script's refs are its own)"));
                    continue;
                }

                List<string> shown = RefStore.IdsOf(stored, parsed.Path);
                refs.Add(new RefUse(name, parsed.Id, shown));
                foreach (string id in shown)
                {
                    ids.Add(id);
                }
            }

            if (name == "id")
            {
                copy[name] = ids;
            }
            else if (ids.Count == 1)
            {
                copy[name] = ids[0]?.DeepClone();
            }
            else
            {
                problems.Add(new OptionProblem(name, $"takes one id; the ref shows {ids.Count}"));
            }
        }

        foreach (string nested in NestedRefs(what, args))
        {
            problems.Add(new OptionProblem(nested, "a ref is not taken inside entries; give id: \"rN\" for its ids"));
        }

        if (problems.Count > 0)
        {
            return OptionProblem.Refusal(what.Label, problems);
        }

        resolved = copy ?? args;
        return null;
    }

    private static bool HoldsRef(JsonNode value)
    {
        foreach (JsonNode? entry in value is JsonArray list ? list : new JsonArray(value.DeepClone()))
        {
            if (RefStore.Parse(JsonNodes.Text(entry)) != null)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsTargetPath(WhatEntry what, string name)
    {
        foreach (TargetPath path in what.Write?.Targets ?? [])
        {
            if (path.Path == name && path.Role != "spread")
            {
                return true;
            }
        }

        return false;
    }

    // The x-target paths inside list entries ("writes[].id") that hold a ref.
    private static List<string> NestedRefs(WhatEntry what, JsonObject args)
    {
        List<string> found = [];
        foreach (TargetPath path in what.Write?.Targets ?? [])
        {
            int brackets = path.Path.IndexOf("[].", StringComparison.Ordinal);
            if (brackets < 0 || args[path.Path[..brackets]] is not JsonArray entries)
            {
                continue;
            }

            string key = path.Path[(brackets + 3)..];
            foreach (JsonNode? entry in entries)
            {
                if (entry?[key] is { } value && HoldsRef(value))
                {
                    found.Add(path.Path);
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>
    /// A direct reply with refs and the cap: a report, a plan, a cut reply, or a list with has_more or 3 or more ids mints rN
    /// holding the call and its whole reply. Over 12 KB a reply is cut to about 4 KB unless it is a report or the call
    /// asked for its size (whole, output_file, list_limits).
    /// </summary>
    private VerbReply Capped(VerbCall call, WhatEntry what, JsonObject options, VerbReply reply)
    {
        if (reply is not VerbReply.Done { Reply: JsonObject whole })
        {
            return reply;
        }

        bool report = JsonNodes.IsTrue(whole["report_only"]);
        JsonObject? replay = whole[ReplayKey] as JsonObject;
        string? replayNote = JsonNodes.Text(whole[ReplayNoteKey]);
        whole.Remove(ReplayKey);
        whole.Remove(ReplayNoteKey);
        if (report && ReplyCap.Bytes(whole) > ReplyCap.ReportLimit)
        {
            // A report is never cut: one too large for the client says how large, and how to narrow it.
            int count = whole["targets"] is JsonArray listed ? listed.Count : 0;
            return new VerbReply.Failed("report_too_large",
                $"The report lists {count} targets ({ReplyCap.Bytes(whole)} bytes), more than the client takes; " +
                "narrow the selection, or set rows for fewer rows per list.",
                data: new JsonObject { ["targets"] = count, ["bytes"] = ReplyCap.Bytes(whole) });
        }

        bool asked = JsonNodes.IsTrue(options["whole"]) || options["output_file"] != null || options["list_limits"] != null;
        string? main = report ? "targets" : ReplyCap.MainListOf(whole, what.MainList);
        bool lists = main != null && whole[main] is JsonArray entries &&
                     (entries.Count >= 3 || JsonNodes.IsTrue(whole["has_more"]));
        bool large = !report && !asked && ReplyCap.Bytes(whole) > ReplyCap.Threshold;
        bool plan = call.Verb == "plan";
        if (!report && !lists && !large && !plan)
        {
            return reply;
        }

        string id = _refs.NextId();
        int? pageStart = what.Options.ContainsKey("offset") ? (int)(JsonNodes.Number(options["offset"]) ?? 0) : null;
        CutReply? cut = large ? ReplyCap.Cut(whole, what.MainList, id, pageStart) : null;
        JsonObject shown;
        if (cut != null)
        {
            shown = cut.Shown;
            main = cut.List;
            _recorder?.RecordCut(_clock.ElapsedMilliseconds, id, call.Verb, call.What, cut.Bytes, cut.Sent);
            lock (_reAskGate)
            {
                _pendingCuts.Add(new PendingCut(id, call.Verb, call.What, WithoutAsks(call.Args)));
            }
        }
        else
        {
            shown = (JsonObject)whole.DeepClone();
            shown["ref"] = id;
        }

        _refs.Keep(new StoredRef(id, call.Verb, what.Name == VerbCatalogue.WhatlessKey ? null : what.Name,
            WithoutAsks(call.Args), whole, shown, main, report) { Replay = replay, ReplayNote = replayNote });
        return new VerbReply.Done(shown);
    }

    // The options a ref keeps for repeat and a re-ask compares: the call's own, without how it asked for the size.
    private static JsonObject WithoutAsks(JsonObject args)
    {
        JsonObject kept = new();
        foreach ((string name, JsonNode? value) in args)
        {
            if (name is not ("whole" or "repeat"))
            {
                kept[name] = value?.DeepClone();
            }
        }

        return kept;
    }

    // One of the next three direct calls asking a cut reply again: whole: true, repeat: "rN", or the same call.
    private void NoteReAsk(VerbCall call)
    {
        lock (_reAskGate)
        {
            for (int index = _pendingCuts.Count - 1; index >= 0; index--)
            {
                PendingCut cut = _pendingCuts[index];
                cut.Calls++;
                string? how = JsonNodes.Text(call.Args["repeat"]) == cut.Ref ? "repeat"
                    : call.Verb != cut.Verb || call.What != cut.What ? null
                    : JsonNodes.IsTrue(call.Args["whole"]) ? "whole"
                    : JsonNode.DeepEquals(WithoutAsks(call.Args), cut.Options) ? "same_call"
                    : null;
                if (how != null)
                {
                    _recorder?.RecordReAsk(_clock.ElapsedMilliseconds, cut.Ref, cut.Calls, how);
                    _pendingCuts.RemoveAt(index);
                }
                else if (cut.Calls >= ReAskCalls)
                {
                    _pendingCuts.RemoveAt(index);
                }
            }
        }
    }
}
