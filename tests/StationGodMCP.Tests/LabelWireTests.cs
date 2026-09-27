#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>label: the wire shape of a rename and of a refused one.</summary>
public sealed class LabelWireTests
{
    [Fact]
    public void RenameReportsBeforeAndAfter()
    {
        var expected = new
        {
            index = 0, ok = true, reference_id = "91234", prefab_name = "DynamicMKIILiquidCanisterEmpty",
            written = "T2", sent_to_host = false,
            previous = new { display_name = "T1", custom_name = (string?)"T1" },
            current = new { display_name = "T2", custom_name = (string?)"T2" }
        };
        WireCheck.Same(expected,
            new LabelledView(0, new ThingView(new ThingId(91234), "DynamicMKIILiquidCanisterEmpty", "T1"), "T2",
                false, new LabelStateView("T1", "T1"), new LabelStateView("T2", "T2")));
    }

    [Fact]
    public void BatchWithRefusal()
    {
        var expected = new
        {
            results = new object[]
            {
                new
                {
                    index = 0, ok = true, reference_id = "91234", prefab_name = "DynamicGasCanisterEmpty",
                    written = "Portable Gas Tank", sent_to_host = false,
                    previous = new { display_name = "Old", custom_name = (string?)"Old" },
                    current = new
                    {
                        display_name = "Portable Gas Tank", custom_name = (string?)"Portable Gas Tank"
                    }
                },
                new
                {
                    index = 1, ok = false, reference_id = (string?)"555",
                    error = new { code = "not_labelable", message = "no" }
                }
            },
            count = 2, success_count = 1, error_count = 1
        };
        BatchBuilder batch = new BatchBuilder(2);
        batch.Succeeded(new LabelledView(0,
            new ThingView(new ThingId(91234), "DynamicGasCanisterEmpty", "Old"), "Portable Gas Tank", false,
            new LabelStateView("Old", "Old"), new LabelStateView("Portable Gas Tank", "Portable Gas Tank")));
        batch.Failed(new NotLabelledView(1, new ThingId(555), ApiErrors.Refused("not_labelable", "no")));
        WireCheck.Same(expected, batch.Build());
    }
}
