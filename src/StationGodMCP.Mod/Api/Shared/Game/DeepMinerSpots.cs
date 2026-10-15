#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Assets.Scripts.Atmospherics;
using Newtonsoft.Json.Linq;
using Reagents;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using TerrainSystem;
using Trading;
using UnityEngine;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Deep miner spots: where a deep miner would mine what. The game gives a miner the first of the world's deep-mining
/// profiles (WorldSettingData.DeepMinablesData) whose conditions hold at its position when it is registered
/// (DeepMiner.OnRegistered); a profile's condition is a region (RegionCondition: RegionManager reads the region set's
/// map texture at the position's x and z). This tool asks the same profiles the same way, through the position form
/// the game's own map motherboard uses (EvaluablePosition, MapMotherboardPanelMouseHandler), so it never keeps a table
/// of its own. With ores it samples a disc nearest first (MinerSpotSearch) and reads the ground at each spot from the
/// terrain's density (VoxelTerrain.GetDensityAtSize), with the miner's own terrain rule (DeepMiner.CanConstruct: world
/// volume at its position at most 6000 L). Read only.
/// </summary>
internal static class DeepMinerSpots
{
    private const int MaximumSamples = 250000;
    private const double FallbackStepM = 4.0;
    private const long SearchBudgetMs = 8000;
    private const double MinerVolumeLimitL = 6000.0;
    private const float SolidDensity = 0.5f;
    private const int CellsTried = 4;

    // ReagentAction keeps one double per reagent (its XML attributes); read them all rather than name a few.
    private static readonly FieldInfo[] ReagentFields = Array.FindAll(
        typeof(ReagentAction).GetFields(BindingFlags.Public | BindingFlags.Instance),
        static field => field.FieldType == typeof(double));

    /// <summary>
    /// The profile at centre and every profile of the world; with ores, the search: the count nearest spots within
    /// radius whose profile holds every ore, at least separation apart, sampled every step metres (null: the finest the
    /// budget allows, never finer than the region map). An ore no profile of this world holds is refused, naming those
    /// they hold. A Work body: the search SamplesPerStep samples a step; its time budget counts only the time spent in
    /// its own steps.
    /// </summary>
    internal static IEnumerable<object?> Reporting(Vector3 centre, IReadOnlyList<string>? ores, double radius,
        int count, double separation, double? step, Answer<DeepMinerSpotsView> answer)
    {
        List<DeepMinablesGenerationData> profiles = WorldSetting.Current?.Data?.DeepMinablesData ??
                                                    throw ApiErrors.Refused("no_world", "find ore: no world is loaded.");
        Dictionary<DeepMinablesGenerationData, ProfileFacts> facts = Facts(profiles);
        List<MinerProfileView> profileViews = profiles.ConvertAll(profile => facts[profile].View);
        MinerPointView at = new MinerPointView(GameLookup.ViewOf(centre), RegionsAt(centre),
            ProfileAt(profiles, centre.x, centre.z)?.Id);
        if (ores == null)
        {
            answer.Value = new DeepMinerSpotsView(at, profileViews, null, null, null);
            yield break;
        }

        OreQuery query = Ores(ores, facts);
        HashSet<DeepMinablesGenerationData> wanted = new HashSet<DeepMinablesGenerationData>();
        foreach (DeepMinablesGenerationData profile in profiles)
        {
            if (query.Matches(facts[profile].Mix))
            {
                wanted.Add(profile);
            }
        }

        double finest = RegionMapStep();
        double sampled = Math.Max(finest, step ?? MinerSpotSearch.StepFor(radius, finest, MaximumSamples));
        MinerSearchResult result = new MinerSearchResult(new List<MinerSample>(), 0, 0, radius, false);
        if (wanted.Count > 0)
        {
            yield return null;
            Stopwatch clock = Stopwatch.StartNew();
            Answer<MinerSearchResult> searched = new Answer<MinerSearchResult>();
            foreach (object? searchStep in new MinerSpotSearch(sampled, radius, separation, count, MaximumSamples)
                         .Searching(centre.x, centre.z,
                             (x, z) => ProfileAt(profiles, x, z) is DeepMinablesGenerationData found &&
                                       wanted.Contains(found),
                             () => clock.ElapsedMilliseconds < SearchBudgetMs, searched))
            {
                clock.Stop();
                yield return searchStep;
                clock.Start();
            }

            result = searched.Value;
        }

        List<MinerSpotView> spots = new List<MinerSpotView>(result.Spots.Count);
        List<MinerBeaconView> beacons = new List<MinerBeaconView>(result.Spots.Count);
        string named = string.Join("+", query.Ores);
        foreach (MinerSample spot in result.Spots)
        {
            MinerSpotView view = SpotView(profiles, spot, centre);
            spots.Add(view);
            beacons.Add(new MinerBeaconView(new[] { view.At.X, view.At.Y, view.At.Z },
                $"{named} {Math.Round(spot.Distance)} m {view.Compass}"));
            yield return null;
        }

        List<string> profileIds = new List<string>();
        foreach (DeepMinablesGenerationData profile in profiles)
        {
            if (wanted.Contains(profile))
            {
                profileIds.Add(profile.Id);
            }
        }

        answer.Value = new DeepMinerSpotsView(at, profileViews,
            new MinerSearchView(query.Ores, profileIds, radius, sampled, separation, result.Samples, result.Matched,
                result.SearchedRadius, result.Truncated), spots, beacons);
    }

