#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>What the route search knows about one small cell, read from the game (GridFacts).</summary>
internal readonly struct SmallCellFacts
{
    internal SmallCellFacts(string? blocked, int blockedAxes, long? familyNetwork, bool familyPiece,
        LargeCellFacts large, int indexX, int indexY, int indexZ, List<long> neighbourNetworks, CellSupport support)
    {
        Support = support;
        Blocked = blocked;
        BlockedAxes = blockedAxes;
        FamilyNetwork = familyNetwork;
        FamilyPiece = familyPiece;
        Large = large;
        IndexX = indexX;
        IndexY = indexY;
        IndexZ = indexZ;
        NeighbourNetworks = neighbourNetworks;
    }

    /// <summary>Why no piece of the kind may stand here at all; null when one may.</summary>
    internal string? Blocked { get; }

    /// <summary>Axes a piece here may not have ends along (bit 0 x, 1 y, 2 z): another kind's piece lies that way.</summary>
    internal int BlockedAxes { get; }

    internal bool FamilyPiece { get; }

    internal long? FamilyNetwork { get; }

    internal LargeCellFacts Large { get; }

    /// <summary>0 to 3 along each axis inside the large cell; 0 is on its minimum face plane.</summary>
    internal int IndexX { get; }

    internal int IndexY { get; }

    internal int IndexZ { get; }

    /// <summary>The kind's networks of pieces in the six neighbouring cells.</summary>
    internal List<long> NeighbourNetworks { get; }

    /// <summary>What holds a piece here up: a frame (its edge, face or inside), a wall's plane, or nothing (air).</summary>
    internal CellSupport Support { get; }

    /// <summary>How many of the large cell's minimum face planes the cell lies on (2 or 3: a frame edge or corner).</summary>
    internal int Planes => (IndexX == 0 ? 1 : 0) + (IndexY == 0 ? 1 : 0) + (IndexZ == 0 ? 1 : 0);
}

/// <summary>A 2 m cell: a frame in it, its room, and which of its six faces carry a wall or window.</summary>
internal readonly struct LargeCellFacts
{
    internal LargeCellFacts(bool frame, bool inRoom, int wallFaces)
    {
        Frame = frame;
        InRoom = inRoom;
        WallFaces = wallFaces;
    }

    internal bool Frame { get; }

    internal bool InRoom { get; }

    /// <summary>Bit i for GridStep.All[i]: a face structure (wall, window) on that face.</summary>
    internal int WallFaces { get; }

    internal bool HasWall(GridStep face) => (WallFaces & (1 << face.Index)) != 0;
}

/// <summary>Which cells a route prefers.</summary>
internal enum RoutePreference
{
    None,

    /// <summary>
    /// The frame edges: small cells on two or three face planes of a frame cell they touch (CellSupport.FrameEdge),
    /// the top edges of a beam included.
    /// </summary>
    FrameEdges,

    /// <summary>Along walls: small cells on a wall's plane or the layer beside it.</summary>
    Walls
}

/// <summary>
/// The route rules as a cost per cell (kind agnostic; the kind decides what blocks through the facts). A blocked cell
/// or an axis another kind's piece lies along is never used; a cell holding the kind's own piece is never passed
/// through (it would join that piece); inside_frames refuses cells outside frame cells; avoid_networks refuses cells
/// next to pieces of networks not named as the route's own ends' (so the route never runs beside another network);
/// prefer adds PreferencePenalty to cells not preferred; avoid_room_interior adds InteriorPenalty to cells in a
/// room's 2 m cell on none of its face planes; avoid_walkways adds it to cells in a room's 2 m cell above its floor
/// plane that are on no vertical face plane (the space a player walks through); frames_first adds AirPenalty to
/// cells in air (CellSupport.Air: on no frame and no wall plane).
/// </summary>
internal sealed class RouteRuleSet
{
    internal const double PreferencePenalty = 2.0;
    internal const double InteriorPenalty = 6.0;

    /// <summary>
    /// frames_first: the extra cost of a cell in air. One air cell costs as much as 50 more supported cells (25 m),
    /// or two min_bends turns, so any frame- or wall-supported route up to 25 m longer per air cell it avoids wins;
    /// a crossing of the narrowest gap (one empty 2 m cell, 3 air cells) pays for a 75 m detour, more than a default
    /// search box holds. It stays finite so that where no supported route exists the search still finds the route
    /// with the fewest air cells.
    /// </summary>
    internal const double AirPenalty = 50.0;

