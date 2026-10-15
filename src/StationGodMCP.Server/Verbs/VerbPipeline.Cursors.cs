using System.Text.Json.Nodes;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// Main lists on the script and program faces (hub.md Faces, D3): the first page is the mod's first page, asked for
/// the mod's walk cursor (cursor: "open") where the what offers one; when more remain and the mod kept a walk
/// (cursor_id), the reply carries cursor (a hub cursor) and main (the main list's key), and MoreAsync gives the next
/// entries on the mod's walk cursor. Every paged list keeps one (cost design A4); a reply with more but no walk names
/// the rest unreachable rather than paging the call again by offset.
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

        if (JsonNodes.Text(page["cursor_id"]) is not { } walk)
        {
            // No walk kept: the rest cannot be paged to.
            page["rest_unreachable"] = $"{what.Label} answers one page and keeps no walk; narrow the call for the rest.";
            return reply;
        }

        HubCursor cursor = _cursors.Open(call.Stream, what, (JsonObject)options.DeepClone(), what.MainList!, walk,
            Shape(call.Face, what, options).Shape);
        page["cursor"] = cursor.Id;
        page["main"] = cursor.Main;
        return reply;
    }

    /// <summary>
    /// The next entries of a script or program reply's main list: {entries, cursor?}; no cursor once the list is done
    /// (the hub cursor is freed then), on the mod's walk cursor. An unknown, freed, evicted, idle or other stream's
    /// cursor answers cursor_expired.
    /// </summary>
    public async Task<VerbReply> MoreAsync(long stream, string cursorId, int? limit, CancellationToken cancellation)
    {
        HubCursor? cursor = _cursors.Get(stream, cursorId);
        if (cursor == null)
        {
            return new VerbReply.Failed("cursor_expired",
                $"No cursor '{cursorId}' on this stream: it ended, the world changed, the list was done, or it was " +
                "dropped (idle too long, or the oldest of too many). Start the walk again.",
                data: new JsonObject { ["cursor"] = cursorId });
        }

        JsonObject options = (JsonObject)cursor.Options.DeepClone();
        options["cursor"] = cursor.ModCursor;

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
