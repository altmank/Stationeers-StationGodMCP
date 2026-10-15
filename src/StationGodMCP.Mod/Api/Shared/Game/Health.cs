#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;
using World = StationGodMCP.WorldModel.World;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The damage state of any thing, read from Thing.DamageState; no LogicType exposes damage.
///
/// How the game keeps damage (CODE, Assets.Scripts.Objects): Thing.DamageState is an IndestructableDamageState. A
/// normal thing gets a ThingDamageState (Thing.InitializeDamageState), whose Total is Clamp(Brute + Burn + Oxygen +
/// Toxic + Radiation + Hydration + Starvation + Decay, 0, MaxDamage) (Stun is not counted) and TotalRatio is Total /
/// MaxDamage: 0 like new, 1 destroyed. A thing marked Indestructable gets the base class, whose Total is always 0 and
/// which refuses all damage: that is damage_state "indestructible" with no total, ratio or health, never a fake 0.
/// Thing.IsBroken is Total >= MaxDamage, and a Structure is also broken below build state 0. When a structure with a
/// broken mesh reaches MaxDamage, ThingDamageState.Destroy sets its build state below 0 (GetBrokenState), calls
/// OnStructureBroken and HealAll, so a broken structure reads 0 damage and 100 % health: condition "broken" and
/// is_broken are the signal, never the numbers (Pure/HealthCondition). A SolarPanel's tooltip
/// shows Health = RoundToInt(100 - TotalRatio * 100) coloured by SolarPanel.DamageColor, and it generates
/// PowerGenerated * GenerationEfficiency * (1 - TotalRatio). A Pipe also has IsBurst (PipeBurst), which the game keeps
/// apart from DamageState: a burst pipe reads 0 damage, so it is reported broken (Wrecks) like a broken structure. A
/// cable an overload burnt is replaced by a separate CableRuptured piece (StructureCableStraightBurnt and the like),
/// undamaged and carrying no power: reported broken too.
///
/// A thing in a slot keeps no position of its own, so position and distance are its outermost holder's
/// (HolderChain.PlaceOf).
///
/// A structure's networks walk its open ends, the costly part of a reading (the catalogue's x-costly): a list reads
/// them only when the call's fields keep networks in that list.
/// </summary>
/// <summary>Reads one thing's damage state into a HealthView.</summary>
internal static class HealthReader
{
    private const int RatioDecimals = 4;
    private const float FullHealthPercent = 100f;

    /// <summary>
    /// withNetworks false leaves networks out (null), as if the thing had none to list; withBuildState (the one-thing
    /// form) adds a structure's build state with what its next state takes.
    /// </summary>
    internal static HealthView Read(Thing thing, PlayerOrigin origin, bool withNetworks, bool withBuildState = false)
    {
        IndestructableDamageState damage = thing.DamageState;
        DamageReading reading = ReadDamage(damage);
        bool broken = Wrecks.IsBroken(thing);
        HealthFlags flags = new HealthFlags(
            thing is SolarPanel panel && damage != null && !damage.Indestructable ? panel.DamageColor : null,
            broken,
            thing.IsBeingDestroyed,
            thing is Pipe pipe ? PipeBurstName(pipe.IsBurst) : null,
            HealthCondition.Of(broken, damage != null, damage != null && damage.Indestructable, reading.DamageRatio),
            thing is Structure structure ? structure.CurrentBuildStateIndex < 0 : (bool?)null,
            thing is Pipe recorded ? PipeDamageRecord.Causes((byte)recorded.DamageRecord) : null);
        Vector3 place = HolderChain.PlaceOf(thing).Position;
        return new HealthView(
            GameLookup.ViewOf(thing), KindOf(thing), thing.GetType().Name, reading, flags,
            GameLookup.ViewOf(place), origin.DistanceTo(place), Labels.CustomNameOf(thing),
            withNetworks && thing is Structure ? EndsReader.NetworksOf(thing) : null,
            withBuildState ? BuildStates.Of(thing) : null);
    }

    private static string KindOf(Thing thing) => thing is Structure ? "structure" : thing is Item ? "item" : "other";

