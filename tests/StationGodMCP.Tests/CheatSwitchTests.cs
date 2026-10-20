#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Protocol;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using StationGodMCP.Tests.Scheduling;
using StationGodMCP.Tests.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The cheat switch (2.7.0): outside a creative world every cheat the catalogue marks is refused as cheats_off unless
/// the console turned cheats on; it runs out by itself, a world change clears it, the replies of the cheats it lets
/// through carry the cheat note, acting ones are logged, and its console command parses on, off and the state.
/// </summary>
public sealed class CheatSwitchTests
{
    private static readonly DateTime Noon = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The refusal code a call gets through a table of echo handlers gated by a fresh switch (on for minutesOn when
    /// given), or null when its handler ran.
    /// </summary>
    internal static string? Refused(string verb, JObject parameters, bool creative, int? minutesOn)
    {
        CheatSwitch cheats = new CheatSwitch(new FakeClock(), () => Noon, () => creative);
        if (minutesOn is { } minutes)
        {
            cheats.TurnOn(minutes);
        }

        return (Gated(cheats).Run(verb, parameters, null, checkedAlready: true) as VerbOutcome.Refused)?.Error.Code;
    }

    private static VerbDispatcher Gated(CheatSwitch cheats) =>
        new VerbDispatcher(VerbSurface.Verbs.SelectMany(verb => verb.Whats).Select(TestVerbs.Echo), cheats);

    private static CheatSwitch Switch(FakeClock clock, bool creative = false) =>
        new CheatSwitch(clock, () => Noon, () => creative);

    // Every what the catalogue marks a cheat, with options that make it one; the gate reads only what, the cheat
    // option and confirm, so the echo handlers need nothing else.
    public static IEnumerable<object[]> Cheats() => new[]
    {
        new object[] { "build", "item", null! }, new object[] { "build", "blueprint", null! },
        new object[] { "build", "terrain", null! }, new object[] { "build", "structure", "free" },
        new object[] { "build", "cable", "free" }, new object[] { "build", "pipe", "free" },
        new object[] { "build", "chute", "free" }, new object[] { "build", "finish", "free" },
        new object[] { "move", "item", "force" }, new object[] { "move", "gas", null! },
        new object[] { "move", "player", null! }, new object[] { "remove", "terrain", null! },
        new object[] { "server", "command", null! }, new object[] { "write", "memory", null! },
        new object[] { "write", "battery", null! }, new object[] { "write", "genes", null! },
        new object[] { "write", "health", null! }
    };

    private static JObject Call(string what, string? option, bool confirm = true)
    {
        JObject call = new JObject { ["what"] = what, ["confirm"] = confirm };
        if (what == "command")
        {
            // server command is a cheat by its console line (ConsoleCheatsTests); this one makes things.
            call["command"] = "thing spawn ItemKitWall 5";
        }

        if (option != null)
        {
            call[option] = true;
        }

        return call;
    }

    [Theory]
    [MemberData(nameof(Cheats))]
    public void EveryCheatIsRefusedOffAndRunsOnOrInCreative(string verb, string what, string? option)
    {
        Assert.Equal(CheatSwitch.OffCode, Refused(verb, Call(what, option), creative: false, minutesOn: null));
        Assert.Equal(CheatSwitch.OffCode, Refused(verb, Call(what, option, confirm: false), creative: false, minutesOn: null));
        Assert.Null(Refused(verb, Call(what, option), creative: false, minutesOn: 10));
        Assert.Null(Refused(verb, Call(what, option), creative: true, minutesOn: null));
    }

    [Fact]
    public void TheCatalogueMarksExactlyTheseCheats()
    {
        List<string> marked = VerbSurface.Verbs.SelectMany(verb => verb.Whats)
            .Where(what => what.Write?.Cheat != null).Select(what => what.Label).OrderBy(label => label).ToList();
        List<string> listed = Cheats().Select(row => row[0] + " " + row[1]).OrderBy(label => label).ToList();
        Assert.Equal(listed, marked);
    }

    [Fact]
    public void AWriteWithoutItsCheatOptionAndEveryReadAreNotGated()
    {
        Assert.Null(Refused("build", Call("structure", null), creative: false, minutesOn: null));
        Assert.Null(Refused("move", Call("item", null), creative: false, minutesOn: null));
        Assert.Null(Refused("write", Call("logic", null), creative: false, minutesOn: null));
        Assert.Null(Refused("server", new JObject { ["what"] = "info" }, creative: false, minutesOn: null));
    }

    [Fact]
    public void TheRefusalSaysCheatsAreOffAndHowTheUserTurnsThemOn()
    {
        CheatSwitch cheats = Switch(new FakeClock());
        VerbOutcome.Refused refused = Assert.IsType<VerbOutcome.Refused>(
            Gated(cheats).Run("write", Call("health", null), null, checkedAlready: true));
        Assert.Contains("write health is a cheat", refused.Error.Message);
        Assert.Contains("cheats are off", refused.Error.Message);
        Assert.Contains("stationgod cheats on <minutes>", refused.Error.Message);
    }

    [Fact]
    public void TheSwitchRunsOutByItselfAndAWorldChangeClearsIt()
    {
        FakeClock clock = new FakeClock();
        CheatSwitch cheats = Switch(clock);
        Assert.False(cheats.IsOn);
        cheats.TurnOn(2);
        Assert.Equal(2.0, cheats.MinutesLeft);
        clock.Advance(90_000);
        Assert.Equal(0.5, cheats.MinutesLeft);
        clock.Advance(30_000);
        Assert.False(cheats.IsOn);
        Assert.Null(cheats.MinutesLeft);
        Assert.Equal(CheatSwitch.OffCode, (Gated(cheats).Run("move", Call("player", null), null, checkedAlready: true) as VerbOutcome.Refused)?.Error.Code);

        cheats.TurnOn(5);
        Gated(cheats).Run("write", Call("battery", null), null, checkedAlready: true);
        cheats.Clear();
        Assert.False(cheats.IsOn);
        Assert.Empty(cheats.Runs);
    }

    [Fact]
    public void ACheatLetThroughCarriesTheNoteAndActingOnesAreLogged()
    {
        FakeClock clock = new FakeClock();
        CheatSwitch cheats = Switch(clock);
        cheats.TurnOn(10);
        VerbDispatcher table = Gated(cheats);

        CheatNote.Begin();
        table.Run("build", Call("item", null, confirm: false), null, checkedAlready: true);
        CheatNoteView? report = CheatNote.Take();
        Assert.NotNull(report);
        Assert.StartsWith("A cheat (build item): allowed", report!.Note);
        Assert.Empty(cheats.Runs);

        clock.Advance(60_000);
        CheatNote.Begin();
        table.Run("build", Call("item", null), null, checkedAlready: true);
        CheatNoteView? ran = CheatNote.Take();
        Assert.Equal(9.0, ran!.MinutesLeft);
        Assert.StartsWith("Ran as a cheat (build item)", ran.Note);
        CheatRunView run = Assert.Single(cheats.Runs);
        Assert.Equal("build item", run.What);
        Assert.Equal("2026-10-19T12:00:00Z", run.AtUtc);
        Assert.Equal(9.0, run.MinutesLeft);

        // move item's typed ids act without confirm: force: true acts, so it is logged too.
        table.Run("move", new JObject { ["what"] = "item", ["force"] = true }, null, checkedAlready: true);
        Assert.Equal(2, cheats.Runs.Count);
    }

    [Fact]
    public void ACreativeWorldNeitherNotesNorLogs()
    {
        CheatSwitch cheats = Switch(new FakeClock(), creative: true);
        CheatNote.Begin();
        Assert.IsType<VerbOutcome.Answered>(Gated(cheats).Run("write", Call("genes", null), null, checkedAlready: true));
        Assert.Null(CheatNote.Take());
        Assert.Empty(cheats.Runs);
        Assert.True(cheats.View().Creative);
    }

    [Fact]
    public void TheLogKeepsTheNewestCheats()
    {
        CheatSwitch cheats = Switch(new FakeClock());
        cheats.TurnOn(60);
        VerbDispatcher table = Gated(cheats);
        for (int index = 0; index < CheatSwitch.LogSize + 5; index++)
        {
            table.Run(index == CheatSwitch.LogSize + 4 ? "move" : "write", Call(index == CheatSwitch.LogSize + 4 ? "gas" : "memory", null), null, checkedAlready: true);
        }

        Assert.Equal(CheatSwitch.LogSize, cheats.Runs.Count);
        Assert.Equal("move gas", cheats.Runs[cheats.Runs.Count - 1].What);
    }

    [Fact]
    public void TheReplyWritesTheCheatNoteBesideItsKeys()
    {
        CheatSwitch cheats = Switch(new FakeClock());
        cheats.TurnOn(3);
        VerbNotes notes = new VerbNotes();
        VerbNotes.Begin();
        VerbOutcome.Answered answered = Assert.IsType<VerbOutcome.Answered>(
            Gated(cheats).Run("write", Call("health", null), null, checkedAlready: true));
        notes.Take();
        JObject reply = JObject.FromObject(notes.Attach(answered.Reply), Api.Shared.ApiJson.Serializer);
        Assert.Equal("health", (string?)reply["what"]);
        Assert.True((bool)reply["cheat"]!["cheats_on"]!);
        Assert.Equal(3.0, (double)reply["cheat"]!["minutes_left"]!);
    }

    [Theory]
    [InlineData(new string[0], "OFF")]
    [InlineData(new[] { "cheats" }, "OFF")]
    [InlineData(new[] { "CHEATS", "On", "15" }, "ON for 15.0 more minutes")]
    [InlineData(new[] { "cheats", "on", "0" }, "minutes must be a whole number")]
    [InlineData(new[] { "cheats", "on", "1441" }, "minutes must be a whole number")]
    [InlineData(new[] { "cheats", "on", "ten" }, "minutes must be a whole number")]
    [InlineData(new[] { "cheats", "on" }, "Usage:")]
    [InlineData(new[] { "god" }, "Usage:")]
    public void TheConsoleCommandParsesOnOffAndTheState(string[] arguments, string printed)
    {
        Assert.Contains(printed, Switch(new FakeClock()).Command(arguments));
    }

    [Fact]
    public void TheConsoleCommandTurnsTheSwitchOnAndOff()
    {
        CheatSwitch cheats = Switch(new FakeClock());
        cheats.Command(new[] { "cheats", "on", "30" });
        Assert.True(cheats.IsOn);
        Assert.Contains("ON for 30.0", cheats.Command(new[] { "cheats" }));
        Assert.Contains("OFF", cheats.Command(new[] { "cheats", "off" }));
        Assert.False(cheats.IsOn);
        Assert.Contains("creative", Switch(new FakeClock(), creative: true).Command(Array.Empty<string>()));
    }
}
