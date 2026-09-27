#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Atmospherics;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>The single gas types of Chemistry.GasType (one bit each; the enum also has combinations).</summary>
internal static class GasTypes
{
    internal static readonly Chemistry.GasType[] All = Build();

    internal static int IndexOf(Chemistry.GasType type) => Array.IndexOf(All, type);

    private static Chemistry.GasType[] Build()
    {
        List<Chemistry.GasType> types = new List<Chemistry.GasType>();
        foreach (Chemistry.GasType type in Enum.GetValues(typeof(Chemistry.GasType)))
        {
            uint bits = (uint)type;
            if (bits != 0 && (bits & (bits - 1)) == 0)
            {
                types.Add(type);
            }
        }

        return types.ToArray();
    }
}
