#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>consumables: every food and drink in the world, the packages they are in, and totals.</summary>
internal sealed class ConsumablesView
{
    internal ConsumablesView(List<FoodView> food, List<DrinkView> drinks, List<PackageView> packages,
        List<NotCountedView> notCounted, ConsumableTotalsView totals, LocalPlayerView? localPlayer)
    {
        Food = food;
        Drinks = drinks;
        Packages = packages;
        NotCounted = notCounted;
        Totals = totals;
        LocalPlayer = localPlayer;
    }

    public List<FoodView> Food { get; }

    public List<DrinkView> Drinks { get; }

    public List<PackageView> Packages { get; }

    public List<NotCountedView> NotCounted { get; }

    public ConsumableTotalsView Totals { get; }

    public LocalPlayerView? LocalPlayer { get; }
}

/// <summary>The package (a CardboardBox) an item is packed in, if any.</summary>
internal sealed class PackageRef
{
    internal static readonly PackageRef None = new PackageRef(null, null);

    internal PackageRef(ThingId? id, string? name)
    {
        Id = id;
        Name = name;
    }

    internal ThingId? Id { get; }

    internal string? Name { get; }
}

internal sealed class FoodView : ItemFieldsView
{
    internal FoodView(ItemFields fields, FoodValue value, PackageRef package) : base(fields)
    {
        NutritionEach = value.NutritionEach;
        Nutrition = value.Nutrition;
        FoodQuality = value.FoodQuality;
        DecayTimeLeftS = value.DecayTimeLeftS;
        InPackage = package.Id;
        PackageName = package.Name;
    }

    public double NutritionEach { get; }

    /// <summary>INutrition.Nutrition of the whole stack: what eating all of it adds.</summary>
    public double Nutrition { get; }

    public string FoodQuality { get; }

    /// <summary>Item.TimeToDecayFromNowInSeconds; null when the item does not decay.</summary>
    public int? DecayTimeLeftS { get; }

    public ThingId? InPackage { get; }

    public string? PackageName { get; }
}

/// <summary>What a food item is worth.</summary>
internal sealed class FoodValue
{
    internal FoodValue(double nutritionEach, double nutrition, string foodQuality, int? decayTimeLeftS)
    {
        NutritionEach = nutritionEach;
        Nutrition = nutrition;
        FoodQuality = foodQuality;
        DecayTimeLeftS = decayTimeLeftS;
    }

    internal double NutritionEach { get; }

    internal double Nutrition { get; }

    internal string FoodQuality { get; }

    internal int? DecayTimeLeftS { get; }
}

internal sealed class DrinkView : ItemFieldsView
{
    internal DrinkView(ItemFields fields, double liquidL, double hydration, PackageRef package) : base(fields)
    {
        LiquidL = liquidL;
        Hydration = hydration;
        InPackage = package.Id;
        PackageName = package.Name;
    }

    public double LiquidL { get; }

    /// <summary>IHydration.Hydration of the whole drink.</summary>
    public double Hydration { get; }

    public ThingId? InPackage { get; }

    public string? PackageName { get; }
}

/// <summary>A box or package and what is packed in it; its own quantity is left out.</summary>
internal sealed class PackageView
{
    internal PackageView(ThingView box, ItemPlace place, string runtimeType, List<PackedView> contents,
        ConsumableAmounts amounts)
    {
        ReferenceId = box.ReferenceId;
        PrefabName = box.PrefabName;
        DisplayName = box.DisplayName;
        Location = place.Location;
        CarriedBy = place.CarriedBy;
        HeldIn = place.HeldIn;
        Position = place.Position;
        DistanceM = place.DistanceM;
        RuntimeType = runtimeType;
        Contents = contents;
        Nutrition = amounts.Nutrition;
        LiquidL = amounts.LiquidL;
        Hydration = amounts.Hydration;
    }

    public ThingId ReferenceId { get; }

    public string? PrefabName { get; }

    public string? DisplayName { get; }

    public string Location { get; }

    public string? CarriedBy { get; }

    public List<HeldInView> HeldIn { get; }

    public PositionView Position { get; }

    public double? DistanceM { get; }

    public string RuntimeType { get; }

    public List<PackedView> Contents { get; }

    public double Nutrition { get; }

    public double LiquidL { get; }

    public double Hydration { get; }
}

/// <summary>Nutrition, litres of drink and hydration, summed.</summary>
internal sealed class ConsumableAmounts
{
    internal ConsumableAmounts(double nutrition, double liquidL, double hydration)
    {
        Nutrition = nutrition;
        LiquidL = liquidL;
        Hydration = hydration;
    }

    internal double Nutrition { get; }

    internal double LiquidL { get; }

    internal double Hydration { get; }
}

/// <summary>Items of one prefab packed in a package.</summary>
internal sealed class PackedView
{
    internal PackedView(string prefabName, string? displayName, int items, double quantity)
    {
        PrefabName = prefabName;
        DisplayName = ThingName.Displayed(displayName, prefabName);
        Items = items;
        Quantity = quantity;
    }

    public string PrefabName { get; }

    public string? DisplayName { get; }

    public int Items { get; }

    public double Quantity { get; }
}

/// <summary>Items of one prefab left out of the totals, and why.</summary>
internal sealed class NotCountedView
{
    internal NotCountedView(string prefabName, string? displayName, string reason, int items, double quantity)
    {
        PrefabName = prefabName;
        DisplayName = ThingName.Displayed(displayName, prefabName);
        Reason = reason;
        Items = items;
        Quantity = quantity;
    }

    public string PrefabName { get; }

    public string? DisplayName { get; }

    /// <summary>decayed, seed, planted, no_nutrition or empty.</summary>
    public string Reason { get; }

    public int Items { get; }

    public double Quantity { get; }
}

internal sealed class ConsumableTotalsView
{
    internal ConsumableTotalsView(ConsumableAmounts all, ConsumableAmounts packaged, int foodItems, int drinkItems)
    {
        Nutrition = all.Nutrition;
        NutritionInPackages = packaged.Nutrition;
        LiquidL = all.LiquidL;
        LiquidInPackagesL = packaged.LiquidL;
        Hydration = all.Hydration;
        HydrationInPackages = packaged.Hydration;
        FoodItems = foodItems;
        DrinkItems = drinkItems;
    }

    public double Nutrition { get; }

    public double NutritionInPackages { get; }

    public double LiquidL { get; }

    public double LiquidInPackagesL { get; }

    public double Hydration { get; }

    public double HydrationInPackages { get; }

    public int FoodItems { get; }

    public int DrinkItems { get; }
}
