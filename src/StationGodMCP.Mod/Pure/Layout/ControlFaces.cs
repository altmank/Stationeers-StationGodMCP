#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// The side of a prefab that carries what a player uses: its slots, buttons and switches (the game's own
/// Thing.Interactables, each with the collider a player points at). Derived: the side most of those colliders sit on;
/// Fallback: none sits clearly on one side, so its forward (+z), the side the placement cursor turns toward the player,
/// stands in for it. A thing with none has no control face at all (ControlFaceRule.Of).
/// </summary>
internal sealed class ControlFace
{
    private ControlFace(GridStep local, int votes, int considered, string source, bool fallback)
    {
        Local = local;
        Votes = votes;
        Considered = considered;
        Source = source;
        Fallback = fallback;
    }

    /// <summary>The prefab's own axis the controls face (unturned).</summary>
    internal GridStep Local { get; }

    /// <summary>Controls that sit on that side.</summary>
    internal int Votes { get; }

    /// <summary>Controls looked at (indicators such as Powered or Error left out).</summary>
    internal int Considered { get; }

    internal string Source { get; }

    /// <summary>No control sits clearly on one side: Local is the forward (+z) standing in for it.</summary>
    internal bool Fallback { get; }

    internal static ControlFace Derived(GridStep local, int votes, int considered) =>
        new ControlFace(local, votes, considered,
            $"CODE: {votes} of its {considered} controls (Thing.Interactables: slots, buttons, switches) sit on its " +
            $"local {local.Name} side", false);

    internal static ControlFace Forward(int considered) =>
        new ControlFace(GridStep.All[4], 0, considered,
            $"FALLBACK: none of its {considered} controls sits clearly on one side; its forward (+z) stands in", true);
}

/// <summary>
/// Which side of a body its controls sit on. Each control's centre (in the prefab's own frame) is measured from the
/// centre of the box its meshes fill, per axis as a share of the half size; it votes for the side of its largest share
/// when that is at least OuterShare (a control near the middle votes for nothing). Most votes win; a tie goes to the
/// side whose voters sit furthest out. With no control at all there is no control face: a device whose prefab lists no
/// interactable a player uses (Thing.Interactables empty, as on the Medium Convection Radiator, or indicators only)
/// has nothing to keep clear, so nothing stands in for one.
/// </summary>
internal static class ControlFaceRule
{
    internal const double OuterShare = 0.5;

    internal static ControlFace? Of(IReadOnlyList<Vec3> controls, Box3 body)
    {
        if (controls.Count == 0)
        {
            return null;
        }

        int[] votes = new int[GridStep.All.Length];
        double[] reach = new double[GridStep.All.Length];
        Vec3 centre = body.Centre;
        Vec3 half = body.Size * 0.5;
        foreach (Vec3 control in controls)
        {
            Vec3 offset = control - centre;
            double[] share = { Share(offset.X, half.X), Share(offset.Y, half.Y), Share(offset.Z, half.Z) };
            int axis = share[0] >= share[1] && share[0] >= share[2] ? 0 : share[1] >= share[2] ? 1 : 2;
            if (share[axis] < OuterShare)
            {
                continue;
            }

            double along = axis == 0 ? offset.X : axis == 1 ? offset.Y : offset.Z;
            int side = axis * 2 + (along > 0 ? 0 : 1);
            votes[side]++;
            reach[side] += share[axis];
        }

        int best = -1;
        for (int side = 0; side < votes.Length; side++)
        {
            if (votes[side] > 0 && (best < 0 || votes[side] > votes[best] ||
                                    (votes[side] == votes[best] && reach[side] > reach[best])))
            {
                best = side;
            }
        }

        return best < 0
            ? ControlFace.Forward(controls.Count)
            : ControlFace.Derived(GridStep.All[best], votes[best], controls.Count);
    }

    // How far out along one axis, as a share of the half size; a flat axis (no size) counts as fully out.
    private static double Share(double offset, double half) =>
        half < 1e-6 ? (Math.Abs(offset) < 1e-6 ? 0.0 : 1.0) : Math.Abs(offset) / half;
}
