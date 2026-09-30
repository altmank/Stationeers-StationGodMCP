#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure;

/// <summary>
/// Whether a small-grid piece may take a rocket's small cell, as RocketNetwork.IsCollision decides it
/// (RocketNetwork.cs:641-656): the fuselage gives each of its internal cells a RocketInternalCellType (Pipes 1,
/// Cables 2, Devices 4, Chutes 8, Umbilical 16, Engine 32, CargoBay 64, CableConnector 128, Bulkhead 256), and a piece
/// with a type of its own (IRocketInternals.InternalCellType) fits only where the cell's type holds every bit of it.
/// A piece with no type (None, or no IRocketInternals at all) never fits.
/// </summary>
internal static class RocketCellRule
{
    internal static bool Fits(int cellType, int pieceType) =>
        pieceType != 0 && (cellType & pieceType) == pieceType;
}

/// <summary>
/// Why a build leaves a launching or landing rocket alone. While it launches or lands its parts are
/// parented to a moving transform (Rocket.cs:1637-1645 ReParentRocketParts; 1658-1703 the landing), so a position
/// planned against the grid is stale by the time the job builds. The game has no such rule (no CanConstruct reads the
/// state); this is the tools' own, and it names itself as such.
/// </summary>
internal static class RocketMotionRule
{
    internal static string Refusal(string rocketName, string state) =>
        $"{rocketName} is {state.ToLowerInvariant()}: its parts move with it until it stands on a launch mount or " +
        "is parked in space, so this tool builds and removes nothing of it now (the tool's own rule; the game has " +
        "none)";
}

/// <summary>
/// What stops the last deconstruction step of a fuselage piece (StructureFuselage.CanDeconstruct,
/// StructureFuselage.cs:294-317, asked when the piece is at build state 0): a fuselage piece standing on top of it,
/// then any small-grid piece (device, chute, pipe, cable, other) in one of its internal cells. remove_structure takes a
/// piece down in one go, so this is asked whatever state it stands at, and things the same request removes are
/// treated as gone.
/// </summary>
internal enum FuselageTakedown
{
    Allowed,
    SupportsAnother,
    HoldsInternals
}

internal static class FuselageTakedownRule
{
    internal static FuselageTakedown Judge(bool fuselageAboveStays, bool internalsStay) =>
        fuselageAboveStays ? FuselageTakedown.SupportsAnother
        : internalsStay ? FuselageTakedown.HoldsInternals
        : FuselageTakedown.Allowed;
}

/// <summary>One structure remove_structure takes down alone (not a cable, pipe or chute piece).</summary>
internal readonly struct RocketTakedownItem
{
    internal RocketTakedownItem(int order, bool hull, double y)
    {
        Order = order;
        Hull = hull;
        Y = y;
    }

    /// <summary>Its place in the request.</summary>
    internal int Order { get; }

    /// <summary>A fuselage piece (StructureFuselage and its subclasses).</summary>
    internal bool Hull { get; }

    internal double Y { get; }
}

/// <summary>
/// The order remove_structure takes structures down in, as a player would have to: everything that is not a fuselage
/// piece first, in the request's order (a rocket's internals must be gone before its fuselage goes,
/// StructureFuselage.cs:305-314), then the fuselage pieces from the top down (a piece that supports another may not go,
/// StructureFuselage.cs:298-304). Removing the lower piece first would also make the game split the rocket
/// (StructureFuselage.OnDestroy rebuilds the networks of the parts it joined, StructureFuselage.cs:124-143).
/// </summary>
internal static class RocketTakedownOrder
{
    internal static List<int> Of(IReadOnlyList<RocketTakedownItem> items)
    {
        List<RocketTakedownItem> sorted = new List<RocketTakedownItem>(items);
        sorted.Sort(static (a, b) =>
            a.Hull != b.Hull ? a.Hull.CompareTo(b.Hull)
            : a.Hull && Math.Abs(a.Y - b.Y) > 0.01 ? b.Y.CompareTo(a.Y)
            : a.Order.CompareTo(b.Order));
        return sorted.ConvertAll(static item => item.Order);
    }
}

/// <summary>What stands in one small cell an umbilical's partner search looks at.</summary>
internal abstract class UmbilicalProbe
{
    private UmbilicalProbe()
    {
    }

    /// <summary>No small cell there, or one holding no device (or the searching umbilical itself): the search goes on.</summary>
    internal sealed class Clear : UmbilicalProbe
    {
        internal static readonly Clear Instance = new Clear();
    }

