#nullable enable

using System;

namespace StationGodMCP.Pure;

/// <summary>
/// How move_gas splits one gas over several atmospheres: taken from each in proportion to what it holds, and given to
/// a room's cells in proportion to their volume.
/// </summary>
internal static class GasShares
{
    /// <summary>
    /// The moles each member gives when the given amount (null: all of it) is taken in proportion to what each holds.
    /// The amount is capped at the total, and no member gives more than it holds.
    /// </summary>
    internal static double[] Proportional(double[] held, double? amount)
    {
        double total = 0.0;
        foreach (double moles in held)
        {
            total += Math.Max(0.0, moles);
        }

        double[] taken = new double[held.Length];
        if (!(total > 0.0))
        {
            return taken;
        }

        double wanted = amount.HasValue ? Math.Min(total, amount.Value) : total;
        for (int index = 0; index < held.Length; index++)
        {
            double have = Math.Max(0.0, held[index]);
            taken[index] = Math.Min(have, wanted * have / total);
        }

        return taken;
    }

    /// <summary>
    /// Each volume's share of the whole, adding up to 1; equal shares when no volume is positive. Empty for no
    /// volumes.
    /// </summary>
    internal static double[] ByVolume(double[] volumesL)
    {
        double total = 0.0;
        foreach (double volume in volumesL)
        {
            total += Math.Max(0.0, volume);
        }

        double[] shares = new double[volumesL.Length];
        for (int index = 0; index < volumesL.Length; index++)
        {
            shares[index] = total > 0.0 ? Math.Max(0.0, volumesL[index]) / total : 1.0 / volumesL.Length;
        }

        return shares;
    }
}
