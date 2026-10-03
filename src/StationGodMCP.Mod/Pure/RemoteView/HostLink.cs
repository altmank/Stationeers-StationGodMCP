#nullable enable

namespace StationGodMCP.Pure.RemoteView;

/// <summary>
/// What a server running StationGod tells each joining client: its remote-view protocol and its StationGod version.
/// Its layout (protocol byte, version text) is the same under every protocol, so any StationGod can read who sent it.
/// </summary>
internal sealed class Announcement
{
    internal Announcement(int protocol, string version)
    {
        Protocol = protocol;
        Version = version;
    }

    internal int Protocol { get; }

    internal string Version { get; }

    internal static byte[] Encode(Announcement announcement)
    {
        WireWriter writer = new WireWriter();
        writer.Byte((byte)announcement.Protocol);
        writer.Text(announcement.Version);
        return writer.ToArray();
    }

    /// <summary>The announcement, or null when the bytes do not read as one.</summary>
    internal static Announcement? Decode(byte[] bytes)
    {
        WireReader reader = new WireReader(bytes);
        byte protocol = reader.Byte();
        string? version = reader.Text();
        return reader.Complete && version != null ? new Announcement(protocol, version) : null;
    }
}

/// <summary>
/// What a client knows of the server it plays on. A client sends nothing until the server has announced a StationGod
/// that speaks its protocol: a server without StationGod, or with one that speaks another protocol, would log a warning
/// for every message it cannot read. Unannounced until the join data says otherwise, and again once the client leaves.
/// </summary>
internal abstract class HostLink
{
    private HostLink()
    {
    }

    internal static readonly HostLink None = new Unannounced();

    /// <summary>Whether this client may send its view to the server.</summary>
    internal abstract bool MaySend { get; }

    /// <summary>The link an announcement makes.</summary>
    internal static HostLink From(Announcement announcement) =>
        announcement.Protocol == ViewProtocol.Current
            ? new Compatible(announcement.Version)
            : new Incompatible(announcement.Protocol, announcement.Version);

    /// <summary>No announcement: the server runs no StationGod that announces, or the client has not joined.</summary>
    internal sealed class Unannounced : HostLink
    {
        internal override bool MaySend => false;
    }

    internal sealed class Compatible : HostLink
    {
        internal Compatible(string version)
        {
            Version = version;
        }

        internal string Version { get; }

        internal override bool MaySend => true;
    }

    internal sealed class Incompatible : HostLink
    {
        internal Incompatible(int protocol, string version)
        {
            Protocol = protocol;
            Version = version;
        }

        internal int Protocol { get; }

        internal string Version { get; }

        internal override bool MaySend => false;

        /// <summary>The one log line a client writes for it.</summary>
        internal string Describe(string ownVersion) =>
            $"The server runs StationGod {Version} (view protocol {Protocol}); this game's StationGod {ownVersion} " +
            $"speaks protocol {ViewProtocol.Current}, so it does not send its view and the server's camera tools " +
            "refuse for this player. Run the same StationGod version on both.";
    }
}
