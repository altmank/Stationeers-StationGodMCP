#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Entities;
using Assets.Scripts.Serialization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.LatticeLink;
using StationGodMCP.Net;
using StationGodMCP.Pure;
using StationGodMCP.Pure.RemoteView;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The world's ghost board (GhostBoard): structures planned but not built, placed by players in their game (ghost mode
/// with a kit in hand, or the ghost menu) or by an agent (show ghost), and seen by agents (find ghost) to build from.
/// The host keeps it: it reads the save's stationgod-ghosts.json once the world runs, writes it again a second after
/// each change, draws every ghost in its own game (Ghosts' board layer) and keeps each StationGod client's copy in step
/// (DrawWire's board: the whole board when a client's first view arrives, then each change). A client's player asks
/// the host to place or take off ghosts (GhostWire); a client's board is only the host's, mirrored. A structure built
/// where a ghost of its prefab and turn stands takes that ghost off. The board goes with the world (WorldStores).
/// With Lattice installed the ghosts are Lattice's (LatticeBoard): every change is a Lattice command, Lattice saves,
/// syncs and matches builds, and the board here is the mirror of Lattice's build ghosts (ids are Lattice's ghost ids),
/// still sent to StationGod clients without Lattice. The file is then only read once, to move its ghosts into Lattice.
/// </summary>
internal static class GhostBoardStore
{
    internal const string FileName = "stationgod-ghosts.json";

    private const float SaveAfterS = 1f;

    private static GhostBoard _board = new GhostBoard();
    private static bool _loaded;

    // The file the board was read from, kept so a write after the world changed still goes to its own save.
    private static string? _file;
    private static float _saveAt = float.PositiveInfinity;

    /// <summary>The board: the host's own, or on a client the host's as last sent.</summary>
    internal static GhostBoard Board => _board;

    /// <summary>Whether this game keeps the board (the host, single player included) rather than mirroring it.</summary>
    internal static bool Keeps => NetworkManager.IsServer;

    /// <summary>The save's board file (it may not exist), in the loaded world's save folder; null with none.</summary>
    internal static string? SaveFile => SaveFolders.FileIn(FileName);

    /// <summary>Every frame: on the host, reads the board once the world runs and writes it after changes.</summary>
    internal static void Tick()
    {
        if (GameManager.GameState == GameState.Running)
        {
            LatticePresence.Tick();
        }

        // With Lattice loaded its board is the world's, even before Lattice publishes the world's system.
        if (!Keeps || GameManager.GameState != GameState.Running || LatticePresence.ApiLoaded)
        {
            return;
        }

        if (!_loaded)
        {
            _loaded = true;
            _file = SaveFile;
            Load();
        }

        if (Time.realtimeSinceStartup >= _saveAt)
        {
            _saveAt = float.PositiveInfinity;
            Save();
        }
    }

    /// <summary>
    /// The host places ghosts: each prefab must be a structure; refusals name the placement. The ghosts added, in
    /// order (a ghost on the spot of one of the same prefab and turn replaces it).
    /// </summary>
    internal static List<BoardGhost> Place(IReadOnlyList<GhostPlacement> placements, GhostSource source, string owner,
        List<string> refused, ulong? client = null)
    {
        if (LatticePresence.Running)
        {
            return LatticePresence.Place(placements, source, client, refused, refused);
        }

        List<BoardGhost> added = new List<BoardGhost>();
        List<long> removed = new List<long>();
        double now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        for (int index = 0; index < placements.Count; index++)
        {
            GhostPlacement placement = placements[index];
            if (!(Prefab.Find(placement.PrefabHash) is Structure prefab) || prefab == null)
            {
                refused.Add($"{index}: no structure prefab with hash {placement.PrefabHash}");
                continue;
            }

            int last = prefab.BuildStates != null && prefab.BuildStates.Count > 0 ? prefab.BuildStates.Count - 1 : 0;
            int state = Mathf.Clamp(placement.BuildState, 0, last);
            BoardGhost? ghost = _board.Add(new BoardGhost(0, prefab.PrefabHash, prefab.PrefabName, state,
                placement.Position, placement.Euler, placement.Tint, source, owner, placement.Label, now),
                out BoardGhost? replaced);
            if (ghost == null)
            {
                refused.Add($"{index}: the board is full ({GhostBoard.Maximum} ghosts)");
                continue;
            }

            if (replaced != null)
            {
                removed.Add(replaced.Id);
                added.RemoveAll(earlier => earlier.Id == replaced.Id);
            }

            added.Add(ghost);
        }

        Changed(added, removed);
        return added;
    }

