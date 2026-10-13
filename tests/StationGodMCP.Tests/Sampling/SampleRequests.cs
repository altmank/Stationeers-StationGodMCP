#nullable enable

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Sampling;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Sampling;

/// <summary>read sample's requests as its options give them: ids 100, 101, ... each watched for On.</summary>
internal static class SampleRequests
{
    internal static ReadSampleOptions Options(JObject options) =>
        ReadSampleOptions.Bind(new OptionReader(ReadSampleOptions.Spec, options));

    internal static JObject Watching(int targets = 1, double seconds = 1.0, double interval = 0.05, string? gateway = null)
    {
        JArray ids = new JArray();
        for (int index = 0; index < targets; index++)
        {
            ids.Add((100 + index).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        JObject options = new JObject { ["id"] = ids, ["logic"] = "On", ["seconds"] = seconds, ["interval"] = interval };
        if (gateway != null)
        {
            options["gateway"] = gateway;
        }

        return options;
    }

    internal static SampleRequest Of(int targets = 1, double seconds = 1.0, double interval = 0.05, string? gateway = null) =>
        Of(Watching(targets, seconds, interval, gateway));

    internal static SampleRequest Of(JObject options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        SampleRequest? request = SampleRequest.Of(Options(options), problems);
        Assert.Empty(problems);
        return request!;
    }

    internal static List<OptionProblem> Problems(JObject options)
    {
        List<OptionProblem> problems = new List<OptionProblem>();
        Assert.Null(SampleRequest.Of(Options(options), problems));
        return problems;
    }
}
