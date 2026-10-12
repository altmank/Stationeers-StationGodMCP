using System.Globalization;
using System.Text.Json;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// The subscription relay and the events: one mod subscription per caller subscription, under an id of the session's
/// own (s1, s2, ...), so a caller can neither see nor end another caller's subscription on the shared connection; its
/// events go to the caller under that id. A world change the link learns of (from the mod's event or from a welcome
/// after a reconnect) reaches every session as one world_changed event.
/// </summary>
public sealed partial class HubSession
{
    private readonly Dictionary<string, Relay> _relays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _callerSubscriptions = new(StringComparer.Ordinal);
    private int _lastSubscription;

    // The subscription is bound on the link's reader as its reply arrives (GameCall.OnReply), before the link raises
    // any later event, so its first update can never overtake it and nothing needs buffering.
    private async Task SubscribeAsync(string id, JsonElement parameters, int? deadline, long hubStream,
        CancellationToken cancellation)
    {
        GameCall call = new("subscribe", parameters, hubStream)
        {
            DeadlineMs = deadline,
            OnReply = reply => Bind(id, hubStream, reply)
        };
        GameReply answered = await _link.CallAsync(call, cancellation).ConfigureAwait(false);

        // A reply the link made itself (unreachable, refused unsent) did not pass Bind; one that did finds its id gone.
        Answer(id, HubLines.Reply(id, answered));
    }

    private void Bind(string id, long hubStream, GameReply reply)
    {
        lock (_gate)
        {
            if (reply is not GameReply.Ok { Result.ValueKind: JsonValueKind.Object } ok ||
                Text(ok.Result, "subscription") is not { } modId)
            {
                Answer(id, HubLines.Reply(id, reply));
                return;
            }

            _lastSubscription++;
            string callerId = "s" + _lastSubscription.ToString(CultureInfo.InvariantCulture);
            _relays[callerId] = new Relay(modId, hubStream);
            _callerSubscriptions[modId] = callerId;
            Answer(id, HubLines.Ok(id, HubLines.Element(HubLines.WithSubscription(ok.Result, callerId)), ok));
        }
    }

    private async Task UnsubscribeAsync(string id, JsonElement parameters, CancellationToken cancellation)
    {
        string? callerId = Text(parameters, "subscription");
        Relay? relay = null;
        lock (_gate)
        {
            if (callerId != null)
            {
                _relays.TryGetValue(callerId, out relay);
            }
        }

        if (relay == null)
        {
            Answer(id, HubLines.Error(id, "unknown_subscription", $"This session has no subscription '{callerId}'."));
            return;
        }

        GameReply reply = await _link.CallAsync(
            new GameCall("unsubscribe", HubLines.Parameters("subscription", relay.ModId), relay.Stream), cancellation)
            .ConfigureAwait(false);
        if (reply is GameReply.Ok or GameReply.Refused { Code: "unknown_subscription" })
        {
            lock (_gate)
            {
                Forget(callerId!, relay.ModId);
            }
        }

        Answer(id, reply is GameReply.Ok { Result.ValueKind: JsonValueKind.Object } ok
            ? HubLines.Ok(id, HubLines.Element(HubLines.WithSubscription(ok.Result, callerId!)), ok)
            : HubLines.Reply(id, reply));
    }

    private async Task UnsubscribeQuietlyAsync(Relay relay, CancellationToken cancellation)
    {
        try
        {
            await _link.CallAsync(new GameCall("unsubscribe", HubLines.Parameters("subscription", relay.ModId), relay.Stream),
                cancellation).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Out of time: the stream's end, or the token's window, ends it on the mod's side.
        }
    }

    // Raised on the link's reader for every event: a relayed subscription's goes to the caller under its id. The rest
    // (the link's own topics, other sessions' subscriptions, the mod's world_changed, which OnWorldChanged covers) are
    // not this caller's.
    private void OnEvent(JsonElement message)
    {
        if (Text(message, "subscription") is not { } modId)
        {
            return;
        }

        lock (_gate)
        {
            if (_closing || !_welcomed || !_callerSubscriptions.TryGetValue(modId, out string? callerId))
            {
                return;
            }

            Send(HubLines.WithSubscription(message, callerId));
            if (Text(message, "event") == "subscription_ended")
            {
                Forget(callerId, modId);
            }
        }
    }

    // Raised once per new world id by the link, from the mod's world_changed or from a reconnect's welcome.
    private void OnWorldChanged(string? worldId)
    {
        JsonElement? world = _link.World;
        if (world is { } known && Text(known, "id") is { } knownId && knownId != (worldId ?? ""))
        {
            world = null;
        }

        lock (_gate)
        {
            if (_closing || !_welcomed)
            {
                return;
            }

            Send(HubLines.WorldChanged(world, worldId));
        }
    }

    // Under the gate.
    private void Forget(string callerId, string modId)
    {
        _relays.Remove(callerId);
        _callerSubscriptions.Remove(modId);
    }

    // Under the gate: the mod ends a stream's subscriptions and cursors without an event.
    private void ForgetStream(long hubStream)
    {
        List<string>? gone = null;
        foreach (KeyValuePair<string, Relay> relay in _relays)
        {
            if (relay.Value.Stream == hubStream)
            {
                (gone ??= []).Add(relay.Key);
            }
        }

        if (gone != null)
        {
            foreach (string callerId in gone)
            {
                Forget(callerId, _relays[callerId].ModId);
            }

            gone.Clear();
        }

        foreach (KeyValuePair<string, long> cursor in _cursors)
        {
            if (cursor.Value == hubStream)
            {
                (gone ??= []).Add(cursor.Key);
            }
        }

        if (gone != null)
        {
            foreach (string cursor in gone)
            {
                _cursors.Remove(cursor);
            }
        }
    }

    /// <summary>A relayed subscription: the mod's id and the hub stream it was made on.</summary>
    private sealed class Relay(string modId, long stream)
    {
        internal string ModId { get; } = modId;

        internal long Stream { get; } = stream;
    }
}
