#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// find_things: every thing in the world whose name matches, whatever it is: items, portable tanks and other dynamic
/// things, structures and devices, players and animals. Walks OcclusionManager.AllThings (every registered thing,
/// cursors excluded), skipping things being destroyed and organs. A name matches on the name the game shows (the
/// Labeller's name when there is one) and on the prefab's own name under a label (Labels). Nearest first, paged; only
/// the page is described in full. Read only.
/// </summary>
internal static class FindThingsApi
{
    private const int DefaultLimit = 100;
    private const int MaximumLimit = 500;

    internal static FindThingsView Handle(Args args)
    {
        ThingFilter filter = ThingFilter.Parse(args);
        PlayerOrigin origin = PlayerOrigin.Current().RequireIf(filter.NearPlayerM.HasValue);
        PageRequest page = PageRequest.From(args, DefaultLimit, MaximumLimit);
        List<Thing> things = Pools.Snapshot(OcclusionManager.AllThings);
        List<ThingHit> hits = new List<ThingHit>();
        foreach (Thing thing in things)
        {
            if (thing != null && !thing.IsCursor && !thing.IsBeingDestroyed && !(thing is Organ) && filter.Keeps(thing))
            {
                double? distance = origin.ExactDistanceTo(PositionOf(thing));
                if (filter.IsNear(distance))
                {
                    hits.Add(new ThingHit(thing, distance));
                }
            }
        }

        hits.Sort(static (a, b) => ThingHit.NearestFirst(a, b));
        Slice<ThingHit> slice = Slice<ThingHit>.Of(hits, page);
        List<FoundThingView> views = new List<FoundThingView>(slice.Items.Count);
        foreach (ThingHit hit in slice.Items)
        {
            views.Add(ViewOf(hit.Thing, origin));
        }

        return new FindThingsView(Slice<FoundThingView>.Page(views, page, hits.Count), things.Count, origin.View);
    }

    // Where a thing is for distances: a thing in a slot is where its outermost holder is, as find_items measures.
    private static Vector3 PositionOf(Thing thing) =>
        thing is DynamicThing dynamic ? HolderChain.RootOf(dynamic).Position : thing.Position;

    private static FoundThingView ViewOf(Thing thing, PlayerOrigin origin)
    {
        HolderChain? chain = thing is DynamicThing dynamic ? HolderChain.Of(dynamic) : null;
        Vector3 position = chain != null ? chain.Root.Position : thing.Position;
        return new FoundThingView(
            GameLookup.ViewOf(thing),
            Labels.CustomNameOf(thing),
            Labels.GameNameOf(thing),
            ThingKinds.Of(thing),
            thing.GetType().Name,
            Labels.CanRename(thing),
            chain != null ? chain.Location : thing is Structure ? FoundThingView.Built : FoundThingView.World,
            chain?.Carrier != null ? chain.Carrier.DisplayName : null,
            chain != null ? chain.View() : new List<HeldInView>(),
            GameLookup.ViewOf(position),
            origin.DistanceTo(position),
            thing is Device device && Devices.IsInAllDevices(device),
            AtmosphereContentsApi.HoldsAtmosphere(thing));
    }
}

/// <summary>find_things' filter: names, kind, class, labelled or not, holding an atmosphere, how near.</summary>
internal sealed class ThingFilter
{
    private const string AnyKind = "any";

    // Each class's answer to runtime_type, worked out once per call: a world has a few hundred classes.
    private readonly Dictionary<Type, bool> _typeMatches = new Dictionary<Type, bool>();

    private ThingFilter(string? nameContains, string? prefabContains, string kind, string? runtimeType,
        bool labelledOnly, bool? hasAtmosphere, double? nearPlayerM)
    {
        NameContains = nameContains;
        PrefabContains = prefabContains;
        Kind = kind;
        RuntimeType = runtimeType;
        LabelledOnly = labelledOnly;
        HasAtmosphere = hasAtmosphere;
        NearPlayerM = nearPlayerM;
    }

    internal string? NameContains { get; }

    internal string? PrefabContains { get; }

    /// <summary>any, or one of ThingKinds.</summary>
    internal string Kind { get; }

    /// <summary>A class name the thing's class is or derives from, ignoring case.</summary>
    internal string? RuntimeType { get; }

    internal bool LabelledOnly { get; }

    internal bool? HasAtmosphere { get; }

    internal double? NearPlayerM { get; }

    internal static ThingFilter Parse(Args args)
    {
        string kind = args.OptionalString("kind") ?? AnyKind;
        if (kind != AnyKind && !ThingKinds.IsKind(kind))
        {
            throw ApiErrors.InvalidArgument(
                "Argument 'kind' must be any, item, dynamic, structure, entity or other.");
        }

        string? runtimeType = args.OptionalString("runtime_type");
        return new ThingFilter(args.OptionalString("name_contains"), args.OptionalString("prefab_contains"), kind,
            string.IsNullOrEmpty(runtimeType) ? null : runtimeType, args.OptionalBool("labelled_only") ?? false,
            args.OptionalBool("has_atmosphere"), args.OptionalPositiveDouble("near_player_m"));
    }

    // Cheapest tests first: the label flag, the prefab name, the kind, the class, then the display name, which reads
    // the localisation table, and last the atmosphere, which looks at the thing's networks and slots.
    internal bool Keeps(Thing thing) =>
        (!LabelledOnly || !string.IsNullOrEmpty(thing.CustomName)) &&
        ItemFilter.Contains(thing.PrefabName, PrefabContains) &&
        (Kind == AnyKind || ThingKinds.Of(thing) == Kind) &&
        (RuntimeType == null || IsOfType(thing.GetType())) &&
        (string.IsNullOrEmpty(NameContains) || Labels.NameContains(thing, NameContains!)) &&
        (!HasAtmosphere.HasValue || AtmosphereContentsApi.HoldsAtmosphere(thing) == HasAtmosphere.Value);

    private bool IsOfType(Type type)
    {
        if (!_typeMatches.TryGetValue(type, out bool matches))
        {
            for (Type? candidate = type; candidate != null && !matches; candidate = candidate.BaseType)
            {
                matches = string.Equals(candidate.Name, RuntimeType, StringComparison.OrdinalIgnoreCase);
            }

            _typeMatches[type] = matches;
        }

        return matches;
    }

    internal bool IsNear(double? distance) =>
        !NearPlayerM.HasValue || (distance.HasValue && distance.Value <= NearPlayerM.Value);
}

/// <summary>A matching thing and its unrounded distance, for sorting before the page is described.</summary>
internal readonly struct ThingHit
{
    internal ThingHit(Thing thing, double? distance)
    {
        Thing = thing;
        Distance = distance;
    }

    internal Thing Thing { get; }

    internal double? Distance { get; }

    /// <summary>Nearest first (no player: all last), then by prefab name and reference id.</summary>
    internal static int NearestFirst(ThingHit a, ThingHit b)
    {
        int byDistance = (a.Distance ?? double.MaxValue).CompareTo(b.Distance ?? double.MaxValue);
        if (byDistance != 0)
        {
            return byDistance;
        }

        int byName = string.CompareOrdinal(a.Thing.PrefabName, b.Thing.PrefabName);
        return byName != 0 ? byName : a.Thing.ReferenceId.CompareTo(b.Thing.ReferenceId);
    }
}
