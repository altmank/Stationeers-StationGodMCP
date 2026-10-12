#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Shaping;
using Xunit;

namespace StationGodMCP.Tests.Paging;

/// <summary>
/// The walk form of a paged find (Api/Shared/WalkRequest), through a find shaped as find_things is (scan, sort, page,
/// keep the rest; or a later page from the cursor), since find_things itself needs the game: which arguments go with
/// cursor and cursor_id, the first page keeping a cursor only when more follows, later pages coming from the kept ids
/// without a scan, cursor_expired's data, the truncation note, and FindThingsView's two walk keys.
/// </summary>
public sealed class WalkRequestTests
{
    private static readonly CallContext Hub = new CallContext("c1", 2);

    [Fact]
    public void AFirstPageWithMoreKeepsACursorAndLaterPagesComeFromItWithoutAScan()
    {
        Find find = new Find(20);
        using CallContext.Scope scope = CallContext.Enter(Hub);

        Found first = find.Call(new JObject { ["cursor"] = true, ["limit"] = 8 });
        Found second = find.Call(new JObject { ["cursor_id"] = first.CursorId, ["limit"] = 8 });
        Found last = find.Call(new JObject { ["cursor_id"] = first.CursorId, ["limit"] = 8 });

        Assert.Equal(1, find.Scans);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5, 6, 7, 8 }, first.Page.Items);
        Assert.NotNull(first.CursorId);
        Assert.Equal(new long[] { 9, 10, 11, 12, 13, 14, 15, 16 }, second.Page.Items);
        Assert.Equal(first.CursorId, second.CursorId);
        Assert.Equal(8, second.Page.Offset);
        Assert.Equal(20, second.Page.Total);
        Assert.True(second.Page.HasMore);
        Assert.Equal(new long[] { 17, 18, 19, 20 }, last.Page.Items);
        Assert.False(last.Page.HasMore);
        Assert.Null(last.CursorId);
        Assert.Equal(0, find.Cursors.Count);
    }

    [Fact]
    public void AFirstPageThatHoldsEverythingKeepsNoCursor()
    {
        Find find = new Find(5);
        using CallContext.Scope scope = CallContext.Enter(Hub);

        Found only = find.Call(new JObject { ["cursor"] = true, ["limit"] = 8 });

        Assert.False(only.Page.HasMore);
        Assert.Null(only.CursorId);
        Assert.Equal(0, find.Cursors.Count);
    }

    [Fact]
    public void WithoutCursorTheFindPagesAsBeforeAndKeepsNothing()
    {
        Find find = new Find(20);
        using CallContext.Scope scope = CallContext.Enter(Hub);

        Found page = find.Call(new JObject { ["limit"] = 8, ["offset"] = 8 });

        Assert.Equal(9, page.Page.Items[0]);
        Assert.Null(page.CursorId);
        Assert.Equal(0, find.Cursors.Count);
    }

    [Fact]
    public void ALaterPageSkipsRemovedThingsCountsThemAndChecksNothingElse()
    {
        Find find = new Find(12);
        using CallContext.Scope scope = CallContext.Enter(Hub);
        Found first = find.Call(new JObject { ["cursor"] = true, ["limit"] = 4 });
        find.World.Remove(5);
        find.World.Remove(7);
        find.World[6] = "renamed";

        Found second = find.Call(new JObject { ["cursor_id"] = first.CursorId, ["limit"] = 4 });

        Assert.Equal(new long[] { 6, 8, 9, 10 }, second.Page.Items);
        Assert.Equal(2, second.Skipped);
        Assert.Equal(4, second.Page.Offset);
        Assert.True(second.Page.HasMore);
    }

    [Fact]
    public void OffsetWithCursorIsRefused()
    {
        Find find = new Find(20);
        using CallContext.Scope scope = CallContext.Enter(Hub);

        ApiException refused = Assert.Throws<ApiException>(() => find.Call(new JObject { ["cursor"] = true, ["offset"] = 8 }));

        Assert.Equal("invalid_argument", refused.Code);
        Assert.Contains("offset", refused.Message);
        Assert.Equal(0, find.Scans);
    }

    [Theory]
    [InlineData("name_contains", "x")]
    [InlineData("order", "reference_id")]
    [InlineData("offset", 8)]
    [InlineData("cursor", true)]
    public void ALaterPageTakesOnlyCursorIdAndLimit(string name, object value)
    {
        Find find = new Find(20);
        using CallContext.Scope scope = CallContext.Enter(Hub);
        Found first = find.Call(new JObject { ["cursor"] = true });

        ApiException refused = Assert.Throws<ApiException>(() =>
            find.Call(new JObject { ["cursor_id"] = first.CursorId, [name] = JToken.FromObject(value) }));

        Assert.Equal("invalid_argument", refused.Code);
        Assert.Contains($"'{name}'", refused.Message);
        Assert.Contains("cursor_id", refused.Message);
    }

    [Fact]
    public void ACursorOutsideACallsContextIsRefused()
    {
        Find find = new Find(20);

        ApiException refused = Assert.Throws<ApiException>(() => find.Call(new JObject { ["cursor"] = true }));

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
            id = find.Call(new JObject { ["cursor"] = true, ["limit"] = 6 }).CursorId!;
        }

        find.Cursors.Ended(StreamEnd.Of("c1", 2));
        using CallContext.Scope scope = CallContext.Enter(Hub);
        ApiException expired = Assert.Throws<ApiException>(() => find.Call(new JObject { ["cursor_id"] = id }));

        Assert.Equal(WalkCursors.CursorExpiredCode, expired.Code);
        Assert.Equal("{\"cursor_id\":\"" + id + "\",\"position\":6}", WireCheck.New(expired.Details!));
        ApiException unknown = Assert.Throws<ApiException>(() => find.Call(new JObject { ["cursor_id"] = "k999999" }));
        Assert.Equal("{\"cursor_id\":\"k999999\",\"position\":null}", WireCheck.New(unknown.Details!));
    }

    [Fact]
    public void ACursorFromAnotherStreamIsExpired()
    {
        Find find = new Find(20);
        string id;
        using (CallContext.Enter(Hub))
        {
            id = find.Call(new JObject { ["cursor"] = true }).CursorId!;
        }

        using CallContext.Scope scope = CallContext.Enter(new CallContext("c1", 9));
        ApiException expired = Assert.Throws<ApiException>(() => find.Call(new JObject { ["cursor_id"] = id }));

        Assert.Equal(WalkCursors.CursorExpiredCode, expired.Code);
    }

    [Fact]
    public void TheTruncationNoteNamesTheCursorInsteadOfAnOffset()
    {
        Find find = new Find(20);
        using CallContext.Scope scope = CallContext.Enter(Hub);
        Truncations.Begin();
        Found first = find.Call(new JObject { ["cursor"] = true, ["limit"] = 8 });
        Truncation note = Assert.Single(Truncations.Take());

        Assert.Equal("things", note.List);
        Assert.Equal(8, note.Returned);
        Assert.Equal(20, note.Total);
        Assert.Contains("cursor_id " + first.CursorId, note.More);
        Assert.DoesNotContain("offset", note.More);

        Truncations.Begin();
        find.Call(new JObject { ["cursor_id"] = first.CursorId, ["limit"] = 8 });
        Assert.Contains("cursor_id " + first.CursorId, Assert.Single(Truncations.Take()).More);
        Truncations.Begin();
        find.Call(new JObject { ["cursor_id"] = first.CursorId, ["limit"] = 8 });
        Assert.Empty(Truncations.Take());
    }

    [Fact]
    public void FindThingsViewWritesCursorIdAndSkippedOnlyWhenAWalkHasThem()
    {
        Slice<FoundThingView> page = Slice<FoundThingView>.Walked(new List<FoundThingView>(), 8, 8, 20, true);

        string walked = WireCheck.New(new FindThingsView(page, 0, null, "k3", 2));
        string plain = WireCheck.New(new FindThingsView(page, 0, null));

        Assert.Contains("\"cursor_id\":\"k3\"", walked);
        Assert.Contains("\"skipped\":2", walked);
        Assert.Contains("\"has_more\":true", walked);
        Assert.DoesNotContain("cursor_id", plain);
        Assert.DoesNotContain("skipped", plain);
    }

    [Fact]
    public void TheCatalogueTakesTheWalkArgumentsAndALaterPageCostsLessThanAScan()
    {
        StationGodMCP.Pure.Catalogue.Catalogue catalogue = StationGodMCP.Pure.Catalogue.Catalogue.Load(
            System.IO.File.ReadAllText(StationGodMCP.Tests.CatalogueChecks.CatalogueFiles.AssembledPath));
        Assert.True(catalogue.TryGet("find_things", out StationGodMCP.Pure.Catalogue.CatalogueMethod? found));
        StationGodMCP.Pure.Catalogue.CatalogueMethod method = found!;

        Assert.Empty(method.Check(JObject.Parse("""{"cursor":true,"limit":500}""")));
        Assert.Empty(method.Check(JObject.Parse("""{"cursor_id":"k12","limit":500}""")));
        Assert.NotEmpty(method.Check(JObject.Parse("""{"cursor_id":12}""")));
        Assert.Equal(StationGodMCP.Pure.Catalogue.CostClass.World, method.CostAt(JObject.Parse("""{"cursor":true}""")).Cost);
        Assert.Equal(StationGodMCP.Pure.Catalogue.CostClass.Bounded,
            method.CostAt(JObject.Parse("""{"cursor_id":"k12"}""")).Cost);
    }

    private sealed class Found
    {
        internal Found(Slice<long> page, string? cursorId, int? skipped)
        {
            Page = page;
            CursorId = cursorId;
            Skipped = skipped;
        }

        internal Slice<long> Page { get; }

        internal string? CursorId { get; }

        internal int? Skipped { get; }
    }

    /// <summary>A find shaped as find_things is: ids 1..n, each a named thing, in id order.</summary>
    private sealed class Find
    {
        internal Find(int things)
        {
            for (long id = 1; id <= things; id++)
            {
                World[id] = "thing " + id;
            }
        }

        internal Dictionary<long, string> World { get; } = new Dictionary<long, string>();

        internal WalkCursors Cursors { get; } = new WalkCursors();

        internal int Scans { get; private set; }

        internal Found Call(JObject parameters)
        {
            Args args = new Args(parameters);
            WalkRequest walk = WalkRequest.From(args, Cursors);
            PageRequest page = PageRequest.From(args, 8, 500);
            if (walk.Continues)
            {
                List<object> found = new List<object>(page.Limit);
                WalkStep step = walk.Next(page.Limit, id => World.ContainsKey(id) ? Boxed(id) : null, found);
                walk.Note("things", page, found.Count, step.Total, step.CursorId);
                List<long> ids = found.ConvertAll(thing => (long)thing);
                return new Found(Slice<long>.Walked(ids, step.Offset, page.Limit, step.Total, step.HasMore),
                    step.CursorId, step.Skipped);
            }

            Scans++;
            List<long> hits = new List<long>(World.Keys);
            hits.Sort();
            Slice<long> slice = Slice<long>.Of(hits, page);
            string? cursorId = walk.Keep(hits, slice.Items.Count, static id => id);
            walk.Note("things", page, slice.Items.Count, hits.Count, cursorId);
            return new Found(slice, cursorId, null);
        }

        // The walk takes things by reference; a test's "thing" is its id, boxed.
        private static object Boxed(long id) => id;
    }
}
