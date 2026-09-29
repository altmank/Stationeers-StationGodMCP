#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Objects.Electrical;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// solar_aim: the Horizontal and Vertical that point a solar panel's cells at the sun, from the panel's own pivots and
/// the game's sun vector: no daylight sensor, no calibration. Read only; it does not move the panel.
///
/// How the game aims and scores a panel (CODE): SolarPanelArm.FacingDirection is Cells.forward, and
/// SolarPanelArm.CalculateSolarEfficiency scores it against OrbitalSimulation.WorldSunVector. A Horizontal write of H
/// degrees sets every arm's YawPivot.localRotation to Euler(0, 0, H); a Vertical write of V degrees (clamped to
/// 15..165) maps to 0..1 and sets PitchPivot.localRotation to Euler(Lerp(-75, 75, that), 0, 0), i.e. Euler(V - 90,
/// 0, 0). So
///     facing(H, V) = YawParent * Euler(0, 0, H) * A * Euler(V - 90, 0, 0) * B * forward
/// where YawParent is the yaw pivot's parent's world rotation and A and B are the fixed rotations from the yaw pivot to
/// the pitch pivot's parent and from the pitch pivot to the cells, all read from the live transforms of the first arm
/// (SolarPanel._panelArms, through GameMembers). The best pair comes from AngleSearch.SolarNearest: of the two
/// equivalent poses (H, V) and (H + 180, 180 - V), the one nearer the panel's current Horizontal and Vertical. With the
/// sun below the horizon the same search gives the pose closest to the sun now: tilted toward where it set until
/// midnight, toward where it will rise after.
/// </summary>
internal static class SolarAimApi
{
    internal static SolarAimView Handle(Args args)
    {
        ThingId id = args.ThingId("reference_id");
        if (!GameLookup.TryFindThing(id, out Thing thing) || !(thing is SolarPanel panel))
        {
            throw ApiErrors.Refused("not_solar_panel", $"Reference id {id} is not a solar panel.");
        }

        Vector3 sun = OrbitalSimulation.WorldSunVector.normalized;
        SunView sunView = new SunView(sun.x, sun.y, sun.z, OrbitalSimulation.IsEclipse);
        PanelAimView current = new PanelAimView(
            panel.GetLogicValue(LogicType.Horizontal),
            panel.GetLogicValue(LogicType.Vertical),
            panel.GenerationEfficiency);
        // SolarPanel.IsOperable (protected): not broken and at its last build state.
        bool operable = !panel.IsBroken && panel.CurrentBuildStateIndex == panel.BuildStates.Count - 1;
        SolarPanelArm? arm = FirstTurningArm(panel);
        if (arm == null)
        {
            return new SolarFixedView(id, panel.PrefabName, operable, sunView, current);
        }

        AngleBest best = AngleSearch.SolarNearest(new PanelFacing(arm, sun), (float)current.Horizontal,
            (float)current.Vertical);
        double offDegrees = SolarAlignment.OffDegrees(best.Score);
        return new SolarTurnView(id, panel.PrefabName, operable, sunView, current, best.Horizontal, best.Vertical,
            offDegrees, SolarAlignment.Alignment(offDegrees));
    }

    // The first arm whose yaw pivot, pitch pivot, cells and yaw parent all exist; null when the panel has none.
    private static SolarPanelArm? FirstTurningArm(SolarPanel panel)
    {
        if (!(GameMembers.SolarPanelArms.GetValue(panel) is List<SolarPanelArm> arms))
        {
            return null;
        }

        foreach (SolarPanelArm candidate in arms)
        {
            if (candidate != null && candidate.YawPivot != null && candidate.PitchPivot != null &&
                candidate.Cells != null && candidate.YawPivot.parent != null)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>How squarely the cells face the sun at a pair of angles: the dot product, 1 when square on.</summary>
    private sealed class PanelFacing : IAngleScore
    {
        private readonly Quaternion _yawParent;
        private readonly Quaternion _toPitchParent;
        private readonly Quaternion _toCells;
        private readonly Vector3 _sun;

        internal PanelFacing(SolarPanelArm arm, Vector3 sun)
        {
            _yawParent = arm.YawPivot.parent.rotation;
            _toPitchParent = Quaternion.Inverse(arm.YawPivot.rotation) * arm.PitchPivot.parent.rotation;
            _toCells = Quaternion.Inverse(arm.PitchPivot.rotation) * arm.Cells.rotation;
            _sun = sun;
        }

        public float Score(float horizontal, float vertical) =>
            Vector3.Dot(
                _yawParent * Quaternion.Euler(0f, 0f, horizontal) * _toPitchParent
                * Quaternion.Euler(vertical - SolarAlignment.PitchZero, 0f, 0f) * _toCells * Vector3.forward,
                _sun);
    }
}
