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
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// thing_health: the damage state of any thing, read from Thing.DamageState; no LogicType exposes damage. Read only.
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
/// apart from DamageState: a burst pipe reads 0 damage, so it is reported broken (Wrecks) like a broken structure.
///
/// Three forms: reference_id, one thing; reference_ids, up to 256, a result per id; neither, a scan of every damaged
/// thing registered in OcclusionManager.AllThings, broken first, then worst first, paged; broken things are listed
/// whatever their (healed) numbers say, and broken_only lists only them. The scan leaves out things being destroyed,
/// indestructible damage states, entities (see player_vitals) and organs.
/// </summary>
internal static class ThingHealthApi
{
    internal const int MaximumIds = 256;

    internal static object Handle(Args args)
    {
        switch (HealthRequest.Parse(args))
        {
            case HealthRequest.One one:
                return HealthReader.Read(GameLookup.RequireThing(one.Id), PlayerOrigin.Current());
            case HealthRequest.Many many:
                return ReadMany(many.Ids);
            case HealthRequest.Scan scan:
                return HealthScanner.Scan(scan);
            default:
                throw ApiErrors.InvalidArgument("Unknown thing_health form.");
        }
    }

    private static BatchResultView ReadMany(JArray ids)
    {
        PlayerOrigin origin = PlayerOrigin.Current();
        BatchBuilder batch = new BatchBuilder(ids.Count);
        for (int index = 0; index < ids.Count; index++)
        {
            if (!ThingId.TryRead(ids[index], out ThingId id))
            {
                batch.Failed(
                    index, ApiErrors.InvalidArgument("Argument 'reference_id' must be an Int64 encoded as a string."));
            }
            else if (!GameLookup.TryFindThing(id, out Thing thing))
            {
                batch.Failed(index, ApiErrors.ThingNotFound(id));
            }
            else
            {
                batch.Succeeded(new HealthItemView(index, HealthReader.Read(thing, origin)));
            }
        }

        return batch.Build();
    }
}

/// <summary>thing_health's three forms.</summary>
internal abstract class HealthRequest
{
    private static readonly string[] ScanOnly =
    {
        "min_damage_ratio", "min_ratio", "structures_only", "broken_only", "near_player_m", "offset", "limit"
    };

    private HealthRequest()
    {
    }

    internal static HealthRequest Parse(Args args)
    {
        bool one = args.Has("reference_id");
        bool many = args.Has("reference_ids");
        if (one && many)
        {
            throw ApiErrors.InvalidArgument("Pass reference_id or reference_ids, not both.");
        }

        if (one || many)
        {
            args.Reject(one ? "reference_id" : "reference_ids", ScanOnly);
            return one
                ? new One(args.ThingId("reference_id"))
                : new Many(args.Array("reference_ids", ThingHealthApi.MaximumIds));
        }

        return Scan.From(args);
    }

    internal sealed class One : HealthRequest
    {
        internal One(ThingId id)
        {
            Id = id;
        }

        internal ThingId Id { get; }
    }

    internal sealed class Many : HealthRequest
    {
        internal Many(JArray ids)
        {
            Ids = ids;
        }

        internal JArray Ids { get; }
    }

    internal sealed class Scan : HealthRequest
    {
        internal const int DefaultLimit = 200;
        internal const int MaximumLimit = 500;

        private Scan(double minDamageRatio, bool structuresOnly, bool brokenOnly, double? nearPlayerM,
            PageRequest page)
        {
            MinDamageRatio = minDamageRatio;
            StructuresOnly = structuresOnly;
            BrokenOnly = brokenOnly;
            NearPlayerM = nearPlayerM;
            Page = page;
        }

        internal double MinDamageRatio { get; }

        internal bool StructuresOnly { get; }

        /// <summary>Only things in the game's broken state.</summary>
        internal bool BrokenOnly { get; }

        internal double? NearPlayerM { get; }

        internal PageRequest Page { get; }

        // min_ratio is the older name of min_damage_ratio, still read.
        internal static Scan From(Args args)
        {
            string name = args.Has("min_damage_ratio") ? "min_damage_ratio" : "min_ratio";
            double minimum = args.OptionalDouble(name) ?? 0.0;
            if (minimum < 0.0 || minimum >= 1.0)
            {
                throw ApiErrors.InvalidArgument($"Argument '{name}' must be at least 0 and below 1.");
            }

            return new Scan(
                minimum,
                args.OptionalBool("structures_only") ?? false,
                args.OptionalBool("broken_only") ?? false,
                args.OptionalPositiveDouble("near_player_m"),
                PageRequest.From(args, DefaultLimit, MaximumLimit));
        }
    }
}

/// <summary>Reads one thing's damage state into a HealthView.</summary>
internal static class HealthReader
{
    private const int RatioDecimals = 4;
    private const float FullHealthPercent = 100f;

    internal static HealthView Read(Thing thing, PlayerOrigin origin)
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
            thing is Structure structure ? structure.CurrentBuildStateIndex < 0 : (bool?)null);
        return new HealthView(
            GameLookup.ViewOf(thing), KindOf(thing), thing.GetType().Name, reading, flags,
            GameLookup.ViewOf(thing.Position), origin.DistanceTo(thing.Position), Labels.CustomNameOf(thing),
            thing is Structure ? EndsReader.NetworksOf(thing) : null);
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

    private static string PipeBurstName(PipeBurst burst)
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

/// <summary>thing_health's scan over OcclusionManager.AllThings.</summary>
internal static class HealthScanner
{
    internal static HealthScanView Scan(HealthRequest.Scan scan)
    {
        PlayerOrigin origin = PlayerOrigin.Current().RequireIf(scan.NearPlayerM.HasValue);
        List<Thing> things = Pools.Snapshot(OcclusionManager.AllThings);
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
        Slice<HealthRow> rowPage = Slice<HealthRow>.Of(rows, scan.Page);
        return new HealthScanView(ReadPage(rowPage, scan.Page, origin), structures, broken, things.Count,
            scan.MinDamageRatio, origin.View);
    }

    private static Slice<HealthView> ReadPage(Slice<HealthRow> rows, PageRequest page, PlayerOrigin origin)
    {
        List<HealthView> views = new List<HealthView>(rows.Items.Count);
        foreach (HealthRow row in rows.Items)
        {
            views.Add(HealthReader.Read(row.Thing, origin));
        }

        return Slice<HealthView>.Page(views, page, rows.Total);
    }

    // A thing the scan lists, or null: not being destroyed, not an entity or organ, destructible, damaged above the
    // floor, a structure when asked, and near the player when asked.
    private static HealthRow? RowOf(Thing thing, HealthRequest.Scan scan, PlayerOrigin origin)
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

        double? distance = origin.ExactDistanceTo(thing.Position);
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