    /// <summary>A device that is not an umbilical: the search along this column stops.</summary>
    internal sealed class Blocker : UmbilicalProbe
    {
        internal Blocker(long id, string name)
        {
            Id = id;
            Name = name;
        }

        internal long Id { get; }

        internal string Name { get; }
    }

    /// <summary>Another umbilical: the partner when compatible and facing back; otherwise the column stops.</summary>
    internal sealed class Umbilical : UmbilicalProbe
    {
        internal Umbilical(long id, string name, bool compatible, double facing)
        {
            Id = id;
            Name = name;
            Compatible = compatible;
            Facing = facing;
        }

        internal long Id { get; }

        internal string Name { get; }

        /// <summary>IUmbilical.IsCompatibleWith, asked of the searching one.</summary>
        internal bool Compatible { get; }

        /// <summary>Dot of the other's backward (-Forward) with the searcher's forward; the game wants at least 0.9.</summary>
        internal double Facing { get; }
    }
}

/// <summary>Why the search along one column ended without a partner.</summary>
internal readonly struct UmbilicalStop
{
    internal UmbilicalStop(int column, int step, string reason)
    {
        Column = column;
        Step = step;
        Reason = reason;
    }

    internal int Column { get; }

    internal int Step { get; }

    internal string Reason { get; }
}

/// <summary>The partner an umbilical's search finds, or none, and why each column stopped.</summary>
internal sealed class UmbilicalSearchResult
{
    internal UmbilicalSearchResult(UmbilicalProbe.Umbilical? partner, int step, List<UmbilicalStop> stops)
    {
        Partner = partner;
        Step = step;
        Stops = stops;
    }

    internal UmbilicalProbe.Umbilical? Partner { get; }

    /// <summary>Small cells from the first searched cell to the partner's (0 = the first); -1 without a partner.</summary>
    internal int Step { get; }

    /// <summary>PartnerDistance as the game sets it: Step + 1; 0 without a partner.</summary>
    internal int PartnerDistance => Partner == null ? 0 : Math.Max(Step + 1, 0);

    internal List<UmbilicalStop> Stops { get; }
}

/// <summary>
/// RocketUmbilicalHelper.FindAndSetOtherUmbilical (RocketUmbilicalHelper.cs:50-101) read without setting anything:
/// from the searching umbilical's first search cell (FirstPartnerSearchPosition snapped to the small grid), along its
/// forward, steps 0 to 13 (a small cell each), in one column, or three for an Umbilical type (a male: the column
/// itself and two more along its right). A cell with no device, or with the searcher itself, is passed; a device that
/// is not an umbilical stops that column; an umbilical that is incompatible or does not face back (dot below 0.9)
/// stops it too; a fit one becomes the partner if nearer than the best so far, and later columns search no further than
/// that. PartnerDistance is the step plus one.
/// </summary>
internal static class UmbilicalSearch
{
    internal const int LastStep = 13;

    internal const double MinimumFacing = 0.9;

    internal static UmbilicalSearchResult Run(bool widened, Func<int, int, UmbilicalProbe> probe)
    {
        UmbilicalProbe.Umbilical? partner = null;
        int best = 9999;
        List<UmbilicalStop> stops = new List<UmbilicalStop>();
        int columns = widened ? 2 : 0;
        for (int column = 0; column <= columns; column++)
        {
            for (int step = 0; step <= LastStep && best >= step; step++)
            {
                UmbilicalProbe found = probe(column, step);
                if (found is UmbilicalProbe.Blocker blocker)
                {
                    stops.Add(new UmbilicalStop(column, step, $"{blocker.Name} ({blocker.Id}) is in the way"));
                    break;
                }

                if (!(found is UmbilicalProbe.Umbilical other))
                {
                    continue;
                }

                if (!other.Compatible || other.Facing < MinimumFacing)
                {
                    stops.Add(new UmbilicalStop(column, step, !other.Compatible
                        ? $"{other.Name} ({other.Id}) is not a matching umbilical"
                        : $"{other.Name} ({other.Id}) does not face it (dot " +
                          other.Facing.ToString("0.##", CultureInfo.InvariantCulture) + ", 0.9 needed)"));
                    break;
                }

                if (step < best)
                {
                    partner = other;
                    best = step;
                }
            }
        }

        return new UmbilicalSearchResult(partner, partner == null ? -1 : best, stops);
    }
}
