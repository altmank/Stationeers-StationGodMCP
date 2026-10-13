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
        ReadSample.Handler(static () => StationGodMod.Hub),
    });
}
