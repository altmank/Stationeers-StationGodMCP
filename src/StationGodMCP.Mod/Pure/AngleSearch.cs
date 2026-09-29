#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>How good an aim is at one pair of angles. The search calls it in a fixed order.</summary>
internal interface IAngleScore
{
    float Score(float horizontal, float vertical);
}

/// <summary>The best pair of angles a search found, and its score.</summary>
internal sealed class AngleBest
{
    internal AngleBest(float horizontal, float vertical, float score)
    {
        Horizontal = horizontal;
        Vertical = vertical;
        Score = score;
    }

    internal float Horizontal { get; }

    internal float Vertical { get; }

    internal float Score { get; }
}

/// <summary>
/// The two angle searches the aiming tools use. Both are exhaustive at a coarse step and then refine, and both keep
/// the first of equal scores, so a given score function always gives the same answer.
/// </summary>
internal static class AngleSearch
{
    /// <summary>
    /// solar_aim: the highest score over horizontal 0..358 in 2 degree steps and vertical 15..165 in 2 degree
    /// steps, then two refinements of +-2 in 0.2 steps and +-0.2 in 0.02 steps, the vertical held inside 15..165.
    /// The horizontal comes back in 0..360.
    /// </summary>
    internal static AngleBest SolarMaximum(IAngleScore score)
    {
        GridMaximum search = new GridMaximum(score, SolarVerticalMin, SolarVerticalMax);
        search.Scan(0f, 358f, 2f, SolarVerticalMin, SolarVerticalMax, 2f);
        search.Scan(search.BestH - 2f, search.BestH + 2f, 0.2f, search.BestV - 2f, search.BestV + 2f, 0.2f);
        search.Scan(search.BestH - 0.2f, search.BestH + 0.2f, 0.02f, search.BestV - 0.2f, search.BestV + 0.2f, 0.02f);
        return new AngleBest(WrapTurn(search.BestH), search.BestV, search.Best);
    }

    /// <summary>
    /// solar_aim with the panel's current pose: every Horizontal H and Vertical V has a twin, H + 180 and 180 - V, that
    /// turns the cells the same way, so whenever the sun is high enough for both, the two score the same and
    /// SolarMaximum's pick between them flips with rounding from one call to the next. This refines the twin of
    /// SolarMaximum's answer the same way (+-2 in 0.2 steps, then +-0.2 in 0.02 steps) and, when it comes within
    /// TwinToleranceDeg of the best off-sun angle, answers whichever of the two is the smaller turn from the current
    /// pose (the larger of the horizontal and vertical turns, the horizontal wrapping), so a tracker writing every answer
    /// never swings the panel round. A twin that is worse (the sun out of its tilt range) is never taken.
    /// </summary>
    internal static AngleBest SolarNearest(IAngleScore score, float currentHorizontal, float currentVertical)
    {
        AngleBest best = SolarMaximum(score);
        AngleBest twin = SolarRefine(score, best.Horizontal + HalfTurn, HalfTurn - best.Vertical);
        bool tied = SolarAlignment.OffDegrees(twin.Score) - SolarAlignment.OffDegrees(best.Score) <= TwinToleranceDeg;
        return tied && TurnDegrees(twin, currentHorizontal, currentVertical) <
            TurnDegrees(best, currentHorizontal, currentVertical)
            ? twin
            : best;
    }

    /// <summary>The largest off-sun difference, in degrees, at which solar_aim treats the twin pose as equally good.</summary>
    internal const float TwinToleranceDeg = 0.1f;

    // The two SolarMaximum refinements around a seed, the horizontal brought back into 0..360.
    private static AngleBest SolarRefine(IAngleScore score, float seedHorizontal, float seedVertical)
    {
        GridMaximum search = new GridMaximum(score, SolarVerticalMin, SolarVerticalMax);
        search.Scan(seedHorizontal - 2f, seedHorizontal + 2f, 0.2f, seedVertical - 2f, seedVertical + 2f, 0.2f);
        search.Scan(search.BestH - 0.2f, search.BestH + 0.2f, 0.02f, search.BestV - 0.2f, search.BestV + 0.2f, 0.02f);
        return new AngleBest(WrapTurn(search.BestH), search.BestV, search.Best);
    }

