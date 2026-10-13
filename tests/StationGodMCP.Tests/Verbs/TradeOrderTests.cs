#nullable enable

using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// trade buy, trade sell and trade inventory without the game: the item lines (name, prefab, both, neither, a bad
/// quantity, more than 32, an unknown key), the missing id and items, the list form, the card, and the whats' labels;
/// then the replies both forms share (a one-id trade, the rows, the report's targets and problems).
/// </summary>
public sealed class TradeOrderTests
{
    private static TradeOrder? Buy(string json, List<OptionProblem> problems) =>
        TradeOrder.Of(TradeBuyOptions.Bind(new OptionReader(TradeBuyOptions.Spec, JObject.Parse(json))), problems);

    private static TradeOrder? Sell(string json, List<OptionProblem> problems) =>
        TradeOrder.Of(TradeSellOptions.Bind(new OptionReader(TradeSellOptions.Spec, JObject.Parse(json))), problems);

    private static TradeStockOrder Stock(string json) =>
        TradeStockOrder.Of(TradeInventoryOptions.Bind(new OptionReader(TradeInventoryOptions.Spec,
            JObject.Parse(json))));

    // ---- the lines ----

    [Fact]
    public void EachLineIsReadByNamePrefabOrBoth()
    {
        List<OptionProblem> problems = new();

        TradeOrder order = Buy("""
            {"id": "7", "items": [{"name": "Iron", "quantity": 5}, {"prefab": "ItemGoldIngot", "quantity": 1},
             {"name": "Oxite", "prefab": "ItemOxite", "quantity": 32}]}
            """, problems)!;

        Assert.Empty(problems);
        Assert.Equal(3, order.Items.Count);
        Assert.Equal("Iron", order.Items[0].Name);
        Assert.Null(order.Items[0].Prefab);
        Assert.Equal(5, order.Items[0].Quantity);
        Assert.Null(order.Items[1].Name);
        Assert.Equal("ItemGoldIngot", order.Items[1].Prefab);
        Assert.Equal(1, order.Items[1].Quantity);
        Assert.Equal("Oxite", order.Items[2].Name);
        Assert.Equal("ItemOxite", order.Items[2].Prefab);
        Assert.Equal(32, order.Items[2].Quantity);
    }

    [Fact]
    public void ALinesTextIsTrimmedAndBlankAsAbsent()
    {
        List<OptionProblem> problems = new();

        TradeOrder order = Buy("""{"id": "7", "items": [{"prefab": " ItemOxite ", "quantity": 2}]}""", problems)!;

        Assert.Empty(problems);
        Assert.Equal("ItemOxite", Assert.Single(order.Items).Prefab);
    }

