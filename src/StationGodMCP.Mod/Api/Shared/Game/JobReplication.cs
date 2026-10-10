#nullable enable

using System.Runtime.CompilerServices;
using System.Threading;
using Assets.Scripts;
using Assets.Scripts.Networking;
using HarmonyLib;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The host's side of ClientReplication for the held-tick jobs: after a job step, the next one waits until a state
/// packet written once that step's frame has ended has gone out to the clients. Counts the state packets the game
/// writes (StateWritePatch). Host only; with no client connected nothing waits. Main thread; the count is read and
/// written through Interlocked, since the game writes state packets on an async send path.
/// </summary>
internal static class JobReplication
{
    private static readonly ClientReplication Gate = new ClientReplication();
    private static bool? _counting;

    private static long _stateWrites;

    /// <summary>State packets the game has written since the mod loaded.</summary>
    internal static long StateWrites => Interlocked.Read(ref _stateWrites);

    internal static void Written() => Interlocked.Increment(ref _stateWrites);

    /// <summary>A job step ran this frame.</summary>
    internal static void Changed() => Gate.Changed(Time.frameCount);

    /// <summary>Whether a job may take its next step this frame.</summary>
    internal static bool MayStep()
    {
        bool watching = NetworkManager.IsServer && NetworkServer.HasClients();
        if (watching && !Counting())
        {
            Net.OnceLog.Warning("job_replication_uncounted",
                "The state packet hook is not in place: jobs do not wait for clients to receive each step, so a " +
                "client may end with other pipe and chute networks than the host.");
            watching = false;
        }

        switch (Gate.Pass(new HostSending(Time.frameCount, StateWrites, watching, Time.realtimeSinceStartup)))
        {
            case ReplicationPass.Open:
                return true;
            case ReplicationPass.TimedOut:
                StationGodMod.LogWarning(
                    $"No state packet went to the clients within {ClientReplication.TimeoutSeconds:0} s of a job " +
                    "step; the job goes on, and a client may need to rejoin if its networks differ from the host's.");
                return true;
            default:
                return false;
        }
    }

    // StateWritePatch is applied with the mod's other patches; a game build without the target leaves it out.
    private static bool Counting()
    {
        if (_counting.HasValue)
        {
            return _counting.Value;
        }

        bool counting = false;
        if (GameMembers.PatchWriteStateImmediate.TryResolve())
        {
            Patches? patches = Harmony.GetPatchInfo(GameMembers.PatchWriteStateImmediate.Info);
            if (patches != null)
            {
                foreach (Patch patch in patches.Postfixes)
                {
                    counting |= patch.owner == StationGodMod.ModId;
                }
            }
        }

        _counting = counting;
        return counting;
    }
}

[HarmonyPatch(typeof(FragmentHandler), "WriteStateImmediate")]
internal static class StateWritePatch
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Postfix() => JobReplication.Written();
}
