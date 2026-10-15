#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.WorldModel;

/// <summary>
/// The damaged things, followed (cost design A2): every registered thing that is a wreck (Wrecks.IsBroken) or whose
/// destructible damage state is above 0, as find damaged keeps it at min_damage 0. Kept by the game's damage change
/// points (WorldEvents: IndestructableDamageState.Damage, which every damage, heal and repair goes through; its Copy and
/// Read; a pipe bursting; a structure's build state), each telling the thing from whatever thread wrote it, and seeded
/// as the world index files each thing. The main thread files what was told a budget a frame (Tick) and whatever still
/// waits before a reader reads (Settle). A thing the world let go is dropped when a reader meets it.
/// <para>
/// Verify mode ([Performance] VerifyFacts): a reader also walks every registered thing and logs a difference.
/// </para>
/// </summary>
[ReplyFact(FactRule.Followed, "which things are damaged or wrecked")]
internal static class DamagedThings
{
    // Things filed a frame: about 0.2 ms of a frame at most.
    private const int FileBudget = 2048;

    // Things verify mode compares a step.
    private const int VerifyBatch = 512;

    private static readonly FollowedSet<Thing> Set = new FollowedSet<Thing>(Holds);
    private static long _verified;
    private static long _differences;

    /// <summary>[Performance] VerifyFacts: a reader also walks every thing and logs a difference.</summary>
    internal static bool Verify { get; set; }

    internal static bool Ready => WorldEvents.DamageMissing.Length == 0 && ThingIndex.Ready;

    /// <summary>A change point changed the thing's damage or wreck state. Any thread.</summary>
    internal static void Changed(Thing thing)
    {
        if (!ReferenceEquals(thing, null))
        {
            Set.Changed(thing, Holds(thing));
        }
    }

    /// <summary>The world index filed the thing for the first time: read it at the next filing. Main thread.</summary>
    internal static void Filed(Thing thing) => Set.Queue(thing);

    /// <summary>Each server frame: files a budget of what was told.</summary>
    internal static void Tick()
    {
        if (!Ready)
        {
            return;
        }

        using ProfScope scope = Prof.Scope(ProfId.WorldFacts);
        Set.Drain(FileBudget);
    }

    /// <summary>
    /// The damaged things now (a copy proportional to them, never the world): the world index settled and everything
    /// told filed first, things the world let go dropped. Refuses game_changed when a change point is missing.
    /// </summary>
    internal static List<Thing> Members()
    {
        if (!Ready)
        {
            throw new GameChangedException(WorldEvents.DamageMissing.Length > 0
                ? WorldEvents.DamageMissing
                : "the world index is off");
        }

        using ProfScope scope = Prof.Scope(ProfId.WorldFacts);
        ThingIndex.Settle();
        Set.Drain(int.MaxValue);
        List<Thing> members = Set.Members();
        int kept = 0;
        for (int index = 0; index < members.Count; index++)
        {
            Thing thing = members[index];
            if (World.Holds(WorldClass.Thing, thing))
            {
                members[kept++] = thing;
            }
            else
            {
                Set.Remove(thing);
            }
        }

        members.RemoveRange(kept, members.Count - kept);
        return members;
    }

    /// <summary>
    /// Verify mode: every registered thing (the world index's Thing class) against the set, a difference logged; the
    /// walk is the world's size, so it runs only in verify mode, a batch of things a step (a Work body).
    /// </summary>
    internal static IEnumerable<object?> Comparing(List<Thing> members)
    {
        HashSet<long> followed = new HashSet<long>();
        foreach (Thing thing in members)
        {
            followed.Add(thing.ReferenceId);
        }

        List<string> missing = new List<string>();
        int walked = 0;
        List<Thing> things = World.Candidates(WorldClass.Thing);
        for (int index = 0; index < things.Count; index++)
        {
            Thing thing = things[index];
            if (World.Holds(WorldClass.Thing, thing) && Holds(thing))
            {
                walked++;
                if (!followed.Remove(thing.ReferenceId) && missing.Count < 5)
                {
                    missing.Add($"{thing.ReferenceId} {thing.PrefabName}");
                }
            }

            if (index % VerifyBatch == VerifyBatch - 1)
            {
                yield return null;
            }
        }

        _verified++;
        if (walked == members.Count && followed.Count == 0)
        {
            yield break;
        }

        _differences++;
        StationGodMod.LogWarning($"Damaged things differ from a walk: {walked} damaged, {members.Count} followed; " +
                                 $"missing [{string.Join(", ", missing)}]; extra {followed.Count}.");
    }

    /// <summary>server info runtime.facts.damaged.</summary>
    internal static DamagedFactView View() =>
        new DamagedFactView(Ready, Verify, Set.Count, Set.Waiting, Set.Filed, _verified, _differences);

    /// <summary>A world left.</summary>
    internal static void Clear() => Set.Clear();

    // Whether the thing is in the set: a wreck, or destructible and damaged above 0. Read on any thread: plain field
    // reads, no engine calls.
    private static bool Holds(Thing thing)
    {
        IndestructableDamageState damage = thing.DamageState;
        if (!ReferenceEquals(damage, null) && damage.Indestructable)
        {
            return false;
        }

        bool measurable = !ReferenceEquals(damage, null) && damage.MaxDamage > 0f;
        return HealthCondition.ScanKeeps(Wrecks.IsBroken(thing), measurable, measurable ? damage!.TotalRatio : 0f, 0.0,
            false);
    }
}
