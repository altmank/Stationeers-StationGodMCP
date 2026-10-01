#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>The reply envelope, game_clock and mod_info: the old StationApi shapes against the new views.</summary>
public sealed class HostWireTests
{
    [Fact]
    public void EnvelopesSameWire()
    {
        WireCheck.Same(new { id = "7", ok = true, result = new { member_count = 0 }, elapsed_ms = 1.25 },
            new ReplyView("7", new ChuteSummaryView(0), 1.25));
        WireCheck.Same(new { id = (string?)null, ok = true, result = new { member_count = 0 }, elapsed_ms = 0.5 },
            new ReplyView(null, new ChuteSummaryView(0), 0.5));
        WireCheck.Same(
            new { id = "7", ok = false, error = new { code = "method_not_found", message = "m" }, elapsed_ms = 0.1 },
            new ErrorReplyView("7", new ErrorView("method_not_found", "m"), 0.1));
        WireCheck.Same(
            new { id = "7", ok = false, error = new { code = "game_timeout", message = "t" } },
            new ErrorReplyView("7", new ErrorView("game_timeout", "t"), null));
    }

    [Fact]
    public void GameClockSameWireAfterRenames()
    {
        var old = new { game_time = 812.5f, paused = false, time_of_day = 0.25f, days_past = 3u };
        Dictionary<string, string> renames = new Dictionary<string, string>
        {
            ["game_time"] = "game_time_s",
            ["time_of_day"] = "time_of_day_ratio"
        };
        WireCheck.SameAfterRenames(old, new GameClockView(812.5f, false, 0.25f, 3u), renames);
    }

    [Fact]
    public void ModInfoSameWire()
    {
        var old = new
        {
            mod_id = "net.xceled.stationeers.stationgodmcp", mod_version = "1.0.0", assembly_version = "1.0.0.0",
            informational_version = (string?)null, pipe_name = "StationGodMCP-Test",
            methods = new List<object>
            {
                new { method = "game_clock", calls = 2L, errors = 0L, total_ms = 0.5, mean_ms = (double?)0.25,
                    max_ms = 0.3 },
                new { method = "planet", calls = 0L, errors = 0L, total_ms = 0.0, mean_ms = (double?)null,
                    max_ms = 0.0 }
            },
            count = 2,
            reflection = new List<object> { new { member = "Plant._stageTime", resolved = true, optional = false } },
            missing_count = 0
        };
        ModInfoView view = new ModInfoView(
            new ModIdentity("net.xceled.stationeers.stationgodmcp", "1.0.0", "1.0.0.0", null, "StationGodMCP-Test"),
            new List<MethodStatsView>
            {
                new MethodStatsView("game_clock", 2, 0, 0.5, 0.3), new MethodStatsView("planet", 0, 0, 0.0, 0.0)
            },
            new List<ReflectionMemberView> { new ReflectionMemberView("Plant._stageTime", true, false) }, 0,
            RuntimeWireTests.EmptyRuntime());
        // 1.9.1 added runtime at the end; everything before it is unchanged.
        WireCheck.SameAfterRenames(old, view, new Dictionary<string, string>(), "runtime");
    }
}
