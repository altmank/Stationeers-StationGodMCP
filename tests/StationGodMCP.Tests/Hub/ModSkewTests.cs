#nullable enable

using System.Text.Json;
using System.Text.Json.Nodes;
using StationGodMCP.Client;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests.Hub;

/// <summary>
/// Help and refusals follow the game's mod, not only this server's model (LIVE 2026-10-25: help listed show ghost look
/// and xray, the game ran 2.10.3 and refused them as "not an option"): an option the mod lacks is refused
/// needs_newer_mod before it is sent, one only the mod has is needs_newer_server, and help names what the mod lacks.
/// </summary>
public sealed class ModSkewTests
{
    // The built-in model with show ghost's look and xray gone (2.10.3) and an option only a newer mod has.
    private static FakeGameLink OlderMod()
    {
        JsonObject document = JsonNode.Parse(GameCatalogue.BuiltIn.Document.GetRawText())!.AsObject();
        JsonObject ghost = document["verbs"]!["show"]!["whats"]!["ghost"]!["options"]!.AsObject();
        ghost.Remove("look");
        ghost.Remove("xray");
        ghost["glow"] = new JsonObject { ["schema"] = new JsonObject { ["type"] = "boolean" } };
        using JsonDocument parsed = JsonDocument.Parse(document.ToJsonString());
        return new FakeGameLink
        {
            GameModel = GameCatalogue.FromServer(parsed.RootElement, "sha256:" + new string('b', 64)),
            ModVersion = "2.10.3"
        };
    }

    [Fact]
    public void AnOptionTheGamesModLacksIsRefusedNamingBothVersionsAndNeverSent()
    {
        FakeGameLink game = OlderMod();

        VerbReply.Failed refused = game.Pipeline()
            .Call("show", "ghost", """{"prefab":"StructureFrame","at":[0,0,0],"look":"wire","xray":false}""").Failed();

        Assert.Equal(ModSkew.NeedsNewerMod, refused.Code);
        Assert.Contains("look, xray need a newer StationGod mod: the game runs 2.10.3", refused.Message);
        Assert.Empty(game.CallsTo("show"));
    }

    [Fact]
    public void ACallWithoutThemStillGoes()
    {
        FakeGameLink game = OlderMod();

        game.Pipeline().Call("show", "ghost", """{"prefab":"StructureFrame","at":[0,0,0]}""").Done();

        Assert.Single(game.CallsTo("show"));
    }

    [Fact]
    public void AnOptionOnlyTheGamesModHasAsksForANewerServer()
    {
        VerbReply.Failed refused = OlderMod().Pipeline()
            .Call("show", "ghost", """{"prefab":"StructureFrame","at":[0,0,0],"glow":true}""").Failed();

        Assert.Equal(ModSkew.NeedsNewerServer, refused.Code);
        Assert.Contains("glow is in the game's mod (2.10.3) but not in this MCP server", refused.Message);
    }

    [Fact]
    public void HelpNamesWhatTheGamesModLacks()
    {
        VerbPipeline pipeline = OlderMod().Pipeline();

        string what = (string)pipeline.Call("help", null, """{"verb":"show","what":"ghost"}""").Done()!["text"]!;
        string verb = (string)pipeline.Call("help", null, """{"verb":"show"}""").Done()!["text"]!;
        string index = (string)pipeline.Call("help", null).Done()!["text"]!;

        Assert.EndsWith("The game runs StationGod 2.10.3; this MCP server is " + Program.ServerVersion +
            ". Not in the game's mod (they need a newer one): look, xray.\n", what);
        Assert.Contains("show ghost: look, xray need a newer mod", verb);
        Assert.Contains("Some whats and options need a newer mod", index);
    }

    [Fact]
    public void TheSameModelAddsNothing()
    {
        FakeGameLink game = new() { GameModel = GameCatalogue.BuiltIn, ModVersion = Program.ServerVersion };
        VerbPipeline pipeline = game.Pipeline();

        Assert.Null(pipeline.Skew());
        Assert.DoesNotContain("The game runs",
            (string)pipeline.Call("help", null, """{"verb":"show","what":"ghost"}""").Done()!["text"]!);
    }
}
