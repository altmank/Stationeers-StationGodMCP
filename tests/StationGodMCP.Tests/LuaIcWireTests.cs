#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The IC tools' Lua additions on the wire: a Console (reference_id) holding a ScriptedScreens board (holder) with a
/// Lua chip (chip), and the lua runtime block.
/// </summary>
public sealed class LuaIcWireTests
{
    private const string Script = "local n = 0\nfunction tick(dt)\n  n = n + 1\nend\n";

    private static readonly IcPlace Console = new IcPlace("world", new ThingId(120300),
        new ThingView(new ThingId(122516), "CircuitboardIntegratedCircuit", "Circuitboard (Lua Chip)"));

    private static readonly ThingView LuaChip =
        new ThingView(new ThingId(122517), "ItemIntegratedCircuitLua", "Integrated Circuit (Lua)");

    private static readonly ChipState Clean = new ChipState(0.0, false, "", "None", "");

    private static LuaStateView Running(LuaLogView? log) =>
        new LuaStateView(new LuaRuntimeFlags(false, true, true, false), 2, null, log, null);

    [Fact]
    public void SourceNamesConsoleBoardAndChip()
    {
        JObject json = Json(new IcSourceView(Console, new IcChip(IcChip.Lua, LuaChip, Script, Running(null)), 0.0));

        Assert.Equal("120300", (string?)json["reference_id"]);
        Assert.Equal("122516", (string?)json["holder"]!["reference_id"]);
        Assert.Equal("CircuitboardIntegratedCircuit", (string?)json["holder"]!["prefab_name"]);
        Assert.Equal("122517", (string?)json["chip"]!["reference_id"]);
        Assert.Equal("lua", (string?)json["language"]);
        Assert.Equal(Script, (string?)json["source"]);
        Assert.Equal(Script.Length, (int)json["source_length"]!);
        Assert.True((bool)json["lua"]!["running"]!);
    }

    [Fact]
    public void Ic10ChipHasNoLuaBlock()
    {
        IcChip chip = new IcChip(IcChip.Ic10, LuaChip, "yield", null);
        JObject json = Json(new IcSourceSetView(Console, chip, Clean));

        Assert.Equal("ic10", (string?)json["language"]);
        Assert.Equal(JTokenType.Null, json["lua"]!.Type);
    }

    [Fact]
    public void LuaBlockAfterAWriteIsCompiling()
    {
        LuaStateView compiling = new LuaStateView(new LuaRuntimeFlags(true, false, false, false), 3, null, null, null);
        JObject lua = (JObject)Json(new IcSourceSetView(Console, new IcChip(IcChip.Lua, LuaChip, Script, compiling),
            Clean))["lua"]!;

        Assert.Equal(
            "{\"compiling\":true,\"has_runtime\":false,\"init_complete\":false,\"running\":false,\"library\":false," +
            "\"source_version\":3,\"last_error\":null,\"log\":null,\"unavailable\":null}",
            lua.ToString(Newtonsoft.Json.Formatting.None));
    }

    [Fact]
    public void LastErrorStopsRunning()
    {
        LuaErrorView error = new LuaErrorView("compile", 4, "unexpected symbol near 'end'", "");
        LuaStateView failed = new LuaStateView(new LuaRuntimeFlags(false, true, true, false), 4, error, null, null);
        JObject json = Json(failed);

        Assert.False((bool)json["running"]!);
        Assert.Equal("compile", (string?)json["last_error"]!["kind"]);
        Assert.Equal(4, (int)json["last_error"]!["line"]!);
        Assert.Equal(JTokenType.Null, json["last_error"]!["traceback"]!.Type);
    }

    [Fact]
    public void LibraryChipIsNotRunning()
    {
        LuaStateView library = new LuaStateView(new LuaRuntimeFlags(false, true, true, true), 1, null, null, null);

        Assert.False(library.Running);
    }

    [Fact]
    public void StatusCarriesBoardKindAndLogTail()
    {
        LuaLogView log = new LuaLogView(new List<string> { "tick 9", "tick 10" }, 10);
        IcRuntimeView runtime = new IcRuntimeView(new IcExecutionView(0, null, false), new List<RegisterView>(),
            new SpecialRegisters(16, 0.0, 17, 0.0), new StackWindowView(512, 0, new List<double>()),
            new IcSymbols(new List<AliasView>(), new List<DefineView>(), new List<JumpTagView>()));
        IcStatusView view = new IcStatusView(Console, new IcChip(IcChip.Lua, LuaChip, Script, Running(log)), Clean,
            new IcHolderView("computer_board", true, true, true),
            new IcRuntimeParts(new List<IcPinView>(), runtime));
        JObject json = Json(view);

        Assert.Equal("computer_board", (string?)json["housing"]!["kind"]);
        Assert.Equal(new[] { "tick 9", "tick 10" }, json["lua"]!["log"]!["lines"]!.ToObject<string[]>());
        Assert.Equal(10, (int)json["lua"]!["log"]!["line_count"]!);
        Assert.True((bool)json["lua"]!["log"]!["truncated"]!);
    }

    [Fact]
    public void RestartReportsTheLuaBlock()
    {
        IcControlView view = new IcControlView(Console, new IcControlOutcome("restart", false, 0.0), Clean,
            new IcChip(IcChip.Lua, LuaChip, Script, Running(null)));
        JObject json = Json(view);

        Assert.Equal("restart", (string?)json["action"]);
        Assert.Equal("lua", (string?)json["language"]);
        Assert.Null(json["source"]);
        Assert.True((bool)json["lua"]!["has_runtime"]!);
    }

    private static JObject Json(object view) => JObject.Parse(WireCheck.New(view));
}
