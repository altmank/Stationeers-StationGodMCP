#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Shaping;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The order of a paged world list (find_things, find_items, list_containers, outer_frames). Nearest follows the
/// player, so pages taken while the player moves overlap and leave gaps; reference_id pages fit together whatever the
/// player does.
/// </summary>
public sealed class ListOrderTests
{
    private const int Things = 40;
    private const int PageSize = 7;

    [Fact]
    public void ReferenceIdPagesCoverEveryThingOnceWhileThePlayerMoves()
    {
        List<long> seen = PageThroughWhileMoving(ListOrder.ReferenceId);

        Assert.Equal(Enumerable.Range(1, Things).Select(id => (long)id), seen.OrderBy(id => id));
    }

    [Fact]
    public void NearestPagesSeeSomeThingsTwiceAndSkipOthersWhileThePlayerMoves()
    {
        List<long> seen = PageThroughWhileMoving(ListOrder.Nearest);

        Assert.Equal(Things, seen.Count);
        Assert.NotEqual(Things, seen.Distinct().Count());
    }

    [Fact]
    public void NearestSortsByDistanceWithNoDistanceLastThenNameRankAndId()
    {
        List<ListKey> keys = new List<ListKey>
        {
            new ListKey(null, "A", 0, 1),
            new ListKey(5.0, "B", 0, 9),
            new ListKey(5.0, "A", 1, 3),
            new ListKey(5.0, "A", 0, 8),
            new ListKey(5.0, "A", 0, 4),
            new ListKey(2.0, "Z", 0, 99),
        };

        keys.Sort((a, b) => ListKey.Compare(ListOrder.Nearest, a, b));

        Assert.Equal(new long[] { 99, 4, 8, 3, 9, 1 }, keys.Select(key => key.Id));
    }

    [Fact]
    public void ReferenceIdIgnoresDistanceAndTellsSharedIdsApartByNameThenRank()
    {
        List<ListKey> keys = new List<ListKey>
        {
            new ListKey(1.0, "Steel", 1, 20),
            new ListKey(50.0, "Iron", 0, 7),
            new ListKey(2.0, "Steel", 0, 20),
            new ListKey(0.5, "Copper", 1, 20),
        };

        keys.Sort((a, b) => ListKey.Compare(ListOrder.ReferenceId, a, b));

        Assert.Equal(new[] { (7L, "Iron", 0), (20L, "Copper", 1), (20L, "Steel", 0), (20L, "Steel", 1) },
            keys.Select(key => (key.Id, key.Name!, key.Rank)));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("nearest", false)]
    [InlineData(" Reference_ID ", true)]
    public void TheOrderArgumentTakesItsWordsAndDefaultsToNearest(string? given, bool byReferenceId)
    {
        JObject args = given == null ? new JObject() : new JObject { ["order"] = given };

        Assert.Equal(byReferenceId ? ListOrder.ReferenceId : ListOrder.Nearest, ListOrderArg.From(new Args(args)));
    }

    [Fact]
    public void AnotherOrderIsAnInvalidArgument()
    {
        ApiException error = Assert.Throws<ApiException>(
            () => ListOrderArg.From(new Args(new JObject { ["order"] = "position" })));

        Assert.Equal(ApiErrors.InvalidArgumentCode, error.Code);
    }

    [Fact]
    public void ACutNearestPageMeasuredFromThePlayerSaysHowToPageStably()
    {
        Truncations.Begin();
        PageRequest page = PageRequest.From(new Args(JObject.Parse("{\"offset\": 500, \"limit\": 500}")), 8, 500);
        page.Note("things", 500, 9600, ListOrders.PagingAdvice(ListOrder.Nearest, measuredFromPlayer: true));

        Assert.Equal(
            "pass limit (max 500) or offset 1000; offset 500 skipped the first entries; nearest-first pages shift " +
            "when the player moves: page with order reference_id",
            Truncations.Take().Single().More);
    }

    [Theory]
    [InlineData("nearest", false)]
    [InlineData("reference_id", true)]
    [InlineData("reference_id", false)]
    public void APageThatCannotShiftGivesNoPagingAdvice(string word, bool measuredFromPlayer)
    {
        Assert.True(ListOrders.TryParse(word, out ListOrder order));

        Assert.Equal(string.Empty, ListOrders.PagingAdvice(order, measuredFromPlayer));
    }

    // Things on a line at x = id; between pages the player walks along it, so distances change from call to call.
    private static List<long> PageThroughWhileMoving(ListOrder order)
    {
        List<long> seen = new List<long>();
        for (int offset = 0, call = 0; offset < Things; offset += PageSize, call++)
        {
            double player = call * 6.0;
            List<ListKey> keys = Enumerable.Range(1, Things)
                .Select(id => new ListKey(System.Math.Abs(id - player), "StructureFrame", 0, id))
                .ToList();
            keys.Sort((a, b) => ListKey.Compare(order, a, b));
            seen.AddRange(keys.Skip(offset).Take(PageSize).Select(key => key.Id));
        }

        return seen;
    }
}
