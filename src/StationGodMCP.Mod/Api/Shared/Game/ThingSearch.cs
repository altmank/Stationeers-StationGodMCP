#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Verbs;
using UnityEngine;
using StationGodMCP.WorldModel;
using World = StationGodMCP.WorldModel.World;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Every thing in the world a filter keeps, whatever it is: items, portable tanks and other dynamic things, structures
/// and devices, players and animals. The candidates are the world model's registered things (World.Things: cursors
/// are never registered), or with whole prefab names (or one part) only the things the world index files under them.
/// Things being destroyed and organs are never kept. A name matches on the name the game shows (the Labeller's name
/// when there is one) and on the prefab's own name under a label (Labels). Distances are measured from where the
/// player stands, a thing in a slot where its outermost holder is. Every thing reports is_broken and condition from the
/// game's own broken state (Pure/HealthCondition: a broken structure reads 100 % health, so the numbers cannot tell).
/// </summary>
internal static class ThingSearch
{
    /// <summary>
    /// The things a search looks at: those the world index files under the filter's prefabs (checked), else the Thing
    /// class's candidates unchecked (World.Candidates), which the walk's filter checks one per step (WalkFilter).
    /// </summary>
    internal static List<Thing> Candidates(ThingFilter filter) =>
        filter.IndexMatch.IsActive ? World.Things(filter.IndexMatch) : World.Candidates(WorldClass.Thing);

    /// <summary>
    /// The filter as a walk's build asks it over Candidates, distances from where the player stood when the walk
    /// started; unchecked candidates are checked for membership as their turn comes.
    /// </summary>
    internal static IWalkFilter<Thing> WalkFilter(ThingFilter filter, Vector3? from) =>
        new Walk(filter, from, !filter.IndexMatch.IsActive);

    /// <summary>The thing an id still names, or null when it is gone or being destroyed.</summary>
    internal static Thing? Live(long id)
    {
        Thing thing = Thing.Find(id);
        return thing != null && !thing.IsBeingDestroyed ? thing : null;
    }

    internal static FoundThingView ViewOf(Thing thing, PlayerOrigin origin, List<ColorSwatch> swatches)
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
            LocationOf(thing, chain),
            chain?.Carrier != null ? chain.Carrier.DisplayName : null,
            chain != null ? chain.View() : new List<HeldInView>(),
            GameLookup.ViewOf(position),
            origin.DistanceTo(position),
            thing is Device device && Devices.IsInAllDevices(device),
            AtmosphereOwners.HoldsAtmosphere(thing),
            thing is Structure ? Orientations.Of(thing) : null,
            Wrecks.IsBroken(thing),
            ConditionOf(thing),
            Prints.Log.Of(thing.ReferenceId) is PrintRecord record ? new PrintView(record) : null,
            RocketOf(thing),
            ThingColors.ShownColorOf(thing, swatches),
            BuildStates.Unfinished(thing));
    }

    /// <summary>Where a thing is, as a found thing's location: its holder chain's for a dynamic thing, else built or world.</summary>
    internal static string LocationOf(Thing thing, HolderChain? chain) =>
        chain != null ? chain.Location : thing is Structure ? FoundThingView.Built : FoundThingView.World;

    // Whether a thing is one the filter keeps, and its unrounded distance from where the player stood (null without
    // one). Things being destroyed (or gone), cursors and organs never are.
    private static bool Matches(Thing thing, ThingFilter filter, Vector3? from, out double? distance)
    {
        distance = null;
        if (thing == null || thing.IsCursor || thing.IsBeingDestroyed || thing is Organ || !filter.Keeps(thing))
        {
            return false;
        }

        Vector3 position = HolderChain.PlaceOf(thing).Position;
        distance = from.HasValue ? Vector3.Distance(from.Value, position) : (double?)null;
        return filter.IsNear(distance) && filter.IsInside(position);
    }

    private static string? RocketOf(Thing thing) =>
        thing is Structure structure && Rockets.NetworkOf(structure)?.Rocket is { } rocket
            ? rocket.RocketState.ToString()
            : null;

    private static string ConditionOf(Thing thing)
    {
        IndestructableDamageState damage = thing.DamageState;
        bool measurable = damage != null && !damage.Indestructable && damage.MaxDamage > 0f;
        return HealthCondition.Of(Wrecks.IsBroken(thing), damage != null, damage != null && damage.Indestructable,
            measurable ? damage!.TotalRatio : (double?)null);
    }

    private sealed class Walk : IWalkFilter<Thing>
    {
        private readonly ThingFilter _filter;
        private readonly Vector3? _from;
        private readonly bool _checkMembership;

        internal Walk(ThingFilter filter, Vector3? from, bool checkMembership)
        {
            _filter = filter;
            _from = from;
            _checkMembership = checkMembership;
        }

        public bool TryKey(Thing thing, out ListKey key)
        {
            if ((!_checkMembership || World.Holds(WorldClass.Thing, thing)) &&
                Matches(thing, _filter, _from, out double? distance))
            {
                key = new ThingHit(thing, distance).Key;
                return true;
            }

            key = default;
            return false;
        }
    }
}

