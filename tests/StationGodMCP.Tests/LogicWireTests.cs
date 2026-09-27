#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>read_logic, write_logic and their batch forms: the old StationApi shapes against the new views.</summary>
public sealed class LogicWireTests
{
    private static LogicRead Read(double value) =>
        new LogicRead(new ThingId(100), new LogicTypeView(6, "Pressure"), value);

    [Fact]
    public void ReadAndWriteSameWire()
    {
        WireCheck.Same(
            new
            {
                gateway_id = "world", reference_id = "100", logic_type = new { id = (ushort)6, name = "Pressure" },
                value = (object)"Infinity"
            },
            new LogicReadView("world", Read(double.PositiveInfinity)));
        WireCheck.Same(
            new
            {
                gateway_id = "55", reference_id = "100", logic_type = new { id = (ushort)6, name = "Pressure" },
                requested_value = 5.0, previous_value = (object?)null, current_value = (object?)5.0
            },
            new LogicWriteView("55", new LogicWrite(Read(5.0), null, 5.0)));
    }

    [Fact]
    public void BatchesSameWire()
    {
        var old = new
        {
            gateway_id = "world",
            results = new List<object>
            {
                new
                {
                    index = 0, ok = true, reference_id = "100", logic_type = new { id = (ushort)6, name = "Pressure" },
                    requested_value = 1.0, previous_value = (object?)0.0, current_value = (object?)1.0
                },
                new
                {
                    index = 1, ok = false,
                    error = new { code = "logic_not_writable", message = "Device 100 does not expose On (12)." }
                }
            },
            count = 2, success_count = 1, error_count = 1
        };
        BatchBuilder batch = new BatchBuilder(2);
        batch.Succeeded(new LogicWriteItemView(0, new LogicWrite(Read(1.0), 0.0, 1.0)));
        batch.Failed(1, ApiErrors.Refused("logic_not_writable", "Device 100 does not expose On (12)."));
        WireCheck.Same(old, new LogicBatchView("world", batch.Build()));

        var oldReads = new
        {
            gateway_id = "world",
            results = new List<object>
            {
                new
                {
                    index = 0, ok = true, reference_id = "100", logic_type = new { id = (ushort)6, name = "Pressure" },
                    value = (object)101.3
                }
            },
            count = 1, success_count = 1, error_count = 0
        };
        BatchBuilder reads = new BatchBuilder(1);
        reads.Succeeded(new LogicReadItemView(0, Read(101.3)));
        WireCheck.Same(oldReads, new LogicBatchView("world", reads.Build()));
    }
}
