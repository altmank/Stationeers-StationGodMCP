#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>solar_aim: the old StationApi.SolarAim shapes against the new views, from the same values.</summary>
public sealed class SolarAimWireTests
{
    private static readonly SunView Sun = new SunView(0.25f, -0.5f, 0.75f, eclipse: false);
    private static readonly PanelAimView Current = new PanelAimView(123.0, 45.0, 0.875f);

    private static object OldSun => new { x = 0.25f, y = -0.5f, z = 0.75f, above_horizon = false, eclipse = false };

    private static object OldCurrent => new { horizontal = 123.0, vertical = 45.0, ratio = 0.875f };

    [Fact]
    public void FixedPanelSameWire()
    {
        var old = new
        {
            reference_id = "881",
            prefab_name = "StructureSolarPanelFlat",
            can_turn = false,
            note = "this panel has no yaw and pitch pivots, so Horizontal and Vertical writes do not move it",
            sun = OldSun,
            current = OldCurrent
        };
        WireCheck.SameAfterRenames(old,
            new SolarFixedView(new ThingId(881), "StructureSolarPanelFlat", operable: true, Sun, Current),
            new Dictionary<string, string>(), "operable");
    }

    [Fact]
    public void AnUnfinishedPanelSaysItIsNotOperable()
    {
        SolarTurnView view = new SolarTurnView(
            new ThingId(1201), "StructureSolarPanelDual", operable: false, Sun, Current, 90f, 45f, 0.0, 1.0);
        Assert.Contains("\"can_turn\":true,\"operable\":false,", WireCheck.New(view));
    }

    [Fact]
    public void TurningPanelSameWire()
    {
        float bestH = 271.34f, bestV = 44.62f;
        double offDegrees = 3.25, alignment = 0.943;
        var old = new
        {
            reference_id = "882",
            prefab_name = "StructureSolarPanel",
            can_turn = true,
            horizontal = bestH,
            vertical = bestV,
            off_sun_degrees = offDegrees,
            alignment,
            sun = OldSun,
            current = OldCurrent
        };
        SolarTurnView view = new SolarTurnView(
            new ThingId(882), "StructureSolarPanel", operable: true, Sun, Current, bestH, bestV, offDegrees, alignment);
        Dictionary<string, string> renames = new Dictionary<string, string>
        {
            ["off_sun_degrees"] = "off_sun_deg",
            ["alignment"] = "alignment_ratio"
        };
        WireCheck.SameAfterRenames(old, view, renames, "operable");
    }
}

/// <summary>
/// AngleSearch against verbatim copies of the searches it replaced (StationApi.SolarAim's local Search and
/// StationApi.DishAim's grid and compass loops): same answers and the same calls, in the same order.
/// </summary>
public sealed class AngleSearchTests
{
    private sealed class Recorder : IAngleScore
    {
        private readonly Func<float, float, float> _score;

        internal Recorder(Func<float, float, float> score)
        {
            _score = score;
        }

        internal List<(float, float)> Calls { get; } = new List<(float, float)>();

        public float Score(float horizontal, float vertical)
        {
            Calls.Add((horizontal, vertical));
            return _score(horizontal, vertical);
        }
    }

    // A smooth score with one peak, like a panel's dot product with the sun.
    private static float Peak(float h, float v) =>
        (float)(Math.Cos((h - 212.3) * Math.PI / 180.0) * Math.Cos((v - 71.9) * Math.PI / 180.0));

    // A smooth error with one trough, like a dish's angle to its contact.
    private static float Trough(float h, float v) => Math.Abs(h - 0.6123f) * 360f + Math.Abs(v - 0.3377f) * 90f;

