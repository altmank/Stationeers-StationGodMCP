#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Objects.Items;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// write health: a player's body and organs healed of every kind of damage, as the game's own healers heal them.
///
/// How the game keeps a human's damage (CODE, Assets.Scripts.Objects): the body's DamageState is an EntityDamageState
/// (an OrganicDamageState over ThingDamageState), and each organ (Entity.Organs: the brain and the lungs, added as they
/// enter their slots, Entity.cs:898-931) keeps its own OrganicDamageState (Organ.cs:39-41, Brain.cs:214-216). The body
/// reads oxygen and stun from the brain's state and sends them there (EntityDamageState.cs:14-36, and its Damage), so
/// brain damage is the brain organ's own and outlasts a heal of the body alone. HealAll(min) sets every kind a state
/// allows to at most min: brute and burn (ThingDamageState.cs:107-112), oxygen, toxic, radiation, hydration, stun and
/// decay (OrganicDamageState.cs:43-52), and the body's starvation (EntityDamageState.HealAll). The game's healers work
/// on the body and on every organ alike: a medical healing effect calls HealAll on each while it is past half damage
/// (HealingEffect.cs:23-38), the cryo tube calls Heal(amount) on each (CryoTube.cs:526-536, ReviveOccupant :560-572).
/// A heal here is HealAll(0) on the body and on each organ.
///
/// It reaches the clients as any damage does: Damage acts only where the simulation runs, marks the changed kind and
/// sets the thing's network flag 4 on the server (IndestructableDamageState.cs:84-93, OnDamageUpdated :144-150), so
/// the state goes out in the thing's next delta. A player who is not in the game is healed the same way: their body
/// and its organs stay in the world (World.Humans). A dead player is refused: a heal does not change EntityState, and
/// the cryo tube's revival is its own step (CryoTube.ReviveOccupant). Damage is not taken while DamageState.Defend is
/// set (while the game swaps two things between slots, OnServer.cs:655-682); each part is read back, and healed says
/// whether every part reads none.
/// </summary>
internal static class PlayerHeals
{
    private const string BodyPart = "body";

    /// <summary>The player named (by name or id, in the game or not), else the player; refused on a client or when dead.</summary>
    internal static Human Require(string? wanted)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host heals players.");
        }

        Human player = wanted == null ? PlayerOrigin.RequireHuman() : MovablePlayers.Find(wanted);
        if (!PlayerOrigin.IsAlive(player))
        {
            throw ApiErrors.Refused("player_dead",
                $"{player.DisplayName} is dead ({player.State}); a heal does not revive. A powered cryo tube revives a body in it.");
        }

        return player;
    }

    /// <summary>Each part as it reads now, its damage after given as none: the report.</summary>
    internal static HealView Read(Human player)
    {
        List<HealedPartView> parts = new List<HealedPartView>();
        foreach (Part part in Parts(player))
        {
            parts.Add(new HealedPartView(part.Name, GameLookup.ViewOf(part.Thing), part.State.TotalRounded, 0,
                Damage(part.State), DamagePartsView.None));
        }

        return View(player, parts, null);
    }

    /// <summary>Every part healed, each read before and after.</summary>
    internal static HealView Heal(Human player)
    {
        List<HealedPartView> parts = new List<HealedPartView>();
        foreach (Part part in Parts(player))
        {
            int percentBefore = part.State.TotalRounded;
            DamagePartsView before = Damage(part.State);
            part.State.HealAll(0f);
            parts.Add(new HealedPartView(part.Name, GameLookup.ViewOf(part.Thing), percentBefore,
                part.State.TotalRounded, before, Damage(part.State)));
        }

        return View(player, parts, HealReplies.AllWhole(parts));
    }

    private static HealView View(Human player, List<HealedPartView> parts, bool? healed) =>
        new HealView(GameLookup.ViewOf(player), player.OrganBrain != null && player.OrganBrain.IsOnline,
            player.State.ToString(), parts, healed);

    // The body first, then each organ in the order the game keeps them.
    private static List<Part> Parts(Human player)
    {
        List<Part> parts = new List<Part> { new Part(BodyPart, player, player.DamageState) };
        foreach (Organ organ in player.Organs)
        {
            if (organ != null && organ.DamageState != null)
            {
                parts.Add(new Part(NameOf(organ), organ, organ.DamageState));
            }
        }

        return parts;
    }

    private static string NameOf(Organ organ) => organ switch
    {
        Brain => "brain",
        Lungs => "lungs",
        _ => organ.PrefabName
    };

    private static DamagePartsView Damage(IndestructableDamageState state) =>
        new DamagePartsView(state.Brute, state.Burn, state.Oxygen, state.Hydration, state.Starvation, state.Toxic,
            state.Radiation, state.Decay, state.Stun);

    private sealed class Part
    {
        internal Part(string name, Thing thing, IndestructableDamageState state)
        {
            Name = name;
            Thing = thing;
            State = state;
        }

        internal string Name { get; }

        internal Thing Thing { get; }

        internal IndestructableDamageState State { get; }
    }
}