    // The larger of the two turns from the current pose to a pose; the horizontal the short way round.
    private static float TurnDegrees(AngleBest pose, float currentHorizontal, float currentVertical)
    {
        float horizontal = Math.Abs(WrapTurn(pose.Horizontal - currentHorizontal));
        return Math.Max(Math.Min(horizontal, FullTurn - horizontal), Math.Abs(pose.Vertical - currentVertical));
    }

    private static float WrapTurn(float degrees) => ((degrees % FullTurn) + FullTurn) % FullTurn;

    private const float HalfTurn = 180f;

    internal const float SolarVerticalMin = 15f;
    internal const float SolarVerticalMax = 165f;
    private const float FullTurn = 360f;

    /// <summary>
    /// dish_aim: the lowest score over both angles as fractions 0..1, first on a 72 x 19 grid (horizontal i/72,
    /// vertical j/18), then by a compass search that tries +-step on each angle in turn (horizontal wrapping, vertical
    /// clamped), keeps any gain of more than 1e-5 and halves the steps when none helps, from 5/360 and 5/90 down to
    /// 0.01/360 of the horizontal.
    /// </summary>
    internal static AngleBest DishMinimum(IAngleScore score)
    {
        float bestH = 0f, bestV = 0f, best = float.MaxValue;
        for (int i = 0; i < DishHorizontalSteps; i++)
        {
            for (int j = 0; j <= DishVerticalSteps; j++)
            {
                float h = i / (float)DishHorizontalSteps, v = j / (float)DishVerticalSteps;
                float error = score.Score(h, v);
                if (error < best)
                {
                    best = error;
                    bestH = h;
                    bestV = v;
                }
            }
        }

        return CompassSearch(score, bestH, bestV, best);
    }

    private const int DishHorizontalSteps = 72;
    private const int DishVerticalSteps = 18;
    private const float DishMinimumGain = 1e-5f;

    private static AngleBest CompassSearch(IAngleScore score, float bestH, float bestV, float best)
    {
        float stepH = 5f / 360f, stepV = 5f / 90f;
        while (stepH > 0.01f / 360f)
        {
            bool improved = false;
            for (int move = 0; move < 4; move++)
            {
                float dh = move == 0 ? stepH : move == 1 ? -stepH : 0f;
                float dv = move == 2 ? stepV : move == 3 ? -stepV : 0f;
                float h = Repeat(bestH + dh, 1f), v = Clamp01(bestV + dv);
                float error = score.Score(h, v);
                if (error < best - DishMinimumGain)
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

        return new AngleBest(bestH, bestV, best);
    }

    // UnityEngine.Mathf.Repeat and Clamp01, reproduced so this file needs no Unity types.
    internal static float Repeat(float t, float length) =>
        Clamp(t - (float)Math.Floor(t / length) * length, 0f, length);

    internal static float Clamp01(float value) => value < 0f ? 0f : value > 1f ? 1f : value;

    private static float Clamp(float value, float min, float max) => value < min ? min : value > max ? max : value;

    /// <summary>The solar grid search: every pair on a stepped grid, the first highest score kept.</summary>
    private sealed class GridMaximum
    {
        private readonly IAngleScore _score;
        private readonly float _verticalFloor;
        private readonly float _verticalCeiling;

        internal GridMaximum(IAngleScore score, float verticalFloor, float verticalCeiling)
        {
            _score = score;
            _verticalFloor = verticalFloor;
            _verticalCeiling = verticalCeiling;
        }

        internal float BestH { get; private set; }

        internal float BestV { get; private set; } = 90f;

        internal float Best { get; private set; } = float.NegativeInfinity;

        internal void Scan(float h0, float h1, float hStep, float v0, float v1, float vStep)
        {
            for (float h = h0; h <= h1; h += hStep)
            {
                for (float v = Math.Max(_verticalFloor, v0); v <= Math.Min(_verticalCeiling, v1); v += vStep)
                {
                    float value = _score.Score(h, v);
                    if (value > Best)
                    {
                        Best = value;
                        BestH = h;
                        BestV = v;
                    }
                }
            }
        }
    }
}
