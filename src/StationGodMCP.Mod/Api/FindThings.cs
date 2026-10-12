#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Build;
using StationGodMCP.Api.Shared.Game.Runs;
using StationGodMCP.Api.Views;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Paging;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Protocol;
using StationGodMCP.Pure.Scheduling;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// find_things: every thing in the world whose name matches, whatever it is: items, portable tanks and other dynamic
/// things, structures and devices, players and animals. Walks OcclusionManager.AllThings (every registered thing,
/// cursors excluded), or with prefab, prefabs or prefab_contains only the things the prefab index (ThingIndex) files
/// under matching names; scanned counts the things walked. Things being destroyed and organs are skipped. A name matches on the name the game shows (the
/// Labeller's name when there is one) and on the prefab's own name under a label (Labels). Nearest first or by
/// reference id (order), paged; only the page is described in full. Every thing reports is_broken and condition from the game's own broken state
/// (Pure/HealthCondition: a broken structure reads 100 % health, so the numbers cannot tell), and broken filters on it.
/// With cursor the sorted list is walked (WalkRequest): a later page with cursor_id takes the next kept ids instead of
/// scanning, skips things that no longer exist or are being destroyed and does not check the filter again (a thing
/// that stopped matching is still walked, with its current values), and measures distances from where the player is
/// now, as a first page does. A walk's page (the first one's included) stops when its main-thread time, with the
/// serialising its entries will cost (EntryCost), reaches the frame's budget (PageBudget), whatever limit asked: it
/// always holds one thing, and the reply's next and the note's reason frame_budget say where the walk goes on. A walk's
/// first call builds its ordered ids in slices (WalkBuild): the candidates are taken as it starts, distances measured
/// from where the player stood then; what does not fit the frame goes on over later frames with the call deferred
/// (IPendingResult), and the call answers with its first page once the ids are in order. Read only.
/// </summary>
internal static class FindThingsApi
{
    private const int DefaultLimit = ReplyDefaults.FindThings;
    private const int MaximumLimit = 500;

    [Profiled]
    internal static object Handle(Args args)
    {
        WalkRequest walk = WalkRequest.From(args);
        PageRequest page = PageRequest.From(args, DefaultLimit, MaximumLimit);
        if (walk.Continues)
        {
            return NextPage(walk, page);
        }

        ThingFilter filter = ThingFilter.Parse(args);
        PlayerOrigin origin = PlayerOrigin.Current().RequireIf(filter.NearPlayerM.HasValue);
        ListOrder order = ListOrderArg.From(args);
        List<Thing> things = ThingIndex.Things(filter.Prefab) ?? Pools.Snapshot(OcclusionManager.AllThings);
        Vector3? from = StandingAt(origin);
        if (walk.Opens)
        {
            WalkFirstPage first = new WalkFirstPage(walk, page, order, origin.IsPresent,
                new WalkBuild<Thing>(things, new WalkFilter(filter, from), order));
            return first.Step(CurrentFrame.EndMs) ? first.Result() : first;
        }

        List<ThingHit> hits = new List<ThingHit>();
        foreach (Thing thing in things)
        {
            if (Matches(thing, filter, from, out double? distance))
            {
                hits.Add(new ThingHit(thing, distance));
            }
        }

        hits.Sort((a, b) => ListKey.Compare(order, a.Key, b.Key));
        Slice<ThingHit> slice = Slice<ThingHit>.Of(hits, page);
        List<FoundThingView> views = new List<FoundThingView>(slice.Items.Count);
        List<ColorSwatch> swatches = PaintApi.Swatches();
        foreach (ThingHit hit in slice.Items)
        {
            views.Add(ViewOf(hit.Thing, origin, swatches));
        }

        walk.Note("things", page, views.Count, hits.Count, null, ListOrders.PagingAdvice(order, origin.IsPresent));
        return new FindThingsView(Slice<FoundThingView>.Page(views, page, hits.Count), things.Count, origin.View);
    }

