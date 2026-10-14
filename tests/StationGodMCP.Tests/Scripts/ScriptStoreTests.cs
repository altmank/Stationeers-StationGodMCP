#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using StationGodMCP.Server;
using StationGodMCP.Tests.Runs;
using Xunit;

namespace StationGodMCP.Tests.Scripts;

/// <summary>
/// The script store (SPEC-NOTES 5b, T-B-4): the layout under --scripts-dir (shared/ and world/&lt;id&gt;/, &lt;name&gt;.py
/// the current version and &lt;name&gt;/v&lt;N&gt;.py the history), the header, name and scope rules, the save-time
/// parse's refusals, removal and last used. Saving and the parser need the real Python; the name and header rules do
/// not.
/// </summary>
[Collection(PythonCollection.Name)]
public sealed class ScriptStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "sg-store-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptStore _store;

    public ScriptStoreTests()
    {
        _store = new ScriptStore(Path.Combine(_folder, "scripts"),
            new ScriptCode(ScriptCode.DefaultPython, RunBench.SgRoot), Path.Combine(_folder, "journal"));
    }

    private const string Greet = "def main(who: str, times: int = 1) -> str:\n    return ('hi ' + who) * times\n";

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static SavedScript Saved(ScriptOutcome<SavedScript> outcome) =>
        Assert.IsType<ScriptOutcome<SavedScript>.Done>(outcome).Value;

    private static ScriptOutcome<SavedScript>.Refused Refused(ScriptOutcome<SavedScript> outcome) =>
        Assert.IsType<ScriptOutcome<SavedScript>.Refused>(outcome);

    // ---- the layout and the header ----------------------------------------------------------------------------------

    [PythonFact]
    public void SavingWritesTheCurrentVersionAndItsHistoryWithAReadableHeader()
    {
        SavedScript first = Saved(_store.Save("greet", Greet, "greet a player by name", ScriptScope.Shared, null));

        Assert.Equal(1, first.Version);
        string current = Path.Combine(_store.Root, "shared", "greet.py");
        string firstVersion = Path.Combine(_store.Root, "shared", "greet", "v1.py");
        Assert.Equal(current, first.Path);
        Assert.True(File.Exists(current));
        Assert.True(File.Exists(firstVersion));
        Assert.Equal(File.ReadAllText(current), File.ReadAllText(firstVersion));

        string[] header = File.ReadAllLines(current).Take(8).ToArray();
        Assert.Equal("# stationgod script v1", header[0]);
        Assert.Contains("# name: greet", header);
        Assert.Contains("# scope: shared", header);
        Assert.Contains("# version: 1", header);
        Assert.Contains("# description: greet a player by name", header);
        Assert.Contains("# parameters: [{\"name\":\"who\",\"type\":\"str\",\"default\":false,\"kind\":\"pos_or_kw\"}," +
                       "{\"name\":\"times\",\"type\":\"int\",\"default\":true,\"kind\":\"pos_or_kw\"}]", header);
        Assert.Equal("# ---", header[7]);
        Assert.EndsWith(Greet, File.ReadAllText(current));

        SavedScript read = Saved(_store.Open("greet", null));
        Assert.Equal("greet a player by name", read.Description);
        Assert.Equal(2, read.Parameters.Count);
        Assert.Equal(("who", "str", false), (read.Parameters[0].Name, read.Parameters[0].Type, read.Parameters[0].HasDefault));
        Assert.Equal(("times", "int", true), (read.Parameters[1].Name, read.Parameters[1].Type, read.Parameters[1].HasDefault));
    }

    [PythonFact]
    public void SavingAgainWritesTheNextVersionAndKeepsEveryOlderOne()
    {
        Saved(_store.Save("greet", Greet, "one", ScriptScope.Shared, null));
        string one = File.ReadAllText(Path.Combine(_store.Root, "shared", "greet", "v1.py"));
        SavedScript second = Saved(_store.Save("greet", Greet.Replace("hi", "hello"), "two", ScriptScope.Shared, null));

        Assert.Equal(2, second.Version);
        Assert.True(File.Exists(Path.Combine(_store.Root, "shared", "greet", "v1.py")));
        Assert.True(File.Exists(Path.Combine(_store.Root, "shared", "greet", "v2.py")));
        Assert.Equal(one, File.ReadAllText(Path.Combine(_store.Root, "shared", "greet", "v1.py")));
        Assert.Contains("# version: 2", File.ReadAllText(Path.Combine(_store.Root, "shared", "greet.py")));
        Assert.Contains("hello", File.ReadAllText(Path.Combine(_store.Root, "shared", "greet.py")));
        Assert.Equal("two", Saved(_store.Open("greet", null)).Description);
    }

    [Theory]
    [InlineData("")]
    [InlineData("greet/../x")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("..")]
    [InlineData(".hidden")]
    [InlineData("two words")]
    [InlineData("dash-name")]
    [InlineData("for")]
    [InlineData("2fast")]
    public void ANameThatIsNotAModuleNameIsRefused(string name)
    {
        ScriptOutcome<SavedScript>.Refused refused = Refused(_store.Save(name, Greet, "d", ScriptScope.Shared, null));

        Assert.Equal(ScriptFailure.Name, refused.Code);
    }

    [Fact]
    public void AReservedWindowsNameIsRefusedOnWindowsOnly()
    {
        ScriptOutcome<SavedScript> outcome = _store.Save("con", "print(1)", "d", ScriptScope.Shared, null);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(ScriptFailure.Name, Assert.IsType<ScriptOutcome<SavedScript>.Refused>(outcome).Code);
        }
        else
        {
            // Non-Windows leaves the name to the file system; the parser still runs (no Python here is fine: the
            // refusal would be invalid, not name).
            Assert.IsType<ScriptOutcome<SavedScript>.Refused>(outcome);
        }
    }

    [PythonFact]
    public void ANameThatCollidesWithAScriptOfTheOtherVisibleScopeIsRefused()
    {
        Saved(_store.Save("greet", Greet, "shared one", ScriptScope.Shared, null));

        ScriptOutcome<SavedScript>.Refused refused =
            Refused(_store.Save("greet", Greet, "world one", ScriptScope.World, "world-1"));
        Assert.Equal(ScriptFailure.Exists, refused.Code);
        Assert.Contains("other scope", refused.Message);

        // The same name in a world that does not see the first one is fine.
        Saved(_store.Save("wave", Greet, "world one", ScriptScope.World, "world-1"));
        Assert.Equal("world-1", Saved(_store.Open("wave", "world-1")).World);
    }

    [PythonFact]
    public void ANameCollidesCaseInsensitivelyWhereThePlatformDoes()
    {
        Saved(_store.Save("Greet", Greet, "d", ScriptScope.Shared, null));

        ScriptOutcome<SavedScript> outcome = _store.Save("greet", Greet, "d", ScriptScope.Shared, null);
        if (OperatingSystem.IsWindows())
        {
            // The same scope: saving writes the next version of the one file, whatever case was used.
            Assert.Equal(2, Saved(outcome).Version);
        }
        else
        {
            Assert.Equal(1, Saved(outcome).Version);
        }

        ScriptOutcome<SavedScript> other = _store.Save("GREET", Greet, "d", ScriptScope.World, "world-1");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(ScriptFailure.Exists, Assert.IsType<ScriptOutcome<SavedScript>.Refused>(other).Code);
        }
        else
        {
            Assert.Equal(1, Saved(other).Version);
        }
    }

    // ---- the code rules ---------------------------------------------------------------------------------------------

    [PythonFact]
    public void CodeThatDoesNotParseOrHasNoMainIsRefused()
    {
        ScriptOutcome<SavedScript>.Refused broken =
            Refused(_store.Save("bad", "def main(:\n", "d", ScriptScope.Shared, null));
        Assert.Equal(ScriptFailure.Invalid, broken.Code);
        Assert.Contains("does not parse", broken.Message);

        ScriptOutcome<SavedScript>.Refused noMain =
            Refused(_store.Save("bad", "def other():\n    return 1\n", "d", ScriptScope.Shared, null));
        Assert.Equal(ScriptFailure.Invalid, noMain.Code);
        Assert.Contains("no def main(...)", noMain.Message);

        ScriptOutcome<SavedScript>.Refused positional =
            Refused(_store.Save("bad", "def main(a, /):\n    return a\n", "d", ScriptScope.Shared, null));
        Assert.Equal(ScriptFailure.Invalid, positional.Code);
        Assert.Contains("positional-only", positional.Message);
    }

    [PythonFact]
    public void TheParametersComeFromTheSignatureWithDefaultsAndVars()
    {
        SavedScript saved = Saved(_store.Save("sig",
            "def main(a: int, b: str = 'x', *rest, c: float = 1.0, **named) -> None:\n    return None\n",
            "d", ScriptScope.Shared, null));

        Assert.Equal(["a: int", "b: str = ...", "*rest", "c: float = ...", "**named"],
            VerbPipeline.ParameterLines(saved.Parameters).Select(node => (string)node!).ToArray());
        Assert.Equal(["a", "b", "c"], ScriptArguments.Named(saved.Parameters));
        Assert.Equal(ScriptParameter.VarKeyword, saved.Parameters[4].Kind);
    }

    [PythonFact]
    public void ASharedScriptMayNotHardCodeIds()
    {
        // the id position, the digits-only string the orchestrator added, and the id-sized number
        foreach (string code in new[]
                 {
                     "def main() -> None:\n    sg.read('logic', id=122643)\n",
                     "def main() -> None:\n    sg.read('logic', id='4820585')\n",
                     "def main() -> None:\n    target = 122643\n    sg.read('logic', id=target)\n",
                     "def main() -> None:\n    sg.read('logic', reads=[{'id': 42}])\n"
                 })
        {
            ScriptOutcome<SavedScript>.Refused refused =
                Refused(_store.Save("shared", code, "d", ScriptScope.Shared, null));
            Assert.Equal(ScriptFailure.Ids, refused.Code);
        }

        // A wait, a count and a small number bound to a name are not ids (an id written out is, however small).
        SavedScript fine = Saved(_store.Save("shared",
            "def main() -> None:\n    sg.wait(1000)\n    count = 42\n    print(count)\n",
            "d", ScriptScope.Shared, null));
        Assert.Equal(1, fine.Version);

        // The same code is fine world-scoped: that world's ids are the script's own.
        SavedScript world = Saved(_store.Save("worldly", "def main() -> None:\n    sg.read('logic', id=122643)\n",
            "d", ScriptScope.World, "world-1"));
        Assert.Equal("world-1", world.World);
    }

    [PythonFact]
    public void AWorldScriptIsInvisibleInAnotherWorld()
    {
        Saved(_store.Save("mine", Greet, "d", ScriptScope.World, "world-1"));

        Assert.Equal("mine", Saved(_store.Open("mine", "world-1")).Name);
        ScriptOutcome<SavedScript>.Refused refused = Refused(_store.Open("mine", "world-2"));
        Assert.Equal(ScriptFailure.OtherWorld, refused.Code);
        Assert.Contains("world-1", refused.Message);

        // A world save with no world loaded is refused.
        Assert.Equal(ScriptFailure.NoWorld,
            Refused(_store.Save("mine", Greet, "d", ScriptScope.World, null)).Code);
    }

    [PythonFact]
    public void AFileWithoutTheHeaderIsNotAScriptOfTheStore()
    {
        Directory.CreateDirectory(Path.Combine(_store.Root, "shared"));
        File.WriteAllText(Path.Combine(_store.Root, "shared", "loose.py"), "def main():\n    return 1\n");

        Assert.Empty(_store.Find(null, null));
        ScriptOutcome<SavedScript>.Refused refused = Refused(_store.Open("loose", null));
        Assert.Equal(ScriptFailure.Invalid, refused.Code);
        Assert.Contains("header", refused.Message);
    }

    // ---- find, inspect, remove --------------------------------------------------------------------------------------

    [PythonFact]
    public void FindListsWhatTheWorldSeesAndFiltersByText()
    {
        Saved(_store.Save("greet", Greet, "greet a player by name", ScriptScope.Shared, null));
        Saved(_store.Save("pump", Greet, "start the pump", ScriptScope.World, "world-1"));
        Saved(_store.Save("other", Greet, "another world's", ScriptScope.World, "world-2"));

        Assert.Equal(["greet", "pump"], _store.Find(null, "world-1").Select(script => script.Name).ToArray());
        Assert.Equal(["pump"], _store.Find("PUMP", "world-1").Select(script => script.Name).ToArray());
        Assert.Equal(["pump"], _store.Find("start", "world-1").Select(script => script.Name).ToArray());
        Assert.Equal(["greet"], _store.Find(null, null).Select(script => script.Name).ToArray());
    }

    [PythonFact]
    public void RemovingTakesTheHistoryWithItAndAnUnknownNameIsRefusedWithTheNearest()
    {
        Saved(_store.Save("greet", Greet, "d", ScriptScope.Shared, null));
        Saved(_store.Save("greet", Greet, "d", ScriptScope.Shared, null));

        SavedScript removed = Saved(_store.Remove("greet", null));
        Assert.Equal(2, removed.Version);
        Assert.False(File.Exists(Path.Combine(_store.Root, "shared", "greet.py")));
        Assert.False(Directory.Exists(Path.Combine(_store.Root, "shared", "greet")));

        ScriptOutcome<SavedScript>.Refused refused = Refused(_store.Remove("grete", null));
        Assert.Equal(ScriptFailure.NotFound, refused.Code);

        Saved(_store.Save("greeting", Greet, "d", ScriptScope.Shared, null));
        Assert.Contains("nearest: greeting", Refused(_store.Remove("greetin", null)).Message);
    }

    // ---- last used --------------------------------------------------------------------------------------------------

    [PythonFact]
    public async Task LastUsedComesFromTheJournalRunLine()
    {
        string journalFolder = Path.Combine(_folder, "journal");
        Journal journal = Journal.Open(journalFolder, "hub-0000000000000001", mcp: true, () => "world-1");
        ScriptStore store = new(Path.Combine(_folder, "scripts"),
            new ScriptCode(ScriptCode.DefaultPython, RunBench.SgRoot), journalFolder, journal);
        SavedScript saved = Saved(store.Save("greet", Greet, "d", ScriptScope.Shared, null));

        await using RunBench bench = new(adjust: settings => JournalTests.WithJournal(settings, journal), scripts: store);
        VerbReply reply = await bench.Host.RunScriptAsync(
            new JsonObject { ["name"] = "greet", ["args"] = new JsonObject { ["who"] = "lu" } }, ReplyHooks.None);
        Assert.StartsWith("script-1 done", RunBench.Text(reply));

        ScriptUse? used = store.LastUsed("greet");
        Assert.NotNull(used);
        Assert.Equal("script-1", used!.RunId);
        Assert.True(used.At > DateTimeOffset.UtcNow.AddMinutes(-5), used.At.ToString("O"));

        // The version the run ran is on the journal's run line.
        string runLine = journal.Lines().Single(line => line.Contains("\"script\""));
        Assert.Contains("\"script\":\"greet\"", runLine);
        Assert.Contains("\"version\":1", runLine);
        Assert.Contains("\"at\":", runLine);

        // A script nothing has run yet has no last used.
        Assert.Null(store.LastUsed("nothing"));
        Assert.Equal(1, saved.Version);
    }
}
