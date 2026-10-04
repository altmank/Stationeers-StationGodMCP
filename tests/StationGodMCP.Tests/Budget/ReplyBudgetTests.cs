#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using StationGodMCP.Api.Shared;
using Xunit;

namespace StationGodMCP.Tests.Budget;

/// <summary>
/// Every tool's default reply on a large world stays under the budget. Each method of the catalogue has a ReplyShape
/// (its default reply's view, with every list's length on LargeWorld and why) or a reasoned exemption, so a new tool
/// fails here until it is placed; a list no shape bounds gets ReplySynth.UndeclaredLength entries and fails until its
/// bound is declared.
/// </summary>
public sealed class ReplyBudgetTests
{
    /// <summary>The most a default reply may weigh, in UTF-8 bytes of the mod's compact JSON.</summary>
    internal const int BudgetBytes = 8192;

    public static IEnumerable<object[]> Shaped() =>
        ReplyShapes.ByMethod.Keys.OrderBy(name => name, StringComparer.Ordinal).Select(name => new object[] { name });

    [Fact]
    public void EveryMethodHasAShapeOrAnExemption()
    {
        HashSet<string> methods = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement method in StationGodMCP.Client.GameCatalogue.BuiltIn.Document.GetProperty("methods").EnumerateArray())
        {
            if (!method.TryGetProperty("x-mcp", out JsonElement mcp) || mcp.GetString() != "hidden")
            {
                methods.Add(method.GetProperty("name").GetString()!);
            }
        }

        List<string> unplaced = methods.Where(name => !ReplyShapes.ByMethod.ContainsKey(name) &&
                                                      !ReplyShapes.Exemptions.ContainsKey(name)).ToList();
        List<string> both = ReplyShapes.ByMethod.Keys.Where(ReplyShapes.Exemptions.ContainsKey).ToList();
        List<string> unknown = ReplyShapes.ByMethod.Keys.Concat(ReplyShapes.Exemptions.Keys).Where(name => !methods.Contains(name)).ToList();

        Assert.True(unplaced.Count == 0, "No reply shape or exemption: " + string.Join(", ", unplaced));
        Assert.True(both.Count == 0, "Both shaped and exempt: " + string.Join(", ", both));
        Assert.True(unknown.Count == 0, "Not a catalogue method: " + string.Join(", ", unknown));
    }

    [Fact]
    public void EveryExemptionSaysWhy()
    {
        foreach (KeyValuePair<string, string> exemption in ReplyShapes.Exemptions)
        {
            Assert.True(exemption.Value.Length >= 40, $"{exemption.Key}: give the reason in a sentence.");
        }
    }

    [Theory]
    [MemberData(nameof(Shaped))]
    public void TheDefaultReplyFitsTheBudget(string method)
    {
        List<string> failures = new List<string>();
        foreach (ReplyShape shape in ReplyShapes.ByMethod[method])
        {
            Measured measured = Measure(shape.WithTopLimits(DefaultLimitsOf(method)));
            if (measured.Undeclared.Count > 0)
            {
                failures.Add($"{shape.View.Name}: lists with no declared bound: {string.Join(", ", measured.Undeclared)}");
            }
            else if (measured.Bytes > BudgetBytes)
            {
                failures.Add($"{shape.View.Name}: {measured.Bytes} bytes, over the {BudgetBytes} budget");
            }
        }

        Assert.True(failures.Count == 0, method + ": " + string.Join("; ", failures));
    }

    /// <summary>The method's x-default-limits from the built-in catalogue.</summary>
    internal static IReadOnlyDictionary<string, int> DefaultLimitsOf(string name)
    {
        Dictionary<string, int> limits = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JsonElement method in StationGodMCP.Client.GameCatalogue.BuiltIn.Document.GetProperty("methods").EnumerateArray())
        {
            if (method.GetProperty("name").GetString() == name && method.TryGetProperty("x-default-limits", out JsonElement given))
            {
                foreach (JsonProperty limit in given.EnumerateObject())
                {
                    limits[limit.Name] = limit.Value.GetInt32();
                }
            }
        }

        return limits;
    }

    internal static Measured Measure(ReplyShape shape)
    {
        ReplySynth synth = new ReplySynth(shape.Over(ReplyShapes.Common));
        string json = ApiJson.WriteFresh(synth.Make(shape.View));
        return new Measured(Encoding.UTF8.GetByteCount(json),
            synth.Undeclared.OrderBy(member => member, StringComparer.Ordinal).ToList(), json);
    }

    internal sealed class Measured
    {
        internal Measured(int bytes, List<string> undeclared, string json)
        {
            Bytes = bytes;
            Undeclared = undeclared;
            Json = json;
        }

        internal int Bytes { get; }

        internal List<string> Undeclared { get; }

        internal string Json { get; }
    }
}
