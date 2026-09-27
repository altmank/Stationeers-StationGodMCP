#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Api.Views;

/// <summary>trader_contacts: every trader contact in the sky, and every satellite dish with where it points.</summary>
internal sealed class TraderContactsView
{
    internal TraderContactsView(float gameTime, List<TraderContactView> contacts, List<DishView> dishes)
    {
        GameTimeS = gameTime;
        Contacts = contacts;
        Dishes = dishes;
    }

    public float GameTimeS { get; }

    public List<TraderContactView> Contacts { get; }

    public List<DishView> Dishes { get; }
}

/// <summary>Who a contact is: its trader, name, ship and the pad the ship needs.</summary>
internal sealed class ContactIdentity
{
    internal ContactIdentity(ThingId referenceId, string? trader, string? displayName, string shuttle, int[] padSize)
    {
        ReferenceId = referenceId;
        Trader = trader;
        DisplayName = displayName;
        Shuttle = shuttle;
        PadSize = padSize;
    }

    internal ThingId ReferenceId { get; }

    internal string? Trader { get; }

    internal string? DisplayName { get; }

    internal string Shuttle { get; }

    internal int[] PadSize { get; }
}

internal sealed class TraderContactView
{
    internal TraderContactView(ContactIdentity identity, float[] direction, float elevationDeg, ContactPower power,
        bool contacted, float? timeLeftS)
    {
        ReferenceId = identity.ReferenceId;
        Trader = identity.Trader;
        DisplayName = identity.DisplayName;
        Shuttle = identity.Shuttle;
        PadSizeTiles = identity.PadSize;
        Direction = direction;
        ElevationDeg = elevationDeg;
        MinPowerToResolveW = power.ToResolveW;
        MinPowerToContactW = power.ToContactW;
        Contacted = contacted;
        TimeLeftS = timeLeftS;
    }

    public ThingId ReferenceId { get; }

    public string? Trader { get; }

    public string? DisplayName { get; }

    public string Shuttle { get; }

    public int[] PadSizeTiles { get; }

    /// <summary>Unit vector from the base to the contact, world axes, y up.</summary>
    public float[] Direction { get; }

    public float ElevationDeg { get; }

    public float MinPowerToResolveW { get; }

    public float MinPowerToContactW { get; }

    public bool Contacted { get; }

    /// <summary>TraderContact.EndLifetime less the game time; null when the slot has ended it.</summary>
    public float? TimeLeftS { get; }
}

/// <summary>The dish power a contact needs to be resolved and to be contacted.</summary>
internal sealed class ContactPower
{
    internal ContactPower(float toResolveW, float toContactW)
    {
        ToResolveW = toResolveW;
        ToContactW = toContactW;
    }

    internal float ToResolveW { get; }

    internal float ToContactW { get; }
}

internal sealed class DishView
{
    internal DishView(ThingView dish, DishPose pose, int minPowerW, int maxPowerW, float fieldOfViewDeg)
    {
        ReferenceId = dish.ReferenceId;
        DisplayName = dish.DisplayName;
        PrefabName = dish.PrefabName;
        Forward = pose.Forward;
        TransformUp = pose.TransformUp;
        Horizontal = pose.Horizontal;
        Vertical = pose.Vertical;
        MinPowerW = minPowerW;
        MaxPowerW = maxPowerW;
        FieldOfViewDeg = fieldOfViewDeg;
    }

    public ThingId ReferenceId { get; }

    public string? DisplayName { get; }

    public string? PrefabName { get; }

    /// <summary>
    /// SatelliteDish.DishForward: what the game scores contacts against; only updated when the dish moves.
    /// </summary>
    public float[] Forward { get; }

    /// <summary>The dish model's current up, null without a model.</summary>
    public float[]? TransformUp { get; }

    /// <summary>The current pose in logic degrees, not the target.</summary>
    public double Horizontal { get; }

    public double Vertical { get; }

    public int MinPowerW { get; }

    public int MaxPowerW { get; }

    public float FieldOfViewDeg { get; }
}

/// <summary>Where a dish points.</summary>
internal sealed class DishPose
{
    internal DishPose(float[] forward, float[]? transformUp, double horizontal, double vertical)
    {
        Forward = forward;
        TransformUp = transformUp;
        Horizontal = horizontal;
        Vertical = vertical;
    }

    internal float[] Forward { get; }

    internal float[]? TransformUp { get; }

    internal double Horizontal { get; }

    internal double Vertical { get; }
}

/// <summary>dish_aim: the Horizontal and Vertical that point a dish at a contact.</summary>
internal sealed class DishAimView
{
    internal DishAimView(ThingId dishId, ThingId contactId, DishAimResult aim, DishNowView current,
        float stalePoseDeg, int samples)
    {
        DishId = dishId;
        ContactId = contactId;
        Horizontal = aim.Horizontal;
        Vertical = aim.Vertical;
        ErrorDeg = aim.ErrorDeg;
        Pointing = aim.Pointing;
        Target = aim.Target;
        Current = current;
        StalePoseDeg = stalePoseDeg;
        Samples = samples;
    }

