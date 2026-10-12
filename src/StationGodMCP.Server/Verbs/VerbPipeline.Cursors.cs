using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// Main lists on the script and program faces (hub.md Faces, D3): the first page is the handler's first page; when
/// more remain the reply carries cursor (a hub cursor) and main (the main list's key), and MoreAsync gives the next
/// entries, by the mod's walk cursor where the handler walks (find_things), else by offset pages.
/// </summary>
public sealed partial class VerbPipeline
{
    private const string WalkHandler = "find_things";

    private async Task<VerbReply> FirstPageAsync(VerbCall call, WhatEntry what, JsonObject options, HandlerCall first,
        CancellationToken cancellation)
    {
        bool walks = first.Handler == WalkHandler && first.Args["offset"] == null;
        if (walks)
        {
            first.Args["cursor"] = true;
        }

        VerbReply reply = await RunCallAsync(first, call, what, options, null, cancellation).ConfigureAwait(false);
        if (reply is not VerbReply.Done { Reply: JsonObject page } || page[what.MainList!] is not JsonArray entries)
        {
            return reply;
        }

        HubCursor? cursor = null;
        if (walks && JsonNodes.Text(page["cursor_id"]) is { } walk)
        {
            cursor = _cursors.Open(call.Stream, what, first, what.MainList!);
            cursor.WalkCursor = walk;
            page.Remove("cursor_id");
        }
        else if (JsonNodes.IsTrue(page["has_more"]) && _schemas.Arg(first.Handler, "offset") != null)
        {
            cursor = _cursors.Open(call.Stream, what, first, what.MainList!);
            cursor.NextOffset = (int)(JsonNodes.Number(page["offset"]) ?? JsonNodes.Number(first.Args["offset"]) ?? 0) +
                                entries.Count;
        }

        if (cursor != null)
        {
            page["cursor"] = cursor.Id;
            page["main"] = cursor.Main;
        }

        return reply;
    }

    /// <summary>
    /// The next entries of a script or program reply's main list: {entries, cursor?}; no cursor once the list is done
    /// (the hub cursor is freed then). An unknown, freed or other stream's cursor answers cursor_expired.
    /// </summary>
    public async Task<VerbReply> MoreAsync(long stream, string cursorId, int? limit, CancellationToken cancellation)
    {
        HubCursor? cursor = _cursors.Get(stream, cursorId);
        if (cursor == null)
        {
            return new VerbReply.Failed("cursor_expired",
                $"No cursor '{cursorId}' on this stream: it ended, the world changed, or the list was done. Start the walk again.",
                data: new JsonObject { ["cursor"] = cursorId });
        }

        JsonObject args;
        if (cursor.WalkCursor != null)
        {
            args = new JsonObject { ["cursor_id"] = cursor.WalkCursor };
        }
        else
        {
            args = (JsonObject)cursor.Call.Args.DeepClone();
            args["offset"] = cursor.NextOffset;
        }

        if (limit is { } most)
        {
            args["limit"] = most;
        }

        GameReply reply = await SendAsync(new HandlerCall(cursor.Call.Handler, args, cursor.Call.Rename), stream, null,
            cancellation).ConfigureAwait(false);
        if (reply is not GameReply.Ok ok)
        {
            _cursors.Free(cursor.Id);
            return Failure(reply);
        }

        JsonNode? page = JsonNodes.FromElement(ok.Result);
        JsonArray entries = HubCursors.Entries(page, cursor.Main);
        bool more;
        if (cursor.WalkCursor != null)
        {
            string? next = JsonNodes.Text(page?["cursor_id"]);
            more = next != null;
            cursor.WalkCursor = next;
        }
        else
        {
            more = JsonNodes.IsTrue(page?["has_more"]) && entries.Count > 0;
            cursor.NextOffset += entries.Count;
        }

        JsonObject answer = new() { ["entries"] = entries };
        if (more)
        {
            answer["cursor"] = cursor.Id;
        }
        else
        {
            _cursors.Free(cursor.Id);
        }

        return new VerbReply.Done(answer);
    }
}
