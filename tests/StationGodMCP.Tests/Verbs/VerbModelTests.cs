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
/// in welcome.catalogue.hash (SHA-256 over their bytes with every line ending a bare LF, concatenated whole in ordinal
/// file-name order), the assembled document the `catalogue` method answers, and the rules the Client reads out of it.
/// </summary>
public sealed class VerbModelTests
{
    // The identity of the committed catalogue/verbs/*.json: the hash every reader of those bytes must announce,
    // whatever line endings its own copy of them carries.
    private const string CommittedHash = "sha256:b69181df516eee957c0c81d539a9e7e510580484850557e889f0e05877b602f3";

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
        foreach (string path in CataloguePaths())
        {
            byte[] file = Lf(File.ReadAllBytes(path));
            sha.AppendData(file, 0, file.Length);
        }

        Assert.Equal("sha256:" + Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(),
            VerbDocument.Embedded.Hash);
    }

    // The committed LF bytes as a checkout that rewrote them carries them, CRLF for one case and a bare CR for the
    // other, every file of the catalogue in memory. Each reader hashes its own copy of them and must announce the
    // committed hash, so the normalising is the reader's own work on the bytes it read.
    [Theory]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void TheLineEndingsOfTheBytesEachReaderHashesAreBareLf(string newline)
    {
        List<string> names = [];
        Dictionary<string, byte[]> bytes = new(StringComparer.Ordinal);
        SortedDictionary<string, byte[]> files = new(StringComparer.Ordinal);
        foreach (string path in CataloguePaths())
        {
            string file = Path.GetFileName(path);
            byte[] variant = Variant(path, newline);
            AssertVariantCarries(file, variant, newline);
            names.Add(Resource(file));
            bytes.Add(file, variant);
            files.Add(file, variant);
        }

        Assert.Equal(CommittedHash, VerbDocument.HashOf(names, bytes));
        Assert.Equal(CommittedHash, GameCatalogue.HashOf(files));
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

    [Theory]
    [InlineData("server", "info")]
    [InlineData("server", "console")]
    [InlineData("trade", "contacts")]
    [InlineData("show", "highlight")]
    public void AReadWhatOfAWriteVerbReads(string verb, string what)
    {
        // server info at the main menu was refused as a write with no world loaded.
        Assert.Equal("read", GameCatalogue.BuiltIn.EffectiveClass(verb, Json($$"""{"what":"{{what}}"}""")));
        Assert.True(GameCatalogue.BuiltIn.ResendSafe(verb, Json($$"""{"what":"{{what}}"}""")));
    }

    [Fact]
    public void AWriteWhatOfTheSameVerbStillWrites()
    {
        Assert.Equal("write", GameCatalogue.BuiltIn.EffectiveClass("server", Json("""{"what":"stop"}""")));
        Assert.Equal("write", GameCatalogue.BuiltIn.EffectiveClass("trade",
            Json("""{"what":"buy","confirm":true}""")));
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

    // The catalogue's files in the order both hash readers take them: resource name, ordinal.
    private static IEnumerable<string> CataloguePaths() =>
        Directory.GetFiles(Path.Combine(CatalogueFiles.SourceRoot, "verbs"), "*.json")
            .OrderBy(path => Resource(Path.GetFileName(path)), StringComparer.Ordinal);

    private static string Resource(string file) => "catalogue/verbs/" + file;

    private static string Text(byte[] raw) => new UTF8Encoding(false).GetString(raw);

    private static byte[] Bytes(string text) => new UTF8Encoding(false).GetBytes(text);

    // The text with every line ending a bare LF, whatever the checkout that wrote the file did to them.
    private static string BareLf(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    // The file's bytes as each reader hashes them.
    private static byte[] Lf(byte[] raw) => Bytes(BareLf(Text(raw)));

    // The file as a checkout that rewrote its line endings carries it: every one of them the newline given.
    private static byte[] Variant(string path, string newline) =>
        Bytes(BareLf(Text(File.ReadAllBytes(path))).Replace("\n", newline));

    // The variant must really carry the endings the readers normalise, or the hashes above would hold while no reader
    // ever met a CR: a CRLF variant has as many CRs as LFs, a bare-CR variant has CRs and no LF at all.
    private static void AssertVariantCarries(string file, byte[] variant, string newline)
    {
        int crs = variant.Count(value => value == (byte)'\r');
        int lfs = variant.Count(value => value == (byte)'\n');
        bool carries = newline == "\r\n" ? crs > 0 && crs == lfs : crs > 0 && lfs == 0;
        Assert.True(carries, $"{file} carries {crs} CR and {lfs} LF as {newline.Length}-byte endings");
    }
}
