#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using StationGodMCP.LatticeLink.Model;
using StationGodMCP.Pure.RemoteView;
using StationGodMCP.Pure;

namespace StationGodMCP.LatticeLink;

/// <summary>
/// Whether Lattice (the ghost core library, mod net.xceled.stationeers.lattice) runs in this game. Names no Lattice
/// type, so it is safe without Lattice installed: on Mono a method that names a missing type fails when it is first
/// compiled, so every Lattice call sits in LatticeBoard, reached only through the forwarders here after ApiLoaded. Lattice is optional:
/// without it StationGod keeps its own ghost board (GhostBoardStore's file, its own sync).
/// </summary>
internal static class LatticePresence
{
    private static bool _checked;
    private static bool _loaded;

    /// <summary>Whether Lattice.Api is loaded; asked once per world (Lattice loads with the mods, before any world).</summary>
    internal static bool ApiLoaded
    {
        get
        {
            if (!_checked)
            {
                _checked = true;
                _loaded = false;
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (assembly.GetName().Name == "Lattice.Api")
                    {
                        _loaded = true;
                        break;
                    }
                }
            }

            return _loaded;
        }
    }

    /// <summary>Whether this world's ghosts are Lattice's: Lattice is loaded and StationGod has its world's system.</summary>
    internal static bool Running => ApiLoaded && Attached();

    /// <summary>Every frame while a world runs: takes Lattice's system for the world once it is published.</summary>
    internal static void Tick()
    {
        if (ApiLoaded)
        {
            TickBoard();
        }
    }

    /// <summary>The world changed: Lattice is looked for again with the next world.</summary>
    internal static void Forget()
    {
        if (_checked && _loaded)
        {
            DetachBoard();
        }

        _checked = false;
    }

    // Each call into LatticeBoard sits in a method of its own, never inlined, so a caller compiled without Lattice
    // installed never names a Lattice type.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool Attached() => LatticeBoard.IsAttached;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void TickBoard() => LatticeBoard.Tick();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void DetachBoard() => LatticeBoard.Detach();

    /// <summary>LatticeBoard.Place; only while Running.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static List<BoardGhost> Place(IReadOnlyList<GhostPlacement> placements, GhostSource source, ulong? client,
        List<string> refused, List<string> notes, IReadOnlyList<GhostLook>? looks = null) =>
        LatticeBoard.Place(placements, source, client, refused, notes, looks);

    /// <summary>LatticeBoard.Remove; only while Running.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static List<long> Remove(IEnumerable<long> ids, GhostSource source, ulong? client, List<string> refused) =>
        LatticeBoard.Remove(ids, source, client, refused);

    /// <summary>LatticeBoard.Check; only while Running.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static Dictionary<long, GhostCheck>? Check(IReadOnlyList<long> ids, out string? note) =>
        LatticeBoard.Check(ids, out note);

    /// <summary>Whether Lattice draws the ghosts; false while not Running.</summary>
    internal static bool Draws => Running && DrawsBoard();

    /// <summary>The running Lattice contract's version; null while not Running.</summary>
    internal static string? Version => Running ? BoardVersion() : null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool DrawsBoard() => LatticeBoard.Draws;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string? BoardVersion() => LatticeBoard.Version;
}