    [Fact]
    public void SolarMaximumMatchesTheOldSearch()
    {
        Recorder expected = new Recorder(Peak);
        (float oldH, float oldV, float oldBest) = OldSolar(expected);
        Recorder actual = new Recorder(Peak);
        AngleBest found = AngleSearch.SolarMaximum(actual);
        Assert.Equal(oldH, found.Horizontal);
        Assert.Equal(oldV, found.Vertical);
        Assert.Equal(oldBest, found.Score);
        Assert.Equal(expected.Calls, actual.Calls);
    }

    [Fact]
    public void DishMinimumMatchesTheOldSearch()
    {
        Recorder expected = new Recorder(Trough);
        (float oldH, float oldV, float oldBest) = OldDish(expected);
        Recorder actual = new Recorder(Trough);
        AngleBest found = AngleSearch.DishMinimum(actual);
        Assert.Equal(oldH, found.Horizontal);
        Assert.Equal(oldV, found.Vertical);
        Assert.Equal(oldBest, found.Score);
        Assert.Equal(expected.Calls, actual.Calls);
    }

    [Theory]
    [InlineData(0.99f)]
    [InlineData(1.02f)]
    [InlineData(-0.01f)]
    public void RepeatMatchesUnityMathf(float t)
    {
        float unity = Math.Clamp(t - (float)Math.Floor(t / 1f) * 1f, 0f, 1f);
        Assert.Equal(unity, AngleSearch.Repeat(t, 1f));
    }

    private static (float, float, float) OldSolar(IAngleScore score)
    {
        float bestH = 0f, bestV = 90f, best = float.NegativeInfinity;
        void Search(float h0, float h1, float hStep, float v0, float v1, float vStep)
        {
            for (float h = h0; h <= h1; h += hStep)
            {
                for (float v = Math.Max(15f, v0); v <= Math.Min(165f, v1); v += vStep)
                {
                    float value = score.Score(h, v);
                    if (value > best)
                    {
                        best = value;
                        bestH = h;
                        bestV = v;
                    }
                }
            }
        }

        Search(0f, 358f, 2f, 15f, 165f, 2f);
        Search(bestH - 2f, bestH + 2f, 0.2f, bestV - 2f, bestV + 2f, 0.2f);
        Search(bestH - 0.2f, bestH + 0.2f, 0.02f, bestV - 0.2f, bestV + 0.2f, 0.02f);
        bestH = ((bestH % 360f) + 360f) % 360f;
        return (bestH, bestV, best);
    }

    private static (float, float, float) OldDish(IAngleScore score)
    {
        float bestH = 0f, bestV = 0f, best = float.MaxValue;
        for (int i = 0; i < 72; i++)
        {
            for (int j = 0; j <= 18; j++)
            {
                float h = i / 72f, v = j / 18f;
                float error = score.Score(h, v);
                if (error < best)
                {
                    best = error;
                    bestH = h;
                    bestV = v;
                }
            }
        }

        float stepH = 5f / 360f, stepV = 5f / 90f;
        while (stepH > 0.01f / 360f)
        {
            bool improved = false;
            foreach ((float dh, float dv) in new[] { (stepH, 0f), (-stepH, 0f), (0f, stepV), (0f, -stepV) })
            {
                float h = Math.Clamp(bestH + dh - (float)Math.Floor((bestH + dh) / 1f) * 1f, 0f, 1f);
                float v = Math.Clamp(bestV + dv, 0f, 1f);
                float error = score.Score(h, v);
                if (error < best - 1e-5f)
                {
                    best = error;
                    bestH = h;
                    bestV = v;
                    improved = true;
                }
            }

            if (!improved)
            {
                stepH *= 0.5f;
                stepV *= 0.5f;
            }
        }

        return (bestH, bestV, best);
    }
}

public sealed class SolarAlignmentTests
{
    [Fact]
    public void SquareOnIsFullAlignment()
    {
        Assert.Equal(0.0, SolarAlignment.OffDegrees(1f), 6);
        Assert.Equal(1.0, SolarAlignment.Alignment(0.0), 6);
        Assert.Equal(0.0, SolarAlignment.Alignment(180.0), 6);
    }
}
