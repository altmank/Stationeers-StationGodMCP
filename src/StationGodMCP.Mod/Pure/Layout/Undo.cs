#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>One small cell of a network piece and the ends the piece has there.</summary>
internal sealed class PieceCell
{
    internal PieceCell(GridCell cell, EndSet ends)
    {
        Cell = cell;
        Ends = ends;
    }

    internal GridCell Cell { get; }

    internal EndSet Ends { get; }
}

/// <summary>
/// A cable, pipe or chute piece as its place tool builds it again: the tool (place_cables, place_pipes,
/// place_chutes), the grade name it takes (null when no coil or kit lays this piece: the tool cannot build it), and
/// the ends in each of its cells.
/// </summary>
internal sealed class NetworkPiece
{
    internal NetworkPiece(string tool, string? grade, List<PieceCell> cells)
    {
        Tool = tool;
        Grade = grade;
        Cells = cells;
    }

    internal string Tool { get; }

    internal string? Grade { get; }

    internal List<PieceCell> Cells { get; }

    /// <summary>
    /// The piece cell by cell as singles build it again: its own ends in each cell, and in a long straight also the
    /// ends towards its neighbouring cells (the piece runs on through them), so every cell has the ends of a single.
    /// </summary>
    internal static List<PieceCell> CellsOf(PieceModel model)
    {
        List<PieceCell> cells = new List<PieceCell>(model.Cells.Count);
        foreach (GridCell cell in model.Cells)
        {
            EndSet ends = EndSet.AtCell(model, cell);
            foreach (GridStep step in GridStep.All)
            {
                if (model.Occupies(step.From(cell)))
                {
                    ends = ends.With(step);
                }
            }

            cells.Add(new PieceCell(cell, ends));
        }

        return cells;
    }
}

/// <summary>A structure as it stood before a job removed it: enough to build it again the same way.</summary>
internal sealed class ThingSnapshot
{
    internal ThingSnapshot(long id, string prefab, Vec3 position, CubeRotation? turn, int buildState, string? label,
        NetworkPiece? piece = null, bool burnt = false)
    {
        Id = id;
        Prefab = prefab;
        Position = position;
        Turn = turn;
        BuildState = buildState;
        Label = label;
        Piece = piece;
        Burnt = burnt;
    }

    internal long Id { get; }

    internal string Prefab { get; }

    internal Vec3 Position { get; }

    /// <summary>Its turn; null when it stood off the grid's axes (it cannot be placed again exactly).</summary>
    internal CubeRotation? Turn { get; }

    internal int BuildState { get; }

    internal string? Label { get; }

    /// <summary>
    /// Set for a cable, pipe or chute piece: it is built again by its place tool (piece by piece, with that tool's
    /// would_bridge, burst and gas guards), never by place_structure, which joins whatever its ends touch unchecked.
    /// </summary>
    internal NetworkPiece? Piece { get; }

    /// <summary>
    /// What an overload left of a cable (a burnt cable): no coil lays it, so it is never built again; undoing the job
    /// leaves it gone and undoes the rest.
    /// </summary>
    internal bool Burnt { get; }
}

/// <summary>Network pieces one place tool builds again in one job: the tool, the grade, the snapshots.</summary>
internal sealed class PieceRestore
{
    internal PieceRestore(string tool, string grade, List<ThingSnapshot> pieces)
    {
        Tool = tool;
        Grade = grade;
        Pieces = pieces;
    }

    internal string Tool { get; }

    internal string Grade { get; }

    internal List<ThingSnapshot> Pieces { get; }
}

/// <summary>
/// Who removes what an undo removes. A piece of a tool that also builds pieces again is removed by that tool's first
/// piece run (its remove_ids), in the same job that builds the old pieces back: a job that changed a piece in the
/// middle of a network (a tee added onto a trunk, a long straight crossed) is then undone in one step, so the network
/// is never cut between two jobs (the rebuild would be a would_bridge of the two halves) and the run's guards see the
/// networks as they end up. Everything else goes through remove_structure first.
/// </summary>
internal sealed class UndoRemovals
{
    private UndoRemovals(List<long> structures, List<List<long>> byRun)
    {
        Structures = structures;
        ByRun = byRun;
    }

