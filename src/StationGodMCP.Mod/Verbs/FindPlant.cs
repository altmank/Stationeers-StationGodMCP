#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects.Items;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find plant (catalogue find.json): every plant in a tray, planter or station (Plants), by reference id, each in short
/// unless verbose, with include unplanted the crops and seeds not planted too; one id is that plant in full;
/// {plants, count, total, offset, has_more, game_time_s, day_length_s, debug_fast_growth} (ListReply: count the plants
/// list, the plants whole, so offset 0, no limit and has_more false). A list of ids answers one row per id (Rows), each
/// the plant in full or why it is none, under the game's clock. Read only.
/// </summary>
[ReplyFact(FactRule.Followed, "which things exist, by class and prefab", Keeper = typeof(StationGodMCP.WorldModel.ThingIndex))]
[ReplyFact(FactRule.Live, "each plant's growth and health")]
internal static class FindPlant
{
    private const string Unplanted = "unplanted";

    internal static VerbOutcome Run(FindPlantOptions options)
    {
        PlantClock clock = Plants.Clock();
        JsonSerializer serializer = ApiJson.Serializer;
        PerIdList? ids = options.Selector.Ids;
        if (ids is { AnswersRows: true })
        {
            Rows rows = new Rows();
            foreach (string id in ids.Ids)
            {
                rows.Add(id, () => PlantReader.Read(Plants.One(id)));
            }

            return new VerbOutcome.Answered(rows.Reply(ReplyFields.Of(("game_time_s", clock.GameTimeS),
                ("day_length_s", clock.DayLengthS), ("debug_fast_growth", clock.DebugFastGrowth))));
        }

        bool whole = ids != null || options.Fields.Verbose;
        List<Plant> plants = ids != null
            ? new List<Plant> { Plants.One(ids.Ids[0]) }
            : Plants.All(options.Fields.Includes(Unplanted));
        return WorkResult<List<object>>.Of(answer => Reading(plants, whole, answer),
            views => ListReply.Of("plants", views, Paging.All, static plant => plant,
                ReplyFields.Of(("game_time_s", clock.GameTimeS), ("day_length_s", clock.DayLengthS),
                    ("debug_fast_growth", clock.DebugFastGrowth))));
    }

    /// <summary>How many plants one step of Reading describes.</summary>
    internal const int PlantsAStep = 16;

    // The plants' views as work over frames (A3), PlantsAStep a step.
    private static IEnumerable<object?> Reading(List<Plant> plants, bool whole, Pure.Scheduling.Answer<List<object>> answer)
    {
        List<object> views = new List<object>(plants.Count);
        for (int index = 0; index < plants.Count; index++)
        {
            PlantView view = PlantReader.Read(plants[index]);
            views.Add(whole ? view : new PlantBriefView(view));
            if (index % PlantsAStep == PlantsAStep - 1)
            {
                yield return null;
            }
        }

        answer.Value = views;
    }
}
