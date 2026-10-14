#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Client;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Tests.CatalogueChecks;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// The verb model, the one catalogue: the files the mod, the Client and the Server embed, the hash the mod announces
/// in welcome.catalogue.hash (SHA-256 over their raw bytes, concatenated whole in ordinal file-name order), the
/// assembled document the `catalogue` method answers, and the rules the Client reads out of it.
/// </summary>
public sealed class VerbModelTests
{
    [Fact]
    public void TheModAndTheClientComputeTheSameHashFromTheirOwnEmbeds()
    {
        Assert.Matches("^sha256:[0-9a-f]{64}$", VerbDocument.Embedded.Hash);
        Assert.Equal(VerbDocument.Embedded.Hash, GameCatalogue.BuiltIn.Hash);
    }

    [Fact]
    public void TheHashIsTheVerbFilesRawBytesInOrdinalFileNameOrder()
    {
        using IncrementalHash sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in Directory.GetFiles(Path.Combine(CatalogueFiles.SourceRoot, "verbs"), "*.json")
                     .OrderBy(path => "catalogue/verbs/" + Path.GetFileName(path), StringComparer.Ordinal))
        {
            sha.AppendData(File.ReadAllBytes(file));
        }

        Assert.Equal("sha256:" + Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(),
            VerbDocument.Embedded.Hash);
    }

    [Fact]
    public void TheDocumentCarriesEveryFileOfTheCatalogueAndCountsTheWhats()
    {
        JsonDocument document = JsonDocument.Parse(VerbDocument.Embedded.Text);
        JsonElement model = document.RootElement;
        Assert.Equal(new[] { "shared", "errors", "verbs" }, model.EnumerateObject().Select(property => property.Name));

        JsonElement shared = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(CatalogueFiles.SourceRoot, "verbs", "_shared.json"))).RootElement;
        Assert.True(JToken.DeepEquals(JToken.Parse(File.ReadAllText(Path.Combine(CatalogueFiles.SourceRoot, "verbs", "_shared.json"))), JToken.Parse(model.GetProperty("shared").GetRawText())), "the shared file differs");

        JsonElement errors = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(CatalogueFiles.SourceRoot, "verbs", "_errors.json"))).RootElement;
        Assert.True(JToken.DeepEquals(JToken.Parse(File.ReadAllText(Path.Combine(CatalogueFiles.SourceRoot, "verbs", "_errors.json"))), JToken.Parse(model.GetProperty("errors").GetRawText())), "the errors file differs");

        List<string> verbs = [];
        int whats = 0;
        foreach (string verb in shared.GetProperty("verbs").EnumerateArray().Select(name => name.GetString()!))
        {
            JToken file = JToken.Parse(File.ReadAllText(
                Path.Combine(CatalogueFiles.SourceRoot, "verbs", verb + ".json")));
            Assert.True(JToken.DeepEquals(file, JToken.Parse(model.GetProperty("verbs").GetProperty(verb).GetRawText())),
                $"the {verb} file differs");
            verbs.Add(verb);
            whats += ((JObject)file["whats"]!).Count;
        }

        Assert.Equal(verbs.Count, VerbDocument.Embedded.VerbCount);
        Assert.Equal(whats, VerbDocument.Embedded.MethodCount);
    }

    [Fact]
    public void EveryErrorPointerComesFromTheErrorsFile()
    {
        JsonElement errors = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(CatalogueFiles.SourceRoot, "verbs", "_errors.json"))).RootElement;
        int pointers = 0;
        foreach (JsonProperty error in errors.EnumerateObject())
        {
            if (!error.Value.TryGetProperty("see", out JsonElement see))
            {
                continue;
            }

            ErrorSee pointer = Assert.IsType<ErrorSee>(ErrorGuide.SeeOf(error.Name));
            Assert.Equal(see.TryGetProperty("verb", out JsonElement verb) ? verb.GetString() : null, pointer.Verb);
            Assert.Equal(see.TryGetProperty("what", out JsonElement what) ? what.GetString() : null, pointer.What);
            Assert.Equal(see.TryGetProperty("topic", out JsonElement topic) ? topic.GetString() : null, pointer.Topic);
            Assert.Equal(see.TryGetProperty("subtopic", out JsonElement subtopic) ? subtopic.GetString() : null,
                pointer.Subtopic);
            pointers++;
        }

        Assert.True(pointers > 50, $"only {pointers} error codes carry a see");
    }

    [Fact]
    public void AReadOnlyVerbIsResendSafeAndAWriteVerbAtItsReportIsToo()
    {
        Assert.True(GameCatalogue.BuiltIn.ReadsOnly("inspect", Json("""{"what":"device","id":"5"}""")));
        Assert.True(GameCatalogue.BuiltIn.ResendSafe("find", Json("""{"what":"thing"}""")));

        // A confirm what without confirm: true reports, so it changes nothing and may be sent again.
        Assert.Equal("read", GameCatalogue.BuiltIn.EffectiveClass("build", Json("""{"what":"cable"}""")));
        Assert.True(GameCatalogue.BuiltIn.ResendSafe("build", Json("""{"what":"cable"}""")));

        Assert.Equal("write", GameCatalogue.BuiltIn.EffectiveClass("build",
            Json("""{"what":"cable","confirm":true}""")));
        Assert.False(GameCatalogue.BuiltIn.ResendSafe("build", Json("""{"what":"cable","confirm":true}""")));
    }

    [Fact]
    public void ACheatWhatIsACheatWhenItSaysSoOrItsOptionIsSet()
    {
        JsonElement always = Json("""{"what":"memory","id":"5","start_address":0,"values":[1]}""");
        Assert.Equal("cheat", GameCatalogue.BuiltIn.EffectiveClass("write", always));
        Assert.False(GameCatalogue.BuiltIn.ReadsOnly("write", always));

        JsonElement option = Json("""{"what":"item","reference_id":"1","to_id":"2","to_slot":0}""");
        Assert.Equal("write", GameCatalogue.BuiltIn.EffectiveClass("move", option));
        Assert.Equal("cheat", GameCatalogue.BuiltIn.EffectiveClass("move",
            Json("""{"what":"item","reference_id":"1","to_id":"2","to_slot":0,"force":true}""")));
    }

    [Fact]
    public void ADurationOptionLengthensTheWait()
    {
        Assert.Equal(TimeSpan.FromSeconds(12),
            GameCatalogue.BuiltIn.Duration("read", Json("""{"what":"sample","seconds":12}""")));
        Assert.Equal(TimeSpan.FromSeconds(30), GameCatalogue.BuiltIn.Duration("read", Json("""{"what":"sample"}""")));
        Assert.Equal(TimeSpan.Zero, GameCatalogue.BuiltIn.Duration("read", Json("""{"what":"clock"}""")));
        Assert.Equal(TimeSpan.Zero, GameCatalogue.BuiltIn.Duration("find", Json("""{"what":"thing"}""")));
    }

    [Fact]
    public void TheProtocolMethodsCarryTheClientRulesTheModGivesThem()
    {
        Assert.True(GameCatalogue.BuiltIn.Has("catalogue"));
        Assert.True(GameCatalogue.BuiltIn.Has("subscribe"));
        Assert.True(GameCatalogue.BuiltIn.Has("stream_costs"));
        Assert.False(GameCatalogue.BuiltIn.Has("no_such_method"));
        Assert.True(GameCatalogue.BuiltIn.ReadsOnly("stream_costs", Json("{}")));
        Assert.False(GameCatalogue.BuiltIn.ReadsOnly("hold_restore", Json("""{"clear_all":true}""")));
    }

    [Fact]
    public void AModelFetchedFromTheModAnswersTheSameRules()
    {
        GameCatalogue fetched = GameCatalogue.FromServer(
            JsonDocument.Parse(VerbDocument.Embedded.Text).RootElement, "sha256:" + new string('a', 64));

        Assert.Equal("sha256:" + new string('a', 64), fetched.Hash);
        Assert.True(fetched.ReadsOnly("read", Json("""{"what":"clock"}""")));
        Assert.Equal("cheat", fetched.EffectiveClass("write", Json("""{"what":"memory"}""")));
        Assert.False(fetched.Has("read.json"));
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();
}
