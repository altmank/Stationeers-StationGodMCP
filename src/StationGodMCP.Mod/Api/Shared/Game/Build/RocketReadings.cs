#nullable enable

using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Networks;
using Objects.Rockets;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game.Build;

/// <summary>What the read tools say about a thing's rocket and an umbilical's pairing. Nothing here changes the game.</summary>
internal static class RocketReadings
{
    /// <summary>The rocket the structure is part of (Rockets.NetworkOf); null when none.</summary>
    internal static RocketPartView? PartOf(Thing thing)
    {
        RocketNetwork? network = thing is Structure structure ? Rockets.NetworkOf(structure) : null;
        if (network == null)
        {
            return null;
        }

        Rocket? rocket = network.Rocket;
        return new RocketPartView(new ThingId(network.ReferenceId), rocket?.DisplayName ?? string.Empty,
            rocket?.RocketState.ToString() ?? RocketState.None.ToString(), network.DryMass,
            network.StructureList.Count, network.Internals.Count);
    }

    /// <summary>The umbilical's pairing; null when the thing is no umbilical.</summary>
    internal static UmbilicalView? UmbilicalOf(Thing thing)
    {
        if (!(thing is IUmbilical umbilical))
        {
            return null;
        }

        Thing? partner = PartnerOf(umbilical);
        bool widened = umbilical.UmbilicalType == UmbilicalType.Umbilical;
        return new UmbilicalView(widened ? "umbilical" : "socket", umbilical.IsOpen,
            partner != null ? GameLookup.ViewOf(partner) : null, umbilical.PartnerDistance,
            new UmbilicalSearchView(Search(umbilical, widened), How(umbilical, widened)));
    }

    // The partner field each class keeps (set by SetPartner; CrewModule's is public).
    private static Thing? PartnerOf(IUmbilical umbilical)
    {
        object? partner = umbilical switch
        {
            RocketGasUmbilicalMale male => GameMembers.GasUmbilicalMalePartner.GetValue(male),
            RocketGasUmbilicalFemale female => GameMembers.GasUmbilicalFemalePartner.GetValue(female),
            RocketPowerUmbilical power => GameMembers.PowerUmbilicalPartner.GetValue(power),
            RocketChuteUmbilicalMale male => GameMembers.ChuteUmbilicalMalePartner.GetValue(male),
            RocketChuteUmbilicalFemale female => GameMembers.ChuteUmbilicalFemalePartner.GetValue(female),
            RocketCrewUmbilical crew => GameMembers.CrewUmbilicalPartner.GetValue(crew),
            CrewModule module => module.PartnerUmbilical,
            _ => null
        };
        Thing? thing = partner is IUmbilical other ? other.AsThing : null;
        return thing != null && !thing.IsBeingDestroyed ? thing : null;
    }

    // RocketUmbilicalHelper.FindAndSetOtherUmbilical's search (RocketUmbilicalHelper.cs:50-88), each cell probed as it
    // reads it.
    private static UmbilicalSearchResult Search(IUmbilical self, bool widened)
    {
        Thing thing = self.AsThing;
        GridController world = GridController.World;
        Vector3 start = FirstCell(self);
        Vector3 forward = thing.Forward * SmallGrid.SmallGridSize;
        Vector3 right = thing.Transform.right * SmallGrid.SmallGridSize;
        return UmbilicalSearch.Run(widened, (column, step) =>
        {
            SmallCell? cell = world.GetSmallCell(start + forward * step + right * column);
            SmallGrid? device = cell?.Device;
            if (device == null)
            {
                return UmbilicalProbe.Clear.Instance;
            }

            if (!(device is IUmbilical other))
            {
                return new UmbilicalProbe.Blocker(device.ReferenceId, $"{Names.Of(device)} ({device.PrefabName})");
            }

            if (ReferenceEquals(other, self))
            {
                return UmbilicalProbe.Clear.Instance;
            }

            Thing found = other.AsThing;
            return new UmbilicalProbe.Umbilical(found.ReferenceId, $"{Names.Of(found)} ({found.PrefabName})",
                self.IsCompatibleWith(other), Vector3.Dot(-found.Forward, thing.Forward));
        });
    }

    private static Vector3 FirstCell(IUmbilical self) =>
        GridController.World.WorldToLocalGrid(self.FirstPartnerSearchPosition, SmallGrid.SmallGridSize,
            SmallGrid.SmallGridOffset).ToVector3();

    private static string How(IUmbilical self, bool widened)
    {
        Vector3 start = FirstCell(self);
        string front = ViewBasis.Nearest(Bodies.V(self.AsThing.Forward)).Name;
        return $"from {PlacePlanner.Describe(start)} along its front ({front}), " +
               $"{UmbilicalSearch.LastStep + 1} small cells, " +
               (widened ? "3 columns (its own and two to its right)" : "1 column") +
               "; a partner must be the matching kind, face back (dot 0.9 or more) and have no other device between";
    }
}
