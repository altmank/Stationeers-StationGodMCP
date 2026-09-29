#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>Where something should point: a world axis, a point (the player, a thing), or into the room.</summary>
internal abstract class OrientTarget
{
    private OrientTarget()
    {
    }

    /// <summary>
    /// How well a direction leaving a point agrees with the target: 1 straight at it, 0 across, -1 away. roomAhead
    /// says whether the 2 m cell a metre out along the direction is in a room.
    /// </summary>
    internal abstract double Alignment(Vec3 from, GridStep direction, Func<Vec3, GridStep, bool> roomAhead);

    internal abstract string Describe();

    internal sealed class Along : OrientTarget
    {
        internal Along(GridStep axis)
        {
            Axis = axis;
        }

        internal GridStep Axis { get; }

        internal override double Alignment(Vec3 from, GridStep direction, Func<Vec3, GridStep, bool> roomAhead) =>
            Vec3.Of(direction).Dot(Vec3.Of(Axis));

        internal override string Describe() => Axis.Name;
    }

    internal sealed class At : OrientTarget
    {
        internal At(Vec3 point, string name)
        {
            Point = point;
            Name = name;
        }

        internal Vec3 Point { get; }

        internal string Name { get; }

        internal override double Alignment(Vec3 from, GridStep direction, Func<Vec3, GridStep, bool> roomAhead)
        {
            Vec3 towards = (Point - from).Normalized;
            return towards.Length < 1e-9 ? 0.0 : Vec3.Of(direction).Dot(towards);
        }

        internal override string Describe() => Name;
    }

    internal sealed class IntoRoom : OrientTarget
    {
        internal static readonly IntoRoom Instance = new IntoRoom();

        internal override double Alignment(Vec3 from, GridStep direction, Func<Vec3, GridStep, bool> roomAhead) =>
            roomAhead(from, direction) ? 1.0 : roomAhead(from, direction.Opposite) ? -1.0 : 0.0;

        internal override string Describe() => "the room";
    }
}

/// <summary>What surface a piece should rest on: any wall, the floor, the ceiling, or the surface on one side.</summary>
internal abstract class MountIntent
{
    private MountIntent()
    {
    }

    /// <summary>Whether a piece whose back points that way rests as asked.</summary>
    internal abstract bool Accepts(GridStep back);

    internal abstract string Describe();

    internal static MountIntent Wall { get; } = new Kind("a wall", back => !back.IsVertical);

    internal static MountIntent Floor { get; } = new Kind("the floor", back => back.Equals(GridStep.All[3]));

    internal static MountIntent Ceiling { get; } = new Kind("the ceiling", back => back.Equals(GridStep.All[2]));

    internal static MountIntent Side(GridStep back) => new Kind($"the surface on its {back.Name} side",
        step => step.Equals(back));

    private sealed class Kind : MountIntent
    {
        private readonly string _name;
        private readonly Func<GridStep, bool> _accepts;

        internal Kind(string name, Func<GridStep, bool> accepts)
        {
            _name = name;
            _accepts = accepts;
        }

        internal override bool Accepts(GridStep back) => _accepts(back);

        internal override string Describe() => _name;
    }
}

/// <summary>A port to point somewhere: picked by role name (Input, Output, ...) or index, optionally by type.</summary>
internal sealed class PortIntent
{
    internal PortIntent(string? role, int? index, string? type, OrientTarget toward)
    {
        Role = role;
        Index = index;
        Type = type;
        Toward = toward;
    }

    internal string? Role { get; }

    internal int? Index { get; }

    internal string? Type { get; }

    internal OrientTarget Toward { get; }

    internal bool Matches(OrientPort port) =>
        (Index == null || port.Index == Index) &&
        (Role == null || string.Equals(port.Role, Role, StringComparison.OrdinalIgnoreCase)) &&
        (Type == null || port.Type.IndexOf(Type, StringComparison.OrdinalIgnoreCase) >= 0);

