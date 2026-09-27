#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// consumables: every food and drink in the world, wherever it is. Read only.
///
/// Food is any item with INutrition: nutrition = INutrition.Nutrition(quantity), what Human.OnConsumeFood adds when
/// the whole stack is eaten. Drinks are IHydration items (water bottles and packets): Quantity is litres and
/// IHydration.Hydration(litres) the hydration they give (HydrationBase: 5 per litre). Boxes and packages
/// (CardboardBox: cereal bar boxes, emergency supplies, water bottle packages) hold real items in their slots, which
/// unpacking only moves out, so their contents are counted like any other item and each says which package it is in.
/// What rotten food turns into (Item.CreateDecayFood makes a DecayedFood) is not INutrition and cannot be eaten.
/// </summary>
internal static class ConsumablesApi
{
    internal static ConsumablesView Handle(Args args)
    {
        PlayerOrigin origin = PlayerOrigin.Current();
        List<Item> items = WorldItems.All();
        List<ItemRecord> records = new List<ItemRecord>(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            records.Add(WorldItems.Describe(items[index], origin));
        }

        records.Sort(static (a, b) => ItemRecord.NearestFirst(a, b));
        ConsumableTally tally = new ConsumableTally(origin);
        for (int index = 0; index < records.Count; index++)
        {
            tally.Add(records[index]);
        }

        return tally.ToView();
    }
}

/// <summary>Sorts items into food, drinks and not counted, and sums them overall and per package.</summary>
internal sealed class ConsumableTally
{
    private readonly PlayerOrigin _origin;
    private readonly List<FoodView> _food = new List<FoodView>();
    private readonly List<DrinkView> _drinks = new List<DrinkView>();
    private readonly List<PackageTally> _packages = new List<PackageTally>();
    private readonly Dictionary<long, PackageTally> _packagesById = new Dictionary<long, PackageTally>();
    private readonly Dictionary<string, PrefabCount> _excluded =
        new Dictionary<string, PrefabCount>(StringComparer.Ordinal);

    private readonly AmountSum _all = new AmountSum();
    private readonly AmountSum _packaged = new AmountSum();

    internal ConsumableTally(PlayerOrigin origin)
    {
        _origin = origin;
    }

    internal void Add(ItemRecord record)
    {
        Item item = record.Item;
        CardboardBox? package = PackageOf(record);
        if (item is INutrition nutrition)
        {
            AddFood(record, nutrition, package);
        }
        else if (item is global::Objects.Items.DecayedFood)
        {
            Exclude(record, "decayed");
        }
        else if (item is IHydration hydration)
        {
            AddDrink(record, hydration, package);
        }
    }

    private void AddFood(ItemRecord record, INutrition nutrition, CardboardBox? package)
    {
        Item item = record.Item;
        double total = nutrition.Nutrition((float)record.Quantity);
        string? reason = item.IsDecayed ? "decayed"
            : item is Seed ? "seed"
            : item is Plant plant && plant.IsPlanted ? "planted"
            : total <= 0 ? "no_nutrition"
            : null;
        if (reason != null)
        {
            Exclude(record, reason);
            return;
        }

        FoodValue value = new FoodValue(nutrition.Nutrition(1f), total, nutrition.GetFoodQuality().ToString(),
            DecayTimeLeft(item));
        _food.Add(new FoodView(record.Fields(), value, RefOf(package)));
        Count(record, package, new ConsumableAmounts(total, 0, 0));
    }

    private void AddDrink(ItemRecord record, IHydration hydration, CardboardBox? package)
    {
        double litres = record.Quantity;
        if (litres <= 0)
        {
            Exclude(record, "empty");
            return;
        }

        double value = hydration.Hydration((float)litres);
        _drinks.Add(new DrinkView(record.Fields(), litres, value, RefOf(package)));
        Count(record, package, new ConsumableAmounts(0, litres, value));
    }

    private void Count(ItemRecord record, CardboardBox? package, ConsumableAmounts amounts)
    {
        _all.Add(amounts);
        if (package == null)
        {
            return;
        }

        _packaged.Add(amounts);
        if (!_packagesById.TryGetValue(package.ReferenceId, out PackageTally tally))
        {
            tally = new PackageTally(package);
            _packagesById[package.ReferenceId] = tally;
            _packages.Add(tally);
        }

        tally.Add(record, amounts);
    }

    private void Exclude(ItemRecord record, string reason)
    {
        string prefab = record.Item.PrefabName ?? string.Empty;
        string key = reason + "|" + prefab;
        if (!_excluded.TryGetValue(key, out PrefabCount count))
        {
            count = new PrefabCount(prefab, record.Item.DisplayName, reason);
            _excluded[key] = count;
        }

        count.Add(record.Quantity);
    }

