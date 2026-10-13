#nullable enable

using Assets.Scripts.Atmospherics;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// check ignition by id: the atmosphere a tank or canister holds (Thing.InternalAtmosphere), or a pipe network's (by
/// the network's id or a pipe on it), judged by the game's autoignition rule (MixIgnition). Read only.
/// </summary>
internal static class ContentsIgnition
{
    internal const string Tank = "tank";
    internal const string Canister = "canister";
    internal const string Network = "network";

    internal static IgnitionContentsView Read(ThingId id, string label)
    {
        Thing thing = Thing.Find(id.Value);
        if (thing != null && !thing.IsBeingDestroyed)
        {
            if (thing.InternalAtmosphere != null)
            {
                return Of(id, thing is Structure ? Tank : Canister, thing.InternalAtmosphere);
            }

            if (thing is INetworkedPipe { PipeNetwork: { } pipes })
            {
                return Of(new ThingId(pipes.ReferenceId), Network, pipes.Atmosphere);
            }

            throw ApiErrors.Refused("no_atmosphere",
                $"{label}: {thing.DisplayName} ({id}) holds no gas or liquid; give a tank, a canister or a pipe network.");
        }

        if (Referencable.Find<PipeNetwork>(id.Value) is { } network)
        {
            return Of(id, Network, network.Atmosphere);
        }

        throw ApiErrors.Refused(ApiErrors.ThingNotFoundCode,
            $"{label}: no tank, canister or pipe network has id {id}.");
    }

    private static IgnitionContentsView Of(ThingId id, string kind, Atmosphere atmosphere)
    {
        GasMixture mixture = atmosphere.GasMixture;
        return new IgnitionContentsView(id, kind, atmosphere.Temperature.ToDouble(),
            atmosphere.PressureGassesAndLiquids.ToDouble(), MixIgnition.AutoIgnitionK(mixture),
            MixIgnition.IgnitesNow(mixture));
    }
}
