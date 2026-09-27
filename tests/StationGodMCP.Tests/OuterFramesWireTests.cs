#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>outer_frames: the old StationApi.OuterFrames shape against OuterFramesView, from the same values.</summary>
public sealed class OuterFramesWireTests
{
    [Fact]
    public void SameWire()
    {
        var old = new
        {
            frames = new List<object>
            {
                new
                {
                    reference_id = "400", prefab_name = "StructureFrame", display_name = "Steel Frame",
                    position = new { x = 1.0, y = 3.0, z = -5.0 }, distance_m = (double?)4.2,
                    exposed_faces = new List<string> { "+x", "-y" }, exposed_face_count = 2, blocks_air = true,
                    build_state = 2, color = new { index = (int?)null, name = (string?)null }
                }
            },
            count = 1,
            total_frames = 40,
            total_outer = 7,
            offset = 0,
            limit = 1,
            total = 7,
            has_more = true,
            include_inner = false,
            local_player = (object?)null
        };
        PageRequest page = PageRequest.From(new Args(JObject.Parse("{\"limit\": 1}")), 200, 1000);
        FrameView frame = new FrameView(
            new ThingView(new ThingId(400), "StructureFrame", "Steel Frame"), new PositionView(1.0, 3.0, -5.0), 4.2,
            new List<string> { "+x", "-y" }, true, 2, new ColorView(null, null));
        OuterFramesView view = new OuterFramesView(
            Slice<FrameView>.Page(new List<FrameView> { frame }, page, 7), 40, 7, false, null);
        WireCheck.Same(old, view);
    }
}
