#nullable enable

using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// remove item without the game: the per-id decision (removable, or refused with a reason), the words that say where an
/// item is, the cap of 256 ids, the report's rows, and the write rule (a cheat that cannot be undone, targeted by id).
/// </summary>
public sealed class RemoveItemTests
{
    private static readonly ItemAllowance Nothing = ItemAllowance.None;

    private static ItemFacts Facts(RemovableKind kind = RemovableKind.Item, bool carried = false, int contents = 0) =>
        new ItemFacts(kind, "Backpack", carried, contents);

    private static ItemVerdict.Refused Refused(ItemVerdict verdict) => Assert.IsType<ItemVerdict.Refused>(verdict);

    [Fact]
    public void ALooseItemIsRemovable()
    {
        ItemVerdict.Removable removable = Assert.IsType<ItemVerdict.Removable>(RemoveItemRule.Decide(Facts(), Nothing));

        Assert.False(removable.TakesContents);
    }

    [Theory]
    [InlineData("Structure", "remove structure")]
    [InlineData("Player", "not an item")]
    [InlineData("Entity", "not an item")]
    [InlineData("Other", "not an item")]
    public void ANonItemIsRefusedAndSaysWhatItIs(string kindName, string words)
    {
        RemovableKind kind = System.Enum.Parse<RemovableKind>(kindName);
        ItemVerdict.Refused refused = Refused(RemoveItemRule.Decide(Facts(kind), new ItemAllowance(true, true)));

        Assert.Equal(ItemRefusal.NotAnItem, refused.Reason);
        Assert.Contains(words, refused.Message);
    }

    [Fact]
    public void ACarriedItemNeedsAllowCarried()
    {
        ItemVerdict.Refused refused = Refused(RemoveItemRule.Decide(Facts(carried: true), Nothing));

        Assert.Equal(ItemRefusal.Carried, refused.Reason);
        Assert.Contains("allow [\"carried\"]", refused.Message);
        Assert.IsType<ItemVerdict.Removable>(RemoveItemRule.Decide(Facts(carried: true), new ItemAllowance(true, false)));
    }

    [Fact]
    public void AHolderOfItemsNeedsAllowContentsAndThenTakesThemWithIt()
    {
        ItemVerdict.Refused refused = Refused(RemoveItemRule.Decide(Facts(contents: 3), Nothing));

        Assert.Equal(ItemRefusal.HasContents, refused.Reason);
        Assert.Contains("holds 3 items", refused.Message);
        Assert.Contains("allow [\"contents\"]", refused.Message);
        ItemVerdict.Removable removable =
            Assert.IsType<ItemVerdict.Removable>(RemoveItemRule.Decide(Facts(contents: 3), new ItemAllowance(false, true)));
        Assert.True(removable.TakesContents);
    }

    [Fact]
    public void ACarriedBackpackWithItemsNeedsBothAllowsAndOneIsNotEnough()
    {
        ItemFacts pack = Facts(carried: true, contents: 2);

        Assert.Equal(ItemRefusal.Carried, Refused(RemoveItemRule.Decide(pack, new ItemAllowance(false, true))).Reason);
        Assert.Equal(ItemRefusal.HasContents, Refused(RemoveItemRule.Decide(pack, new ItemAllowance(true, false))).Reason);
        Assert.True(Assert.IsType<ItemVerdict.Removable>(RemoveItemRule.Decide(pack, new ItemAllowance(true, true))).TakesContents);
    }

    [Fact]
    public void AllowReadsOnlyTheTwoWordsItKnows()
    {
        ItemAllowance both = ItemAllowance.Of(new[] { "carried", "contents" });
        ItemAllowance neither = ItemAllowance.Of(new[] { "burst" });

        Assert.True(both.Carried && both.Contents);
        Assert.False(neither.Carried || neither.Contents);
        Assert.False(ItemAllowance.Of(null).Carried);
    }

    [Fact]
    public void TheCapIs256AndTheRefusalNamesTheCount()
    {
        Assert.Null(RemoveItemRule.TooMany(256));
        Assert.Contains("257", RemoveItemRule.TooMany(257));
        Assert.Contains("at most 256", RemoveItemRule.TooMany(257));
    }

    [Fact]
    public void WhereSaysGroundSlotAndCarrier()
    {
        Assert.Equal("ground", RemoveItemRule.Where(null, null, null));
        Assert.Equal("in Locker slot 4", RemoveItemRule.Where("Locker", 4, null));
        Assert.Equal("carried by Ann", RemoveItemRule.Where("Ann", 2, "Ann"));
        Assert.Equal("in Backpack slot 1, carried by Ann", RemoveItemRule.Where("Backpack", 1, "Ann"));
    }

    [Fact]
    public void AReportListsEachItemAndItsContentsAndEachRefusalAsAProblem()
    {
        DeletedItemView pack = new DeletedItemView("10", "Backpack", "ground");
        DeletedItemView inside = new DeletedItemView("11", "Wrench", "in Backpack slot 0");
        ItemRemovalView removal = new ItemRemovalView(pack, new[] { inside });
        ReportProblem refused = new ReportProblem(1, "12",
            new VerbError(RemoveItemRule.RefusedCode, "remove item: Wall is a structure"));

        JObject report = Replies.Tree(ItemRemovalReplies.Reported(new[] { removal }, new[] { refused },
            "[cannot undo: items deleted]", includeNotes: false));

        JToken[] targets = report["targets"]!.ToArray();
        Assert.Equal(new[] { "10", "11" }, targets.Select(target => (string?)target["id"]).ToArray());
        Assert.All(targets, target => Assert.Contains("deleted", (string?)target["text"]));
        Assert.All(targets, target => Assert.Contains("[cannot undo: items deleted]", (string?)target["text"]));
        Assert.Equal(2, (int)report["items"]!);
        Assert.Equal(1, (int)report["ids"]!);
        Assert.Null(report["notes"]);
        JToken problem = Assert.Single(report["problems"]!);
        Assert.Equal(1, (int)problem["index"]!);
        Assert.Equal("12", (string?)problem["reference_id"]);
        Assert.Equal(RemoveItemRule.RefusedCode, (string?)problem["code"]);
        Assert.True((bool)report["report_only"]!);
    }

    [Fact]
    public void AReportWithoutRefusalsHasNoProblemsAndNotesComeOnlyWhenAsked()
    {
        ItemRemovalView removal = new ItemRemovalView(new DeletedItemView("10", "Wrench", "ground"),
            new List<DeletedItemView>());

        JObject plain = Replies.Tree(ItemRemovalReplies.Reported(new[] { removal }, new List<ReportProblem>(), "[x]", false));
        JObject noted = Replies.Tree(ItemRemovalReplies.Reported(new[] { removal }, new List<ReportProblem>(), "[x]", true));

        Assert.Empty(plain["problems"]!);
        Assert.Null(plain["notes"]);
        Assert.NotEmpty(noted["notes"]!);
    }

    [Fact]
    public void TheWhatIsACheatThatCannotBeUndoneAndTargetsItsIds()
    {
        WriteSpec write = RemoveItemOptions.Spec.Write!;

        Assert.Equal("cannot", write.Undo);
        Assert.Equal("[cannot undo: items deleted]", write.UndoMark);
        Assert.Equal("always", write.Cheat);
        Assert.Equal("remove item", RemoveItemOptions.Spec.Label);
    }
}
