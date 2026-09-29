#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>A structure as it stood before a job removed it: enough to build it again the same way.</summary>
internal sealed class ThingSnapshot
{
    internal ThingSnapshot(long id, string prefab, Vec3 position, CubeRotation? turn, int buildState, string? label)
    {
        Id = id;
        Prefab = prefab;
        Position = position;
        Turn = turn;
        BuildState = buildState;
        Label = label;
    }

    internal long Id { get; }

    internal string Prefab { get; }

    internal Vec3 Position { get; }

    /// <summary>Its turn; null when it stood off the grid's axes (it cannot be placed again exactly).</summary>
    internal CubeRotation? Turn { get; }

    internal int BuildState { get; }

    internal string? Label { get; }
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

    /// <summary>Why the world no longer is as the job left it; the undo is refused while any is listed.</summary>
    internal List<string> Diverged { get; }

    internal List<string> Notes { get; }

    internal bool Ready => Diverged.Count == 0 && (Remove.Count > 0 || Restore.Count > 0);
}

/// <summary>
/// Plans the inverse of a finished job: remove everything it built, then build again everything it removed, each as it
/// stood. Refused (diverged) when something it built is gone or is no longer that prefab, when something it removed has
/// no snapshot or stood off the grid's axes, or when the job had not finished. The job kinds that can be undone are
/// the place and remove tools (runs and structures).
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
            else if (snapshot.Turn == null)
            {
                diverged.Add($"{id} ({snapshot.Prefab}) stood off the grid's axes and cannot be placed again exactly.");
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
