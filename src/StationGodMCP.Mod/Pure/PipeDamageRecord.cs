#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// A pipe's damage record as names: the game keeps every cause that has damaged the pipe as flags (pressure 1,
/// liquid 2, solid 4), apart from the burst state, which reads pressure for any pipe broken by damage. The record is
/// what the game's tooltip names as the cause.
/// </summary>
internal static class PipeDamageRecord
{
    private const byte Pressure = 1;
    private const byte Liquid = 2;
    private const byte Solid = 4;

    /// <summary>The causes recorded, in the game's tooltip order; empty when none.</summary>
    internal static List<string> Causes(byte record)
    {
        List<string> causes = new List<string>(3);
        if ((record & Pressure) != 0)
        {
            causes.Add("pressure");
        }

        if ((record & Liquid) != 0)
        {
            causes.Add("liquid");
        }

        if ((record & Solid) != 0)
        {
            causes.Add("solid");
        }

        return causes;
    }
}