    internal RouteRuleSet(RoutePreference prefer, bool insideFrames, bool avoidRoomInterior, bool avoidWalkways,
        bool avoidNetworks, HashSet<long> ownNetworks, HashSet<long> avoidIds, bool avoidOwn = false,
        bool framesFirst = false)
    {
        AvoidOwn = avoidOwn;
        FramesFirst = framesFirst;
        Prefer = prefer;
        InsideFrames = insideFrames;
        AvoidRoomInterior = avoidRoomInterior;
        AvoidWalkways = avoidWalkways;
        AvoidNetworks = avoidNetworks;
        OwnNetworks = ownNetworks;
        AvoidIds = avoidIds;
    }

    internal RoutePreference Prefer { get; }

    internal bool InsideFrames { get; }

    /// <summary>Cells in air cost AirPenalty more: a route over frames or along walls wins whenever one exists.</summary>
    internal bool FramesFirst { get; }

    internal bool AvoidRoomInterior { get; }

    internal bool AvoidWalkways { get; }

    /// <summary>Refuse every cell beside a network that is not one of OwnNetworks.</summary>
    internal bool AvoidNetworks { get; }

    /// <summary>The networks the route starts or ends on; running beside them is fine.</summary>
    internal HashSet<long> OwnNetworks { get; }

    /// <summary>Networks never to run beside, even when AvoidNetworks is off.</summary>
    internal HashSet<long> AvoidIds { get; }

    /// <summary>
    /// Keep away from the ends' own networks (InteriorPenalty beside them): with join all, a cell beside one would
    /// join it a second time and close a loop.
    /// </summary>
    internal bool AvoidOwn { get; }

    /// <summary>The same rules without frames_first.</summary>
    internal RouteRuleSet WithoutFramesFirst() =>
        new RouteRuleSet(Prefer, InsideFrames, AvoidRoomInterior, AvoidWalkways, AvoidNetworks, OwnNetworks, AvoidIds,
            AvoidOwn);

    internal CellCost Cost(SmallCellFacts cell)
    {
        if (cell.Blocked != null || cell.FamilyPiece || (InsideFrames && !cell.Large.Frame))
        {
            return CellCost.Blocked;
        }

        foreach (long network in cell.NeighbourNetworks)
        {
            if (AvoidIds.Contains(network) || (AvoidNetworks && !OwnNetworks.Contains(network)))
            {
                return CellCost.Blocked;
            }
        }

        double cost = 1.0;
        if (AvoidOwn && cell.NeighbourNetworks.Exists(OwnNetworks.Contains))
        {
            cost += InteriorPenalty;
        }

        if (FramesFirst && cell.Support == CellSupport.Air)
        {
            cost += AirPenalty;
        }

        if (Prefer == RoutePreference.FrameEdges && cell.Support != CellSupport.FrameEdge)
        {
            cost += PreferencePenalty;
        }

        if (Prefer == RoutePreference.Walls && !AlongWall(cell))
        {
            cost += PreferencePenalty;
        }

        if (AvoidRoomInterior && cell.Large.InRoom && cell.Planes == 0)
        {
            cost += InteriorPenalty;
        }

        if (AvoidWalkways && cell.Large.InRoom && cell.IndexY > 0 && cell.IndexX != 0 && cell.IndexZ != 0)
        {
            cost += InteriorPenalty;
        }

        return CellCost.Of(cost, cell.BlockedAxes);
    }

    /// <summary>
    /// On a wall's plane (index 0 of an axis whose minimum face carries one) or the layer beside it (index 1 beside
    /// the minimum face, index 3 beside the maximum face).
    /// </summary>
    internal static bool AlongWall(SmallCellFacts cell)
    {
        int[] index = { cell.IndexX, cell.IndexY, cell.IndexZ };
        for (int axis = 0; axis < 3; axis++)
        {
            GridStep minimum = GridStep.All[axis * 2 + 1];
            GridStep maximum = GridStep.All[axis * 2];
            if ((index[axis] <= 1 && cell.Large.HasWall(minimum)) || (index[axis] == 3 && cell.Large.HasWall(maximum)))
            {
                return true;
            }
        }

        return false;
    }
}