    internal string Describe() =>
        Index.HasValue ? $"port {Index}" : $"the {(Type != null ? Type + " " : string.Empty)}{Role ?? "port"}";
}

/// <summary>place_structure's orient: what the piece's turn should achieve.</summary>
internal sealed class OrientIntent
{
    internal OrientIntent(MountIntent? mount, bool upright, OrientTarget? controlsToward, List<PortIntent> ports,
        OrientTarget? flowFrom, OrientTarget? flowTo)
    {
        Mount = mount;
        Upright = upright;
        ControlsToward = controlsToward;
        Ports = ports;
        FlowFrom = flowFrom;
        FlowTo = flowTo;
    }

    internal MountIntent? Mount { get; }

    internal bool Upright { get; }

    internal OrientTarget? ControlsToward { get; }

    internal List<PortIntent> Ports { get; }

    internal OrientTarget? FlowFrom { get; }

    internal OrientTarget? FlowTo { get; }
}

/// <summary>A port of a candidate: where its joining cell is and which way a pipe or cable leaves it.</summary>
internal sealed class OrientPort
{
    internal OrientPort(int index, string type, string role, string? flow, Vec3 cell, GridStep outward)
    {
        Index = index;
        Type = type;
        Role = role;
        Flow = flow;
        Cell = cell;
        Outward = outward;
    }

    internal int Index { get; }

    internal string Type { get; }

    internal string Role { get; }

    /// <summary>in, out or null (PortFlow).</summary>
    internal string? Flow { get; }

    /// <summary>The joining cell's centre, metres.</summary>
    internal Vec3 Cell { get; }

    /// <summary>The way a run leaves the port (the opposite of the end it needs toward the device).</summary>
    internal GridStep Outward { get; }

    internal OrientPort Reversed() =>
        new OrientPort(Index, Type, Role, Flow == "in" ? "out" : Flow == "out" ? "in" : null, Cell, Outward);
}

/// <summary>One turn the search tried: where it stands, its ports, and what the layout preview found.</summary>
internal sealed class OrientCandidate
{
    internal OrientCandidate(CubeRotation turn, GridStep back, GridStep front, Vec3 position, List<OrientPort> ports,
        string? refusal, int layoutPenalty, string? notUpright)
    {
        Turn = turn;
        Back = back;
        Front = front;
        Position = position;
        Ports = ports;
        Refusal = refusal;
        LayoutPenalty = layoutPenalty;
        NotUpright = notUpright;
    }

    internal CubeRotation Turn { get; }

    /// <summary>The way to the surface it rests on: -forward when mounted, -up when standing.</summary>
    internal GridStep Back { get; }

    /// <summary>Its front: where controls and screens face.</summary>
    internal GridStep Front { get; }

    internal Vec3 Position { get; }

    internal List<OrientPort> Ports { get; }

    /// <summary>Why the game's cursor would not build it so; null when it would.</summary>
    internal string? Refusal { get; }

    /// <summary>The layout preview's conflicts weighed (LayoutPreview.Penalty).</summary>
    internal int LayoutPenalty { get; }

    /// <summary>Uprightness.Problem for the prefab's visual up; null when upright.</summary>
    internal string? NotUpright { get; }
}

/// <summary>A candidate's score (lower is better) and why; Excluded when it cannot be built or misses the mount.</summary>
internal sealed class OrientScore
{
    internal OrientScore(OrientCandidate candidate, double score, List<string> reasons, bool excluded,
        bool reversedFlow)
    {
        Candidate = candidate;
        Score = score;
        Reasons = reasons;
        Excluded = excluded;
        ReversedFlow = reversedFlow;
    }

    internal OrientCandidate Candidate { get; }

    internal double Score { get; }

    internal List<string> Reasons { get; }

    internal bool Excluded { get; }

    /// <summary>Scored with the device's flow reversed (a turbo volume pump with Mode 1).</summary>
    internal bool ReversedFlow { get; }
}

