#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>move_item: the reply shapes the tool description promises.</summary>
public sealed class MoveItemViewTests
{
    [Fact]
    public void MovedItemShape()
    {
        ItemMovedView moved = new ItemMovedView(
            0, new ThingId(110045), new SlotRefView(new ThingId(57267), 2), new SlotRefView(new ThingId(273), 3),
            100, null, new ThingId(110045));
        Assert.Equal(
            "{\"index\":0,\"ok\":true,\"reference_id\":\"110045\",\"from\":{\"id\":\"57267\",\"slot\":2},"
            + "\"to\":{\"id\":\"273\",\"slot\":3},\"quantity_moved\":100,\"merged_into\":null,"
            + "\"destination_reference_id\":\"110045\"}",
            WireCheck.New(moved));
    }

    [Fact]
    public void RefusedMoveInABatch()
    {
        BatchBuilder batch = new BatchBuilder(1);
        batch.Failed(new ItemNotMovedView(0, new ThingId(110046), ApiErrors.Refused("stack_full", "full")));
        Assert.Equal(
            "{\"results\":[{\"index\":0,\"ok\":false,\"reference_id\":\"110046\","
            + "\"error\":{\"code\":\"stack_full\",\"message\":\"full\"}}],"
            + "\"count\":1,\"success_count\":0,\"error_count\":1}",
            WireCheck.New(batch.Build()));
    }
}
