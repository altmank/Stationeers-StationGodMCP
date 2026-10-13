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
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Protocol;
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
/// apart from DamageState: a burst pipe reads 0 damage, so it is reported broken (Wrecks) like a broken structure. A
/// cable an overload burnt is replaced by a separate CableRuptured piece (StructureCableStraightBurnt and the like),
/// undamaged and carrying no power: reported broken too.
///
/// A thing in a slot keeps no position of its own, so position and distance are its outermost holder's
/// (HolderChain.PlaceOf), as container_contents and find_things place it.
///
/// Three forms: reference_id, one thing; reference_ids, up to 256, a result per id; network_id, every piece of one pipe,
/// cable or chute network (its StructureList; devices are not pieces), worst first, paged, damaged_only keeping the
/// damaged and broken ones, with the broken pieces that touch the network at its ends without being on it
/// (BrokenNeighbours: the member list cannot show those, damaged_only or not).
///
/// A structure's networks walk its open ends, the costly part of a reading (the catalogue's x-costly): the list forms
/// read them only when the call's fields keep networks in that list, since the writer would drop them otherwise. The
/// one-thing form answers at the top level, which fields do not shape, so it always reads them.
/// </summary>
internal static class ThingHealthApi
{
    internal const int MaximumIds = 256;

    internal static object Handle(Args args)
    {
        switch (HealthRequest.Parse(args))
        {
            case HealthRequest.One one:
                return HealthReader.Read(GameLookup.RequireThing(one.Id), PlayerOrigin.Current(), withNetworks: true,
                    withBuildState: true);
            case HealthRequest.Many many:
                return ReadMany(many.Ids, args.Shape.Wants("results", "networks"));
            case HealthRequest.Network network:
                return ReadNetwork(network, args.Shape.Wants("things", "networks"));
            default:
                throw ApiErrors.InvalidArgument(Vocabulary.Text("Unknown {tool:thing_health} form."));
        }
    }

    private static HealthNetworkView ReadNetwork(HealthRequest.Network request, bool withNetworks)
    {
        UpgradeFamily family = request.Kind switch
        {
            "cable" => new CableFamily(),
            "chute" => new ChuteFamily(),
            _ => new PipeFamily(),
        };
        ThingId id = NetworkHandles.Resolve(request.Handle, "network_id", family);
        List<SmallGrid> members = family.NetworkMembers(id);
        List<Thing> pieces = new List<Thing>(members.Count);
        foreach (SmallGrid member in members)
        {
            if (member != null && !member.IsBeingDestroyed)
            {
                pieces.Add(member);
            }
        }

        PlayerOrigin origin = PlayerOrigin.Current();
        List<Thing> ranked = HealthScanner.WorstFirst(pieces, request.DamagedOnly);
        Slice<Thing> page = Slice<Thing>.Of(ranked, request.Page);
        List<HealthView> views = new List<HealthView>(page.Items.Count);
        foreach (Thing thing in page.Items)
        {
            views.Add(HealthReader.Read(thing, origin, withNetworks));
        }

        request.Page.Note("things", views.Count, page.Total);
        List<Thing> touching = new List<Thing>(pieces);
        touching.AddRange(family.NetworkDevices(id));
        return new HealthNetworkView(id, family.NetworkKind, pieces.Count, request.DamagedOnly,
            Slice<HealthView>.Page(views, request.Page, page.Total),
            BrokenNeighbours.Around(family.NetworkKind, touching, "things"));
    }

    private static BatchResultView ReadMany(JArray ids, bool withNetworks)
    {
        PlayerOrigin origin = PlayerOrigin.Current();
        BatchBuilder batch = new BatchBuilder(ids.Count);
        for (int index = 0; index < ids.Count; index++)
        {
            if (!ThingId.TryRead(ids[index], out ThingId id))
            {
                batch.Failed(
                    index, ApiErrors.InvalidArgument(Vocabulary.Text("Argument '{arg:reference_id}' must be an Int64 " +
                        "encoded as a string.")));
            }
            else if (!GameLookup.TryFindThing(id, out Thing thing))
            {
                batch.Failed(index, ApiErrors.ThingNotFound(id));
            }
            else
            {
                batch.Succeeded(new HealthItemView(index, HealthReader.Read(thing, origin, withNetworks)));
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

    private const int DefaultLimit = ReplyDefaults.HealthThings;
    private const int MaximumLimit = 500;

    private HealthRequest()
    {
    }

    private static readonly string[] NetworkOnly = { "network_id", "kind", "damaged_only" };

    internal static HealthRequest Parse(Args args)
    {
        bool one = args.Has("reference_id");
        bool many = args.Has("reference_ids");
        bool network = args.Has("network_id");
        if ((one ? 1 : 0) + (many ? 1 : 0) + (network ? 1 : 0) != 1)
        {
            throw ApiErrors.InvalidArgument(Vocabulary.Text("Pass one of {arg:reference_id}, {arg:reference_ids} or " +
                "{arg:network_id}."));
        }

        if (network)
        {
            args.Reject("{arg:network_id}", "min_damage_ratio", "min_ratio", "structures_only", "broken_only",
                "near_player_m");
            return Network.From(args);
        }

        args.Reject(one ? "{arg:reference_id}" : "{arg:reference_ids}", "kind", "damaged_only");
        args.Reject(one ? "{arg:reference_id}" : "{arg:reference_ids}", ScanOnly);
        return one
            ? new One(args.ThingId("reference_id"))
            : new Many(args.Array("reference_ids", ThingHealthApi.MaximumIds));
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

    /// <summary>Every piece of one network: network_id (a handle), kind, damaged_only, paged.</summary>
    internal sealed class Network : HealthRequest
    {
        private Network(JToken handle, string kind, bool damagedOnly, PageRequest page)
        {
            Handle = handle;
            Kind = kind;
            DamagedOnly = damagedOnly;
            Page = page;
        }

        internal JToken Handle { get; }

        /// <summary>pipe, cable or chute.</summary>
        internal string Kind { get; }

        /// <summary>Only the damaged and broken pieces (as the scan lists them).</summary>
        internal bool DamagedOnly { get; }

        internal PageRequest Page { get; }

        internal static Network From(Args args)
        {
            string kind = (args.OptionalString("kind") ?? "pipe").Trim().ToLowerInvariant();
            if (kind != "pipe" && kind != "cable" && kind != "chute")
            {
                throw ApiErrors.InvalidArgument(Vocabulary.Text("Argument '{arg:kind}' must be pipe, cable or chute."));
            }

            return new Network(args.Optional("network_id")!, kind, args.OptionalBool("damaged_only") ?? false,
                PageRequest.From(args, DefaultLimit, MaximumLimit));
        }
    }
}
