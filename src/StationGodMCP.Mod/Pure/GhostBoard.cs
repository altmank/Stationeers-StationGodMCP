#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.RemoteView;

namespace StationGodMCP.Pure;

/// <summary>
/// One ghost on the board: a structure planned but not built, placed by a player in their game or by an agent (show
/// ghost). It lasts until removed or built: no seconds. Its turn is Euler degrees in Quaternion.Euler's order, the
/// quarter turns build structure's rotation takes when the ghost sits on the grid.
/// </summary>
internal sealed class BoardGhost
{
    internal BoardGhost(long id, int prefabHash, string prefabName, int buildState, Vec3 position, Vec3 euler, Rgba tint,
        GhostSource source, string owner, string? label, double placedAt)
    {
        Id = id;
        PrefabHash = prefabHash;
        PrefabName = prefabName;
        BuildState = buildState;
        Position = position;
        Euler = euler;
        Tint = tint;
        Source = source;
        Owner = owner;
        Label = label;
        PlacedAt = placedAt;
    }

    internal long Id { get; }

    internal int PrefabHash { get; }

    internal string PrefabName { get; }

    internal int BuildState { get; }

    internal Vec3 Position { get; }

    internal Vec3 Euler { get; }

    internal Rgba Tint { get; }

    internal GhostSource Source { get; }

    /// <summary>The player's name, or "agent".</summary>
    internal string Owner { get; }

    internal string? Label { get; }

    /// <summary>When it was placed: Unix seconds.</summary>
    internal double PlacedAt { get; }

    internal BoardGhost WithId(long id) => new BoardGhost(id, PrefabHash, PrefabName, BuildState, Position, Euler, Tint,
        Source, Owner, Label, PlacedAt);
}

/// <summary>
/// The ghosts of one world, kept by the host (clients hold a mirror the host keeps in step): ids from 1 up, never
/// reused in a world; at most Maximum. A ghost placed where one of the same prefab and turn already stands replaces
/// it (a second click on the same spot changes nothing but its tint and label). A structure built where a ghost of its
/// prefab and turn stands takes that ghost off (Built).
/// </summary>
internal sealed class GhostBoard
{
    internal const int Maximum = 4096;

    /// <summary>How close a ghost and a build (or two ghosts) stand to be the same spot, in metres.</summary>
    internal const double SameSpotM = 0.1;

    /// <summary>How close two turns are to be the same, in degrees per axis (after wrapping).</summary>
    private const double SameTurnDeg = 1.0;

    private readonly List<BoardGhost> _ghosts = new List<BoardGhost>();
    private long _next = 1;

    internal IReadOnlyList<BoardGhost> All => _ghosts;

    internal int Count => _ghosts.Count;

    /// <summary>
    /// Adds a ghost (its id is the board's next); the ghost it replaces, when one of the same prefab and turn stood on
    /// the spot, goes into replaced. Null when the board is full.
    /// </summary>
    internal BoardGhost? Add(BoardGhost ghost, out BoardGhost? replaced)
    {
        replaced = null;
        int same = IndexOfSame(ghost.PrefabHash, ghost.Position, ghost.Euler);
        if (same >= 0)
        {
            replaced = _ghosts[same];
            _ghosts.RemoveAt(same);
        }
        else if (_ghosts.Count >= Maximum)
        {
            return null;
        }

        BoardGhost added = ghost.WithId(_next++);
        _ghosts.Add(added);
        return added;
    }

    /// <summary>Takes the ghost off; false when there is none with that id.</summary>
    internal bool Remove(long id)
    {
        int index = _ghosts.FindIndex(ghost => ghost.Id == id);
        if (index < 0)
        {
            return false;
        }

        _ghosts.RemoveAt(index);
        return true;
    }