    private static DamageReading ReadDamage(IndestructableDamageState? damage)
    {
        if (damage == null)
        {
            return new DamageReading("none", null, null, null, null, null, null);
        }

        bool destructible = !damage.Indestructable;
        bool measured = destructible && damage.MaxDamage > 0f;
        float ratio = measured ? damage.TotalRatio : 0f;
        return new DamageReading(
            destructible ? "destructible" : "indestructible",
            damage.GetType().Name,
            damage.MaxDamage,
            measured ? damage.Total : (double?)null,
            measured ? Math.Round(ratio, RatioDecimals) : (double?)null,
            measured ? Mathf.RoundToInt(FullHealthPercent - ratio * FullHealthPercent) : (int?)null,
            new DamagePartsView(damage.Brute, damage.Burn, damage.Oxygen, damage.Hydration, damage.Starvation,
                damage.Toxic, damage.Radiation, damage.Decay, damage.Stun));
    }

    internal static string PipeBurstName(PipeBurst burst)
    {
        switch ((byte)burst)
        {
            case 0:
                return "none";
            case 1:
                return "pressure";
            case 2:
                return "liquid";
            case 4:
                return "solid";
            default:
                return ((byte)burst).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}

/// <summary>
/// What a damage scan keeps: damage ratio above minDamageRatio, only broken things, only structures, and within metres
/// of the player.
/// </summary>
internal sealed class HealthScanFilter
{
    internal HealthScanFilter(double minDamageRatio, bool structuresOnly, bool brokenOnly, double? nearPlayerM)
    {
        MinDamageRatio = minDamageRatio;
        StructuresOnly = structuresOnly;
        BrokenOnly = brokenOnly;
        NearPlayerM = nearPlayerM;
    }

    public double MinDamageRatio { get; }

    public bool StructuresOnly { get; }

    public bool BrokenOnly { get; }

    public double? NearPlayerM { get; }
}

/// <summary>A scan's things, broken first then worst first, the structures and broken things among them, and the things scanned.</summary>
internal sealed class HealthScanned
{
    internal HealthScanned(List<Thing> things, int structures, int broken, int scanned)
    {
        Things = things;
        Structures = structures;
        Broken = broken;
        Scanned = scanned;
    }

    public List<Thing> Things { get; }

    public int Structures { get; }

    public int Broken { get; }

    public int Scanned { get; }
}

/// <summary>
/// A scan of every damaged thing registered (World.Things), broken first, then worst first; broken
/// things are listed whatever their (healed) numbers say. It leaves out things being destroyed, indestructible damage
/// states, entities and organs.
/// </summary>
internal static class HealthScanner
{
    internal static HealthScanned Scan(HealthScanFilter scan, PlayerOrigin origin)
    {
        List<Thing> things = World.Things();
        List<HealthRow> rows = new List<HealthRow>();
        int structures = 0;
        int broken = 0;
        for (int index = 0; index < things.Count; index++)
        {
            HealthRow? row = RowOf(things[index], scan, origin);
            if (row == null)
            {
                continue;
            }

            rows.Add(row);
            structures += row.Thing is Structure ? 1 : 0;
            broken += Wrecks.IsBroken(row.Thing) ? 1 : 0;
        }

        rows.Sort(static (a, b) => HealthRow.WorstFirst(a, b));
        return new HealthScanned(rows.ConvertAll(static row => row.Thing), structures, broken, things.Count);
    }

    /// <summary>
    /// The things worst first (broken, then highest ratio, then highest total, then lowest id), as the scan orders
    /// them; damagedOnly keeps only what the scan would list (broken, or damaged above 0).
    /// </summary>
    internal static List<Thing> WorstFirst(List<Thing> things, bool damagedOnly)
    {
        List<HealthRow> rows = new List<HealthRow>(things.Count);
        foreach (Thing thing in things)
        {
            IndestructableDamageState damage = thing.DamageState;
            bool broken = Wrecks.IsBroken(thing);
            bool measurable = damage != null && !damage.Indestructable && damage.MaxDamage > 0f;
            float ratio = measurable ? damage!.TotalRatio : 0f;
            if (damagedOnly && !HealthCondition.ScanKeeps(broken, measurable, ratio, 0.0, false))
            {
                continue;
            }

            rows.Add(new HealthRow(thing, (float)HealthCondition.RankRatio(broken, ratio),
                measurable ? damage!.Total : 0f));
        }

        rows.Sort(static (a, b) => HealthRow.WorstFirst(a, b));
        List<Thing> ranked = new List<Thing>(rows.Count);
        foreach (HealthRow row in rows)
        {
            ranked.Add(row.Thing);
        }

        return ranked;
    }

    // A thing the scan lists, or null: not being destroyed, not an entity or organ, destructible, damaged above the
    // floor, a structure when asked, and near the player when asked.
    private static HealthRow? RowOf(Thing thing, HealthScanFilter scan, PlayerOrigin origin)
    {
        if (thing == null || thing.IsCursor || thing.IsBeingDestroyed || thing is Entity || IsOrgan(thing) ||
            (scan.StructuresOnly && !(thing is Structure)))
        {
            return null;
        }

        IndestructableDamageState damage = thing.DamageState;
        if (damage != null && damage.Indestructable)
        {
            return null;
        }

        bool broken = Wrecks.IsBroken(thing);
        bool measurable = damage != null && damage.MaxDamage > 0f;
        float ratio = measurable ? damage!.TotalRatio : 0f;
        if (!HealthCondition.ScanKeeps(broken, measurable, ratio, scan.MinDamageRatio, scan.BrokenOnly))
        {
            return null;
        }

        // A stored item or planted plant keeps no position of its own: its outermost holder places it.
        double? distance = origin.ExactDistanceTo(HolderChain.PlaceOf(thing).Position);
        if (scan.NearPlayerM.HasValue && !(distance <= scan.NearPlayerM.Value))
        {
            return null;
        }

        return new HealthRow(thing, (float)HealthCondition.RankRatio(broken, ratio), measurable ? damage!.Total : 0f);
    }

    private static bool IsOrgan(Thing thing) =>
        thing is Organ ||
        (thing is DynamicThing dynamic && dynamic.ParentSlot != null && dynamic.ParentSlot.Type == Slot.Class.Organ);

    private sealed class HealthRow
    {
        internal HealthRow(Thing thing, float ratio, float total)
        {
            Thing = thing;
            Ratio = ratio;
            Total = total;
        }

        internal Thing Thing { get; }

        internal float Ratio { get; }

        internal float Total { get; }

        // Highest ratio, then highest total, then lowest reference id.
        internal static int WorstFirst(HealthRow a, HealthRow b)
        {
            int byRatio = b.Ratio.CompareTo(a.Ratio);
            if (byRatio != 0)
            {
                return byRatio;
            }

            int byTotal = b.Total.CompareTo(a.Total);
            return byTotal != 0 ? byTotal : a.Thing.ReferenceId.CompareTo(b.Thing.ReferenceId);
        }
    }
}

/// <summary>
/// Every piece of one pipe, cable or chute network (its StructureList; devices are not pieces), worst first,
/// damaged_only keeping the damaged and broken ones (Ranked), with the network's facts: its id and kind, its pieces,
/// and the broken pieces that touch it at its ends without being on it (BrokenNeighbours: the member list cannot show
/// those, damaged_only or not).
/// </summary>
internal sealed class NetworkHealth
{
    private NetworkHealth(List<Thing> ranked, NetworkHealthFacts facts)
    {
        Ranked = ranked;
        Facts = facts;
    }

    internal List<Thing> Ranked { get; }

    internal NetworkHealthFacts Facts { get; }

    internal static NetworkHealth Scan(UpgradeFamily family, ThingId id, bool damagedOnly)
    {
        List<SmallGrid> members = family.NetworkMembers(id);
        List<Thing> pieces = new List<Thing>(members.Count);
        foreach (SmallGrid member in members)
        {
            if (member != null && !member.IsBeingDestroyed)
            {
                pieces.Add(member);
            }
        }

        List<Thing> touching = new List<Thing>(pieces);
        touching.AddRange(family.NetworkDevices(id));
        return new NetworkHealth(HealthScanner.WorstFirst(pieces, damagedOnly),
            new NetworkHealthFacts(id, family.NetworkKind, pieces.Count, damagedOnly,
                BrokenNeighbours.Around(family.NetworkKind, touching, "things")));
    }
}

/// <summary>A network's health facts, which every page of its walk answers.</summary>
internal sealed class NetworkHealthFacts
{
    internal NetworkHealthFacts(ThingId networkId, string kind, int pieces, bool damagedOnly,
        BrokenNeighbourReport broken)
    {
        NetworkId = networkId;
        Kind = kind;
        Pieces = pieces;
        DamagedOnly = damagedOnly;
        Broken = broken;
    }

    public ThingId NetworkId { get; }

    public string Kind { get; }

    public int Pieces { get; }

    public bool DamagedOnly { get; }

    public BrokenNeighbourReport Broken { get; }
}
