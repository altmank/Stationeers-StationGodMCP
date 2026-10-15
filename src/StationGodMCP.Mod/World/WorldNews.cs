#nullable enable

using StationGodMCP.Pure.Subscriptions;

namespace StationGodMCP.WorldModel;

/// <summary>
/// The world model's news for find subscriptions (cost design A6): a find re-reads early when one of its channels moved.
/// Each channel is a counter that only grows: a class of the world index filed into or out of (ThingIndex), cells whose
/// air, gravity, room or terrain changed (WorldEvents.Cells), the damaged set joined or left (DamagedThings), cable
/// networks' device lists dirtied (WorldEvents.DataNetworkChanges). Main thread.
/// </summary>
internal sealed class WorldNews : IWorldNews
{
    internal static readonly WorldNews Instance = new WorldNews();

    private WorldNews()
    {
    }

    public long Version(FindChannels channels)
    {
        long version = 0;
        version += Class(channels, FindChannels.Things, WorldClass.Thing);
        version += Class(channels, FindChannels.Frames, WorldClass.Frame);
        version += Class(channels, FindChannels.Devices, WorldClass.Device);
        version += Class(channels, FindChannels.Items, WorldClass.Item);
        version += Class(channels, FindChannels.Plants, WorldClass.Plant);
        version += Class(channels, FindChannels.Pads, WorldClass.Pad);
        version += Class(channels, FindChannels.Consumables, WorldClass.Consumable);
        version += (channels & FindChannels.Cells) != 0 ? WorldEvents.Cells.Told : 0;
        version += (channels & FindChannels.Damaged) != 0 ? DamagedThings.Moved : 0;
        version += (channels & FindChannels.DataNetworks) != 0 ? WorldEvents.DataNetworkChanges : 0;
        return version;
    }

    private static long Class(FindChannels channels, FindChannels channel, WorldClass worldClass) =>
        (channels & channel) != 0 && ThingIndex.Ready ? ThingIndex.ChangesOf(worldClass) : 0;
}