    /// <summary>The host takes ghosts off by id; the ids it took off (an unknown id is skipped).</summary>
    internal static List<long> Remove(IEnumerable<long> ids, GhostSource source = GhostSource.Agent,
        ulong? client = null)
    {
        if (LatticePresence.Running)
        {
            List<string> refused = new List<string>();
            List<long> gone = LatticePresence.Remove(ids, source, client, refused);
            foreach (string refusal in refused)
            {
                StationGodMod.LogWarning($"Ghosts were not taken off: {refusal}.");
            }

            return gone;
        }

        List<long> removed = new List<long>();
        foreach (long id in ids)
        {
            if (_board.Remove(id))
            {
                removed.Add(id);
            }
        }

        Changed(new List<BoardGhost>(), removed);
        return removed;
    }

    /// <summary>The host takes off every ghost the rule picks; their ids.</summary>
    internal static List<long> RemoveWhere(Predicate<BoardGhost> rule)
    {
        if (LatticePresence.Running)
        {
            List<long> picked = new List<long>();
            foreach (BoardGhost ghost in _board.All)
            {
                if (rule(ghost))
                {
                    picked.Add(ghost.Id);
                }
            }

            return Remove(picked);
        }

        List<long> removed = _board.RemoveWhere(rule);
        Changed(new List<BoardGhost>(), removed);
        return removed;
    }

    /// <summary>
    /// A thing registered on the host: a structure built where a ghost of its prefab and turn stands takes it off.
    /// Cheap for every other thing (the board's prefab check first).
    /// </summary>
    internal static void Arrived(Thing thing)
    {
        if (!_loaded || LatticePresence.ApiLoaded || _board.Count == 0 || !(thing is Structure structure) || !_board.Holds(structure.PrefabHash))
        {
            return;
        }

        Quaternion rotation = structure.ThingTransformRotation;
        BoardGhost? built = _board.Built(structure.PrefabHash, Bodies.V(structure.ThingTransformPosition),
            Bodies.V(rotation.eulerAngles));
        if (built != null)
        {
            Changed(new List<BoardGhost>(), new List<long> { built.Id });
        }
    }

    /// <summary>This game's player places a ghost: on the host at once, from a client by asking the host.</summary>
    internal static string? RequestPlace(GhostPlacement placement)
    {
        if (LatticePresence.Running)
        {
            List<string> refused = new List<string>();
            LatticePresence.Place(new[] { placement }, GhostSource.Player, null, refused, new List<string>());
            return refused.Count > 0 ? refused[0] : null;
        }

        if (Keeps)
        {
            List<string> refused = new List<string>();
            Place(new[] { placement }, GhostSource.Player, LocalName(), refused);
            return refused.Count > 0 ? refused[0] : null;
        }

        if (!ViewReporter.HostListens)
        {
            return "the server runs no StationGod that keeps ghosts (2.9.0 or later)";
        }

        return StationGodNet.SendGhostRequest(GhostWire.Encode(new GhostRequest.Place(new[] { placement })))
            ? null
            : "the request could not be sent to the server";
    }

