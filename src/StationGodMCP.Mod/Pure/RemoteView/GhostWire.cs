#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure.RemoteView;

/// <summary>One ghost a player asks to place: the prefab, its build state, where, the turn, the tint and a label.</summary>
internal sealed class GhostPlacement
{
    internal GhostPlacement(int prefabHash, int buildState, Vec3 position, Vec3 euler, Rgba tint, string? label)
    {
        PrefabHash = prefabHash;
        BuildState = buildState;
        Position = position;
        Euler = euler;
        Tint = tint;
        Label = label;
    }

    internal int PrefabHash { get; }

    internal int BuildState { get; }

    internal Vec3 Position { get; }

    internal Vec3 Euler { get; }

    internal Rgba Tint { get; }

    internal string? Label { get; }
}

/// <summary>What a client's player asks of the host's ghost board: ghosts to place, or ghosts (by id) to take off.</summary>
internal abstract class GhostRequest
{
    private GhostRequest()
    {
    }

    internal sealed class Place : GhostRequest
    {
        internal Place(IReadOnlyList<GhostPlacement> ghosts)
        {
            Ghosts = ghosts;
        }

        internal IReadOnlyList<GhostPlacement> Ghosts { get; }
    }

    internal sealed class Remove : GhostRequest
    {
        internal Remove(IReadOnlyList<long> ids)
        {
            Ids = ids;
        }

        internal IReadOnlyList<long> Ids { get; }
    }
}

/// <summary>
/// A GhostRequest's bytes, protocol 4: protocol (1), kind (1: 1 place, 2 remove), count (4), then each placement as
/// prefab hash (4), build state (4), position (12), Euler degrees (12), tint (16), label (text), or each id (8).
/// Counts above the caps are refused on both ends.
/// </summary>
internal static class GhostWire
{
    internal const int MaximumPlacements = 64;
    internal const int MaximumIds = 256;

    private const byte PlaceKind = 1;
    private const byte RemoveKind = 2;

    internal static byte[] Encode(GhostRequest request)
    {
        WireWriter writer = new WireWriter();
        writer.Byte(ViewProtocol.Current);
        switch (request)
        {
            case GhostRequest.Place place:
                writer.Byte(PlaceKind);
                writer.Int32(place.Ghosts.Count);
                foreach (GhostPlacement ghost in place.Ghosts)
                {
                    writer.Int32(ghost.PrefabHash);
                    writer.Int32(ghost.BuildState);
                    writer.Vector(ghost.Position);
                    writer.Vector(ghost.Euler);
                    writer.Single(ghost.Tint.Red);
                    writer.Single(ghost.Tint.Green);
                    writer.Single(ghost.Tint.Blue);
                    writer.Single(ghost.Tint.Alpha);
                    writer.Text(ghost.Label);
                }

                break;
            case GhostRequest.Remove remove:
                writer.Byte(RemoveKind);
                writer.Int32(remove.Ids.Count);
                foreach (long id in remove.Ids)
                {
                    writer.Int64(id);
                }

                break;
            default:
                throw new System.InvalidOperationException($"No wire form for {request.GetType().Name}.");
        }

        return writer.ToArray();
    }

    internal static WireRead<GhostRequest> Decode(byte[] bytes)
    {
        WireReader reader = new WireReader(bytes);
        byte protocol = reader.Byte();
        if (reader.Failed)
        {
            return new WireRead<GhostRequest>.Malformed("empty");
        }

        if (protocol != ViewProtocol.Current)
        {
            return new WireRead<GhostRequest>.OtherProtocol(protocol);
        }

        byte kind = reader.Byte();
        int count = reader.Int32();
        GhostRequest? request = null;
        if (kind == PlaceKind && count >= 0 && count <= MaximumPlacements)
        {
            List<GhostPlacement> ghosts = new List<GhostPlacement>(count);
            for (int index = 0; index < count && !reader.Failed; index++)
            {
                ghosts.Add(new GhostPlacement(reader.Int32(), reader.Int32(), reader.Vector(), reader.Vector(),
                    new Rgba(reader.Single(), reader.Single(), reader.Single(), reader.Single()), reader.Text()));
            }

            request = new GhostRequest.Place(ghosts);
        }
        else if (kind == RemoveKind && count >= 0 && count <= MaximumIds)
        {
            List<long> ids = new List<long>(count);
            for (int index = 0; index < count && !reader.Failed; index++)
            {
                ids.Add(reader.Int64());
            }

            request = new GhostRequest.Remove(ids);
        }

        if (request == null)
        {
            return new WireRead<GhostRequest>.Malformed($"kind {kind} with {count} entries");
        }

        return reader.Complete
            ? new WireRead<GhostRequest>.Read(request)
            : new WireRead<GhostRequest>.Malformed(reader.Failed ? "the payload ends early"
                : $"{reader.Remaining} bytes past its end");
    }
}