/// <summary>
/// What a thing search keeps: part of a name, prefabs, a category (ThingKinds), a game class, labelled or not, broken or
/// not, its maker in the print log, the colour it shows or painted off its own, where it is (ThingLocations), holding
/// an atmosphere, how near the player, and an area its outermost holder stands in. Left out, each keeps every thing.
/// </summary>
internal sealed class ThingFilter
{
    // Each class's answer to the runtime type, worked out once per call: a world has a few hundred classes.
    private readonly Dictionary<Type, bool> _typeMatches = new Dictionary<Type, bool>();

    internal ThingFilter(string? name, PrefabFilter? prefab, string category, string? runtimeType, bool labelled,
        bool? hasAtmosphere, double? nearPlayerM, bool? broken, PrintFilter made, string location, PointArea area,
        int? colorIndex = null, bool? painted = null)
    {
        ColorIndex = colorIndex;
        Painted = painted;
        Name = name;
        Prefab = prefab;
        Category = category;
        RuntimeType = string.IsNullOrEmpty(runtimeType) ? null : runtimeType;
        Labelled = labelled;
        HasAtmosphere = hasAtmosphere;
        NearPlayerM = nearPlayerM;
        Broken = broken;
        Made = made;
        Location = location;
        Area = area;
    }

    internal string? Name { get; }

    internal PrefabFilter? Prefab { get; }

    /// <summary>any, or one of ThingKinds.</summary>
    internal string Category { get; }

    /// <summary>A class name the thing's class is or derives from, ignoring case.</summary>
    internal string? RuntimeType { get; }

    internal bool Labelled { get; }

    internal bool? HasAtmosphere { get; }

    /// <summary>Metres around the player: only things whose outermost holder stands that near.</summary>
    internal double? NearPlayerM { get; }

    /// <summary>true: only things in the game's broken state (Wrecks); false: only things not broken.</summary>
    internal bool? Broken { get; }

    /// <summary>Only items the print log has, from that maker, since that game time.</summary>
    internal PrintFilter Made { get; }

    /// <summary>Only things showing this colour (its index in the game's list; ThingColors.ShownIndexOf).</summary>
    internal int? ColorIndex { get; }

    /// <summary>true: only paintable things painted off their prefab's colour; false: only those still in it.</summary>
    internal bool? Painted { get; }

    /// <summary>any, or the one location kept (ThingLocations).</summary>
    internal string Location { get; }

    /// <summary>A box, or a sphere around a point: only things whose outermost holder stands there.</summary>
    internal PointArea Area { get; }

    /// <summary>The prefab index's match for the filter's prefabs (PrefabMatch.ForIndex).</summary>
    internal PrefabMatch IndexMatch => PrefabMatch.ForIndex(Prefab);

    // Cheapest tests first: the label flag, the prefab name, the category, the class, then the display name, which reads
    // the localisation table, and last the atmosphere, which looks at the thing's networks and slots.
    internal bool Keeps(Thing thing) =>
        (!Labelled || !string.IsNullOrEmpty(thing.CustomName)) &&
        (Prefab == null || Prefab.Keeps(thing.PrefabName, thing.PrefabHash)) &&
        (Category == ThingLocations.Any || ThingKinds.Of(thing) == Category) &&
        (!Broken.HasValue || Wrecks.IsBroken(thing) == Broken.Value) &&
        (!ColorIndex.HasValue || ThingColors.ShownIndexOf(thing) == ColorIndex.Value) &&
        (!Painted.HasValue || ThingColors.IsRepainted(thing) == Painted.Value) &&
        (!Made.IsActive || Made.Keeps(Prints.Log.Of(thing.ReferenceId))) &&
        (RuntimeType == null || IsOfType(thing.GetType())) &&
        (string.IsNullOrEmpty(Name) || Labels.NameContains(thing, Name!)) &&
        (Location == ThingLocations.Any || IsAt(thing)) &&
        (!HasAtmosphere.HasValue || AtmosphereOwners.HoldsAtmosphere(thing) == HasAtmosphere.Value);

    internal bool IsNear(double? distance) =>
        !NearPlayerM.HasValue || (distance.HasValue && distance.Value <= NearPlayerM.Value);

    internal bool IsInside(Vector3 position) => Area.Contains(new Vec3(position.x, position.y, position.z));

    // A dynamic thing's holder chain is walked only when the location filter asks for one.
    private bool IsAt(Thing thing) =>
        ThingSearch.LocationOf(thing, thing is DynamicThing dynamic ? HolderChain.Of(dynamic) : null) == Location;

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

    /// <summary>Sorted by distance, then prefab name and reference id; or by reference id.</summary>
    internal ListKey Key => new ListKey(Distance, Thing.PrefabName, 0, Thing.ReferenceId);
}
