#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Shaping;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// fields names that match no key: one safe match is read instead and reported in fields_mapped; a near key that is not
/// safe, or several, stay unmatched with fields_closest naming them; none leaves fields_unmatched and fields_valid.
/// </summary>
public sealed class FieldMappingTests
{
    private const string Containers =
        """{"containers":[{"reference_id":"40","prefab_name":"StructureStorageLocker","display_name":"Locker","slot_count":30,"used_slot_count":2,"distance_m":3.5}],"count":1}""";

    [Theory]
    [InlineData("used_slots", "used_slot_count")]
    [InlineData("slots_used", "used_slot_count")]
    [InlineData("distance", "distance_m")]
    [InlineData("displayName", "display_name")]
    [InlineData("DisplayName", "display_name")]
    [InlineData("dispaly_name", "display_name")]
    [InlineData("name", "display_name")]
    public void OneCloseKeyIsTheMatch(string requested, string key)
    {
        Assert.Equal(key, FieldMatch.Safe(requested, Keys()));
    }

    [Fact]
    public void ReorderedWordsMatch()
    {
        Assert.Equal("slots_used", FieldMatch.Safe("used_slots", new[] { "slots_used", "slot_count" }));
    }

    [Theory]
    [InlineData("network", "networks")]
    [InlineData("network", "network_id")]
    [InlineData("refund", "refund_enabled")]
    [InlineData("slots", "slot_count")]
    [InlineData("pressure_kpa_c", "pressure_kpa_a")]
    public void ANearKeyThatMeansSomethingElseIsOnlyNamed(string requested, string key)
    {
        Assert.Null(FieldMatch.Safe(requested, new[] { key }));
        Assert.Equal(new[] { key }, FieldMatch.Closest(requested, new[] { key }));
    }

    [Fact]
    public void TwoSafeMatchesAreAmbiguous()
    {
        string[] keys = { "distance_m", "distance_s" };

        Assert.Null(FieldMatch.Safe("distance", keys));
        Assert.Equal(keys, FieldMatch.Closest("distance", keys));
    }

    [Theory]
    [InlineData("colour")]
    [InlineData("no_such_key")]
    [InlineData("pos")]
    public void FarNamesMatchNothing(string requested)
    {
        Assert.Empty(FieldMatch.Closest(requested, Keys()));
    }

    [Fact]
    public void AnUnmatchedNameWithOneCloseKeyIsReadAsItAndReported()
    {
        JObject reply = Shaped(new[] { "reference_id", "used_slots" });

        Assert.Equal(2, ((JObject)reply["containers"]![0]!).Count);
        Assert.Equal(2, (int)reply["containers"]![0]!["used_slot_count"]!);
        Assert.Equal("used_slot_count", (string)reply["fields_mapped"]!["used_slots"]!);
        Assert.Null(reply["fields_unmatched"]);
        Assert.Null(reply["fields_valid"]);
    }

    [Fact]
    public void AListPathIsReadAsItsCloseKey()
    {
        JObject reply = Shaped(new[] { "containers.distance" });

        Assert.Equal(3.5, (double)reply["containers"]![0]!["distance_m"]!);
        Assert.Equal("distance_m", (string)reply["fields_mapped"]!["containers.distance"]!);
    }

    [Fact]
    public void ANearKeyThatIsNotSafeStaysUnmatchedAndIsNamed()
    {
        JObject reply = Shaped(new[] { "reference_id", "slot" });

        Assert.Null(reply["fields_mapped"]);
        Assert.Equal(new[] { "slot" }, reply["fields_unmatched"]!.ToObject<string[]>());
        Assert.Equal(new[] { "slot_count" }, reply["fields_closest"]!["slot"]!.ToObject<string[]>());
        Assert.Contains("used_slot_count", reply["fields_valid"]!.ToObject<string[]>()!);
    }

