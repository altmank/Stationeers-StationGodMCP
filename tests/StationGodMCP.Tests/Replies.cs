#nullable enable

using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Tests;

/// <summary>A reply (a view, a reply object, a report) as the token tree its one write gives, for tests to read.</summary>
internal static class Replies
{
    internal static JObject Tree(object reply) => JObject.Parse(ApiJson.WriteShared(reply));

    /// <summary>An answered outcome's reply as a tree.</summary>
    internal static JObject Tree(VerbOutcome outcome) => Tree(((VerbOutcome.Answered)outcome).Reply);
}