    /// <summary>This game's player takes a ghost off.</summary>
    internal static string? RequestRemove(long id)
    {
        if (LatticePresence.Running)
        {
            List<string> refused = new List<string>();
            List<long> gone = LatticePresence.Remove(new[] { id }, GhostSource.Player, null, refused);
            return refused.Count > 0 ? refused[0] : Keeps && gone.Count == 0 ? $"no ghost {id}" : null;
        }

        if (Keeps)
        {
            return Remove(new[] { id }).Count > 0 ? null : $"no ghost {id}";
        }

        if (!ViewReporter.HostListens)
        {
            return "the server runs no StationGod that keeps ghosts (2.9.0 or later)";
        }

        return StationGodNet.SendGhostRequest(GhostWire.Encode(new GhostRequest.Remove(new[] { id })))
            ? null
            : "the request could not be sent to the server";
    }

    /// <summary>The host's side of a client's request.</summary>
    internal static void Receive(long connectionId, byte[] payload)
    {
        switch (GhostWire.Decode(payload))
        {
            case WireRead<GhostRequest>.Read read:
                string owner = OwnerOf(connectionId);
                Client? sender = Client.Find(connectionId);
                ulong? client = sender != null ? sender.ClientId : (ulong?)null;
                switch (read.Value)
                {
                    case GhostRequest.Place place:
                        List<string> refused = new List<string>();
                        Place(place.Ghosts, GhostSource.Player, owner, refused, client);
                        foreach (string refusal in refused)
                        {
                            StationGodMod.LogWarning($"{owner}'s ghost was not placed: {refusal}.");
                        }

                        return;
                    case GhostRequest.Remove remove:
                        Remove(remove.Ids, GhostSource.Player, client);
                        return;
                }

                return;
            case WireRead<GhostRequest>.OtherProtocol other:
                OnceLog.Warning($"ghost_protocol:{connectionId}",
                    $"Ignoring {OwnerOf(connectionId)}'s ghosts: their StationGod speaks view protocol " +
                    $"{other.Protocol}, this one {ViewProtocol.Current}.");
                return;
            case WireRead<GhostRequest>.Malformed malformed:
                OnceLog.Warning($"ghost_malformed:{connectionId}",
                    $"A ghost request from {OwnerOf(connectionId)} did not read ({malformed.Reason}).");
                return;
        }
    }

    /// <summary>The host sends a client the whole board (its first view just arrived).</summary>
    internal static void SyncTo(long connectionId)
    {
        if (!Keeps)
        {
            return;
        }

        IReadOnlyList<BoardGhost> all = _board.All;
        int sent = 0;
        do
        {
            int count = Math.Min(DrawWire.MaximumBoardGhosts, all.Count - sent);
            List<BoardGhost> chunk = new List<BoardGhost>(count);
            for (int index = 0; index < count; index++)
            {
                chunk.Add(all[sent + index]);
            }

            StationGodNet.SendDraw(connectionId,
                DrawWire.Encode(new DrawCommand.Board(sent == 0, chunk, new List<long>())));
            sent += count;
        }
        while (sent < all.Count);
    }

    /// <summary>A client's side: the host's board, or a change to it.</summary>
    internal static void Mirror(DrawCommand.Board command)
    {
        if (LatticePresence.ApiLoaded)
        {
            // This client's Lattice brings the host's ghosts itself.
            return;
        }

        _board.Apply(command.Replace, command.Added, command.Removed);
        if (command.Replace)
        {
            Ghosts.ClearBoard();
        }

        foreach (long id in command.Removed)
        {
            Ghosts.RemoveBoard(id);
        }

        foreach (BoardGhost ghost in command.Added)
        {
            Ghosts.ShowBoard(ghost);
        }
    }

    /// <summary>The world changed: the board and its drawing go; the next world's is read when it runs.</summary>
    internal static void Forget()
    {
        LatticePresence.Forget();
        if (_loaded && _saveAt < float.PositiveInfinity)
        {
            Save();
        }

        _board = new GhostBoard();
        _loaded = false;
        _file = null;
        _saveAt = float.PositiveInfinity;
        Ghosts.ClearBoard();
    }

