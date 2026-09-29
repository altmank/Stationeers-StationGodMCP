#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Motherboards;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using Trading;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// trader_contacts: trader contacts in the sky (TraderContact.AllStationContacts) and where each satellite dish
/// points, so a dish can be aimed from outside the game. Device logic reports only the one contact a dish is locked
/// onto (SignalID) and nothing reports where a dish points. The game scores every contact against
/// SatelliteDish.DishForward: angle error = acos(dot(DishForward, contact.Angle)) (SatelliteDish.ScanForDishContacts).
/// Read only.
/// </summary>
internal static class TraderContactsApi
{
    internal static TraderContactsView Handle(Args args)
    {
        float now = GameManager.GameTime;
        List<TraderContactView> contacts = new List<TraderContactView>();
        foreach (TraderContact contact in Contacts.All())
        {
            contacts.Add(ContactView(contact, now));
        }

        List<DishView> dishes = new List<DishView>();
        foreach (Device device in Device.AllDevices.ToList())
        {
            if (device is SatelliteDish dish && dish != null)
            {
                dishes.Add(DishViewOf(dish));
            }
        }

        return new TraderContactsView(now, contacts, dishes);
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

/// <summary>The trader contacts, by id, and how the trader tools name one.</summary>
internal static class Contacts
{
    /// <summary>Whether the dish turns now: SatelliteDish.CanRotate's three conditions, read one by one.</summary>
    internal static DishReadiness Readiness(SatelliteDish dish) =>
        new DishReadiness(dish.IsStructureCompleted, dish.Powered, dish.OnOff);

    /// <summary>Every contact in the sky, by reference id.</summary>
    internal static List<TraderContact> All()
    {
        List<TraderContact> contacts = new List<TraderContact>(TraderContact.AllStationContacts.Count);
        foreach (TraderContact contact in TraderContact.AllStationContacts)
        {
            if (contact != null)
            {
                contacts.Add(contact);
            }
        }

        contacts.Sort(static (a, b) => a.ReferenceId.CompareTo(b.ReferenceId));
        return contacts;
    }

    internal static TraderContact? Find(long id)
    {
        foreach (TraderContact contact in TraderContact.AllStationContacts)
        {
            if (contact != null && contact.ReferenceId == id)
            {
                return contact;
            }
        }

        return null;
    }

    internal static ContactIdentity Identity(TraderContact contact)
    {
        TraderDataInstance? data = contact.DataInstance;
        Vector2 pad = contact.RequiredPadSize();
        return new ContactIdentity(new ThingId(contact.ReferenceId), data?.TraderData?.Id, data?.DisplayName,
            contact.ShuttleType.ToString(), new[] { (int)pad.x, (int)pad.y });
    }

    internal static float[] Vector(Vector3 vector) => new[] { vector.x, vector.y, vector.z };
}