    private static CardboardBox? PackageOf(ItemRecord record)
    {
        foreach (Slot slot in record.Path)
        {
            if (slot.Parent is CardboardBox box)
            {
                return box;
            }
        }

        return null;
    }

    private static PackageRef RefOf(CardboardBox? package) =>
        package != null ? new PackageRef(new ThingId(package.ReferenceId), package.DisplayName) : PackageRef.None;

    private static int? DecayTimeLeft(Item item)
    {
        try
        {
            return item.CanItemDecay() && item.CurrentDecayRate > 0f ? item.TimeToDecayFromNowInSeconds : null;
        }
        catch (Exception)
        {
            // Item.CanItemDecay reads the item's DamageState, which a half-built or dying item may not have.
            return null;
        }
    }

    internal ConsumablesView ToView()
    {
        List<PackageView> packages = new List<PackageView>(_packages.Count);
        foreach (PackageTally tally in _packages)
        {
            packages.Add(tally.ToView(_origin));
        }

        List<PrefabCount> excluded = new List<PrefabCount>(_excluded.Values);
        excluded.Sort(static (a, b) => PrefabCount.ByReasonThenPrefab(a, b));
        List<NotCountedView> notCounted = new List<NotCountedView>(excluded.Count);
        foreach (PrefabCount count in excluded)
        {
            notCounted.Add(new NotCountedView(count.Prefab, count.Display, count.Reason ?? string.Empty, count.Items,
                count.Quantity));
        }

        ConsumableTotalsView totals = new ConsumableTotalsView(_all.ToAmounts(), _packaged.ToAmounts(), _food.Count,
            _drinks.Count);
        return new ConsumablesView(_food, _drinks, packages, notCounted, totals, _origin.View);
    }
}

/// <summary>What one package holds, per prefab, and its sums.</summary>
internal sealed class PackageTally
{
    private readonly CardboardBox _box;
    private readonly List<PrefabCount> _contents = new List<PrefabCount>();
    private readonly Dictionary<string, PrefabCount> _byPrefab =
        new Dictionary<string, PrefabCount>(StringComparer.Ordinal);

    private readonly AmountSum _sum = new AmountSum();

    internal PackageTally(CardboardBox box)
    {
        _box = box;
    }

    internal void Add(ItemRecord record, ConsumableAmounts amounts)
    {
        string prefab = record.Item.PrefabName ?? string.Empty;
        if (!_byPrefab.TryGetValue(prefab, out PrefabCount count))
        {
            count = new PrefabCount(prefab, record.Item.DisplayName, null);
            _byPrefab[prefab] = count;
            _contents.Add(count);
        }

        count.Add(record.Quantity);
        _sum.Add(amounts);
    }

    internal PackageView ToView(PlayerOrigin origin)
    {
        List<PackedView> contents = new List<PackedView>(_contents.Count);
        foreach (PrefabCount count in _contents)
        {
            contents.Add(new PackedView(count.Prefab, count.Display, count.Items, count.Quantity));
        }

        ItemPlace place = WorldItems.Describe(_box, origin).Place();
        return new PackageView(GameLookup.ViewOf(_box), place, _box.GetType().Name, contents, _sum.ToAmounts());
    }
}

/// <summary>Items of one prefab, counted, with their quantities summed.</summary>
internal sealed class PrefabCount
{
    internal PrefabCount(string prefab, string? display, string? reason)
    {
        Prefab = prefab;
        Display = display;
        Reason = reason;
    }

    internal string Prefab { get; }

    internal string? Display { get; }

    internal string? Reason { get; }

    internal int Items { get; private set; }

    internal double Quantity { get; private set; }

    internal void Add(double quantity)
    {
        Items++;
        Quantity += quantity;
    }

    internal static int ByReasonThenPrefab(PrefabCount a, PrefabCount b)
    {
        int byReason = string.CompareOrdinal(a.Reason, b.Reason);
        return byReason != 0 ? byReason : string.CompareOrdinal(a.Prefab, b.Prefab);
    }
}

/// <summary>A running sum of nutrition, litres of drink and hydration.</summary>
internal sealed class AmountSum
{
    private double _nutrition;
    private double _liquidL;
    private double _hydration;

    internal void Add(ConsumableAmounts amounts)
    {
        _nutrition += amounts.Nutrition;
        _liquidL += amounts.LiquidL;
        _hydration += amounts.Hydration;
    }

    internal ConsumableAmounts ToAmounts() => new ConsumableAmounts(_nutrition, _liquidL, _hydration);
}