    // Where the player stands now, for distances: null without a player.
    private static Vector3? StandingAt(PlayerOrigin origin)
    {
        Human? player = origin.Player;
        return player != null ? player.Position : (Vector3?)null;
    }

    // Whether a thing is one the filter keeps, and its unrounded distance from where the player stood (null without
    // one). Things being destroyed (or gone), cursors and organs never are.
    private static bool Matches(Thing thing, ThingFilter filter, Vector3? from, out double? distance)
    {
        distance = null;
        if (thing == null || thing.IsCursor || thing.IsBeingDestroyed || thing is Organ || !filter.Keeps(thing))
        {
            return false;
        }

        Vector3 position = PositionOf(thing);
        distance = from.HasValue ? Vector3.Distance(from.Value, position) : (double?)null;
        return filter.IsNear(distance) && filter.IsInside(position);
    }

    // A walk's page stops at the frame's budget, leaving room for serialising what it holds.
    private static PageBudget FrameBudget() =>
        PageBudget.OfFrame(StopwatchClock.Instance, EntryCost.FindThings.MsPerEntry);

    // A walk's later page: the next kept ids that still name a thing, described as a first page describes them, each
    // as it is taken, so the page's budget counts the describing.
    private static FindThingsView NextPage(WalkRequest walk, PageRequest page)
    {
        PlayerOrigin origin = PlayerOrigin.Current();
        List<ColorSwatch> swatches = PaintApi.Swatches();
        List<FoundThingView> views = new List<FoundThingView>(page.Limit);
        WalkStep step = walk.Next(page.Limit, id => Live(id) is Thing thing ? ViewOf(thing, origin, swatches) : null,
            views, FrameBudget());
        walk.Note("things", page, views.Count, step.Total, step.CursorId, cut: step.Cut);
        EntryCost.FindThings.Expect(views.Count);
        return new FindThingsView(Slice<FoundThingView>.Walked(views, step.Offset, page.Limit, step.Total, step.HasMore),
            0, origin.View, step.CursorId, step.Skipped, step.Next);
    }

    // A walk's first call: its ordered ids built in slices, then its first page served from them as a later page is.
    // The page needs room of its own, so a build that ends with the frame's budget spent serves it in the next slice.
    private sealed class WalkFirstPage : IPendingResult
    {
        private readonly WalkRequest _walk;
        private readonly PageRequest _page;
        private readonly ListOrder _order;
        private readonly bool _measured;
        private readonly WalkBuild<Thing> _build;

        internal WalkFirstPage(WalkRequest walk, PageRequest page, ListOrder order, bool measured,
            WalkBuild<Thing> build)
        {
            _walk = walk;
            _page = page;
            _order = order;
            _measured = measured;
            _build = build;
        }

        public bool Step(double untilMs)
        {
            if (_build.Done)
            {
                return true;
            }

            return _build.Run(StopwatchClock.Instance, untilMs) && StopwatchClock.Instance.NowMs < untilMs;
        }

        public object Result()
        {
            PlayerOrigin origin = PlayerOrigin.Current();
            List<ColorSwatch> swatches = PaintApi.Swatches();
            List<FoundThingView> views = new List<FoundThingView>(Math.Min(_page.Limit, _build.Count));
            WalkStep step = _walk.First(_build.Ids(), _page.Limit,
                id => Live(id) is Thing thing ? ViewOf(thing, origin, swatches) : null, views, FrameBudget());
            _walk.Note("things", _page, views.Count, step.CursorId != null ? step.Total : views.Count, step.CursorId,
                ListOrders.PagingAdvice(_order, _measured), step.Cut);
            EntryCost.FindThings.Expect(views.Count);
            return new FindThingsView(Slice<FoundThingView>.Walked(views, 0, _page.Limit, step.Total, step.HasMore),
                _build.Candidates, origin.View, step.CursorId, step.Skipped > 0 ? step.Skipped : (int?)null,
                step.Next);
        }
    }

