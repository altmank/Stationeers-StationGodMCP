#nullable enable

using System.Text.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Api.Shared;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// atmosphere_contents sources, mounted_pipe_network included (live: a Pipe Analyzer answered no_atmosphere while the
/// game read the pipe network in its cell): every source the handler writes is one the catalogue describes, and an
/// entry carries it as written.
/// </summary>
public sealed class AtmosphereSourceTests
{
    [Fact]
    public void TheCatalogueDescribesEverySource()
    {
        using JsonDocument catalogue = JsonDocument.Parse(GameCatalogueText());
        string description = string.Empty;
        foreach (JsonElement method in catalogue.RootElement.GetProperty("methods").EnumerateArray())
        {
            if (method.GetProperty("name").GetString() == "atmosphere_contents")
            {
                description = method.GetProperty("description").GetString()!;
            }
        }

        foreach (string source in AtmosphereSource.All)
        {
            Assert.Contains(source, description);
        }
    }

    [Fact]
    public void AMountedEntryCarriesItsSource()
    {
        HeldAtmosphereEntryView entry = new HeldAtmosphereEntryView(AtmosphereSource.MountedPipeNetwork,
            new ThingView(new ThingId(5), "StructurePipeAnalysizer", "Pipe Analyzer"), null, null);

        Assert.Equal("mounted_pipe_network", (string?)JObject.Parse(WireCheck.New(entry))["source"]);
    }

    private static string GameCatalogueText() => StationGodMCP.Client.GameCatalogue.BuiltIn.Document.GetRawText();
}
