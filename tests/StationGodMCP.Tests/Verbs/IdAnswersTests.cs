#nullable enable

using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// A per-id read answers a refusal as a value (an ApiException built, not thrown): a list's refused id is its row's
/// error, as a thrown one still is.
/// </summary>
public class IdAnswersTests
{
    [Fact]
    public void ARefusalAnsweredAsAValueIsItsRowsError()
    {
        Rows rows = new Rows();
        rows.Add("1", () => new JObject { ["seen"] = 1 });
        rows.Add("2", () => ApiErrors.ThingNotFound(new ThingId(2)));
        rows.Add("3", () => throw ApiErrors.Refused("device_not_found", "No device 3."));

        JObject reply = Replies.Tree(rows.Reply());

        Assert.Equal((2, 1), ((int)reply["error_count"]!, (int)reply["success_count"]!));
        Assert.Equal(("2", "thing_not_found"),
            ((string?)reply["results"]![1]!["reference_id"], (string?)reply["results"]![1]!["error"]!["code"]));
        Assert.Equal("device_not_found", (string?)reply["results"]![2]!["error"]!["code"]);
        Assert.Equal(1, (int)reply["results"]![0]!["seen"]!);
    }
}
