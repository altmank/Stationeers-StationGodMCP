#nullable enable

using System.Collections.Generic;
using System.Text.Json;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Device fixes from round 3 of the headless live test (2026-09-29): the sidecar refuses a required argument left
/// out and an array outside minItems / maxItems, tells an enum given as a number its values, and takes an integer past
/// a long's range as an integer; the IC tools' schemas name not_ic_housing and the selector targets' real default.
/// </summary>
public sealed class DevicesRound3Tests
{
    [Theory]
    [InlineData("""{"reference_id":"424"}""")]
    [InlineData("""{"reference_id":"424","action":null}""")]
    public void AMissingRequiredArgumentIsRefused(string arguments)
    {
        Assert.Equal("Argument 'action' is required: one of pause, step, resume, restart.",
            Assert.Single(Problems("control_ic_execution", arguments)));
    }

    [Fact]
    public void EveryMissingRequiredArgumentIsNamed()
    {
        Assert.Equal(
            new[] { "Argument 'reference_id' is required.", "Argument 'start_address' is required.", "Argument 'count' is required." },
            Problems("read_memory", "{}"));
    }

    [Fact]
    public void NoArgumentsAtAllStillNeedTheRequiredOnes()
    {
        Assert.Equal("Argument 'reference_id' is required.", Assert.Single(Problems("get_ic_status", "null")));
        Assert.Empty(Problems("list_devices", "null"));
    }

    [Fact]
    public void AnArrayEntryKeepsItsOwnRequiredFieldsForTheMod()
    {
        // read_logic_many answers a bad entry by itself (ok false), so the sidecar leaves the entry's fields alone.
        Assert.Empty(Problems("read_logic_many", """{"reads":[{"logic_type":"On"}]}"""));
    }

    [Fact]
    public void AnEnumGivenAsANumberIsToldItsValues()
    {
        Assert.Equal("Argument 'action' must be one of pause, step, resume, restart.",
            Assert.Single(Problems("control_ic_execution", """{"reference_id":"99999999","action":5}""")));
    }

    [Fact]
    public void AnIdGivenAsANumberStillGetsTheQuoteHint()
    {
        Assert.Equal("Argument 'reference_id' must be a string, e.g. \"424\" in quotes.",
            Assert.Single(Problems("get_ic_status", """{"reference_id":424}""")));
    }

    [Theory]
    [InlineData("1e19")]
    [InlineData("9223372036854775808")]
    public void AWholeNumberPastALongIsStillAnInteger(string written)
    {
        string arguments = $$"""{"reference_id":"422","start_address":{{written}},"count":1}""";

        Assert.Empty(Problems("read_memory", arguments));
    }

    [Fact]
    public void AnEmptyTargetListIsRefusedBeforeTheHolder()
    {
        Assert.Equal("Argument 'target_reference_ids' must be an array of 1 to 256 entries; it has 0.",
            Assert.Single(Problems("resolve_ic_selectors", """{"reference_id":"422","target_reference_ids":[]}""")));
    }

    [Fact]
    public void AnOverlongArrayIsRefused()
    {
        string values = string.Join(",", new int[513]);

        Assert.Equal("Argument 'values' must be an array of 1 to 512 entries; it has 513.",
            Assert.Single(Problems("write_memory", $$"""{"reference_id":"422","start_address":0,"values":[{{values}}]}""")));
    }

    [Theory]
    [InlineData("get_ic_source")]
    [InlineData("set_ic_source")]
    [InlineData("get_ic_status")]
    [InlineData("control_ic_execution")]
    [InlineData("resolve_ic_selectors")]
    public void TheIcToolsNameNotIcHousing(string tool)
    {
        Assert.Contains("not_ic_housing", ReferenceIdDescription(tool));
    }

    [Fact]
    public void SelectorTargetsDefaultToTheDataNetwork()
    {
        string description = Program.InputSchemas["resolve_ic_selectors"].GetProperty("properties")
            .GetProperty("target_reference_ids").GetProperty("description").GetString()!;

        Assert.Contains("defaults to the devices on the holder's data network", description);
        Assert.DoesNotContain("every visible device", description);
    }

    private static string ReferenceIdDescription(string tool) =>
        Program.InputSchemas[tool].GetProperty("properties").GetProperty("reference_id").GetProperty("description")
            .GetString()!;

    private static IReadOnlyList<string> Problems(string tool, string arguments)
    {
        using JsonDocument document = JsonDocument.Parse(arguments);
        return ArgumentCheck.Problems(Program.InputSchemas[tool], document.RootElement);
    }}
