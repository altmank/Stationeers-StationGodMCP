#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Tests.Scheduling;
using Xunit;
using static StationGodMCP.Tests.Paging.WalkBuildTests;

namespace StationGodMCP.Tests.Paging;

/// <summary>
/// find thing's walk (SPEC-NOTES 11; Api/Shared/WalkRequest), through its paging options and a page built as find thing
/// builds it (FramedPage over a WalkBuild), since find thing itself needs the game: the wire cursor "open" keeps a
/// cursor only when the first page has has_more, later pages come from the kept ids without a scan and re-check
/// existence only, a page without the cursor keeps nothing, and a cursor that is gone answers cursor_expired with its
/// id and position.
/// </summary>
public sealed class WalkRequestTests
{
    private static readonly CallContext Hub = new CallContext("c1", 2);

    [Fact]
    public void AFirstPageWithMoreKeepsACursorAndLaterPagesComeFromItWithoutAScan()
    {
        Find find = new Find(20);
        using CallContext.Scope scope = CallContext.Enter(Hub);

        JObject first = find.Call("""{"cursor": "open", "limit": 8}""");
        string cursor = (string)first["cursor_id"]!;
        JObject second = find.Call($$"""{"cursor": "{{cursor}}", "limit": 8}""");
        JObject last = find.Call($$"""{"cursor": "{{cursor}}", "limit": 8}""");

        Assert.Equal(1, find.Scans);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7, 8 }, Ids(first));
        Assert.True((bool)first["has_more"]!);
        Assert.Equal(new long[] { 9, 10, 11, 12, 13, 14, 15, 16 }, Ids(second));
        Assert.Equal(cursor, (string?)second["cursor_id"]);
        Assert.Equal(8, (int)second["offset"]!);
        Assert.Equal(20, (int)second["total"]!);
        Assert.Equal(0, (int)second["scanned"]!);
        Assert.True((bool)second["has_more"]!);
        Assert.Equal(new long[] { 17, 18, 19, 20 }, Ids(last));
        Assert.False((bool)last["has_more"]!);
        Assert.Null(last["cursor_id"]);
        Assert.Equal(0, find.Cursors.Count);
    }

    [Fact]
    public void AFirstPageThatHoldsEverythingKeepsNoCursor()
    {
        Find find = new Find(5);
        using CallContext.Scope scope = CallContext.Enter(Hub);

        JObject only = find.Call("""{"cursor": "open", "limit": 8}""");

        Assert.False((bool)only["has_more"]!);
        Assert.Null(only["cursor_id"]);
        Assert.Equal(0, find.Cursors.Count);
    }

    [Fact]
    public void WithoutTheCursorAPageIsCutAtItsOffsetAndKeepsNothing()
    {
        Find find = new Find(20);
        using CallContext.Scope scope = CallContext.Enter(Hub);

        JObject page = find.Call("""{"limit": 8, "offset": 8}""");

        Assert.Equal(9, Ids(page)[0]);
        Assert.Equal(8, (int)page["offset"]!);
        Assert.Null(page["cursor_id"]);
        Assert.Equal(0, find.Cursors.Count);
    }

    [Fact]
    public void AnOpenedWalkStartsAtItsOffsetAndKeepsWhatFollows()
    {
        Find find = new Find(20);
        using CallContext.Scope scope = CallContext.Enter(Hub);

        JObject first = find.Call("""{"cursor": "open", "limit": 4, "offset": 10}""");
        JObject second = find.Call($$"""{"cursor": "{{(string)first["cursor_id"]!}}", "limit": 4}""");

        Assert.Equal(new long[] { 11, 12, 13, 14 }, Ids(first));
        Assert.Equal(new long[] { 15, 16, 17, 18 }, Ids(second));
        Assert.Equal(14, (int)second["offset"]!);
        Assert.Equal(20, (int)second["total"]!);
    }

    [Fact]
    public void ALaterPageSkipsRemovedThingsAndChecksNothingElse()
    {
        Find find = new Find(12);
        using CallContext.Scope scope = CallContext.Enter(Hub);
        JObject first = find.Call("""{"cursor": "open", "limit": 4}""");
        find.Remove(5);
        find.Remove(7);
        find.Unmatch(6);

        JObject second = find.Call($$"""{"cursor": "{{(string)first["cursor_id"]!}}", "limit": 4, "order": "nearest"}""");

        Assert.Equal(new long[] { 6, 8, 9, 10 }, Ids(second));
        Assert.Equal(4, (int)second["offset"]!);
        Assert.True((bool)second["has_more"]!);
    }

    [Fact]
    public void ACursorOutsideACallsStreamIsRefused()
    {
        Find find = new Find(20);

        ApiException refused = Assert.Throws<ApiException>(() => find.Call("""{"cursor": "open"}"""));

        Assert.Equal("invalid_argument", refused.Code);
        Assert.Equal(0, find.Scans);
    }

    [Fact]
    public void AnExpiredCursorAnswersCursorExpiredWithItsIdAndPosition()
    {
        Find find = new Find(20);
        string id;
        using (CallContext.Enter(Hub))
        {
            id = (string)find.Call("""{"cursor": "open", "limit": 6}""")["cursor_id"]!;
        }

        find.Cursors.Ended(StreamEnd.Of("c1", 2));
        using CallContext.Scope scope = CallContext.Enter(Hub);
        ApiException expired = Assert.Throws<ApiException>(() => find.Call($$"""{"cursor": "{{id}}"}"""));

        Assert.Equal(WalkCursors.CursorExpiredCode, expired.Code);
        Assert.Equal("{\"cursor_id\":\"" + id + "\",\"position\":6}", WireCheck.New(expired.Details!));
        Assert.DoesNotContain("cursor true", expired.Message);
        ApiException unknown = Assert.Throws<ApiException>(() => find.Call("""{"cursor": "k999999"}"""));
        Assert.Equal("{\"cursor_id\":\"k999999\",\"position\":null}", WireCheck.New(unknown.Details!));
    }

    [Fact]
    public void ACursorFromAnotherStreamIsExpired()
    {
        Find find = new Find(20);
        string id;
        using (CallContext.Enter(Hub))
        {
            id = (string)find.Call("""{"cursor": "open"}""")["cursor_id"]!;
        }

        using CallContext.Scope scope = CallContext.Enter(new CallContext("c1", 9));
        ApiException expired = Assert.Throws<ApiException>(() => find.Call($$"""{"cursor": "{{id}}"}"""));

        Assert.Equal(WalkCursors.CursorExpiredCode, expired.Code);
    }

    [Fact]
    public void TheCursorIdIsAWireReplyKeyTheCatalogueDeclares()
    {
        System.Text.Json.Nodes.JsonObject reply =
            StationGodMCP.Tests.Verbs.VerbCatalogue.Verb("find")["whats"]!["thing"]!["reply"]!.AsObject();
        Find find = new Find(20);
        using CallContext.Scope scope = CallContext.Enter(Hub);

        JObject first = find.Call("""{"cursor": "open"}""");

        Assert.Equal("str | None", (string?)reply["x-wire"]!["cursor_id"]);
        List<string> declared = new List<string>();
        foreach (KeyValuePair<string, System.Text.Json.Nodes.JsonNode?> key in reply["variants"]![0]!["keys"]!.AsObject())
        {
            declared.Add(key.Key);
        }

        declared.Add("cursor_id");
        List<string> answered = new List<string>();
        foreach (JProperty key in first.Properties())
        {
            answered.Add(key.Name);
        }

        Assert.Equal(declared, answered);
    }

    private static long[] Ids(JObject reply) =>
        ((JArray)reply["things"]!).ToObject<List<JObject>>()!.ConvertAll(thing => long.Parse((string)thing["reference_id"]!))
            .ToArray();

    /// <summary>find thing over a fake world: ids 1..n, each matching, in id order; a call is one page as find thing takes it.</summary>
    private sealed class Find
    {
        private readonly Dictionary<long, FakeThing> _byId = new Dictionary<long, FakeThing>();
        private readonly List<FakeThing> _world = new List<FakeThing>();

        internal Find(int things)
        {
            for (long id = 1; id <= things; id++)
            {
                FakeThing thing = new FakeThing { Id = id, Prefab = "Thing", Matches = true };
                _world.Add(thing);
                _byId.Add(id, thing);
            }
        }

        internal WalkCursors Cursors { get; } = new WalkCursors();

        internal int Scans { get; private set; }

        internal void Remove(long id) => _byId[id].Alive = false;

        internal void Unmatch(long id) => _byId[id].Matches = false;

        internal JObject Call(string options)
        {
            Pure.Verbs.Paging paging = FramedPageTests.PagingOf(options);
            WalkRequest walk = WalkRequest.Of(paging, Cursors);
            FakeClock clock = new FakeClock();
            FramedPage<Api.Views.FoundThingView> page;
            if (walk.Continues)
            {
                page = FramedPage<Api.Views.FoundThingView>.Later(walk, paging.Limit!.Value, Live);
            }
            else
            {
                Scans++;
                page = FramedPage<Api.Views.FoundThingView>.Scan(walk, paging.Offset, paging.Limit!.Value,
                    new WalkBuild<FakeThing>(_world, new FakeFilter(clock, 0.0), ListOrder.ReferenceId), Live);
            }

            while (!page.Step(clock, clock.NowMs + 1000.0, 0.0))
            {
            }

            FramedResult<Api.Views.FoundThingView> result = page.Result();
            return Api.Views.FoundThings.Reply(result.Page, page.Scanned, null, result.CursorId);
        }

        private Api.Views.FoundThingView? Live(long id) =>
            _byId.TryGetValue(id, out FakeThing? thing) && thing.Alive
                ? new Api.Views.FoundThingView(new ThingView(new ThingId(id), thing.Prefab, "Thing"), null,
                    "Thing", "item", "Item", false, "ground", null, new List<Api.Views.HeldInView>(),
                    new PositionView(0, 0, 0), null, false, false)
                : null;
    }
}
