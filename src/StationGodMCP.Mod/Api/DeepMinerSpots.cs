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
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using TerrainSystem;
using Trading;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// deep_miner_spots: where a deep miner would mine what. The game gives a miner the first of the world's deep-mining
/// profiles (WorldSettingData.DeepMinablesData) whose conditions hold at its position when it is registered
/// (DeepMiner.OnRegistered); a profile's condition is a region (RegionCondition: RegionManager reads the region set's
/// map texture at the position's x and z). This tool asks the same profiles the same way, through the position form
/// the game's own map motherboard uses (EvaluablePosition, MapMotherboardPanelMouseHandler), so it never keeps a table
/// of its own. With ores it samples a disc nearest first (MinerSpotSearch) and reads the ground at each spot from the
/// terrain's density (VoxelTerrain.GetDensityAtSize), with the miner's own terrain rule (DeepMiner.CanConstruct: world
/// volume at its position at most 6000 L). Read only.
/// </summary>
internal static class DeepMinerSpotsApi
{
    private const double DefaultRadiusM = 500.0;
    private const double MaximumRadiusM = 3000.0;
    private const int DefaultCount = ReplyDefaults.DeepMinerSpots;
    private const int MaximumCount = 20;
    private const double DefaultSeparationM = 100.0;
    private const int MaximumOres = 4;
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

    internal static DeepMinerSpotsView Handle(Args args)
    {
        List<DeepMinablesGenerationData> profiles = WorldSetting.Current?.Data?.DeepMinablesData ??
                                                    throw ApiErrors.Refused("no_world", "No world is loaded.");
        Vector3 centre = Centre(args);
        Dictionary<DeepMinablesGenerationData, ProfileFacts> facts = Facts(profiles);
        List<MinerProfileView> profileViews = profiles.ConvertAll(profile => facts[profile].View);
        MinerPointView at = new MinerPointView(GameLookup.ViewOf(centre), RegionsAt(centre),
            ProfileAt(profiles, centre.x, centre.z)?.Id);
        if (!args.Has("ores"))
        {
            args.Reject("a point report (without ores)", "radius_m", "count", "min_separation_m", "step_m");
            return new DeepMinerSpotsView(at, profileViews, null, null, null);
        }

        OreQuery query = Ores(args, facts);
        HashSet<DeepMinablesGenerationData> wanted = new HashSet<DeepMinablesGenerationData>();
        foreach (DeepMinablesGenerationData profile in profiles)
        {
            if (query.Matches(facts[profile].Mix))
            {
                wanted.Add(profile);
            }
        }

        return Search(args, profiles, wanted, query, centre, at, profileViews);
    }

    private static DeepMinerSpotsView Search(Args args, List<DeepMinablesGenerationData> profiles,
        HashSet<DeepMinablesGenerationData> wanted, OreQuery query, Vector3 centre, MinerPointView at,
        List<MinerProfileView> profileViews)
    {
        double radius = Bounded(args, "radius_m", DefaultRadiusM, 1.0, MaximumRadiusM);
        int count = args.OptionalInt("count", 1, MaximumCount) ?? DefaultCount;
        double separation = Bounded(args, "min_separation_m", DefaultSeparationM, 0.0, MaximumRadiusM);
        double finest = RegionMapStep();
        double step = Math.Max(finest, args.Has("step_m")
            ? Bounded(args, "step_m", finest, 0.5, 500.0)
            : MinerSpotSearch.StepFor(radius, finest, MaximumSamples));
        Stopwatch clock = Stopwatch.StartNew();
        MinerSearchResult result = wanted.Count == 0
            ? new MinerSearchResult(new List<MinerSample>(), 0, 0, radius, false)
            : new MinerSpotSearch(step, radius, separation, count, MaximumSamples).Run(centre.x, centre.z,
                (x, z) => ProfileAt(profiles, x, z) is DeepMinablesGenerationData found && wanted.Contains(found),
                () => clock.ElapsedMilliseconds < SearchBudgetMs);
        List<MinerSpotView> spots = new List<MinerSpotView>(result.Spots.Count);
        List<MinerBeaconView> beacons = new List<MinerBeaconView>(result.Spots.Count);
        string ores = string.Join("+", query.Ores);
        foreach (MinerSample spot in result.Spots)
        {
            MinerSpotView view = SpotView(profiles, spot, centre);
            spots.Add(view);
            beacons.Add(new MinerBeaconView(new[] { view.At.X, view.At.Y, view.At.Z },
                $"{ores} {Math.Round(spot.Distance)} m {view.Compass}"));
        }

        List<string> profileIds = new List<string>();
        foreach (DeepMinablesGenerationData profile in profiles)
        {
            if (wanted.Contains(profile))
            {
                profileIds.Add(profile.Id);
            }
        }

        return new DeepMinerSpotsView(at, profileViews,
            new MinerSearchView(query.Ores, profileIds, radius, step, separation, result.Samples, result.Matched,
                result.SearchedRadius, result.Truncated), spots, beacons);
    }

    // at, else the player.
    private static Vector3 Centre(Args args)
    {
        if (args.Has("at"))
        {
            return RunArgs.PositionOf(args.Optional("at")!, "at");
        }

        return PlayerOrigin.RequireHuman().Position;
    }

    private static OreQuery Ores(Args args, Dictionary<DeepMinablesGenerationData, ProfileFacts> facts)
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

        JArray array = args.Array("ores", MaximumOres);
        List<string> ores = new List<string>(array.Count);
        foreach (JToken entry in array)
        {
            string? ore = entry.Type == JTokenType.String ? entry.Value<string>()?.Trim() : null;
            if (ore == null || !known.Contains(ore))
            {
                throw ApiErrors.InvalidArgument(
                    $"ores: '{entry}' is in no deep-mining profile of this world; they hold " +
                    $"{string.Join(", ", known)} (Hydrocarbon is coal).");
            }

            ores.Add(ore);
        }

        return new OreQuery(ores);
    }

    private static double Bounded(Args args, string name, double fallback, double minimum, double maximum)
    {
        double value = args.OptionalDouble(name) ?? fallback;
        if (!(value >= minimum && value <= maximum))
        {
            throw ApiErrors.InvalidArgument(
                $"Argument '{name}' must be from {minimum.ToString(CultureInfo.InvariantCulture)} to " +
                $"{maximum.ToString(CultureInfo.InvariantCulture)}.");
        }

        return value;
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

    private static List<MinerRegionView> RegionsAt(Vector3 position)
    {
        List<(RegionSet set, Region region)> pairs = new List<(RegionSet set, Region region)>();
        RegionManager.TryGetRegionsAtWorldPosition(position, pairs);
        return pairs.ConvertAll(static pair => new MinerRegionView(pair.set.Id, pair.region.Id,
            pair.region.Name != null ? (string)pair.region.Name : null));
    }

    // One pixel of the finest region map, in metres (RegionManager.TryGetRegionAtWorldPosition).
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