    [Fact]
    public void NameReadsAsTheShownName()
    {
        JObject reply = Shaped(new[] { "name" });

        Assert.Equal("Locker", (string)reply["containers"]![0]!["display_name"]!);
        Assert.Equal("display_name", (string)reply["fields_mapped"]!["name"]!);
    }

    [Fact]
    public void AFarNameIsReportedAsBefore()
    {
        JObject reply = Shaped(new[] { "reference_id", "colour" });

        Assert.Null(reply["fields_mapped"]);
        Assert.Null(reply["fields_closest"]);
        Assert.Equal(new[] { "colour" }, reply["fields_unmatched"]!.ToObject<string[]>());
    }

    [Fact]
    public void AnExactMatchIsNeverRemapped()
    {
        JObject reply = Shaped(new[] { "display_name" });

        Assert.Null(reply["fields_mapped"]);
        Assert.Equal("Locker", (string)reply["containers"]![0]!["display_name"]!);
    }

    private const string Atmospheres =
        """{"reference_id":"9","atmospheres":[{"source":"internal","atmosphere":{"total_mol":12.5,"pressure_kpa":101.3,"temperature_k":293.1,"contents":[{"gas":"Oxygen","amount_mol":4.0}]}}],"count":1}""";

    [Theory]
    [InlineData("total_moles", "total_mol")]
    [InlineData("temperature_kelvin", "temperature_k")]
    [InlineData("distance_metres", "distance_m")]
    public void ASpeltOutUnitIsTheShortOne(string requested, string key)
    {
        Assert.Equal(key, FieldMatch.Safe(requested, new[] { key, "count" }));
    }

    [Fact]
    public void ANameBelowTheEntriesIsReadAsItsPath()
    {
        JObject reply = Shaped(Atmospheres, new[] { "pressure_kpa", "total_moles", "temperature_k" });

        JObject atmosphere = (JObject)reply["atmospheres"]![0]!["atmosphere"]!;
        Assert.Equal(101.3, (double)atmosphere["pressure_kpa"]!);
        Assert.Equal(12.5, (double)atmosphere["total_mol"]!);
        Assert.Equal(293.1, (double)atmosphere["temperature_k"]!);
        Assert.Null(atmosphere["contents"]);
        Assert.Equal("atmospheres.atmosphere.total_mol", (string)reply["fields_mapped"]!["total_moles"]!);
        Assert.Equal("atmospheres.atmosphere.pressure_kpa", (string)reply["fields_mapped"]!["pressure_kpa"]!);
        Assert.Null(reply["fields_unmatched"]);
    }

    [Fact]
    public void ANearNameBelowTheEntriesIsNamedByItsPath()
    {
        JObject reply = Shaped(Atmospheres, new[] { "gases" });

        Assert.Equal(new[] { "gases" }, reply["fields_unmatched"]!.ToObject<string[]>());
        Assert.Contains("atmospheres.atmosphere.contents.gas", reply["fields_closest"]!["gases"]!.ToObject<string[]>()!);
    }

    [Fact]
    public void ANameEndingSeveralPathsIsNotRead()
    {
        const string twice =
            """{"a":[{"x":{"total_mol":1.0},"y":{"total_mol":2.0},"id":"1"}]}""";

        JObject reply = Shaped(twice, new[] { "total_mol" });

        Assert.Null(reply["fields_mapped"]);
        Assert.Equal(new[] { "a.x.total_mol", "a.y.total_mol" }, reply["fields_closest"]!["total_mol"]!.ToObject<string[]>());
    }

    private static JObject Shaped(string reply, IEnumerable<string> fields) =>
        (JObject)ShapingChecks.Parse(ShapingChecks.ModFields(reply, fields));

    private static JObject Shaped(IEnumerable<string> fields) =>
        (JObject)ShapingChecks.Parse(ShapingChecks.ModFields(Containers, fields));

    private static string[] Keys() => new[]
    {
        "containers", "count", "reference_id", "prefab_name", "display_name", "slot_count", "used_slot_count",
        "distance_m"
    };
}