    /// <summary>What remove_structure removes before any placement.</summary>
    internal List<long> Structures { get; }

    /// <summary>For each piece run (UndoPlan.RestorePieces, same order): the pieces it removes itself.</summary>
    internal List<List<long>> ByRun { get; }

    /// <summary>How many pieces the piece runs remove themselves.</summary>
    internal int InRuns
    {
        get
        {
            int count = 0;
            ByRun.ForEach(ids => count += ids.Count);
            return count;
        }
    }

    /// <param name="remove">UndoPlan.Remove.</param>
    /// <param name="runs">UndoPlan.RestorePieces.</param>
    /// <param name="toolOf">The place tool whose piece the thing with that id is; null for anything else.</param>
    internal static UndoRemovals Of(List<long> remove, List<PieceRestore> runs, System.Func<long, string?> toolOf)
    {
        List<List<long>> byRun = runs.ConvertAll(_ => new List<long>());
        List<long> structures = new List<long>();
        foreach (long id in remove)
        {
            string? tool = toolOf(id);
            int run = tool == null ? -1 : runs.FindIndex(group => group.Tool == tool);
            (run >= 0 ? byRun[run] : structures).Add(id);
        }

        return new UndoRemovals(structures, byRun);
    }
}

/// <summary>What a finished job did, as its log and the snapshots taken when it started tell it.</summary>
internal sealed class JobFacts
{
    internal JobFacts(string jobId, string tool, string status, List<(long Id, string? Prefab)> created,
        List<long> removed, Dictionary<long, ThingSnapshot> snapshots)
    {
        JobId = jobId;
        Tool = tool;
        Status = status;
        Created = created;
        Removed = removed;
        Snapshots = snapshots;
    }

    internal string JobId { get; }

    internal string Tool { get; }

    internal string Status { get; }

    /// <summary>Every thing the job built (new and changed pieces), with the prefab the log names when it does.</summary>
    internal List<(long Id, string? Prefab)> Created { get; }

    /// <summary>Every thing the job removed (changed pieces' old ones included).</summary>
    internal List<long> Removed { get; }

    internal Dictionary<long, ThingSnapshot> Snapshots { get; }
}

/// <summary>The inverse of a job: what to remove, what to build again, and why it cannot be done when it cannot.</summary>
internal sealed class UndoPlan
{
    internal UndoPlan(List<long> remove, List<ThingSnapshot> restore, List<string> diverged, List<string> notes)
    {
        Remove = remove;
        Restore = restore;
        Diverged = diverged;
        Notes = notes;
    }

    internal List<long> Remove { get; }

    internal List<ThingSnapshot> Restore { get; }

    /// <summary>What place_structure builds again: everything restored that is not a network piece.</summary>
    internal List<ThingSnapshot> RestoreStructures => Restore.FindAll(snapshot => snapshot.Piece == null);

    /// <summary>The network pieces restored, one group per place tool and grade, in the order first met.</summary>
    internal List<PieceRestore> RestorePieces
    {
        get
        {
            List<PieceRestore> groups = new List<PieceRestore>();
            foreach (ThingSnapshot snapshot in Restore)
            {
                NetworkPiece? piece = snapshot.Piece;
                if (piece?.Grade == null)
                {
                    continue;
                }

                PieceRestore? group = groups.Find(found => found.Tool == piece.Tool && found.Grade == piece.Grade);
                if (group == null)
                {
                    group = new PieceRestore(piece.Tool, piece.Grade, new List<ThingSnapshot>());
                    groups.Add(group);
                }

                group.Pieces.Add(snapshot);
            }

            return groups;
        }
    }

    /// <summary>Why the world no longer is as the job left it; the undo is refused while any is listed.</summary>
    internal List<string> Diverged { get; }