    public ThingId DishId { get; }

    public ThingId ContactId { get; }

    /// <summary>Logic degrees to write to the dish's Horizontal.</summary>
    public double Horizontal { get; }

    public double Vertical { get; }

    /// <summary>
    /// The angle left between the aimed pose and the contact: above 0 only when the dish cannot reach it. Under 2
    /// degrees the contact gets the dish's whole Setting.
    /// </summary>
    public float ErrorDeg { get; }

    public float[] Pointing { get; }

    public float[] Target { get; }

    public DishNowView Current { get; }

    /// <summary>
    /// How far the model's pose was from its own animator's evaluation before sampling. Above a degree means the
    /// animator was not being evaluated (culled), so the game's DishForward may lag the angles.
    /// </summary>
    public float StalePoseDeg { get; }

    public int Samples { get; }
}

/// <summary>The best pose found, in logic degrees, and how far off it still is.</summary>
internal sealed class DishAimResult
{
    internal DishAimResult(double horizontal, double vertical, float errorDeg, float[] pointing, float[] target)
    {
        Horizontal = horizontal;
        Vertical = vertical;
        ErrorDeg = errorDeg;
        Pointing = pointing;
        Target = target;
    }

    internal double Horizontal { get; }

    internal double Vertical { get; }

    internal float ErrorDeg { get; }

    internal float[] Pointing { get; }

    internal float[] Target { get; }
}

internal sealed class DishNowView
{
    internal DishNowView(double horizontal, double vertical, float[] forward, float? errorDeg)
    {
        Horizontal = horizontal;
        Vertical = vertical;
        Forward = forward;
        ErrorDeg = errorDeg;
    }

    public double Horizontal { get; }

    public double Vertical { get; }

    public float[] Forward { get; }

    /// <summary>The angle between DishForward and the contact; null while DishForward is unset.</summary>
    public float? ErrorDeg { get; }
}

/// <summary>trader_inventory: what each trader buys and sells, at what price and how many.</summary>
internal sealed class TraderInventoryView
{
    internal TraderInventoryView(List<TraderStockView> contacts)
    {
        Contacts = contacts;
    }

    public List<TraderStockView> Contacts { get; }
}

internal sealed class TraderStockView
{
    internal TraderStockView(ContactIdentity identity, bool contacted, List<TraderBuysView> buys,
        List<TraderSellsView> sells)
    {
        ReferenceId = identity.ReferenceId;
        Trader = identity.Trader;
        DisplayName = identity.DisplayName;
        Shuttle = identity.Shuttle;
        PadSizeTiles = identity.PadSize;
        Contacted = contacted;
        Buys = buys;
        Sells = sells;
    }

    public ThingId ReferenceId { get; }

    public string? Trader { get; }

    public string? DisplayName { get; }

    public string Shuttle { get; }

    public int[] PadSizeTiles { get; }

    public bool Contacted { get; }

    public List<TraderBuysView> Buys { get; }

    public List<TraderSellsView> Sells { get; }
}

/// <summary>What the trader pays for.</summary>
internal sealed class TraderBuysView
{
    internal TraderBuysView(TradeItem item, int wanted, List<string?> conditions, double? have, int? sellable)
    {
        Name = item.Name;
        PrefabName = item.PrefabName;
        CreditsEach = item.CreditsEach;
        Wanted = wanted;
        Gas = item.Gas;
        Conditions = conditions;
        Have = have;
        Sellable = sellable;
    }

    public string? Name { get; }

    public string? PrefabName { get; }

    public float CreditsEach { get; }

    public int Wanted { get; }

    public bool Gas { get; }

    public List<string?> Conditions { get; }

    /// <summary>How many exist outside the trader as items (find_items' rules; machine stock not counted).</summary>
    public double? Have { get; }

    /// <summary>
    /// While the trader is landed, how many it would take now: on the pad network's vending machines and on the
    /// player, meeting its conditions (gas: the pad network's gas in units). Null while not landed.
    /// </summary>
    public int? Sellable { get; }
}

/// <summary>What the trader offers.</summary>
internal sealed class TraderSellsView
{
    internal TraderSellsView(TradeItem item, int stock, string? details)
    {
        Name = item.Name;
        PrefabName = item.PrefabName;
        CreditsEach = item.CreditsEach;
        Stock = stock;
        Gas = item.Gas;
        Details = details;
    }

    public string? Name { get; }

    public string? PrefabName { get; }

    public float CreditsEach { get; }

    public int Stock { get; }

    public bool Gas { get; }

    public string? Details { get; }
}

/// <summary>A traded item's name, prefab, price per unit (TransactionData.Value) and whether it is a gas.</summary>
internal sealed class TradeItem
{
    internal TradeItem(string? name, string? prefabName, float creditsEach, bool gas)
    {
        Name = name;
        PrefabName = prefabName;
        CreditsEach = creditsEach;
        Gas = gas;
    }

    internal string? Name { get; }

    internal string? PrefabName { get; }

    internal float CreditsEach { get; }

    internal bool Gas { get; }
}
