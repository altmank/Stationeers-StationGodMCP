#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Trading;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;
using UnityEngine;
using World = StationGodMCP.WorldModel.World;

namespace StationGodMCP.Verbs;

/// <summary>
/// trade contacts (catalogue trade.json): trader contacts in the sky (TraderContact.AllStationContacts) and where each
/// satellite dish points, so a dish can be aimed from outside the game. Device logic reports only the one contact a
/// dish is locked onto (SignalID) and nothing reports where a dish points. The game scores every contact against
/// SatelliteDish.DishForward: angle error = acos(dot(DishForward, contact.Angle)) (SatelliteDish.ScanForDishContacts).
/// No options. Read only.
/// </summary>
internal static class TradeContacts
{
    internal static VerbOutcome Run(TradeContactsOptions options)
    {
        float now = GameManager.GameTime;
        List<TraderContactView> contacts = new List<TraderContactView>();
        foreach (TraderContact contact in Contacts.All())
        {
            contacts.Add(ContactView(contact, now));
        }

        List<DishView> dishes = new List<DishView>();
        foreach (SatelliteDish dish in World.Dishes())
        {
            if (dish != null)
            {
                dishes.Add(DishViewOf(dish));
            }
        }

        return new VerbOutcome.Answered(
            JObject.FromObject(new TraderContactsView(now, contacts, dishes), ApiJson.Fresh()));
    }

    // TraderContact.EndLifetime = InitialLifeTime (game time at spawn or load) + Lifetime; 0 once the slot ended it.
    private static TraderContactView ContactView(TraderContact contact, float now)
    {
        Vector3 direction = contact.Angle;
        float elevation = Mathf.Asin(Mathf.Clamp(direction.y, -1f, 1f)) * Mathf.Rad2Deg;
        float? left = contact.EndLifetime > 0f ? Math.Max(0f, contact.EndLifetime - now) : null;
        return new TraderContactView(Contacts.Identity(contact), Contacts.Vector(direction), elevation,
            new ContactPower(contact.MinimumWattsToResolve, contact.MinimumWattsToContact), contact.Contacted, left);
    }

    // SatelliteDish.DishForward is only updated when Horizontal or Vertical changes; DishTransform.up is the model now.
    private static DishView DishViewOf(SatelliteDish dish)
    {
        Transform? model = dish.DishTransform;
        float[]? up = model != null ? Contacts.Vector(model.up) : null;
        DishPose pose = new DishPose(Contacts.Vector(dish.DishForward), up, dish.GetLogicValue(LogicType.Horizontal),
            dish.GetLogicValue(LogicType.Vertical));
        return new DishView(GameLookup.ViewOf(dish), pose, Contacts.Readiness(dish),
            (int)GameMembers.DishMinWattage.GetValue(dish)!, (int)GameMembers.DishMaxWattage.GetValue(dish)!,
            (float)GameMembers.DishFieldOfView.GetValue(dish)!);
    }
}