/// <summary>
/// Scores the turns of a placement against an OrientIntent. A turn the cursor refuses, or that does not rest on the
/// asked surface, is excluded. The rest pay: not upright (when asked) 20; each target missed (front, each port, each
/// end of the flow) up to 10 by how far its direction turns away (1 - alignment) * 5; the layout preview's conflicts
/// (a problem 100, a warning 10, info 1). Lowest wins; ties keep the search order.
/// </summary>
internal static class OrientSearch
{
    internal const double UprightCost = 20.0;
    internal const double MissWeight = 5.0;

    internal static OrientScore Score(OrientCandidate candidate, OrientIntent intent,
        Func<Vec3, GridStep, bool> roomAhead, bool reversedFlow = false)
    {
        List<string> reasons = new List<string>();
        if (candidate.Refusal != null)
        {
            reasons.Add("cursor: " + candidate.Refusal);
            return new OrientScore(candidate, double.PositiveInfinity, reasons, true, reversedFlow);
        }

        if (intent.Mount != null && !intent.Mount.Accepts(candidate.Back))
        {
            reasons.Add($"rests toward {candidate.Back.Name}, not on {intent.Mount.Describe()}");
            return new OrientScore(candidate, double.PositiveInfinity, reasons, true, reversedFlow);
        }

        double score = candidate.LayoutPenalty;
        if (candidate.LayoutPenalty > 0)
        {
            reasons.Add($"layout conflicts weigh {candidate.LayoutPenalty}");
        }

        if (intent.Upright && candidate.NotUpright != null)
        {
            score += UprightCost;
            reasons.Add("not upright: " + candidate.NotUpright);
        }

        if (intent.ControlsToward != null)
        {
            score += Miss(intent.ControlsToward, candidate.Position, candidate.Front, "front", reasons, roomAhead);
        }

        List<OrientPort> ports = reversedFlow ? candidate.Ports.ConvertAll(port => port.Reversed()) : candidate.Ports;
        foreach (PortIntent wanted in intent.Ports)
        {
            OrientPort? port = ports.Find(wanted.Matches);
            if (port == null)
            {
                score += 2 * MissWeight;
                reasons.Add($"{wanted.Describe()}: no such port");
                continue;
            }

            score += Miss(wanted.Toward, port.Cell, port.Outward, $"port {port.Index}", reasons, roomAhead);
        }

        foreach (OrientPort port in ports)
        {
            OrientTarget? target = port.Flow == "in" ? intent.FlowFrom : port.Flow == "out" ? intent.FlowTo : null;
            if (target != null)
            {
                score += Miss(target, port.Cell, port.Outward, $"port {port.Index} ({port.Flow})", reasons,
                    roomAhead);
            }
        }

        return new OrientScore(candidate, score, reasons, false, reversedFlow);
    }

    /// <summary>The scores sorted best first, excluded last; a stable sort keeps the search order on ties.</summary>
    internal static List<OrientScore> Rank(List<OrientScore> scores)
    {
        List<(OrientScore Score, int Order)> ordered = new List<(OrientScore, int)>(scores.Count);
        for (int index = 0; index < scores.Count; index++)
        {
            ordered.Add((scores[index], index));
        }

        ordered.Sort(static (a, b) =>
        {
            int excluded = a.Score.Excluded.CompareTo(b.Score.Excluded);
            if (excluded != 0)
            {
                return excluded;
            }

            int by = a.Score.Score.CompareTo(b.Score.Score);
            return by != 0 ? by : a.Order.CompareTo(b.Order);
        });
        return ordered.ConvertAll(item => item.Score);
    }

    private static double Miss(OrientTarget target, Vec3 from, GridStep direction, string what, List<string> reasons,
        Func<Vec3, GridStep, bool> roomAhead)
    {
        double alignment = target.Alignment(from, direction, roomAhead);
        double cost = (1.0 - alignment) * MissWeight;
        if (cost > 0.5)
        {
            reasons.Add($"{what} faces {direction.Name}, {(alignment < -0.5 ? "away from" : "not straight at")} " +
                        target.Describe());
        }

        return cost;
    }
}
