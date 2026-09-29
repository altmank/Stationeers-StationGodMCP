#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// How the things a place or remove job will remove stood when the job was started, kept by the job's id for
/// undo_job (the job logs name removed things by id only). The last KeptJobs jobs; in memory only.
/// </summary>
internal static class JobSnapshots
{
    private const int KeptJobs = 32;

    private static readonly Dictionary<string, (string Tool, Dictionary<long, ThingSnapshot> Removed)> ByJob =
        new Dictionary<string, (string, Dictionary<long, ThingSnapshot>)>();

    private static readonly Queue<string> Order = new Queue<string>();

    /// <summary>
    /// Records a started (or queued) job's snapshots under the job id its reply carries; a reply without one (busy,
    /// refused) records nothing. Returns the reply unchanged.
    /// </summary>
    internal static object Record(object reply, string tool, IEnumerable<Structure> removed)
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

        if (!ByJob.ContainsKey(jobId))
        {
            Order.Enqueue(jobId);
        }

        ByJob[jobId] = (tool, snapshots);
        while (ByJob.Count > KeptJobs && Order.Count > 0)
        {
            ByJob.Remove(Order.Dequeue());
        }

        return reply;
    }

    internal static (string Tool, Dictionary<long, ThingSnapshot> Removed)? Of(string jobId) =>
        ByJob.TryGetValue(jobId, out (string, Dictionary<long, ThingSnapshot>) entry) ? entry : null;

    internal static ThingSnapshot Of(Structure structure)
    {
        Quaternion rotation = structure.ThingTransformRotation;
        return new ThingSnapshot(structure.ReferenceId, structure.PrefabName, Bodies.V(structure.ThingTransformPosition),
            CubeRotation.FromQuaternion(rotation.x, rotation.y, rotation.z, rotation.w),
            structure.CurrentBuildStateIndex,
            string.IsNullOrEmpty(structure.CustomName) ? null : structure.CustomName);
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
