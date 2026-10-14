using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// Main lists on the script and program faces (hub.md Faces, D3): the first page is the mod's first page, asked for
/// the mod's walk cursor (cursor: "open") where the what offers one; when more remain the reply carries cursor (a hub
/// cursor) and main (the main list's key), and MoreAsync gives the next entries on the mod's walk cursor where the
/// reply carried its cursor_id, by offset pages of the same verb call where it did not.
/// </summary>
public sealed partial class VerbPipeline
{
    /// <summary>The wire cursor's word on a first page (the mod's Paging.OpenCursor): keep the rest under a cursor.</summary>
    private const string OpenCursor = "open";

    private async Task<VerbReply> FirstPageAsync(VerbCall call, WhatEntry what, JsonObject options,
        CancellationToken cancellation)
    {
        JsonObject sent = options;
        if (what.WireCursor && options["cursor"] == null)
        {
            sent = (JsonObject)options.DeepClone();
            sent["cursor"] = OpenCursor;
        }

        VerbReply reply = await ModAsync(call, what, sent, cancellation).ConfigureAwait(false);
        if (reply is not VerbReply.Done { Reply: JsonObject page } || page[what.MainList!] is not JsonArray entries ||
            !JsonNodes.IsTrue(page["has_more"]))
        {
            return reply;
        }

        if (!what.Options.ContainsKey("offset"))
        {
            // No offset: the rest cannot be paged to.
            page["rest_unreachable"] = $"{what.Label} answers one page and takes no offset; narrow the call for the rest.";
            return reply;
        }

        HubCursor cursor = _cursors.Open(call.Stream, what, (JsonObject)options.DeepClone(), what.MainList!,
            Shape(options).Shape);
        cursor.NextOffset = (int)(JsonNodes.Number(page["offset"]) ?? JsonNodes.Number(options["offset"]) ?? 0) +
                            entries.Count;
        cursor.ModCursor = JsonNodes.Text(page["cursor_id"]);
        page["cursor"] = cursor.Id;
        page["main"] = cursor.Main;
        return reply;
    }

    /// <summary>
    /// The next entries of a script or program reply's main list: {entries, cursor?}; no cursor once the list is done
    /// (the hub cursor is freed then). Pages on the mod's walk cursor (cursor_id) where the first page opened one, and
    /// by offset otherwise. An unknown, freed or other stream's cursor answers cursor_expired.
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

        JsonObject options = (JsonObject)cursor.Options.DeepClone();
        if (cursor.ModCursor is { } walking)
        {
            options["cursor"] = walking;
        }
        else
        {
            options["offset"] = cursor.NextOffset;
        }

        if (limit is { } most)
        {
            options["limit"] = most;
        }

        GameReply reply = await SendAsync(cursor.What, options, stream, cursor.Shape, cancellation).ConfigureAwait(false);
        if (reply is not GameReply.Ok ok)
        {
            _cursors.Free(cursor.Id);
            return Failure(reply);
        }

        JsonNode? page = JsonNodes.FromElement(ok.Result);
        JsonArray entries = HubCursors.Entries(page, cursor.Main);
        cursor.NextOffset += entries.Count;
        JsonObject answer = new() { ["entries"] = entries };
        if (JsonNodes.IsTrue(page?["has_more"]) && entries.Count > 0)
        {
            cursor.ModCursor = JsonNodes.Text(page?["cursor_id"]) ?? cursor.ModCursor;
            answer["cursor"] = cursor.Id;
        }
        else
        {
            _cursors.Free(cursor.Id);
        }

        return new VerbReply.Done(answer);
    }
}