    internal List<string> Notes { get; }

    internal bool Ready => Diverged.Count == 0 && (Remove.Count > 0 || Restore.Count > 0);
}

/// <summary>
/// Plans the inverse of a finished job: remove everything it built, then build again everything it removed, each as it
/// stood (network pieces by their place tool). Refused (diverged) when something it built is gone or is no longer that
/// prefab, when something it removed has no snapshot, stood off the grid's axes or is a network piece no coil or kit
/// lays, or when the job had not finished. A burnt cable the job removed is never built again: a note says so and the
/// rest is undone. The job kinds that can be undone are the place and remove tools (runs and structures).
/// </summary>
internal static class UndoPlanner
{
    internal static readonly string[] Tools =
    {
        "place_cables", "place_pipes", "place_chutes", "remove_cables", "remove_pipes", "remove_chutes",
        "place_structure", "remove_structure"
    };

    private static readonly string[] Finished =
        { "applied", "applied_with_differences", "applied_unchecked", "stopped", "gas_lost" };

    /// <param name="standing">The prefab name of the thing standing with that id now; null when none does.</param>
    internal static UndoPlan Plan(JobFacts job, System.Func<long, string?> standing)
    {
        List<string> diverged = new List<string>();
        List<string> notes = new List<string>();
        if (System.Array.IndexOf(Tools, job.Tool) < 0)
        {
            diverged.Add($"{job.JobId} is a {job.Tool} job; undo_job undoes place_* and remove_* jobs only " +
                         "(paste_blueprint has its own undo).");
            return new UndoPlan(new List<long>(), new List<ThingSnapshot>(), diverged, notes);
        }

        if (System.Array.IndexOf(Finished, job.Status) < 0)
        {
            diverged.Add($"{job.JobId} is {job.Status}, not finished.");
            return new UndoPlan(new List<long>(), new List<ThingSnapshot>(), diverged, notes);
        }

        if (job.Status != "applied")
        {
            notes.Add($"The job ended {job.Status}: only what its log says it did is undone.");
        }

        List<long> remove = new List<long>();
        foreach ((long id, string? prefab) in job.Created)
        {
            string? now = standing(id);
            if (now == null)
            {
                diverged.Add($"{id} ({prefab ?? "built by the job"}) is gone.");
            }
            else if (prefab != null && now != prefab)
            {
                diverged.Add($"{id} is now {now}, not the {prefab} the job built.");
            }
            else if (!remove.Contains(id))
            {
                remove.Add(id);
            }
        }

        List<ThingSnapshot> restore = new List<ThingSnapshot>();
        foreach (long id in job.Removed)
        {
            if (!job.Snapshots.TryGetValue(id, out ThingSnapshot snapshot))
            {
                diverged.Add($"{id} was removed with no snapshot of how it stood (the job ran before this mod " +
                             "version, or was not started by a place or remove tool).");
            }
            else if (snapshot.Burnt)
            {
                notes.Add($"{id} ({snapshot.Prefab}) was a burnt cable; burnt pieces are never built again, so it " +
                          "stays gone.");
            }
            else if (snapshot.Turn == null)
            {
                diverged.Add($"{id} ({snapshot.Prefab}) stood off the grid's axes and cannot be placed again exactly.");
            }
            else if (snapshot.Piece != null && snapshot.Piece.Grade == null)
            {
                diverged.Add($"{id} ({snapshot.Prefab}) is a network piece no coil or kit lays; undo_job builds " +
                             $"network pieces again only through {snapshot.Piece.Tool}, so its guards apply.");
            }
            else if (standing(id) != null)
            {
                notes.Add($"{id} ({snapshot.Prefab}) still stands; it is not built again.");
            }
            else
            {
                restore.Add(snapshot);
            }
        }

        if (remove.Count == 0 && restore.Count == 0 && diverged.Count == 0)
        {
            diverged.Add($"{job.JobId} built and removed nothing that can be undone.");
        }

        return new UndoPlan(remove, restore, diverged, notes);
    }
}