    /// <summary>Takes off every ghost the rule picks; their ids, in board order.</summary>
    internal List<long> RemoveWhere(Predicate<BoardGhost> rule)
    {
        List<long> removed = new List<long>();
        foreach (BoardGhost ghost in _ghosts)
        {
            if (rule(ghost))
            {
                removed.Add(ghost.Id);
            }
        }

        _ghosts.RemoveAll(rule);
        return removed;
    }

    internal BoardGhost? Find(long id) => _ghosts.Find(ghost => ghost.Id == id);

    /// <summary>A structure of the prefab was built at the point in the turn: the ghost standing for it, taken off.</summary>
    internal BoardGhost? Built(int prefabHash, Vec3 position, Vec3 euler)
    {
        int index = IndexOfSame(prefabHash, position, euler);
        if (index < 0)
        {
            return null;
        }

        BoardGhost built = _ghosts[index];
        _ghosts.RemoveAt(index);
        return built;
    }

    /// <summary>The ghost a structure of the prefab built at the point in the turn would complete; left on the board.</summary>
    internal BoardGhost? StandingFor(int prefabHash, Vec3 position, Vec3 euler)
    {
        int index = IndexOfSame(prefabHash, position, euler);
        return index < 0 ? null : _ghosts[index];
    }

    /// <summary>Whether a structure of the prefab at the point in the turn is what the ghost stands for.</summary>
    internal static bool StandsFor(BoardGhost ghost, int prefabHash, Vec3 position, Vec3 euler) =>
        ghost.PrefabHash == prefabHash && Distance(ghost.Position, position) <= SameSpotM && SameTurn(ghost.Euler, euler);

    /// <summary>Whether any ghost is of the prefab (a cheap check before Built).</summary>
    internal bool Holds(int prefabHash) => _ghosts.Exists(ghost => ghost.PrefabHash == prefabHash);

    /// <summary>A mirror's step: the ghosts the host sent, the ids it took off; a reset empties the mirror first.</summary>
    internal void Apply(bool reset, IReadOnlyList<BoardGhost> added, IReadOnlyList<long> removed)
    {
        if (reset)
        {
            _ghosts.Clear();
        }

        HashSet<long> gone = new HashSet<long>(removed);
        _ghosts.RemoveAll(ghost => gone.Contains(ghost.Id));
        foreach (BoardGhost ghost in added)
        {
            _ghosts.RemoveAll(standing => standing.Id == ghost.Id);
            _ghosts.Add(ghost);
            _next = Math.Max(_next, ghost.Id + 1);
        }
    }

    internal void Clear()
    {
        _ghosts.Clear();
        _next = 1;
    }

    /// <summary>The board as its file holds it: {next, ghosts: [...]}.</summary>
    internal JObject ToJson()
    {
        JArray ghosts = new JArray();
        foreach (BoardGhost ghost in _ghosts)
        {
            JObject entry = new JObject
            {
                ["id"] = ghost.Id,
                ["prefab_hash"] = ghost.PrefabHash,
                ["prefab_name"] = ghost.PrefabName,
                ["build_state"] = ghost.BuildState,
                ["position"] = new JArray(ghost.Position.X, ghost.Position.Y, ghost.Position.Z),
                ["euler"] = new JArray(ghost.Euler.X, ghost.Euler.Y, ghost.Euler.Z),
                ["tint"] = new JArray(ghost.Tint.Red, ghost.Tint.Green, ghost.Tint.Blue, ghost.Tint.Alpha),
                ["source"] = ghost.Source == GhostSource.Player ? "player" : "agent",
                ["owner"] = ghost.Owner,
                ["placed_at"] = ghost.PlacedAt
            };
            if (ghost.Label != null)
            {
                entry["label"] = ghost.Label;
            }

            ghosts.Add(entry);
        }

        return new JObject { ["next"] = _next, ["ghosts"] = ghosts };
    }

