#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Jobs;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// How the things a place or remove job will remove stood when the job was started, and where the job took its
/// materials and put its refund (JobSource), kept by the job's id for undo (the job logs name removed things by id
/// only, and a job's status does not echo its request). Kept in KeptJobs.Main.Snapshots, bounded by memory, the job's
/// pin (its caller's stream) passed over until it is released; in memory only.
/// </summary>
internal static class JobSnapshots
{
    /// <summary>Undone jobs whose undo is remembered (RecordUndo).</summary>
    internal const int KeptUndos = 32;

    private static KeptJobStore<RecordedJob> ByJob => KeptJobs.Main.Snapshots;

    private static readonly Dictionary<string, List<string>> Undos = new Dictionary<string, List<string>>();

    private static readonly Queue<string> UndoOrder = new Queue<string>();

    /// <summary>
    /// Records a started (or queued) job's snapshots under the job id its reply carries; a reply without one (busy,
    /// refused) records nothing. source: where the job took its materials and put its refund (JobSource). burnt: the
    /// burnt cables it removes (debris no coil lays), recorded as such so undo leaves them gone. Returns the reply
    /// unchanged.
    /// </summary>
    internal static object Record(object reply, string tool, IEnumerable<Structure> removed, JobSource source,
        IEnumerable<Structure>? burnt = null, IReadOnlyList<BoardGhost>? completes = null)
    {
        string? jobId = JobIdIn(reply);
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

        RecordedJob job = new RecordedJob(tool, snapshots, source, completes);
        ByJob.Put(jobId, job, KeptJobSizes.Snapshot(job));
        return reply;
    }

    /// <summary>The world was left: its jobs' snapshots and undo records name things of that world only.</summary>
    internal static void Clear()
    {
        ByJob.Clear();
        Undos.Clear();
        UndoOrder.Clear();
    }

    internal static RecordedJob? Of(string jobId) => ByJob.Of(jobId);

    /// <summary>Whether the job's snapshots were evicted under their memory bound (undo_expired).</summary>
    internal static bool Evicted(string jobId) => ByJob.WasEvicted(jobId);

    /// <summary>
    /// Records the jobs a real undo run started to undo a job (its removal, placement and piece runs), so a later
    /// undo of the same job says it was already undone. The last KeptUndos undone jobs; in memory only.
    /// </summary>
    internal static void RecordUndo(string jobId, List<string> undoJobIds)
    {
        if (undoJobIds.Count == 0)
        {
            return;
        }

        if (!Undos.ContainsKey(jobId))
        {
            UndoOrder.Enqueue(jobId);
        }

        Undos[jobId] = undoJobIds;
        while (Undos.Count > KeptUndos && UndoOrder.Count > 0)
        {
            Undos.Remove(UndoOrder.Dequeue());
        }
    }

    /// <summary>The jobs an earlier real undo run of the job started; empty when there was none.</summary>
    internal static List<string> UndoOf(string jobId) =>
        Undos.TryGetValue(jobId, out List<string> ids) ? ids : new List<string>();

    /// <summary>A job's id in a tool's reply; null when the reply started none (busy, refused, a dry run).</summary>
    internal static string? JobIdOf(object? reply) => reply == null ? null : JobIdIn(reply);

    internal static ThingSnapshot Of(Structure structure)
    {
        Quaternion rotation = structure.ThingTransformRotation;
        NetworkPiece? piece = PieceOf(structure);
        return new ThingSnapshot(structure.ReferenceId, structure.PrefabName, Bodies.V(structure.ThingTransformPosition),
            CubeRotation.FromQuaternion(rotation.x, rotation.y, rotation.z, rotation.w),
            structure.CurrentBuildStateIndex,
            string.IsNullOrEmpty(structure.CustomName) ? null : structure.CustomName, piece,
            colour: ColourOf(structure, piece != null));
    }

    // The paint a thing is built again in: a piece's (its run lays it in that colour, as a split long's singles take
    // theirs), or a structure's that build structure can build in a colour (PlacePlanner's not_paintable rule); null
    // for the rest, so its undo is never refused for a colour it cannot take.
    private static int? ColourOf(Structure structure, bool piece)
    {
        if (structure.CustomColor == null)
        {
            return null;
        }

        bool builds = piece || (!structure.HasColorState && structure.IsPaintable && structure.PaintableMaterial != null &&
                                structure.structureRenderMode == StructureRenderMode.Standard);
        return builds ? GameManager.GetColorIndex(structure.CustomColor) : null;
    }

    private static ThingSnapshot BurntOf(Structure structure) =>
        new ThingSnapshot(structure.ReferenceId, structure.PrefabName, Bodies.V(structure.ThingTransformPosition), null,
            structure.CurrentBuildStateIndex, null, null, burnt: true);

    private static readonly RunKind[] Kinds = { new CableRunKind(), new PipeRunKind(), new ChuteRunKind() };

    /// <summary>The kind of piece the standing thing with that id is (cable, pipe or chute); null otherwise.</summary>
    internal static string? PieceToolOf(long id)
    {
        if (!GameLookup.TryFindThing(new ThingId(id), out Thing thing) || !(thing is SmallGrid piece))
        {
            return null;
        }

        RunKind? kind = System.Array.Find(Kinds, candidate => candidate.Family.IsPiece(piece));
        return kind?.Noun;
    }

    // A cable, pipe or chute piece as build lays it again: the kind, the grade name that lays it (null when no coil
    // or kit does) and its ends cell by cell; null for anything else.
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
            return new NetworkPiece(kind.Noun, name, NetworkPiece.CellsOf(PieceShapes.Live(piece)));
        }

        return null;
    }

    /// <summary>A reply as the client sees it, for reading its fields.</summary>
    internal static JObject Wire(object reply) =>
        JObject.Parse(JsonConvert.SerializeObject(reply, ApiJson.Settings));

    private static string? JobIdIn(object reply)
    {
        JToken? id = Wire(reply)["job_id"];
        return id != null && id.Type == JTokenType.String ? id.Value<string>() : null;
    }
}
