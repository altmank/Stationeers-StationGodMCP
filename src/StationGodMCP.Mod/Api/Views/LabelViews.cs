#nullable enable

using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>One rename that was made, with the names before and after, read back from the thing.</summary>
internal sealed class LabelledView : BatchItemView
{
    internal LabelledView(int index, ThingView thing, string written, bool sentToHost, LabelStateView previous,
        LabelStateView current) : base(index, ok: true)
    {
        ReferenceId = thing.ReferenceId;
        PrefabName = thing.PrefabName;
        Written = written;
        SentToHost = sentToHost;
        Previous = previous;
        Current = current;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    /// <summary>The name as the Labeller writes it: the prefab's name for an empty one, cut, tags stripped.</summary>
    public string Written { get; }

    /// <summary>A multiplayer client sent it to the host; current shows it once the host's update arrives.</summary>
    public bool SentToHost { get; }

    public LabelStateView Previous { get; }

    public LabelStateView Current { get; }
}

/// <summary>A thing's names at one moment.</summary>
internal sealed class LabelStateView
{
    internal LabelStateView(string? displayName, string? customName)
    {
        DisplayName = displayName;
        CustomName = customName;
    }

    public string? DisplayName { get; }

    /// <summary>Thing.CustomName as stored, null when it has none.</summary>
    public string? CustomName { get; }
}

/// <summary>One rename that was refused; nothing was changed for it.</summary>
internal sealed class NotLabelledView : BatchItemView
{
    internal NotLabelledView(int index, ThingId? referenceId, ApiException error) : base(index, ok: false)
    {
        ReferenceId = referenceId;
        Error = new ErrorView(error.Code, error.Message);
    }

    public ThingId? ReferenceId { get; }

    public ErrorView Error { get; }
}