    /// <summary>
    /// The board a file holds; an entry that does not read is skipped and counted in skipped. The ids are kept, and the
    /// next id is past every one of them.
    /// </summary>
    internal static GhostBoard FromJson(JObject json, out int skipped)
    {
        GhostBoard board = new GhostBoard();
        skipped = 0;
        if (json["ghosts"] is JArray ghosts)
        {
            foreach (JToken token in ghosts)
            {
                BoardGhost? ghost = token is JObject entry ? Read(entry) : null;
                if (ghost == null || board._ghosts.Count >= Maximum || board.Find(ghost.Id) != null)
                {
                    skipped++;
                    continue;
                }

                board._ghosts.Add(ghost);
                board._next = Math.Max(board._next, ghost.Id + 1);
            }
        }

        if (json["next"] is { Type: JTokenType.Integer } next)
        {
            board._next = Math.Max(board._next, (long)next);
        }

        return board;
    }

    private static BoardGhost? Read(JObject entry)
    {
        try
        {
            if (!(entry["position"] is JArray at) || at.Count != 3 || !(entry["euler"] is JArray turn) ||
                turn.Count != 3 || !(entry["tint"] is JArray tint) || tint.Count != 4)
            {
                return null;
            }

            long id = (long)entry["id"]!;
            return id <= 0
                ? null
                : new BoardGhost(id, (int)entry["prefab_hash"]!, (string?)entry["prefab_name"] ?? string.Empty,
                    (int?)entry["build_state"] ?? 0, new Vec3((double)at[0], (double)at[1], (double)at[2]),
                    new Vec3((double)turn[0], (double)turn[1], (double)turn[2]),
                    new Rgba((float)tint[0], (float)tint[1], (float)tint[2], (float)tint[3]),
                    (string?)entry["source"] == "player" ? GhostSource.Player : GhostSource.Agent,
                    (string?)entry["owner"] ?? string.Empty, (string?)entry["label"],
                    (double?)entry["placed_at"] ?? 0.0);
        }
        catch (Exception exception) when (exception is FormatException || exception is InvalidCastException ||
                                          exception is ArgumentException || exception is OverflowException)
        {
            return null;
        }
    }

    // The ghost of the prefab standing on the spot in the turn; -1 for none.
    private int IndexOfSame(int prefabHash, Vec3 position, Vec3 euler)
    {
        for (int index = 0; index < _ghosts.Count; index++)
        {
            if (StandsFor(_ghosts[index], prefabHash, position, euler))
            {
                return index;
            }
        }

        return -1;
    }

    private static double Distance(Vec3 a, Vec3 b)
    {
        double x = a.X - b.X;
        double y = a.Y - b.Y;
        double z = a.Z - b.Z;
        return Math.Sqrt(x * x + y * y + z * z);
    }

    /// <summary>
    /// Whether two Euler turns are the same rotation: compared as rotation matrices, so (0, 180, 0) and (180, 0, 180)
    /// match as the game's own turns do.
    /// </summary>
    internal static bool SameTurn(Vec3 a, Vec3 b)
    {
        double[] first = Matrix(a);
        double[] second = Matrix(b);
        double limit = 2 * Math.Sin(SameTurnDeg * Math.PI / 360.0);
        for (int index = 0; index < 9; index++)
        {
            if (Math.Abs(first[index] - second[index]) > limit)
            {
                return false;
            }
        }

        return true;
    }

    // Quaternion.Euler(x, y, z) as a matrix: Unity applies z, then x, then y (R = Ry * Rx * Rz).
    private static double[] Matrix(Vec3 euler)
    {
        double x = euler.X * Math.PI / 180.0;
        double y = euler.Y * Math.PI / 180.0;
        double z = euler.Z * Math.PI / 180.0;
        double cx = Math.Cos(x), sx = Math.Sin(x), cy = Math.Cos(y), sy = Math.Sin(y), cz = Math.Cos(z), sz = Math.Sin(z);
        return new[]
        {
            cy * cz + sy * sx * sz, -cy * sz + sy * sx * cz, sy * cx,
            cx * sz, cx * cz, -sx,
            -sy * cz + cy * sx * sz, sy * sz + cy * sx * cz, cy * cx
        };
    }
}
