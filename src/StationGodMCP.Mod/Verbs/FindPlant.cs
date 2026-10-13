#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects.Items;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// find plant (catalogue find.json): every plant in a tray, planter or station (Plants), by reference id, each in short
/// unless verbose, with include unplanted the crops and seeds not planted too; one id is that plant in full. A list of
/// ids answers one row per id (Rows), each the plant in full or why it is none, under the game's clock. Read only.
/// </summary>
internal static class FindPlant
{
    private const string Unplanted = "unplanted";

    internal static VerbOutcome Run(FindPlantOptions options)
    {
        PlantClock clock = Plants.Clock();
        JsonSerializer serializer = ApiJson.Fresh();
        PerIdList? ids = options.Selector.Ids;
        if (ids is { AnswersRows: true })
        {
            Rows rows = new Rows();
            foreach (string id in ids.Ids)
            {
                rows.Add(id, () => JObject.FromObject(PlantReader.Read(Plants.One(id)), serializer));
            }

            return new VerbOutcome.Answered(rows.Reply(ReplyFields.Of(("game_time_s", clock.GameTimeS),
                ("day_length_s", clock.DayLengthS), ("debug_fast_growth", clock.DebugFastGrowth))));
        }

        bool whole = ids != null || options.Fields.Verbose;
        List<Plant> plants = ids != null
            ? new List<Plant> { Plants.One(ids.Ids[0]) }
            : Plants.All(options.Fields.Includes(Unplanted));
        List<object> views = new List<object>(plants.Count);
        foreach (Plant plant in plants)
        {
            PlantView view = PlantReader.Read(plant);
            views.Add(whole ? view : new PlantBriefView(view));
        }

        return new VerbOutcome.Answered(ReplyFields.Of(("game_time_s", clock.GameTimeS),
            ("day_length_s", clock.DayLengthS), ("debug_fast_growth", clock.DebugFastGrowth), ("plants", views),
            ("count", views.Count)));
    }
}