    /// <summary>
    /// Lattice's change, mirrored: the ghosts it added or changed (by Lattice's id), the ids it took off; reset first
    /// empties the board (Lattice attached for a world, or its board was replaced). Drawn here and sent to StationGod
    /// clients; Lattice saves it.
    /// </summary>
    internal static void FromLattice(bool reset, List<BoardGhost> added, List<long> removed)
    {
        _board.Apply(reset, added, removed);
        if (!reset)
        {
            Changed(added, removed);
            return;
        }

        Ghosts.ClearBoard();
        foreach (BoardGhost ghost in _board.All)
        {
            Ghosts.ShowBoard(ghost);
        }

        if (Keeps)
        {
            foreach (long connection in RemoteViews.Connections())
            {
                SyncTo(connection);
            }
        }
    }

    // A change: drawn in this game, sent to every StationGod client, saved a second later (by Lattice when it runs).
    private static void Changed(List<BoardGhost> added, List<long> removed)
    {
        if (added.Count == 0 && removed.Count == 0)
        {
            return;
        }

        foreach (long id in removed)
        {
            Ghosts.RemoveBoard(id);
        }

        foreach (BoardGhost ghost in added)
        {
            Ghosts.ShowBoard(ghost);
        }

        for (int start = 0; start < Math.Max(added.Count, 1); start += DrawWire.MaximumBoardGhosts)
        {
            int count = Math.Min(DrawWire.MaximumBoardGhosts, added.Count - start);
            List<BoardGhost> chunk = added.GetRange(start, Math.Max(count, 0));
            DrawCommand.Board command = new DrawCommand.Board(false, chunk, start == 0 ? removed : new List<long>());
            byte[] payload = DrawWire.Encode(command);
            foreach (long connection in RemoteViews.Connections())
            {
                StationGodNet.SendDraw(connection, payload);
            }
        }

        if (!LatticePresence.ApiLoaded)
        {
            _saveAt = Time.realtimeSinceStartup + SaveAfterS;
        }
    }

    private static void Load()
    {
        string? file = _file;
        if (file == null || !File.Exists(file))
        {
            return;
        }

        try
        {
            _board = GhostBoard.FromJson(JObject.Parse(File.ReadAllText(file)), out int skipped);
            if (skipped > 0)
            {
                StationGodMod.LogWarning($"{skipped} ghosts in {file} did not read and were left out.");
            }

            foreach (BoardGhost ghost in _board.All)
            {
                Ghosts.ShowBoard(ghost);
            }

            foreach (long connection in RemoteViews.Connections())
            {
                SyncTo(connection);
            }

            StationGodMod.Log($"Ghost board: {_board.Count} ghosts read from {file}.");
        }
        catch (Exception exception) when (exception is IOException || exception is JsonException ||
                                          exception is UnauthorizedAccessException)
        {
            StationGodMod.LogWarning($"The ghost board {file} could not be read: {exception.Message}");
        }
    }

    private static void Save()
    {
        string? file = _file;
        if (file == null)
        {
            return;
        }

        try
        {
            if (_board.Count == 0)
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }

                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            string temporary = file + ".tmp";
            File.WriteAllText(temporary, _board.ToJson().ToString(Formatting.Indented));
            if (File.Exists(file))
            {
                File.Delete(file);
            }

            File.Move(temporary, file);
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
        {
            StationGodMod.LogWarning($"The ghost board could not be written to {file}: {exception.Message}");
        }
    }

    private static string LocalName()
    {
        Human human = Human.LocalHuman;
        return human != null && !string.IsNullOrEmpty(human.DisplayName) ? human.DisplayName : "host";
    }

    private static string OwnerOf(long connectionId)
    {
        Client? client = Client.Find(connectionId);
        return client != null && !string.IsNullOrEmpty(client.name) ? client.name : $"connection {connectionId}";
    }
}