    // find_things' filter as a walk's build asks it, distances from where the player stood when the walk started.
    private sealed class WalkFilter : IWalkFilter<Thing>
    {
        private readonly ThingFilter _filter;
        private readonly Vector3? _from;

        internal WalkFilter(ThingFilter filter, Vector3? from)
        {
            _filter = filter;
            _from = from;
        }

        public bool TryKey(Thing thing, out ListKey key)
        {
            if (Matches(thing, _filter, _from, out double? distance))
            {
                key = new ThingHit(thing, distance).Key;
                return true;
            }

            key = default;
            return false;
        }
    }

    // The thing a walk's id still names, or null when it is gone or being destroyed.
    private static Thing? Live(long id)
    {
        Thing thing = Thing.Find(id);
        return thing != null && !thing.IsBeingDestroyed ? thing : null;
    }

    // Where a thing is for distances: a thing in a slot is where its outermost holder is, as find_items measures.
    private static Vector3 PositionOf(Thing thing) => HolderChain.PlaceOf(thing).Position;

    private static FoundThingView ViewOf(Thing thing, PlayerOrigin origin, List<ColorSwatch> swatches)
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
            AtmosphereContentsApi.HoldsAtmosphere(thing),
            thing is Structure ? Orientations.Of(thing) : null,
            Wrecks.IsBroken(thing),
            ConditionOf(thing),
            Prints.Log.Of(thing.ReferenceId) is PrintRecord record ? new PrintView(record) : null,
            RocketOf(thing),
            PaintApi.ShownColorOf(thing, swatches),
            BuildStates.Unfinished(thing));
    }

    private static string? RocketOf(Thing thing) =>
        thing is Structure structure && Rockets.NetworkOf(structure)?.Rocket is { } rocket
            ? rocket.RocketState.ToString()
            : null;

    /// <summary>Where a thing is, as the reply's location: its holder chain's for a dynamic thing, else built or world.</summary>
    internal static string LocationOf(Thing thing, HolderChain? chain) =>
        chain != null ? chain.Location : thing is Structure ? FoundThingView.Built : FoundThingView.World;

    private static string ConditionOf(Thing thing)
    {
        IndestructableDamageState damage = thing.DamageState;
        bool measurable = damage != null && !damage.Indestructable && damage.MaxDamage > 0f;
        return HealthCondition.Of(Wrecks.IsBroken(thing), damage != null, damage != null && damage.Indestructable,
            measurable ? damage!.TotalRatio : (double?)null);
    }
}

/// <summary>
/// find_things' filter: names, kind, class, labelled or not, broken or not, where it is, holding an atmosphere, how near.
/// </summary>
internal sealed class ThingFilter
{
    private const string AnyKind = "any";

    // Each class's answer to runtime_type, worked out once per call: a world has a few hundred classes.
    private readonly Dictionary<Type, bool> _typeMatches = new Dictionary<Type, bool>();

    private ThingFilter(string? nameContains, PrefabMatch prefab, string kind, string? runtimeType,
        bool labelledOnly, bool? hasAtmosphere, double? nearPlayerM, bool? broken, PrintFilter made, string location,
        PointArea area)
    {
        Area = area;
        Location = location;
        Made = made;
        Broken = broken;
        NameContains = nameContains;
        Prefab = prefab;
        Kind = kind;
        RuntimeType = runtimeType;
        LabelledOnly = labelledOnly;
        HasAtmosphere = hasAtmosphere;
        NearPlayerM = nearPlayerM;
    }

    internal string? NameContains { get; }

    /// <summary>prefab (the exact prefab name) and prefab_contains.</summary>
    internal PrefabMatch Prefab { get; }

    /// <summary>any, or one of ThingKinds.</summary>
    internal string Kind { get; }

    /// <summary>A class name the thing's class is or derives from, ignoring case.</summary>
    internal string? RuntimeType { get; }

