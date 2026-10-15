#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// write chip's replies: its parts (WriteParts: every part checked before any acts, the refused ones refusing the
/// call at once; then in order, one part answering its reply, several {code, pins, run}, a refusal while acting
/// stopping the rest, named in not_run), and each part's reply in the catalogue's keys: code (the
/// source written, a Lua chip compiling), pins (the changes and every pin, a pin holding nothing left out) and run
/// (the action and the chip after it).
/// </summary>
public sealed class WriteChipTests
{
    private const string Script = "local n = 0\nfunction tick(dt)\n  n = n + 1\nend\n";

    private static readonly IcPlace Console = new IcPlace("world", new ThingId(120300),
        new ThingView(new ThingId(122516), "CircuitboardIntegratedCircuit", "Circuitboard (Lua Chip)"));

    private static readonly ThingView LuaChip =
        new ThingView(new ThingId(122517), "ItemIntegratedCircuitLua", "Integrated Circuit (Lua)");

    private static readonly ChipState Clean = new ChipState(0.0, false, "", "None", "");

    private static readonly LuaStateView Compiling =
        new LuaStateView(new LuaRuntimeFlags(true, false, false, false), 3, null, null, null);

    private static JObject Answered(VerbOutcome outcome) => Replies.Tree(((VerbOutcome.Answered)outcome).Reply);

    // ---- the parts ----

    private static VerbOutcome Write(string[] parts, Func<string, object> act) =>
        WriteParts.Of("write chip", parts, part => () => act(part));

    [Fact]
    public void OnePartAnswersItsOwnReply()
    {
        JObject reply = Answered(Write(new[] { "run" }, _ => new JObject { ["action"] = "pause" }));

        Assert.Equal("pause", (string?)reply["action"]);
        Assert.Null(reply["run"]);
    }

    [Fact]
    public void SeveralPartsActInOrderEachUnderItsName()
    {
        List<string> acted = new List<string>();
        JObject reply = Answered(Write(new[] { "code", "pins", "run" }, part =>
        {
            acted.Add(part);
            return new JObject { ["part"] = part };
        }));

        Assert.Equal(new[] { "code", "pins", "run" }, acted);
        Assert.Equal(new[] { "code", "pins", "run" }, reply.Properties().Select(key => key.Name));
        Assert.Null(reply["not_run"]);
    }

    [Fact]
    public void EveryPartRefusedByItsCheckRefusesTheCallAtOnceBeforeAnyActs()
    {
        List<string> checkedParts = new List<string>();
        List<string> acted = new List<string>();
        VerbOutcome outcome = WriteParts.Of("write chip", new[] { "code", "pins", "run" }, part =>
        {
            checkedParts.Add(part);
            return part == "pins"
                ? () =>
                {
                    acted.Add(part);
                    return new JObject();
                }
                : throw ApiErrors.Refused("no_chip", $"{part}: the housing has no chip.");
        });

        VerbError refused = Assert.IsType<VerbOutcome.Refused>(outcome).Error;
        Assert.Equal(new[] { "code", "pins", "run" }, checkedParts);
        Assert.Empty(acted);
        Assert.Equal("invalid_argument", refused.Code);
        Assert.Equal(new[] { "code", "run" },
            ((JArray)refused.Data!["problems"]!).Select(problem => (string?)problem["path"]));
        Assert.Contains("code: cannot be written (no_chip): code: the housing has no chip.", refused.Message);
    }

    [Fact]
    public void AFirstPartRefusedWhileActingIsTheCallsRefusalNamingWhatDidNotRun()
    {
        List<string> acted = new List<string>();
        VerbOutcome outcome = Write(new[] { "code", "run" }, part =>
        {
            acted.Add(part);
            throw ApiErrors.Refused("no_chip", "The housing has no chip.");
        });

        VerbError refused = ((VerbOutcome.Refused)outcome).Error;
        Assert.Equal(new[] { "code" }, acted);
        Assert.Equal("no_chip", refused.Code);
        Assert.Equal("The housing has no chip. Not run: run.", refused.Message);
    }

    [Fact]
    public void ALaterPartRefusedWhileActingKeepsWhatActedAndNamesTheRest()
    {
        JObject reply = Answered(Write(new[] { "code", "pins", "run" }, part => part == "pins"
            ? throw ApiErrors.Refused("not_on_data_network", "pins.d0: off the network.")
            : new JObject { ["part"] = part }));

        Assert.Equal("code", (string?)reply["code"]!["part"]);
        Assert.Equal("not_on_data_network", (string?)reply["pins"]!["code"]);
        Assert.Null(reply["run"]);
        Assert.Equal(new JArray("run"), reply["not_run"]);
    }

