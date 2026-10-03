#nullable enable

using System;
using System.Runtime.CompilerServices;
using Assets.Scripts;
using Assets.Scripts.Networking;
using LaunchPadBooster;
using LaunchPadBooster.Networking;
using StationGodMCP.Pure.RemoteView;

namespace StationGodMCP.Net;

/// <summary>
/// StationGod's messages between games, over StationeersLaunchPad's LaunchPadBooster: a client's view to the server
/// (ViewMessage), the server's drawings back to that client (DrawMessage), and the server's announcement in every
/// joining player's join data (HostAnnouncement).
///
/// StationGod is optional on every machine. Registered as not required, with a version check that accepts any version,
/// so LaunchPadBooster never refuses a join over StationGod: a player without it, or with another version, joins as
/// before. Nothing is sent to a game that has not shown it reads it, because LaunchPadBooster logs a warning for every
/// message of a type it does not know: a client sends its view only after the server announced the same view protocol
/// (HostLink), and the server draws only on a client it has a view from. A client without StationGod reads the
/// announcement's section as unknown and logs one LaunchPadBooster warning per join, nothing more.
///
/// LaunchPadBooster adds its own join check to a game as soon as any mod registers networking with it, and a game
/// without that check cannot join one that has it: on a server where StationGod is the only such mod, a player needs
/// some mod that uses LaunchPadBooster networking. [Multiplayer] ShareViews = false keeps StationGod out of it.
/// </summary>
internal static class StationGodNet
{
    /// <summary>The largest payload either message carries (LaunchPadBooster's limit is 1 MiB with its header).</summary>
    internal const int MaximumPayload = 900_000;

    /// <summary>True once registered with LaunchPadBooster; nothing is sent or read otherwise.</summary>
    internal static bool Active { get; private set; }

    /// <summary>Why it is not active, a clause for the camera tools' refusals; null while active.</summary>
    internal static string? Inactive { get; private set; } = "it has not started";

    internal static void Register(bool enabled)
    {
        if (!enabled)
        {
            Inactive = "[Multiplayer] ShareViews is off in its config";
            StationGodMod.Log("Multiplayer views are off ([Multiplayer] ShareViews = false).");
            return;
        }

        try
        {
            RegisterWithBooster();
            Active = true;
            Inactive = null;
        }
        catch (Exception exception)
        {
            // A missing or older LaunchPadBooster (no Networking API) or a second registration: views stay local.
            Inactive = "this StationeersLaunchPad has no mod networking";
            StationGodMod.LogWarning("Multiplayer views are off: this StationeersLaunchPad has no mod networking. " +
                                     $"Single player is unaffected. {exception.Message}");
        }
    }

    /// <summary>Sends a view payload to the server; false when it could not.</summary>
    internal static bool SendView(byte[] payload)
    {
        if (!Active || payload.Length > MaximumPayload)
        {
            return false;
        }

        try
        {
            SendViewToHost(payload);
            return true;
        }
        catch (Exception exception)
        {
            // LaunchPadBooster's send: a connection closing under it. The next view tries again.
            OnceLog.Warning("send_view", $"Could not send the view to the server: {exception.Message}");
            return false;
        }
    }

    /// <summary>Sends a drawing payload to one client; false when it could not (no such client, too large).</summary>
    internal static bool SendDraw(long connectionId, byte[] payload)
    {
        if (!Active || payload.Length > MaximumPayload)
        {
            return false;
        }

        Client? client = Client.Find(connectionId);
        if (client == null || client.state == ClientState.Disconnected)
        {
            return false;
        }

        try
        {
            SendDrawToClient(client, payload);
            return true;
        }
        catch (Exception exception)
        {
            OnceLog.Warning("send_draw", $"Could not send the drawing to {client.name}: {exception.Message}");
            return false;
        }
    }

    // Its own method, never inlined, so a missing or older LaunchPadBooster fails inside Register's try.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RegisterWithBooster()
    {
        Mod mod = new Mod(StationGodMod.ModId, StationGodMod.Version);
        mod.Networking.Required = false;
        mod.Networking.VersionValidator = new AnyVersion();
        mod.Networking.JoinSuffixSerializer = new HostAnnouncement();
        mod.Networking.RegisterMessage<ViewMessage>();
        mod.Networking.RegisterMessage<DrawMessage>();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SendViewToHost(byte[] payload) => new ViewMessage(payload).SendToHost();

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void SendDrawToClient(Client client, byte[] payload) => new DrawMessage(payload).SendToClient(client);
}

/// <summary>
/// Every StationGod version may join every other: what the two can do together is settled by the view protocol in
/// the announcement and in each payload, never by refusing the join.
/// </summary>
public sealed class AnyVersion : IVersionValidator
{
    public bool ValidateVersion(string version) => true;
}

/// <summary>
/// The server's announcement in each joining player's join data: its view protocol and StationGod version. A client
/// reads it into its HostLink; a client without StationGod skips the section (LaunchPadBooster logs it once).
/// </summary>
public sealed class HostAnnouncement : IJoinSuffixSerializer
{
    private const int MaximumBytes = 1024;

