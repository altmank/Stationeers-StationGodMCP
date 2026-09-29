#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Device fixes from the headless live test of 1.4.4 (2026-09-29): memory ranges checked against the stack size,
/// colours not in the game's list, and failed batch logic entries that name what they asked for.
/// </summary>
public sealed class DevicesLiveTestTests
{
    [Theory]
    [InlineData(0, 32)]
    [InlineData(31, 1)]
    [InlineData(10, 22)]
    public void ARangeInsideTheStackFits(int start, int count) => Assert.Null(MemoryRange.Problem(start, count, 32));

    [Theory]
    [InlineData(32, 1)]
    [InlineData(30, 3)]
    [InlineData(0, 512)]
    [InlineData(31, 2)]
    [InlineData(2147483647, 1)]
    public void ARangePastTheLogicSortersStackIsRefused(int start, int count)
    {
        string? problem = MemoryRange.Problem(start, count, 32);
        Assert.NotNull(problem);
        Assert.Contains("addresses 0 to 31", problem);
    }

    [Fact]
    public void AnEmptyMemoryHoldsNothing() =>
        Assert.Contains("holds no values", MemoryRange.Problem(0, 1, 0));

    [Fact]
    public void AnUnknownSizeChecksOnlyTheAddressSpace()
    {
        Assert.Null(MemoryRange.Problem(1000, 512, null));
        Assert.Contains("address space", MemoryRange.Problem(int.MaxValue, 2, null));
    }

    [Fact]
    public void AColourOutsideTheListHasNoNameOrIndex()
    {
        WireCheck.Same(new { index = (int?)null, name = (string?)null, is_default = false },
            ThingColorView.Of(-1, "<A:EN:0>", false));
        WireCheck.Same(new { index = (int?)4, name = "Red", is_default = false }, ThingColorView.Of(4, "Red", false));
    }

    [Fact]
    public void AFailedLogicEntryNamesItsDeviceAndType()
    {
        var old = new
        {
            gateway_id = "world",
            results = new List<object>
            {
                new
                {
                    index = 0, ok = false, reference_id = "377", logic_type = new { id = (ushort)12, name = "Power" },
                    error = new { code = "logic_not_writable", message = "no" }
                },
                new
                {
                    index = 1, ok = false, reference_id = (string?)null, logic_type = (object?)null,
                    error = new { code = "invalid_argument", message = "bad" }
                }
            },
            count = 2, success_count = 0, error_count = 2
        };
        BatchBuilder batch = new BatchBuilder(2);
        batch.Failed(new LogicFailedItemView(0, new ThingId(377), new LogicTypeView(12, "Power"),
            ApiErrors.Refused("logic_not_writable", "no")));
        batch.Failed(new LogicFailedItemView(1, null, null, ApiErrors.InvalidArgument("bad")));
        WireCheck.Same(old, new LogicBatchView("world", batch.Build()));
    }
}
