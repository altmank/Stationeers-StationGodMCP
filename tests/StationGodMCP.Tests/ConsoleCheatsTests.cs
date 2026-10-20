#nullable enable

using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Console commands are not cheats en masse (2.7.1): server command is a cheat only for a line that changes the world
/// outside normal play, by the game's own commands word by word, Terraforming Reloaded's terraform, and anything
/// unknown; read-only and diagnostic lines run with cheats off, and a refusal names the command and why.
/// </summary>
public sealed class ConsoleCheatsTests
{
    [Theory]
    [InlineData("help")]
    [InlineData("help commands")]
    [InlineData("organs")]
    [InlineData("thing")]
    [InlineData("thing find 12345")]
    [InlineData("thing info 12345")]
    [InlineData("THING Info-Verbose 12345")]
    [InlineData("-thing checkslots 12345")]
    [InlineData("rocket print")]
    [InlineData("rocket print 4410")]
    [InlineData("rocket mounts")]
    [InlineData("rocket relink 4410")]
    [InlineData("listnetworkdevices 9001")]
    [InlineData("atmos count")]
    [InlineData("network clients")]
    [InlineData("status")]
    [InlineData("version")]
    [InlineData("world")]
    [InlineData("difficulty")]
    [InlineData("celestial Vulcan")]
    [InlineData("trader contacts")]
    [InlineData("plant genes 3311")]
    [InlineData("player list")]
    [InlineData("storm debug")]
    [InlineData("orbit")]
    [InlineData("spacemap")]
    [InlineData("save")]
    [InlineData("say hello")]
    [InlineData("terraform")]
    [InlineData("terraform status")]
    [InlineData("terraform set")]
    [InlineData("terraform set MaxPressureKPa")]
    [InlineData("terraform gas")]
    [InlineData("terraform curves export")]
    [InlineData("serverrun thing info 12345")]
    [InlineData("")]
    public void ReadOnlyAndDiagnosticCommandsAreNotCheats(string line)
    {
        Assert.Null(ConsoleCheats.Why(line));
    }

    [Theory]
    [InlineData("thing spawn ItemKitWall 5", "makes things out of nothing")]
    [InlineData("thing spawnvel ItemKitWall 1 0 0", "makes things out of nothing")]
    [InlineData("thing delete 12345", "deletes a thing")]
    [InlineData("deletenear 10", "[creative]")]
    [InlineData("teleport 100 200", "[creative]")]
    [InlineData("voxelfillnear 5", "[creative]")]
    [InlineData("deletelooseitems", "loose item")]
    [InlineData("deleteoutofbounds", "outside the playable bounds")]
    [InlineData("setbatteries", "battery")]
    [InlineData("power chargeall", "battery")]
    [InlineData("plant grow all", "grows plants")]
    [InlineData("structure completeall", "builds every structure")]
    [InlineData("difficulty Easy", "difficulty")]
    [InlineData("storm start", "starts a storm")]
    [InlineData("addgas 123 Oxygen 10", "gas")]
    [InlineData("player setmood xceled 1", "mood")]
    [InlineData("rocket refuel 4410", "refuels")]
    [InlineData("rocket relink 4410 apply", "relinks")]
    [InlineData("celestial Vulcan period 10", "orbit")]
    [InlineData("trader land 55 0", "summons")]
    [InlineData("terraform gas add Oxygen 1000", "gas")]
    [InlineData("terraform gas remove Oxygen all confirm", "gas")]
    [InlineData("terraform set MaxPressureKPa 50 confirm", "setting")]
    [InlineData("terraform size 0.5 confirm", "size")]
    [InlineData("terraform reset confirm", "planet back")]
    [InlineData("terraform curves reload", "curves")]
    [InlineData("serverrun thing spawn ItemKitWall", "makes things")]
    public void CommandsThatChangeTheWorldAreCheatsAndSayWhy(string line, string why)
    {
        Assert.Contains(why, ConsoleCheats.Why(line));
    }

    [Theory]
    [InlineData("somemodcommand do it", "'somemodcommand' is a command StationGod has not classified")]
    [InlineData("thing teleportall", "'teleportall' is a subcommand StationGod has not classified")]
    [InlineData("terraform explode", "'explode' is a subcommand")]
    [InlineData("rocket nuke 1", "'nuke' is a subcommand")]
    public void AnUnknownCommandOrSubcommandIsACheatNamedInTheReason(string line, string why)
    {
        Assert.Contains(why, ConsoleCheats.Why(line));
    }

    [Theory]
    [InlineData("thing spawn ItemKitWall 5", "thing spawn")]
    [InlineData("teleport 100 200", "teleport")]
    [InlineData("somemodcommand do it", "somemodcommand")]
    [InlineData("serverrun thing spawn X", "serverrun thing spawn")]
    public void TheCommandIsNamedByItsKeyAndSubcommand(string line, string named)
    {
        Assert.Equal(named, ConsoleCheats.Named(line));
    }

    [Fact]
    public void ServerCommandIsGatedByItsLineNotEnMasse()
    {
        JObject Line(string command) => new JObject { ["what"] = "command", ["command"] = command, ["confirm"] = true };

        Assert.Null(CheatSwitchTests.Refused("server", Line("help"), creative: false, minutesOn: null));
        Assert.Null(CheatSwitchTests.Refused("server", Line("thing info 12345"), creative: false, minutesOn: null));
        Assert.Null(CheatSwitchTests.Refused("server", Line("terraform status"), creative: false, minutesOn: null));
        Assert.Equal(CheatSwitch.OffCode,
            CheatSwitchTests.Refused("server", Line("thing spawn ItemKitWall"), creative: false, minutesOn: null));
        Assert.Equal(CheatSwitch.OffCode,
            CheatSwitchTests.Refused("server", Line("somemodcommand"), creative: false, minutesOn: null));
        Assert.Null(CheatSwitchTests.Refused("server", Line("thing spawn ItemKitWall"), creative: false, minutesOn: 5));
        Assert.Null(CheatSwitchTests.Refused("server", Line("thing spawn ItemKitWall"), creative: true, minutesOn: null));
    }

    [Fact]
    public void TheRefusalAndTheNoteNameTheCommandAndWhy()
    {
        WriteSpec write = ServerCommandOptions.Spec.Write!;
        JObject call = new JObject { ["what"] = "command", ["command"] = "thing spawn ItemKitWall 5", ["confirm"] = true };
        string label = write.CheatLabel("server command", call);
        Assert.Equal("server command 'thing spawn' (makes things out of nothing)", label);
        Assert.StartsWith("server command 'thing spawn' (makes things out of nothing) is a cheat, and cheats are off",
            CheatSwitch.Refusal(label));
        Assert.Contains("Ran as a cheat (server command 'thing spawn'", new CheatNoteView(4.0, true, label).Note);
    }

    [Fact]
    public void TheSwitchsOwnCommandIsLeftToTheConsoleOnlyRefusal()
    {
        Assert.Null(ConsoleCheats.Why("stationgod cheats on 5"));
    }
}
