#nullable enable

using Newtonsoft.Json;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>The interactable a press used: its name and its index in the thing's list.</summary>
internal sealed record InteractedName(string Name, int Index);

/// <summary>
/// write interact's reply: the thing, the interactable pressed, its state before and after (the state may follow a
/// frame later than the press), the player and the game's own message when it gave one.
/// </summary>
internal sealed record InteractedView(ThingView Thing, InteractedName Interactable, int Before, int After, string Player,
    [property: JsonProperty(NullValueHandling = NullValueHandling.Ignore)] string? Message);
