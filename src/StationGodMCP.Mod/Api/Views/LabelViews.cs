#nullable enable

namespace StationGodMCP.Api.Views;

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
