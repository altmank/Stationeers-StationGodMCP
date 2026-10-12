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

    private static readonly string[] Spreads = ["network", "room", "near", "radius", "box"];

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

        // whole alone on a reply that is not a live value is answered from the store.
        if (onlyWhole && JsonNodes.IsTrue(call.Args["whole"]) && stored.Verb != "read" && stored.Whole is JsonObject whole)
        {
            JsonObject kept = (JsonObject)whole.DeepClone();
            kept["ref"] = stored.Id;
            kept["from_store"] = true;
            return new RepeatOf.Answer(new VerbReply.Done(kept));
        }

        // A report repeated with confirm: true acts on exactly the targets it listed.
        if (stored.Report && JsonNodes.IsTrue(call.Args["confirm"]) && RefStore.IdsOf(stored, string.Empty) is { Count: > 0 } listed)
        {
            JsonArray ids = [];
            foreach (string id in listed)
            {
                ids.Add(id);
            }

            options["id"] = ids;
            foreach (string spread in Spreads)
            {
                options.Remove(spread);
            }
        }

        options.Remove("repeat");
        return new RepeatOf.Call(new VerbCall(call.Verb, call.What ?? stored.What, options, call.Face, call.Stream, call.Line)
        {
            Gate = call.Gate
        });
    }

    // A ref as a target (id: "r7", "r7.things.2", or refs in a list) is the ids its reply showed.
    private VerbReply.Failed? ResolveRefs(WhatEntry what, JsonObject args, Dictionary<string, string> refs,
        out JsonObject resolved)
    {
        resolved = args;
        JsonNode? id = args["id"];
        if (id == null || !what.Options.ContainsKey("id"))
        {
            return null;
        }

        bool anyRef = false;
        foreach (JsonNode? entry in id is JsonArray list ? list : new JsonArray(id.DeepClone()))
        {
            anyRef |= RefStore.Parse(JsonNodes.Text(entry)) != null;
        }

        if (!anyRef)
        {
            return null;
        }

        JsonArray ids = [];
        foreach (JsonNode? entry in id is JsonArray given ? given : new JsonArray(id.DeepClone()))
        {
            if (RefStore.Parse(JsonNodes.Text(entry)) is not { } parsed)
            {
                ids.Add(entry?.DeepClone());
                continue;
            }

            if (_refs.Get(parsed.Id) is not { } stored)
            {
                return OptionProblem.Refusal(what.Label, [new OptionProblem("id",
                    $"no ref '{parsed.Id}' (refs last until the world changes; a script's refs are its own)")]);
            }

            refs["id"] = parsed.Id;
            foreach (string shown in RefStore.IdsOf(stored, parsed.Path))
            {
                ids.Add(shown);
            }
        }

        resolved = (JsonObject)args.DeepClone();
        resolved["id"] = ids;
        return null;
    }

    /// <summary>
    /// A direct reply with refs and the cap: a report, a cut reply, or a list with has_more or 3 or more ids mints rN
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
        bool asked = JsonNodes.IsTrue(options["whole"]) || options["output_file"] != null || options["list_limits"] != null;
        string? main = report ? "targets" : ReplyCap.MainListOf(whole, what.MainList);
        bool lists = main != null && whole[main] is JsonArray entries &&
                     (entries.Count >= 3 || JsonNodes.IsTrue(whole["has_more"]));
        bool large = !report && !asked && ReplyCap.Bytes(whole) > ReplyCap.Threshold;
        if (!report && !lists && !large)
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
            WithoutAsks(call.Args), whole, shown, main, report));
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
