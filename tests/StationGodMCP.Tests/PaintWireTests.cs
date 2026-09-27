#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>paint: the old StationApi.Paint shapes against the new views, from the same values. No renames.</summary>
public sealed class PaintWireTests
{
    [Fact]
    public void ColorListSameWire()
    {
        var old = new
        {
            colors = new List<object>
            {
                new { index = 0, name = "Blue", paint_only = false },
                new { index = 1, name = (string?)null, paint_only = true }
            },
            count = 2
        };
        PaintColorsView view = new PaintColorsView(new List<PaintColorView>
        {
            new PaintColorView(0, "Blue", false),
            new PaintColorView(1, null, true)
        });
        WireCheck.Same(old, view);
    }

    [Fact]
    public void ResultsSameWire()
    {
        object previous = new { index = (int?)3, name = "Orange", is_default = true };
        object after = new { index = (int?)0, name = "Blue", is_default = false };
        var old = new
        {
            results = new List<object>
            {
                new { index = 0, ok = true, reference_id = "11", previous_color = previous, color = after },
                new
                {
                    index = 1, ok = false, reference_id = "12", previous_color = previous,
                    error = new { code = "not_paintable", message = "no" }
                },
                new
                {
                    index = 2, ok = false, reference_id = (string?)null, previous_color = (object?)null,
                    error = new { code = "invalid_argument", message = "bad id" }
                }
            },
            count = 3,
            success_count = 1,
            error_count = 2
        };
        ThingColorView was = new ThingColorView(3, "Orange", true);
        BatchBuilder batch = new BatchBuilder(3);
        batch.Succeeded(new PaintedView(0, new ThingId(11), was, new ThingColorView(0, "Blue", false)));
        batch.Failed(new NotPaintedView(1, new ThingId(12), was, ApiErrors.Refused("not_paintable", "no")));
        batch.Failed(new NotPaintedView(2, null, null, ApiErrors.InvalidArgument("bad id")));
        WireCheck.Same(old, batch.Build());
    }
}
