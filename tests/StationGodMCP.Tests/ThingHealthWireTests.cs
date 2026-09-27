#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>thing_health: the old StationApi.ThingHealth shapes against the new views, with the renames.</summary>
public sealed class ThingHealthWireTests
{
    private static readonly Dictionary<string, string> RecordRenames = new Dictionary<string, string>
    {
        ["ratio"] = "damage_ratio"
    };

    private static object OldRecord(bool destructible) => new
    {
        reference_id = "700",
        prefab_name = "StructureSolarPanel",
        display_name = "Solar Panel",
        kind = "structure",
        type = "SolarPanel",
        damage_state = destructible ? "destructible" : "indestructible",
        damage_state_class = destructible ? "ThingDamageState" : "IndestructableDamageState",
        max_damage = (float?)100f,
        total_damage = destructible ? 30.5 : (double?)null,
        ratio = destructible ? 0.305 : (double?)null,
        health_percent = destructible ? 70 : (int?)null,
        band = destructible ? "yellow" : null,
        damage = new
        {
            brute = 30.5f, burn = 0f, oxygen = 0f, hydration = 0f, starvation = 0f, toxic = 0f, radiation = 0f,
            decay = 0f, stun = 0f
        },
        is_broken = false,
        being_destroyed = false,
        pipe_burst = (string?)null,
        position = new { x = 1.5, y = 2.0, z = -3.5 },
        distance_m = (double?)12.3
    };

    private static HealthView NewRecord(bool destructible) => new HealthView(
        new ThingView(new ThingId(700), "StructureSolarPanel", "Solar Panel"),
        "structure",
        "SolarPanel",
        new DamageReading(
            destructible ? "destructible" : "indestructible",
            destructible ? "ThingDamageState" : "IndestructableDamageState",
            100f,
            destructible ? 30.5 : null,
            destructible ? 0.305 : null,
            destructible ? 70 : null,
            new DamagePartsView(30.5f, 0f, 0f, 0f, 0f, 0f, 0f, 0f, 0f)),
        new HealthFlags(destructible ? "yellow" : null, false, false, null),
        new PositionView(1.5, 2.0, -3.5),
        12.3);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OneThingSameWire(bool destructible)
    {
        WireCheck.SameAfterRenames(OldRecord(destructible), NewRecord(destructible), RecordRenames);
    }

    [Fact]
    public void BatchSameWire()
    {
        // The old batch patched index and ok in front of the record's JObject.
        JObject item = JObject.Parse(WireCheck.Old(OldRecord(true)));
        item.AddFirst(new JProperty("ok", true));
        item.AddFirst(new JProperty("index", 0));
        var old = new
        {
            results = new object[]
            {
                item,
                new
                {
                    index = 1,
                    ok = false,
                    error = new { code = "thing_not_found", message = "No thing with reference id 9." }
                }
            },
            count = 2,
            success_count = 1,
            error_count = 1
        };
        BatchBuilder batch = new BatchBuilder(2);
        batch.Succeeded(new HealthItemView(0, NewRecord(true)));
        batch.Failed(1, ApiErrors.ThingNotFound(new ThingId(9)));
        Dictionary<string, string> renames = new Dictionary<string, string> { ["results[].ratio"] = "damage_ratio" };
        WireCheck.SameAfterRenames(old, batch.Build(), renames);
    }

    [Fact]
    public void ScanSameWire()
    {
        var old = new
        {
            things = new[] { OldRecord(true) },
            count = 1,
            total_matches = 3,
            structures = 2,
            broken = 0,
            scanned = 5000,
            min_ratio = 0.25,
            offset = 2,
            limit = 1,
            has_more = false,
            local_player = new
            {
                reference_id = "151", display_name = "LU", position = new { x = 0.0, y = 1.0, z = 2.0 }
            }
        };
        PageRequest page = PageRequest.From(new Args(JObject.Parse("{\"offset\": 2, \"limit\": 1}")), 200, 500);
        HealthScanView view = new HealthScanView(
            Slice<HealthView>.Page(new List<HealthView> { NewRecord(true) }, page, 3), 2, 0, 5000, 0.25,
            new LocalPlayerView(new ThingId(151), "LU", new PositionView(0.0, 1.0, 2.0)));
        Dictionary<string, string> renames = new Dictionary<string, string>
        {
            ["things[].ratio"] = "damage_ratio",
            ["total_matches"] = "total",
            ["min_ratio"] = "min_damage_ratio"
        };
        WireCheck.SameAfterRenames(old, view, renames);
    }
}