    [Fact]
    public void ALineWithNeitherNameNorPrefabIsAProblem()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Buy("""{"id": "7", "items": [{"quantity": 2}]}""", problems));

        Assert.Equal("items[0]", Assert.Single(problems).Path);
        Assert.Equal("needs name, prefab or both", problems[0].Problem);
    }

    [Theory]
    [InlineData("""{"prefab": "ItemOxite", "quantity": 0}""", "is a whole number from 1 to 2147483647")]
    [InlineData("""{"prefab": "ItemOxite", "quantity": 1.5}""", "is a whole number from 1 to 2147483647")]
    [InlineData("""{"prefab": "ItemOxite", "quantity": "5"}""", "is a whole number from 1 to 2147483647")]
    [InlineData("""{"prefab": "ItemOxite"}""", "is required: how many units")]
    [InlineData("""{"name": "Oxite", "quantity": null}""", "is required: how many units")]
    public void AQuantityIsAWholeNumberOfOneOrMore(string line, string problem)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Buy("{\"id\": \"7\", \"items\": [" + line + "]}", problems));

        Assert.Equal("items[0].quantity", Assert.Single(problems).Path);
        Assert.Equal(problem, problems[0].Problem);
    }

    [Theory]
    [InlineData("""{"id": "7", "items": ["ItemOxite"]}""", "items[0]", "is an object: {name or prefab, quantity}")]
    [InlineData("""{"id": "7", "items": [{"prefab": 5, "quantity": 2}]}""", "items[0].prefab",
        "is the entry's prefab, as text")]
    [InlineData("""{"id": "7", "items": [{"name": 5, "quantity": 2}]}""", "items[0].name",
        "is the entry's displayed name, as text")]
    public void ALineThatIsNoLineIsAProblemAtItsPath(string json, string path, string problem)
    {
        List<OptionProblem> problems = new();

        Assert.Null(Buy(json, problems));

        Assert.Equal(path, Assert.Single(problems).Path);
        Assert.Equal(problem, problems[0].Problem);
    }

    [Fact]
    public void LinesBeyondThirtyTwoAreOneProblemAboutItems()
    {
        StringBuilder list = new StringBuilder();
        for (int index = 0; index < 33; index++)
        {
            list.Append(index > 0 ? ", " : "").Append("""{"prefab": "ItemOxite", "quantity": 1}""");
        }

        List<OptionProblem> problems = new();

        Assert.Null(Buy("{\"id\": \"7\", \"items\": [" + list + "]}", problems));

        Assert.Equal("items", Assert.Single(problems).Path);
        Assert.Equal("is 1 to 32 lines, each {name or prefab, quantity}", problems[0].Problem);
    }

    [Fact]
    public void ItemsGivenAsNoListIsAProblem()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Buy("""{"id": "7", "items": {"prefab": "ItemOxite", "quantity": 1}}""", problems));

        Assert.Equal("items", Assert.Single(problems).Path);
    }

    [Fact]
    public void ItemsIsRequired()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Buy("""{"id": "7"}""", problems));

        Assert.Equal("items", Assert.Single(problems).Path);
        Assert.Equal("is required: 1 to 32 lines, each {name or prefab, quantity}", problems[0].Problem);
    }

    [Fact]
    public void TheIdIsRequired()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Buy("""{"items": [{"prefab": "ItemOxite", "quantity": 1}]}""", problems));

        Assert.Equal("id", Assert.Single(problems).Path);
        Assert.Equal("is required: the trader contact, or a list of them", problems[0].Problem);
    }

    [Fact]
    public void AnUnknownLineKeyNamesTheNearestThatIsNot()
    {
        // prefab_name, the old argument, is gone: prefab and name are the keys now.
        List<OptionProblem> problems = new();

        Assert.Null(Buy("""
            {"id": "7", "items": [{"prefab": "ItemOxite", "quantity": 2, "prefab_name": "ItemOxite"}]}
            """, problems));

        OptionProblem problem = Assert.Single(problems);
        Assert.Equal("items[0].prefab_name", problem.Path);
        Assert.Equal("is not a line key", problem.Problem);
        Assert.Equal(new[] { "prefab", "name" }, problem.Nearest);
    }

    // ---- the ids, the card and the labels ----

    [Fact]
    public void TheWhatsOwnLabelMarkAndConfirmRead()
    {
        List<OptionProblem> problems = new();

        TradeOrder buy = Buy("""{"id": "7", "items": [{"prefab": "ItemIronIngot", "quantity": 1}]}""", problems)!;

        Assert.Empty(problems);
        Assert.Equal("trade buy", buy.Label);
        Assert.Equal("[cannot undo: trade]", buy.Mark);
        Assert.False(buy.Acts);
        Assert.False(buy.Ids.AnswersRows);
        Assert.Equal(new[] { "7" }, buy.Ids.Ids);
        Assert.Null(buy.Card);

        problems.Clear();
        TradeOrder sell = Sell("""
            {"id": ["7", "8"], "items": [{"prefab": "ItemGoldIngot", "quantity": 2}], "confirm": true}
            """, problems)!;

        Assert.Empty(problems);
        Assert.Equal("trade sell", sell.Label);
        Assert.True(sell.Acts);
        Assert.True(sell.Ids.AnswersRows);
        Assert.Equal(new[] { "7", "8" }, sell.Ids.Ids);
    }

    [Theory]
    [InlineData("""{"id": "7", "card": " 9 ", "items": [{"prefab": "ItemOxite", "quantity": 1}]}""", "9")]
    [InlineData("""{"id": "7", "card": "   ", "items": [{"prefab": "ItemOxite", "quantity": 1}]}""", null)]
    [InlineData("""{"id": "7", "items": [{"prefab": "ItemOxite", "quantity": 1}]}""", null)]
    public void TheCardIsItsIdTextTrimmedAndBlankIsAbsent(string json, string? card)
    {
        List<OptionProblem> problems = new();

        TradeOrder order = Buy(json, problems)!;

        Assert.Empty(problems);
        Assert.Equal(card, order.Card);
    }

    [Fact]
    public void TheWhatsLabelPrefixesEveryRefusal()
    {
        List<OptionProblem> problems = new();

        Assert.Null(Sell("""{"id": "7", "items": [{"prefab_name": "ItemGoldIngot", "quantity": 2}]}""", problems));

        VerbError refused = OptionProblem.Refusal(TradeSellOptions.Spec.Label, problems);
        Assert.Equal("invalid_argument", refused.Code);
        Assert.StartsWith("trade sell: items[0].prefab_name: is not a line key (nearest: prefab, name); "
            + "items[0]: needs name, prefab or both.", refused.Message);
    }

    [Fact]
    public void TradeInventoryReadsItsIdAndCard()
    {
        TradeStockOrder one = Stock("""{"id": "7", "card": "9"}""");
        Assert.False(one.Ids!.AnswersRows);
        Assert.Equal(new[] { "7" }, one.Ids.Ids);
        Assert.Equal("9", one.Card);

        TradeStockOrder many = Stock("""{"id": ["7", "8"]}""");
        Assert.True(many.Ids!.AnswersRows);
        Assert.Null(many.Card);
    }

    // ---- the replies ----

    [Fact]
    public void AOneIdReportNamesTheContactAndTheCard()
    {
        JObject report = TradeReplies.Reported(
            new List<TradeResult> { Traded(id: 7, creditsAfter: 90f) }, asList: false, "[cannot undo: trade]");

        Assert.True((bool)report["report_only"]!);
        Assert.Empty(report["problems"]!);
        JArray targets = (JArray)report["targets"]!;
        Assert.Equal(2, targets.Count);
        Assert.Equal("[7, Gas Trader, ? -> ?] [cannot undo: trade]", (string?)targets[0]!["text"]);
        Assert.Equal("[9, Credit Card, 100 -> 90] [cannot undo: trade]", (string?)targets[1]!["text"]);
        Assert.Equal("[cannot undo: trade]", (string?)targets[0]!["undo"]);
        // The body of a one-id report is the trade itself.
        Assert.Equal("7", (string?)report["reference_id"]);
        Assert.Equal("Gas Trader", (string?)report["name"]);
        Assert.Equal(90f, (float)report["credits_after"]!);
    }

    [Fact]
    public void AListReportListsEveryTradeAndEveryRefusedId()
    {
        List<TradeResult> results = new List<TradeResult>
        {
            Traded(id: 7, creditsAfter: 90f),
            new TradeResult.Failed(1, "8",
                new VerbError("not_landed", "This trader is not landed at a pad; no trader is landed."))
        };

        JObject report = TradeReplies.Reported(results, asList: true, "[cannot undo: trade]");

        Assert.True((bool)report["report_only"]!);
        Assert.Equal(2, ((JArray)report["targets"]!).Count);
        JArray trades = (JArray)report["trades"]!;
        Assert.Equal("7", (string?)Assert.Single(trades)!["reference_id"]);
        JObject problem = (JObject)Assert.Single((JArray)report["problems"]!);
        Assert.Equal(1, (int)problem["index"]!);
        Assert.Equal("8", (string?)problem["reference_id"]);
        Assert.Equal("not_landed", (string?)problem["code"]);
        Assert.False(report.ContainsKey("reference_id"));
    }

    [Fact]
    public void AnActAnswersOneIdsTradeAndAListTheRows()
    {
        TradeResult.Traded traded = Traded(id: 7, creditsAfter: 90f);
        List<TradeResult> results = new List<TradeResult> { traded };

        VerbOutcome.Answered one = Assert.IsType<VerbOutcome.Answered>(
            TradeReplies.Acted(Ids("7", asList: false), results));
        Assert.Equal("7", (string?)one.Reply["reference_id"]);
        Assert.Equal("Gas Trader", (string?)one.Reply["name"]);
        Assert.Equal(90f, (float)one.Reply["credits_after"]!);
        Assert.Null(one.Reply["ok"]);

        VerbOutcome.Answered many = Assert.IsType<VerbOutcome.Answered>(
            TradeReplies.Acted(Ids("7", asList: true), results));
        Assert.Equal(1, (int)many.Reply["count"]!);
        Assert.Equal(1, (int)many.Reply["success_count"]!);
        Assert.Equal(0, (int)many.Reply["error_count"]!);
        Assert.Equal("7", (string?)many.Reply["results"]![0]!["reference_id"]);
        Assert.Equal(90f, (float)many.Reply["results"]![0]!["credits_after"]!);
    }

    [Fact]
    public void OneIdRefusedIsTheCallsRefusalAndARowInAList()
    {
        List<TradeResult> results = new List<TradeResult>
        {
            new TradeResult.Failed(0, "7", new VerbError("not_landed", "no trader is landed"))
        };

        VerbOutcome.Refused refused = Assert.IsType<VerbOutcome.Refused>(
            TradeReplies.Acted(Ids("7", asList: false), results));
        Assert.Equal("not_landed", refused.Error.Code);

        VerbOutcome.Answered rows = Assert.IsType<VerbOutcome.Answered>(
            TradeReplies.Acted(Ids("7", asList: true), results));
        Assert.Equal(0, (int)rows.Reply["success_count"]!);
        Assert.Equal(1, (int)rows.Reply["error_count"]!);
        Assert.Equal("not_landed", (string?)rows.Reply["results"]![0]!["error"]!["code"]);
    }

    // ---- helpers ----

    private static PerIdList Ids(string id, bool asList) => new PerIdList(new[] { id }, asList, null);

    private static TradeResult.Traded Traded(long id, float creditsAfter) =>
        new TradeResult.Traded(id.ToString(System.Globalization.CultureInfo.InvariantCulture), "Credit Card",
            new TradeView(new ThingId(id), "Gas Trader", new ThingId(9), 100f, creditsAfter,
                new BatchBuilder(0).Build(), new List<DeliveredView>(), null));
}
