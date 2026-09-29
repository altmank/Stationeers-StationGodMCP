#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// How the things a place or remove job will remove stood when the job was started, and where the job took its
/// materials and put its refund (JobSource), kept by the job's id for undo_job (the job logs name removed things by id
/// only, and a job's status does not echo its request). The last KeptJobs jobs; in memory only.
/// </summary>
internal static class JobSnapshots
{
    private const int KeptJobs = 32;

    private static readonly Dictionary<string, RecordedJob> ByJob = new Dictionary<string, RecordedJob>();

    private static readonly Queue<string> Order = new Queue<string>();

    /// <summary>
    /// Records a started (or queued) job's snapshots under the job id its reply carries; a reply without one (busy,
    /// refused) records nothing. burnt: the burnt cables it removes (debris no coil lays), recorded as such so undo_job
    /// leaves them gone. Returns the reply unchanged.
    /// </summary>
    internal static object Record(object reply, string tool, IEnumerable<Structure> removed, Args args,
        IEnumerable<Structure>? burnt = null)
    {
        string? jobId = JobIdOf(reply);
        if (jobId == null)
        {
            return reply;
        }

        Dictionary<long, ThingSnapshot> snapshots = new Dictionary<long, ThingSnapshot>();
        foreach (Structure structure in removed)
        {
            if (structure != null)
            {
                snapshots[structure.ReferenceId] = Of(structure);
            }
        }

        foreach (Structure structure in burnt ?? System.Array.Empty<Structure>())
        {
            if (structure != null)
            {
                snapshots[structure.ReferenceId] = BurntOf(structure);
            }
        }

        if (!ByJob.ContainsKey(jobId))
        {
            Order.Enqueue(jobId);
        }

        ByJob[jobId] = new RecordedJob(tool, snapshots, SourceOf(args));
        while (ByJob.Count > KeptJobs && Order.Count > 0)
        {
            ByJob.Remove(Order.Dequeue());
        }

        return reply;
    }

    internal static RecordedJob? Of(string jobId) => ByJob.TryGetValue(jobId, out RecordedJob entry) ? entry : null;

    // The request's own source fields; each tool has already checked them.
    private static JobSource SourceOf(Args args) =>
        new JobSource(args.OptionalThingId("from_id")?.Value, args.OptionalBool("free") ?? false,
            args.OptionalString("refund_to")?.Trim().ToLowerInvariant(), args.OptionalBool("refund"));

    internal static ThingSnapshot Of(Structure structure)
    {
        Quaternion rotation = structure.ThingTransformRotation;
        return new ThingSnapshot(structure.ReferenceId, structure.PrefabName, Bodies.V(structure.ThingTransformPosition),
            CubeRotation.FromQuaternion(rotation.x, rotation.y, rotation.z, rotation.w),
            structure.CurrentBuildStateIndex,
            string.IsNullOrEmpty(structure.CustomName) ? null : structure.CustomName, PieceOf(structure));
    }

    private static ThingSnapshot BurntOf(Structure structure) =>
        new ThingSnapshot(structure.ReferenceId, structure.PrefabName, Bodies.V(structure.ThingTransformPosition), null,
            structure.CurrentBuildStateIndex, null, null, burnt: true);

    private static readonly RunKind[] Kinds = { new CableRunKind(), new PipeRunKind(), new ChuteRunKind() };

    // A cable, pipe or chute piece as its place tool builds it again: the tool, the grade name that lays it (null when
    // no coil or kit does) and its ends cell by cell; null for anything else.
    private static NetworkPiece? PieceOf(Structure structure)
    {
        if (!(structure is SmallGrid piece))
        {
            return null;
        }

        foreach (RunKind kind in Kinds)
        {
            if (!kind.Family.IsPiece(piece))
            {
                continue;
            }

            Grade? grade = kind.Family.RunGradeOf(piece);
            string? name = grade == null
                ? null
                : System.Array.Find(kind.GradeNames, candidate => kind.GradeOf(candidate)?.SameAs(grade) == true);
            return new NetworkPiece(kind.PlaceTool, name, NetworkPiece.CellsOf(PieceShapes.Live(piece)));
        }

        return null;
    }

    /// <summary>A reply as the client sees it, for reading its fields.</summary>
    internal static JObject Wire(object reply) =>
        JObject.Parse(JsonConvert.SerializeObject(reply, ApiJson.Settings));

    private static string? JobIdOf(object reply)
    {
        JToken? id = Wire(reply)["job_id"];
        return id != null && id.Type == JTokenType.String ? id.Value<string>() : null;
    }
}

/// <summary>What JobSnapshots keeps of one job: the tool, the snapshots of what it removes, and its source.</summary>
internal sealed class RecordedJob
{
    internal RecordedJob(string tool, Dictionary<long, ThingSnapshot> removed, JobSource source)
    {
        Tool = tool;
        Removed = removed;
        Source = source;
    }

    internal string Tool { get; }

    internal Dictionary<long, ThingSnapshot> Removed { get; }

    internal JobSource Source { get; }
}
