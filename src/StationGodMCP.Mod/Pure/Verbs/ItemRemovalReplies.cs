#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure.Verbs;

/// <summary>One item remove item deletes, or would: its id, name and where it is (RemoveItemRule.Where).</summary>
internal sealed class DeletedItemView
{
    internal DeletedItemView(string referenceId, string name, string where)
    {
        ReferenceId = referenceId;
        Name = name;
        Where = where;
    }

    public string ReferenceId { get; }

    public string Name { get; }

    public string Where { get; }
}

/// <summary>
/// What one removable id comes to: the item itself and, when allow contents lifted the guard, the items inside it (at
/// any depth) that go with it.
/// </summary>
internal sealed class ItemRemovalView
{
    internal ItemRemovalView(DeletedItemView item, IReadOnlyList<DeletedItemView> contents)
    {
        ReferenceId = item.ReferenceId;
        Name = item.Name;
        Where = item.Where;
        Contents = contents.Count > 0 ? contents : null;
    }

    public string ReferenceId { get; }

    public string Name { get; }

    public string Where { get; }

    [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public IReadOnlyList<DeletedItemView>? Contents { get; }

    /// <summary>The item and every item that goes with it, in the order they are listed.</summary>
    internal IEnumerable<DeletedItemView> Everything()
    {
        yield return new DeletedItemView(ReferenceId, Name, Where);
        foreach (DeletedItemView inside in Contents ?? (IReadOnlyList<DeletedItemView>)System.Array.Empty<DeletedItemView>())
        {
            yield return inside;
        }
    }
}

/// <summary>
/// remove item's report: each item that would be deleted, and each thing that goes with it, a target [id, name, where
/// -> "deleted"] with the what's undo mark; every refused id a problem at its place among the ids given. The body
/// counts both.
/// </summary>
internal static class ItemRemovalReplies
{
    internal const string Deleted = "deleted";

    internal static ReportReply Reported(IReadOnlyList<ItemRemovalView> removals, IReadOnlyList<ReportProblem> problems,
        string undoMark, bool includeNotes)
    {
        List<ReportTarget> targets = new List<ReportTarget>();
        foreach (ItemRemovalView removal in removals)
        {
            foreach (DeletedItemView item in removal.Everything())
            {
                targets.Add(new ReportTarget(item.ReferenceId, null, item.Name, item.Where, Deleted, undoMark));
            }
        }

        return Report.Of(targets, new ItemRemovalSummary(targets.Count, removals.Count, includeNotes), problems);
    }

    /// <summary>The report's body: how many items would go, in how many of the ids given; notes when asked.</summary>
    internal sealed class ItemRemovalSummary
    {
        internal ItemRemovalSummary(int items, int ids, bool includeNotes)
        {
            Items = items;
            Ids = ids;
            Notes = includeNotes ? Explanations : null;
        }

        public int Items { get; }

        public int Ids { get; }

        [Newtonsoft.Json.JsonProperty(NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public IReadOnlyList<string>? Notes { get; }
    }

    private static readonly string[] Explanations =
    {
        "Items are deleted outright (OnServer.Destroy), as the game deletes a made item; nothing is refunded.",
        "A deleted item cannot be brought back: undo says so."
    };
}