    public void SerializeJoinSuffix(RocketBinaryWriter writer)
    {
        byte[] payload = Announcement.Encode(new Announcement(ViewProtocol.Current, StationGodMod.Version));
        Envelope.Write(writer, payload);
    }

    public void DeserializeJoinSuffix(RocketBinaryReader reader)
    {
        // Join data: an exception here would fail the join, so nothing escapes.
        try
        {
            byte[]? payload = Envelope.Read(reader, MaximumBytes);
            Announcement? announcement = payload != null ? Announcement.Decode(payload) : null;
            if (announcement == null)
            {
                StationGodMod.LogWarning("The server's StationGod announcement did not read; this game sends no view.");
                return;
            }

            ViewReporter.Announced(announcement);
        }
        catch (Exception exception)
        {
            StationGodMod.LogWarning($"The server's StationGod announcement did not read: {exception.Message}");
        }
    }
}

/// <summary>A payload in a message: its byte count, then the bytes.</summary>
internal static class Envelope
{
    internal static void Write(RocketBinaryWriter writer, byte[] payload)
    {
        writer.WriteInt32(payload.Length);
        writer.WriteBytes(payload, payload.Length);
    }

    /// <summary>The payload, or null when its count is negative or past the cap. A short read throws.</summary>
    internal static byte[]? Read(RocketBinaryReader reader, int maximum)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > maximum)
        {
            return null;
        }

        byte[] payload = new byte[length];
        reader.ReadBytes(payload, length);
        return payload;
    }
}

/// <summary>A client's view, to the server. Read and handled on the main thread (LaunchPadBooster's receive).</summary>
public sealed class ViewMessage : INetworkMessage
{
    private const int MaximumBytes = 4096;

    private byte[] _payload = Array.Empty<byte>();
    private bool _readable;

    public ViewMessage()
    {
    }

    internal ViewMessage(byte[] payload)
    {
        _payload = payload;
        _readable = true;
    }

    public void Serialize(RocketBinaryWriter writer) => Envelope.Write(writer, _payload);

    public void Deserialize(RocketBinaryReader reader)
    {
        // LaunchPadBooster's receive has no try: nothing may escape a message.
        try
        {
            byte[]? payload = Envelope.Read(reader, MaximumBytes);
            _readable = payload != null;
            _payload = payload ?? Array.Empty<byte>();
        }
        catch (Exception exception)
        {
            _readable = false;
            OnceLog.Warning("read_view", $"A player's view did not read: {exception.Message}");
        }
    }

    public void Process(long clientId)
    {
        try
        {
            if (NetworkManager.IsServer)
            {
                RemoteViews.Receive(clientId, _readable ? _payload : Array.Empty<byte>());
            }
        }
        catch (Exception exception)
        {
            OnceLog.Warning("process_view", $"A player's view could not be kept: {exception.Message}");
        }
    }
}

/// <summary>The server's drawings for this player, to its client. Read and handled on the main thread.</summary>
public sealed class DrawMessage : INetworkMessage
{
    private byte[] _payload = Array.Empty<byte>();
    private bool _readable;

    public DrawMessage()
    {
    }

    internal DrawMessage(byte[] payload)
    {
        _payload = payload;
        _readable = true;
    }

    public void Serialize(RocketBinaryWriter writer) => Envelope.Write(writer, _payload);

    public void Deserialize(RocketBinaryReader reader)
    {
        try
        {
            byte[]? payload = Envelope.Read(reader, StationGodNet.MaximumPayload);
            _readable = payload != null;
            _payload = payload ?? Array.Empty<byte>();
        }
        catch (Exception exception)
        {
            _readable = false;
            OnceLog.Warning("read_draw", $"The server's drawing did not read: {exception.Message}");
        }
    }

    public void Process(long clientId)
    {
        try
        {
            if (NetworkManager.IsClient && _readable)
            {
                RemoteDrawing.Receive(_payload);
            }
        }
        catch (Exception exception)
        {
            OnceLog.Warning("process_draw", $"The server's drawing could not be shown: {exception.Message}");
        }
    }
}
