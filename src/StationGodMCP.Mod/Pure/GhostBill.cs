#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// What a set of ghosts takes to build, as Lattice's bills read it (IBills.For): the items used, the items take-apart
/// steps give back, each by prefab hash and count, and the tools used.
/// </summary>
internal sealed class GhostBill
{
    internal GhostBill(List<(int Hash, int Count)> needed, List<(int Hash, int Count)> givenBack, List<int> tools)
    {
        Needed = needed;
        GivenBack = givenBack;
        Tools = tools;
    }

    internal List<(int Hash, int Count)> Needed { get; }

    internal List<(int Hash, int Count)> GivenBack { get; }

    internal List<int> Tools { get; }
}
