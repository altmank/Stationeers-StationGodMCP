#nullable enable

using StationGodMCP.Protocol;
using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// The game's (verb, what) handler table: one verb-native handler per what, each binding its generated options class
/// (VerbHandler.For(XOptions.Spec, XOptions.Bind, run)). A verb with no handler here answers method_not_found.
/// </summary>
internal static class VerbHandlers
{
    internal static VerbDispatcher Dispatcher { get; } = new VerbDispatcher(new[]
    {
        VerbHandler.For(FindThingOptions.Spec, FindThingOptions.Bind, FindThing.Run),
        VerbHandler.For(FindItemOptions.Spec, FindItemOptions.Bind, FindItem.Run),
        VerbHandler.For(FindDeviceOptions.Spec, FindDeviceOptions.Bind, FindDevice.Run),
        VerbHandler.For(FindContainerOptions.Spec, FindContainerOptions.Bind, FindContainer.Run),
        VerbHandler.For(FindFrameOptions.Spec, FindFrameOptions.Bind, FindFrame.Run),
        VerbHandler.For(FindDamagedOptions.Spec, FindDamagedOptions.Bind, FindDamaged.Run),
        VerbHandler.For(FindOreOptions.Spec, FindOreOptions.Bind, FindOre.Run),
        VerbHandler.For(FindPlantOptions.Spec, FindPlantOptions.Bind, FindPlant.Run),
        VerbHandler.For(FindRoomOptions.Spec, FindRoomOptions.Bind, FindRoom.Run),
        VerbHandler.For(FindPadOptions.Spec, FindPadOptions.Bind, FindPad.Run),
        VerbHandler.For(FindGatewayOptions.Spec, FindGatewayOptions.Bind, FindGateway.Run),
        VerbHandler.For(FindFoodOptions.Spec, FindFoodOptions.Bind, FindFood.Run),
        VerbHandler.For(FindWaterOptions.Spec, FindWaterOptions.Bind, FindWater.Run),
        VerbHandler.For(SurveyAreaOptions.Spec, SurveyAreaOptions.Bind, SurveyArea.Run),
        VerbHandler.For(SurveyWallOptions.Spec, SurveyWallOptions.Bind, SurveyWall.Run),
        VerbHandler.For(SurveyNetworkOptions.Spec, SurveyNetworkOptions.Bind, SurveyNetwork.Run),
        VerbHandler.For(SurveyFeedsOptions.Spec, SurveyFeedsOptions.Bind, SurveyFeeds.Run),
        VerbHandler.For(InspectDeviceOptions.Spec, InspectDeviceOptions.Bind, InspectDevice.Run),
        VerbHandler.For(InspectSlotsOptions.Spec, InspectSlotsOptions.Bind, InspectSlots.Run),
        VerbHandler.For(InspectContentsOptions.Spec, InspectContentsOptions.Bind, InspectContents.Run),
        VerbHandler.For(InspectConnectionsOptions.Spec, InspectConnectionsOptions.Bind, InspectConnections.Run),
        VerbHandler.For(InspectAtmosphereOptions.Spec, InspectAtmosphereOptions.Bind, InspectAtmosphere.Run),
        VerbHandler.For(InspectReagentsOptions.Spec, InspectReagentsOptions.Bind, InspectReagents.Run),
        VerbHandler.For(InspectHealthOptions.Spec, InspectHealthOptions.Bind, InspectHealth.Run),
        VerbHandler.For(InspectGenesOptions.Spec, InspectGenesOptions.Bind, InspectGenes.Run),
        VerbHandler.For(InspectScreenOptions.Spec, InspectScreenOptions.Bind, InspectScreen.Run),
        VerbHandler.For(InspectChipOptions.Spec, InspectChipOptions.Bind, InspectChip.Run),
        VerbHandler.For(InspectPrefabOptions.Spec, InspectPrefabOptions.Bind, InspectPrefab.Run),
        VerbHandler.For(InspectLookingAtOptions.Spec, InspectLookingAtOptions.Bind, InspectLookingAt.Run),
        VerbHandler.For(InspectPlayerOptions.Spec, InspectPlayerOptions.Bind, InspectPlayer.Run),
        VerbHandler.For(InspectPlanetOptions.Spec, InspectPlanetOptions.Bind, InspectPlanet.Run),
        VerbHandler.For(InspectWeatherOptions.Spec, InspectWeatherOptions.Bind, InspectWeather.Run),
        VerbHandler.For(InspectRocketOptions.Spec, InspectRocketOptions.Bind, InspectRocket.Run),
        VerbHandler.For(InspectVaultOptions.Spec, InspectVaultOptions.Bind, InspectVault.Run),
        VerbHandler.For(ReadClockOptions.Spec, ReadClockOptions.Bind, ReadClock.Run),
        VerbHandler.For(ReadLogicOptions.Spec, ReadLogicOptions.Bind, ReadLogic.Run),
        VerbHandler.For(ReadSnapshotOptions.Spec, ReadSnapshotOptions.Bind, ReadSnapshot.Run),
        VerbHandler.For(ReadMemoryOptions.Spec, ReadMemoryOptions.Bind, ReadMemory.Run),
        VerbHandler.For(ReadFlightLogOptions.Spec, ReadFlightLogOptions.Bind, ReadFlightLog.Run),
        ReadSample.Handler(static () => StationGodMod.Hub),
    });
}
