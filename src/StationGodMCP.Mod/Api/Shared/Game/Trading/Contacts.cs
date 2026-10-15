#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Objects.Electrical;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Views;
using Trading;
using UnityEngine;
using World = StationGodMCP.WorldModel.World;

namespace StationGodMCP.Api.Shared.Game.Trading;

/// <summary>The trader contacts, by id, and how the trade whats name one.</summary>
internal static class Contacts
{
    /// <summary>Whether the dish turns now: SatelliteDish.CanRotate's three conditions, read one by one.</summary>
    internal static DishReadiness Readiness(SatelliteDish dish) =>
        new DishReadiness(dish.IsStructureCompleted, dish.Powered, dish.OnOff);

    /// <summary>Every contact in the sky, by reference id.</summary>
    internal static List<TraderContact> All()
    {
        List<TraderContact> contacts = new List<TraderContact>(World.TraderContacts.Count);
        foreach (TraderContact contact in World.TraderContacts)
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
        foreach (TraderContact contact in World.TraderContacts)
        {
            if (contact != null && contact.ReferenceId == id)
            {
                return contact;
            }
        }

        return null;
    }

    /// <summary>
    /// The reference id an id option names; a text that is no reference id names no contact either, so it is refused
    /// as one that is not there.
    /// </summary>
    internal static ThingId IdOf(string text) =>
        ThingId.TryRead(new JValue(text), out ThingId id)
            ? id
            : throw ApiErrors.Refused("contact_not_found", $"No trader contact has reference id {text}.");

    /// <summary>The contact an id option names; refused contact_not_found when no contact has that reference id.</summary>
    internal static TraderContact Require(string text) =>
        Find(IdOf(text).Value) ?? throw ApiErrors.Refused("contact_not_found",
            $"No trader contact has reference id {text}.");

    internal static ContactIdentity Identity(TraderContact contact)
    {
        TraderDataInstance? data = contact.DataInstance;
        Vector2 pad = contact.RequiredPadSize();
        return new ContactIdentity(new ThingId(contact.ReferenceId), data?.TraderData?.Id, data?.DisplayName,
            contact.ShuttleType.ToString(), new[] { (int)pad.x, (int)pad.y });
    }

    internal static float[] Vector(Vector3 vector) => new[] { vector.x, vector.y, vector.z };
}