    [Fact]
    public void AFaultIsNoRefusal()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Write(new[] { "code" }, _ => throw new InvalidOperationException("broken")));
        Assert.Throws<InvalidOperationException>(() =>
            WriteParts.Of("write chip", new[] { "code" }, _ => throw new InvalidOperationException("broken")));
    }

    // ---- code ----

    [Fact]
    public void AnIc10SourceHasNoLuaBlockAndAPlainErrorCode()
    {
        ChipState state = new ChipState(0, true, "0", "None",
            "Error <color=red>IncorrectArgumentCount</color> at line 0\n", new CompileError(0, "IncorrectArgumentCount"));
        IcChip chip = new IcChip(IcChip.Ic10, LuaChip, "yield", null);
        JObject reply = Answered(Write(new[] { "code" },
            _ => new IcSourceSetView(Console, chip.WithoutSource(), state, new List<SourceNote>())));

        Assert.Equal("ic10", (string?)reply["language"]);
        Assert.Equal(JTokenType.Null, reply["lua"]!.Type);
        Assert.Equal("Error IncorrectArgumentCount at line 0", (string?)reply["error_code"]);
        Assert.Equal(JTokenType.Null, reply["source"]!.Type);
    }

    [Fact]
    public void ALuaSourceJustWrittenIsCompiling()
    {
        JObject lua = (JObject)Answered(Write(new[] { "code" }, _ => new IcSourceSetView(Console,
            new IcChip(IcChip.Lua, LuaChip, Script, Compiling), Clean, new List<SourceNote>())))["lua"]!;

        Assert.Equal(
            "{\"compiling\":true,\"has_runtime\":false,\"init_complete\":false,\"running\":false,\"library\":false," +
            "\"source_version\":3,\"last_error\":null,\"log\":null,\"unavailable\":null}",
            lua.ToString(Newtonsoft.Json.Formatting.None));
    }

    // ---- pins ----

    [Fact]
    public void PinsAnswerTheChangesAndEveryPin()
    {
        SetIcPinsView view = new SetIcPinsView("world", new ThingId(400), true,
            new List<PinChangeView>
            {
                new PinChangeView(0, null, new ThingId(401), true),
                new PinChangeView(3, new ThingId(402), null, true)
            },
            new List<IcPinView>
            {
                new IcPinView(0, new ThingView(new ThingId(401), "StructureGasSensor", "Gas Sensor"), "Sensor", true)
            });
        JObject reply = Answered(Write(new[] { "pins" }, _ => view));

        Assert.Equal(new[] { "gateway_id", "reference_id", "has_data_network", "changes", "pins" },
            reply.Properties().Select(key => key.Name));
        Assert.Equal(JTokenType.Null, reply["changes"]![0]!["previous_reference_id"]!.Type);
        Assert.Equal("402", (string?)reply["changes"]![1]!["previous_reference_id"]);
        Assert.Equal("Gas Sensor", (string?)reply["pins"]![0]!["display_name"]);
        Assert.True((bool)reply["pins"]![0]!["reachable"]!);
    }

    [Fact]
    public void APinWithNeitherDeviceNorAliasHoldsNothing()
    {
        Assert.True(new IcPinView(2, null, null, false).HoldsNothing);
        Assert.True(new IcPinView(2, null, string.Empty, false).HoldsNothing);
        Assert.False(new IcPinView(0, new ThingView(new ThingId(5), "StructureGasSensor", "Gas Sensor"), null, true)
            .HoldsNothing);
        Assert.False(new IcPinView(1, null, "Furnace", false).HoldsNothing);
    }

    // ---- run ----

    [Fact]
    public void ARestartAnswersTheActionAndTheLuaBlock()
    {
        JObject reply = Answered(Write(new[] { "run" }, _ => new IcControlView(Console,
            new IcControlOutcome("restart", false, 0.0), Clean, new IcChip(IcChip.Lua, LuaChip, Script, Compiling))));

        Assert.Equal("restart", (string?)reply["action"]);
        Assert.False((bool)reply["paused"]!);
        Assert.Equal("lua", (string?)reply["language"]);
        Assert.Null(reply["source"]);
        Assert.True((bool)reply["lua"]!["compiling"]!);
    }
}
