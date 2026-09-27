#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>read_memory and write_memory: the old StationApi shapes against the new views; no renames.</summary>
public sealed class MemoryWireTests
{
    [Fact]
    public void ReadAndWriteSameWire()
    {
        MemoryPlace place = new MemoryPlace("world", new ThingId(200), 10, 512);
        WireCheck.Same(
            new
            {
                gateway_id = "world", reference_id = "200", start_address = 10,
                values = new List<object> { 1.0, "NaN" }, count = 2, stack_size = (int?)512
            },
            new MemoryReadView(place, new List<double> { 1.0, double.NaN }));
        WireCheck.Same(
            new
            {
                gateway_id = "world", reference_id = "200", start_address = 10,
                requested_values = new List<double> { 3.0 }, previous_values = new List<object> { 1.0 },
                current_values = new List<object> { 3.0 }, count = 1, stack_size = (int?)512
            },
            new MemoryWriteView(place, new List<double> { 3.0 }, new List<double> { 1.0 }, new List<double> { 3.0 }));
        WireCheck.Same(
            new
            {
                gateway_id = "world", reference_id = "200", start_address = 0,
                requested_values = new List<double> { 3.0 }, previous_values = (List<object>?)null,
                current_values = (List<object>?)null, count = 1, stack_size = (int?)null
            },
            new MemoryWriteView(new MemoryPlace("world", new ThingId(200), 0, null), new List<double> { 3.0 }, null,
                null));
    }
}