    internal bool LabelledOnly { get; }

    internal bool? HasAtmosphere { get; }

    internal double? NearPlayerM { get; }

    /// <summary>
    /// min and max (a box, edges included) or near with radius_m: only things whose outermost holder stands there.
    /// </summary>
    internal PointArea Area { get; }

    /// <summary>any, or the one location kept (ThingLocations).</summary>
    internal string Location { get; }

    /// <summary>
    /// true: only things in the game's broken state (Wrecks: Thing.IsBroken, a burst pipe or a burnt cable); false: only things not
    /// broken.
    /// </summary>
    internal bool? Broken { get; }

    /// <summary>made_by and made_since: only items the print log has, from that maker, since that game time.</summary>
    internal PrintFilter Made { get; }

    internal static ThingFilter Parse(Args args)
    {
        string kind = args.OptionalString("kind") ?? AnyKind;
        if (kind != AnyKind && !ThingKinds.IsKind(kind))
        {
            throw ApiErrors.InvalidArgument(
                Vocabulary.Text("Argument '{arg:kind}' must be any, item, dynamic, structure, entity or other."));
        }

        string? runtimeType = args.OptionalString("runtime_type");
        return new ThingFilter(args.OptionalString("name_contains"), PrefabMatches.Parse(args), kind,
            string.IsNullOrEmpty(runtimeType) ? null : runtimeType, args.OptionalBool("labelled_only") ?? false,
            args.OptionalBool("has_atmosphere"), args.OptionalPositiveDouble("near_player_m"),
            args.OptionalBool("broken"), MadeOf(args), ThingLocations.Parse(args.OptionalString("location")),
            AreaArgs.Parse(args));
    }

    // made_by: a maker's reference id, or text in its prefab or shown name; made_since: a game time (game_clock's
    // game_time_s), or a negative number of seconds before now.
    private static PrintFilter MadeOf(Args args)
    {
        JToken? by = args.Optional("made_by");
        long? makerId = null;
        string? makerText = null;
        if (by != null)
        {
            if (ThingId.TryRead(by, out ThingId id))
            {
                makerId = id.Value;
            }
            else
            {
                makerText = args.OptionalString("made_by");
            }
        }

        double? since = args.OptionalDouble("made_since");
        if (since.HasValue && since.Value < 0)
        {
            since = GameManager.GameTime + since.Value;
        }

        return new PrintFilter(makerId, makerText, since);
    }

    // Cheapest tests first: the label flag, the prefab name, the kind, the class, then the display name, which reads
    // the localisation table, and last the atmosphere, which looks at the thing's networks and slots.
    internal bool Keeps(Thing thing) =>
        (!LabelledOnly || !string.IsNullOrEmpty(thing.CustomName)) &&
        Prefab.Keeps(thing.PrefabName) &&
        (Kind == AnyKind || ThingKinds.Of(thing) == Kind) &&
        (!Broken.HasValue || Wrecks.IsBroken(thing) == Broken.Value) &&
        (!Made.IsActive || Made.Keeps(Prints.Log.Of(thing.ReferenceId))) &&
        (RuntimeType == null || IsOfType(thing.GetType())) &&
        (string.IsNullOrEmpty(NameContains) || Labels.NameContains(thing, NameContains!)) &&
        (Location == ThingLocations.Any || IsAt(thing)) &&
        (!HasAtmosphere.HasValue || AtmosphereContentsApi.HoldsAtmosphere(thing) == HasAtmosphere.Value);

    // A dynamic thing's holder chain is walked only when the location filter asks for one.
    private bool IsAt(Thing thing) =>
        FindThingsApi.LocationOf(thing, thing is DynamicThing dynamic ? HolderChain.Of(dynamic) : null) == Location;

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

    internal bool IsInside(Vector3 position) => Area.Contains(new Vec3(position.x, position.y, position.z));
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
