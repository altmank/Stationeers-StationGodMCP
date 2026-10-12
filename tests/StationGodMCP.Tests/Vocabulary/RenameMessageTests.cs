#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Shaping;
using Xunit;

namespace StationGodMCP.Tests.VocabularyChecks;

/// <summary>
/// One refusal per message family, printed without a map (today's text, byte for byte) and with the call's map (the
/// caller's names): a required argument missing, a value of the wrong kind, a nested path, arguments that do not go
/// together, an unknown argument with its nearest name, the truncation advice, the errors.json pointer, and a handler
/// literal naming another tool.
/// </summary>
public sealed class RenameMessageTests
{
    private static readonly RenameMap VerbNames = VocabularyTests.Map("""
        {"args": {"reference_id": "id", "reference_ids": "id", "room_id": "id (a room)", "min": "box",
                  "max": "box", "near": "near", "radius_m": "radius", "limit": "count", "offset": "skip",
                  "job_id": "job", "items": "reads", "include_logic_types": "include", "dry_run": "report_only"},
         "tools": {"rooms": "survey {what: \"rooms\"}", "advance_build_state": "change {what: \"structure\"}",
                   "describe_device": "inspect {what: \"device\"}"}}
        """);

    private static string Refusal(RenameMap? map, Action act)
    {
        using (Vocabulary.Enter(map))
        {
            return Assert.Throws<ApiException>(act).Message;
        }
    }

    private static void Says(string today, string said, Action act)
    {
        Assert.Equal(today, Refusal(null, act));
        Assert.Equal(said, Refusal(VerbNames, act));
    }

    [Fact]
    public void ARequiredArgumentMissing() =>
        Says("Argument 'reference_id' is required.", "Argument 'id' is required.",
            () => new Args(new JObject()).ThingId("reference_id"));

    [Fact]
    public void AValueOfTheWrongKind() =>
        Says("Argument 'limit' must be an integer from 1 to 500.", "Argument 'count' must be an integer from 1 to 500.",
            () => new Args(JObject.Parse("""{"limit": "many"}""")).OptionalInt("limit", 1, 500));

    [Fact]
    public void ANestedObjectsPath() =>
        Says("Argument 'items[3].reference_id' is required.", "Argument 'reads[3].id' is required.",
            () => new Args(new JObject(), "items[3]").ThingId("reference_id"));

    [Fact]
    public void ArgumentsThatDoNotGoTogether() =>
        Says("Argument 'reference_ids' does not go with job_id.", "Argument 'id' does not go with job.",
            () => new Args(JObject.Parse("""{"job_id": "place-1", "reference_ids": ["4"]}"""))
                .Reject("{arg:job_id}", "reference_ids"));

    [Fact]
    public void EveryArgumentButTheFormsOwn() =>
        Says("Argument 'offset' does not go with a later page (cursor_id).",
            "Argument 'skip' does not go with a later page (cursor_id).",
            () => new Args(JObject.Parse("""{"cursor_id": "w1", "offset": 3}"""))
                .RejectAllBut("a later page ({arg:cursor_id})", "cursor_id", "limit"));

    [Fact]
    public void AnUnknownArgumentWithItsNearestName() =>
        Says("Unknown argument 'reference_idd'; did you mean 'reference_id'? describe_device takes: gateway_id, " +
             "include_logic_types, reference_id. Nothing was run.",
            "Unknown argument 'reference_idd'; did you mean 'id'? inspect {what: \"device\"} takes: gateway_id, " +
            "include, id. Nothing was run.",
            () => new DeclaredArguments(TestCatalogue.File.Value.Catalogue)
                .Check("describe_device", JObject.Parse("""{"reference_id": "1", "reference_idd": "1"}""")));

    [Fact]
    public void ACutListsWayToGetMore()
    {
        Assert.Equal(new[] { "pass limit (max 500) or offset 8", "pass max_checks (max 200)" }, Advice(null));
        Assert.Equal(new[] { "pass count (max 500) or skip 8", "pass max_checks (max 200)" }, Advice(VerbNames));
        Assert.Equal(new[] { "pass count (max 500) or skip 18; skip 10 skipped the first entries" },
            Advice(VerbNames, offset: 10));
    }

    private static List<string> Advice(RenameMap? map, int offset = 0)
    {
        using (Vocabulary.Enter(map))
        {
            Truncations.Begin();
            Truncations.Page("things", offset, 8, 412, 500);
            if (offset == 0)
            {
                Truncations.Capped("spots", 3, 9, "max_checks", 200);
            }

            return Truncations.Take().ConvertAll(static note => note.More);
        }
    }

    [Fact]
    public void AnErrorsPointerNamesItsToolAsTheCallerSaysIt()
    {
        Assert.Equal("advance_build_state", new ErrorView("already_complete", "x").See!.Tool);
        using (Vocabulary.Enter(VerbNames))
        {
            ErrorView said = new ErrorView("already_complete", "x");
            Assert.Equal("change {what: \"structure\"}", said.See!.Tool);
            Assert.Equal("refusals", said.See.Topic);
            Assert.Null(new ErrorView("ambiguous_axis", "x").See!.Tool);
        }
    }

    // 1,000 rounds of six reads: a read that allocated anything would cost at least 24 bytes a round. What is left is
    // the runtime's own bookkeeping around the measurement (64 bytes or less, the same with no reads at all).
    private const int Rounds = 1000;
    private const long Bookkeeping = 256;

    [Fact]
    public void ReadingArgumentsAllocatesNothingWithoutAMapOrWithOne()
    {
        Args args = new Args(JObject.Parse("""{"reference_id": "12", "name": "Pump", "wait": true, "radius_m": 4.5}"""));

        Assert.InRange(ReadAllocations(args, null, enter: false), 0, Bookkeeping);
        Assert.InRange(ReadAllocations(args, null), 0, Bookkeeping);
        Assert.InRange(ReadAllocations(args, VerbNames), 0, Bookkeeping);
    }

    private static long ReadAllocations(Args args, RenameMap? map, bool enter = true)
    {
        Read(args, 3);
        long before = GC.GetAllocatedBytesForCurrentThread();
        if (enter)
        {
            using (Vocabulary.Enter(map))
            {
                Read(args, Rounds);
            }
        }
        else
        {
            Read(args, Rounds);
        }

        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static readonly string[] PollOnly = { "job_id", "status" };

    private static void Read(Args args, int rounds)
    {
        for (int index = 0; index < rounds; index++)
        {
            args.Has("reference_id");
            args.OptionalThingId("reference_id");
            args.OptionalString("name");
            args.OptionalBool("wait");
            args.OptionalDouble("radius_m");
            args.Reject("{arg:job_id}", PollOnly);
        }
    }
}
