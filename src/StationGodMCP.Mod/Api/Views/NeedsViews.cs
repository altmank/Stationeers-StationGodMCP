#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Views;

/// <summary>
/// write needs' reply (NeedsWritten): the player, each need as a share of its capacity before and after (nutrition of
/// the body's nutrition storage, hydration of the 5 its ratio divides by), the waste ratio (none without a stomach or
/// with Sanitation off, never 0 standing in for that), whether Sanitation is on, and the warnings. A need the call did
/// not ask for reads the same before and after.
/// </summary>
internal sealed record NeedsWrittenView(ThingView Player, NeedChange Nutrition, NeedChange Hydration,
    WasteChange? Waste, bool Sanitation, List<NeedsWarning> Warnings);
