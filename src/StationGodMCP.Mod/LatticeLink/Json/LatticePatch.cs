#nullable enable

using System;
using System.Runtime.CompilerServices;
using L = Lattice.Api;

namespace StationGodMCP.LatticeLink.Json;

/// <summary>
/// The running Lattice's patch (ApiVersion.CurrentPatch, added in 0.22.4). Lattice 0.22.0 to 0.22.3 lack it, and reading
/// it there fails as the method that reads it is compiled: that counts as patch 0, so members those patches added are
/// not asked for.
/// </summary>
internal static class LatticePatch
{
    private static int? _running;

    internal static int Running => _running ??= Read();

    private static int Read()
    {
        try
        {
            return Current();
        }
        catch (MissingMemberException)
        {
            return 0;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Current() => L.ApiVersion.CurrentPatch;
}
