#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Subscriptions;

/// <summary>
/// The world model's news a find subscription listens to (cost design A6): each a counter that moves when that part of
/// the world changed. A find re-reads early when one of its channels moved.
/// </summary>
[Flags]
internal enum FindChannels
{
    None = 0,

    /// <summary>Any thing filed into or out of the world index.</summary>
    Things = 1,
    Frames = 1 << 2,
    Devices = 1 << 3,
    Items = 1 << 4,
    Plants = 1 << 5,
    Pads = 1 << 6,
    Consumables = 1 << 7,

    /// <summary>Cells whose structures' air or gravity, room or terrain changed (frames' faces, FrameFaces).</summary>
    Cells = 1 << 8,

    /// <summary>A thing joined or left the damaged set (DamagedThings).</summary>
    Damaged = 1 << 9,

    /// <summary>A cable network's device lists changed (GatewayMaps).</summary>
    DataNetworks = 1 << 10,
}

/// <summary>The world model's counters, by channel. Main thread.</summary>
internal interface IWorldNews
{
    /// <summary>The sum of the channels' counters now: any change on one of them moves it.</summary>
    long Version(FindChannels channels);
}

/// <summary>
/// Which finds can be subscribed to, and the news each listens to. Every one of them also carries live facts (its
/// handler's [ReplyFact]s: quantities, paint, damage values, growth), so it is re-read every interval as well; the
/// channels only bring a re-read forward. find ore (a search around a point) and find script (the hub's) are not here.
/// </summary>
internal sealed class FindTopic
{
    private static readonly Dictionary<string, FindTopic> ByWhat = new Dictionary<string, FindTopic>(StringComparer.Ordinal)
    {
        ["thing"] = new FindTopic("thing", FindChannels.Things),
        ["item"] = new FindTopic("item", FindChannels.Items),
        ["device"] = new FindTopic("device", FindChannels.Devices | FindChannels.DataNetworks),
        ["container"] = new FindTopic("container", FindChannels.Things),
        ["frame"] = new FindTopic("frame", FindChannels.Frames | FindChannels.Cells),
        ["damaged"] = new FindTopic("damaged", FindChannels.Damaged),
        ["pad"] = new FindTopic("pad", FindChannels.Pads),
        ["water"] = new FindTopic("water", FindChannels.Cells),
        ["food"] = new FindTopic("food", FindChannels.Consumables),
        ["plant"] = new FindTopic("plant", FindChannels.Plants),
        ["room"] = new FindTopic("room", FindChannels.Cells),
        ["gateway"] = new FindTopic("gateway", FindChannels.Devices | FindChannels.DataNetworks),
    };

    private FindTopic(string what, FindChannels channels)
    {
        What = what;
        Channels = channels;
    }

    internal string What { get; }

    internal FindChannels Channels { get; }

    /// <summary>The whats a find subscription takes, for messages.</summary>
    internal static IEnumerable<string> Whats => ByWhat.Keys;

    internal static FindTopic? Of(string? what) =>
        what != null && ByWhat.TryGetValue(what, out FindTopic? topic) ? topic : null;
}

/// <summary>
/// What one find subscription asks: the topic and the find's own options (without paging), and the key two subscribers
/// asking the same thing share one evaluation under (the what and the options written with their keys in order).
/// </summary>
internal sealed class FindQuery
{
    internal FindQuery(FindTopic topic, JObject options)
    {
        Topic = topic;
        Options = options;
        Key = topic.What + " " + Canonical(options).ToString(Formatting.None);
    }

    internal FindTopic Topic { get; }

    /// <summary>The find's options as given (no what, no paging).</summary>
    internal JObject Options { get; }

    internal string Key { get; }

    private static JToken Canonical(JToken token)
    {
        if (token is JObject given)
        {
            List<string> names = new List<string>();
            foreach (JProperty property in given.Properties())
            {
                names.Add(property.Name);
            }

            names.Sort(StringComparer.Ordinal);
            JObject sorted = new JObject();
            foreach (string name in names)
            {
                sorted[name] = Canonical(given[name]!);
            }

            return sorted;
        }

        return token is JArray array ? new JArray(Canonical(array)) : token;
    }

    private static IEnumerable<JToken> Canonical(JArray array)
    {
        foreach (JToken item in array)
        {
            yield return Canonical(item);
        }
    }
}
