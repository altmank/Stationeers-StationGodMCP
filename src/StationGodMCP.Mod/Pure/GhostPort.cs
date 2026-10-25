#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// One port of a ghost's piece where the ghost stands, as Lattice reads it (IPieceCatalogue.PortsAt, contract 0.8):
/// its index in the game's OpenEnds, the cell a joining piece takes (metres, its centre), the way that piece's end
/// points into the port, the game's connection type name and role (null for a plain run end).
/// </summary>
internal sealed class GhostPort
{
    internal GhostPort(int index, Vec3 joins, string toward, string type, string? role)
    {
        Index = index;
        Joins = joins;
        Toward = toward;
        Type = type;
        Role = role;
    }

    internal int Index { get; }

    internal Vec3 Joins { get; }

    /// <summary>+x, -y, ...: the end a piece in Joins needs to join the port.</summary>
    internal string Toward { get; }

    internal string Type { get; }

    internal string? Role { get; }
}
