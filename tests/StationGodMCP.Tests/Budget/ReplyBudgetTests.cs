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

    [Fact]
    public void EveryExemptionSaysWhy()
    {
        foreach (KeyValuePair<string, string> exemption in ReplyShapes.Exemptions)
        {
            Assert.True(exemption.Value.Length >= 40, $"{exemption.Key}: give the reason in a sentence.");
        }
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
