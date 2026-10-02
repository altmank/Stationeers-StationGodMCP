#nullable enable

using Newtonsoft.Json;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// The one way a reply names a thing. display_name is the game's DisplayName, but the prefab name where the game has
/// only its "&lt;N:EN:PrefabName&gt;" placeholder (no localised name; ThingName.Displayed).
/// </summary>
internal sealed class ThingView
{
    internal ThingView(ThingId referenceId, string? prefabName, string? displayName)
    {
        ReferenceId = referenceId;
        PrefabName = prefabName;
        DisplayName = ThingName.Displayed(displayName, prefabName);
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }
}

/// <summary>A world position in metres, rounded to 0.1 m.</summary>
internal sealed class PositionView
{
    internal const int Decimals = 1;

    internal PositionView(double x, double y, double z)
    {
        X = System.Math.Round(x, Decimals);
        Y = System.Math.Round(y, Decimals);
        Z = System.Math.Round(z, Decimals);
    }

    public double X { get; }

    public double Y { get; }

    public double Z { get; }
}

/// <summary>A paint colour: its index in the game's colour list and its name; index null when there is none.</summary>
internal sealed class ColorView
{
    internal ColorView(int? index, string? name)
    {
        Index = index;
        Name = name;
    }

    public int? Index { get; }

    public string? Name { get; }
}

/// <summary>The local player, where there is one (a dedicated server has none).</summary>
internal sealed class LocalPlayerView
{
    internal LocalPlayerView(ThingId referenceId, string? displayName, PositionView position)
    {
        ReferenceId = referenceId;
        DisplayName = displayName;
        Position = position;
    }

    public ThingId ReferenceId { get; }

    public string? DisplayName { get; }

    public PositionView Position { get; }
}

/// <summary>An error inside a reply: a batch item's, a move's outcome, or a whole request's.</summary>
internal sealed class ErrorView
{
    internal ErrorView(string code, string message, object? data = null)
    {
        Code = code;
        Message = message;
        Data = data;
    }

    public string Code { get; }

    public string Message { get; }

    /// <summary>Details a program can act on (reply_too_large's sizes); absent when the error has none.</summary>
    [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
    public object? Data { get; }
}
