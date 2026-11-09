#nullable enable

using System;
using System.IO;
using Assets.Scripts;
using Assets.Scripts.Objects;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Shared.Game.Show;
using StationGodMCP.Pure.Facts;
using StationGodMCP.Pure.Pictures;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;
using UnityEngine;
using UnityEngine.Rendering;

namespace StationGodMCP.Verbs;

/// <summary>
/// show picture (catalogue show.json): a PNG of a thing, or the player, from a temporary camera placed by yaw, pitch
/// and distance about the thing's centre, written under the pictures folder ([Pictures] Folder). Refused not_rendered
/// in a game that draws nothing, bad_path for a name or folder that is not plain or leaves the pictures folder,
/// file_exists unless overwrite, busy while another picture is being taken. Read only: nothing in the world changes and
/// nothing is synced; the file is the only thing written.
/// </summary>
[ReplyFact(FactRule.Live, "the thing pictured and the camera that pictured it")]
internal static class ShowPicture
{
    private const string Label = "show picture";
    private const string Player = "player";

    internal sealed class Call
    {
        private Call(ShowPictureOptions options, OptionReader given)
        {
            Options = options;
            Given = given;
        }

        internal ShowPictureOptions Options { get; }

        internal OptionReader Given { get; }

        internal static Call Bind(OptionReader given) => new Call(ShowPictureOptions.Bind(given), given);
    }

    internal static VerbOutcome Run(Call call)
    {
        if (GameManager.IsBatchMode || SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
        {
            throw ApiErrors.Refused("not_rendered",
                $"{Label}: this game draws nothing (-batchmode, -nographics or a dedicated server); run it with graphics.");
        }

        ShowPictureOptions options = call.Options;
        string? path = PicturePaths.Resolve(PictureSettings.Folder, options.Folder, call.Given.Text("file"),
            out string? problem);
        if (path == null)
        {
            throw ApiErrors.Refused(PicturePaths.BadPath, $"{Label}: {problem}");
        }

        if (PicturePaths.FirstReparsePoint(PictureSettings.Folder, path, IsReparsePoint) is { } link)
        {
            throw ApiErrors.Refused(PicturePaths.BadPath,
                $"{Label}: {link} is a junction or symbolic link; pictures are written only through plain folders.");
        }

        bool overwrite = options.Overwrite == true;
        if (!overwrite && File.Exists(path))
        {
            throw ApiErrors.Refused("file_exists", $"{Label}: {path} already exists; give overwrite: true to replace it.");
        }

        string id = call.Given.Text("id") ?? Player;
        Thing target = string.Equals(id.Trim(), Player, StringComparison.OrdinalIgnoreCase)
            ? PlayerOrigin.RequireHuman()
            : TargetIds.Thing(id, Label);
        PictureRequest request = new PictureRequest(target, path, overwrite, (int)(options.Width ?? 1920),
            (int)(options.Height ?? 1080), options.Yaw ?? 0, options.Pitch ?? 10, options.Distance, options.Fov ?? 40,
            string.Equals(options.Lighting, "neutral", StringComparison.Ordinal));
        PictureShot shot = PictureShot.Start(request);
        return shot.Step(CurrentFrame.EndMs) ? VerbOutcome.Of(shot.Result()) : new VerbOutcome.Pending(shot);
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.Exists(path) || Directory.Exists(path)) &&
                   (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            return true;
        }
    }
}