    private static OreQuery Ores(IReadOnlyList<string> given, Dictionary<DeepMinablesGenerationData, ProfileFacts> facts)
    {
        SortedSet<string> known = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ProfileFacts profile in facts.Values)
        {
            foreach (KeyValuePair<string, double> reagent in profile.Mix)
            {
                if (reagent.Value > 0.0)
                {
                    known.Add(reagent.Key);
                }
            }
        }

        List<string> ores = new List<string>(given.Count);
        foreach (string entry in given)
        {
            string ore = entry.Trim();
            if (!known.Contains(ore))
            {
                throw ApiErrors.InvalidArgument(
                    $"find ore: ores: '{entry}' is in no deep-mining profile of this world; they hold " +
                    $"{string.Join(", ", known)} (Hydrocarbon is coal).");
            }

            ores.Add(ore);
        }

        return new OreQuery(ores);
    }

    // The miner's own choice: the first profile whose conditions hold (DeepMiner.OnRegistered).
    private static DeepMinablesGenerationData? ProfileAt(List<DeepMinablesGenerationData> profiles, double x, double z)
    {
        EvaluablePosition position = new EvaluablePosition((float)x, 0f, (float)z);
        foreach (DeepMinablesGenerationData profile in profiles)
        {
            if (profile.Evaluate(position))
            {
                return profile;
            }
        }

        return null;
    }

    [Profiled]
    private static List<MinerRegionView> RegionsAt(Vector3 position)
    {
        List<(RegionSet set, Region region)> pairs = new List<(RegionSet set, Region region)>();
        RegionManager.TryGetRegionsAtWorldPosition(position, pairs);
        return pairs.ConvertAll(static pair => new MinerRegionView(pair.set.Id, pair.region.Id,
            pair.region.Name != null ? (string)pair.region.Name : null));
    }

    // One pixel of the finest region map, in metres (RegionManager.TryGetRegionAtWorldPosition).
    [Profiled]
    private static double RegionMapStep()
    {
        double step = double.MaxValue;
        foreach (RegionSet set in RegionManager.RegionSets ?? new List<RegionSet>())
        {
            if (set.TextureWidth > 0)
            {
                step = Math.Min(step, VoxelConstants.Size / (double)set.TextureWidth);
            }
        }

        return step == double.MaxValue ? FallbackStepM : step;
    }

    [Profiled]
    private static MinerSpotView SpotView(List<DeepMinablesGenerationData> profiles, MinerSample spot, Vector3 centre)
    {
        float x = CellCentre(spot.X);
        float z = CellCentre(spot.Z);
        Bearing bearing = Bearing.Of(spot.X - centre.x, spot.Z - centre.z);
        Ground ground = GroundAt(x, z);
        Vector3 at = new Vector3(x, ground.CellY ?? centre.y, z);
        return new MinerSpotView(GameLookup.ViewOf(at), Math.Round(spot.Distance, 1),
            Math.Round(bearing.Degrees, 1), bearing.Compass, ProfileAt(profiles, spot.X, spot.Z)?.Id ?? string.Empty,
            RegionsAt(at), ground.View);
    }

    // A 2 m cell's centre on the axis: odd whole metres.
    private static float CellCentre(double metres) => (float)(2.0 * Math.Floor(metres / 2.0) + 1.0);

    /// <summary>
    /// The ground at (x, z): the highest solid voxel read from the top of the terrain down, and the first cell centre
    /// from just above it downwards where the miner's terrain rule holds.
    /// </summary>
    private static Ground GroundAt(float x, float z)
    {
        int top = VoxelConstants.Size - 1;
        for (int y = top; y >= 0; y--)
        {
            if (VoxelTerrain.GetDensityAtSize(new Vector3(x, y, z), 1) < SolidDensity)
            {
                continue;
            }

            double groundY = y + 0.5;
            float first = CellCentre(groundY + 1.0);
            for (int cell = 0; cell < CellsTried; cell++)
            {
                float cellY = first - 2f * cell;
                double volume = AtmosphereHelper.GetWorldVolume(new Vector3(x, cellY, z)).ToDouble();
                if (volume <= MinerVolumeLimitL)
                {
                    return new Ground(cellY, new MinerTerrainView(groundY, Math.Round(volume), true,
                        "terrain only: the miner's own rule (world volume at its position at most 6000 L) holds " +
                        "here; the placement cursor's other checks (structures, slope of its whole footprint) " +
                        "were not run"));
                }
            }

            return new Ground(first, new MinerTerrainView(groundY, null, false,
                $"no cell centre from {first} m down {CellsTried} cells has terrain enough for the miner's rule"));
        }

        return new Ground(null, new MinerTerrainView(null, null, null, "no ground found in the terrain here"));
    }

    [Profiled]
    private static Dictionary<DeepMinablesGenerationData, ProfileFacts> Facts(
        List<DeepMinablesGenerationData> profiles)
    {
        Dictionary<DeepMinablesGenerationData, ProfileFacts> facts =
            new Dictionary<DeepMinablesGenerationData, ProfileFacts>(profiles.Count);
        foreach (DeepMinablesGenerationData profile in profiles)
        {
            facts[profile] = ProfileFacts.Of(profile);
        }

        return facts;
    }

    private sealed class Ground
    {
        internal Ground(float? cellY, MinerTerrainView view)
        {
            CellY = cellY;
            View = view;
        }

        internal float? CellY { get; }

        internal MinerTerrainView View { get; }
    }

    /// <summary>A profile's reagent mix (grams per dirty ore) and its view.</summary>
    private sealed class ProfileFacts
    {
        private ProfileFacts(Dictionary<string, double> mix, MinerProfileView view)
        {
            Mix = mix;
            View = view;
        }

        internal Dictionary<string, double> Mix { get; }

        internal MinerProfileView View { get; }

        internal static ProfileFacts Of(DeepMinablesGenerationData profile)
        {
            Dictionary<string, double> mix = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            List<MinerReagentView> reagents = new List<MinerReagentView>();
            if (profile.ReagentAction != null)
            {
                foreach (FieldInfo field in ReagentFields)
                {
                    double grams = (double)field.GetValue(profile.ReagentAction);
                    mix[field.Name] = grams;
                    if (grams > 0.0)
                    {
                        reagents.Add(new MinerReagentView(field.Name, grams));
                    }
                }
            }

            List<string> regions = new List<string>();
            AddRegions(profile.Conditions, profile.ConditionCollections, regions);
            return new ProfileFacts(mix, new MinerProfileView(profile.Id ?? string.Empty, reagents,
                Range(profile.Quantity), Range(profile.Time), regions));
        }

        private static int[] Range(IntRangeData? range) =>
            range != null ? new[] { range.Min, range.Max } : Array.Empty<int>();

        private static void AddRegions(List<ConditionData> conditions, List<ConditionDataCollection> collections,
            List<string> regions)
        {
            foreach (ConditionData condition in conditions)
            {
                if (condition is RegionCondition region)
                {
                    regions.Add(region.Id);
                }
            }

            foreach (ConditionDataCollection collection in collections)
            {
                AddRegions(collection.Conditions, collection.ConditionCollections, regions);
            }
        }
    }
}
