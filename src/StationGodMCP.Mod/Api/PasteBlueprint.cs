#nullable enable

using System;
using System.Collections;
using System.IO;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Networking;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using UnityEngine;

namespace StationGodMCP.Api;

/// <summary>
/// paste_blueprint: paste a BlueprintMod blueprint at an explicit anchor and rotation, with no player. Console bppaste
/// takes both from the local player, which a dedicated server does not have; this makes the call the D.B.P.U. makes
/// (CODE BlueprintMod 1.8.0): BlueprintSerializer.Load(file), then BlueprintCommands.StartStaggeredPaste(data, anchor,
/// data.CopyYAngle, rotation). StartStaggeredPaste starts a coroutine on BlueprintMod's own behaviour, which this
/// handler may do because every request runs on the main thread (StationGodRequestDispatcher, from the mod's Update).
/// The pieces are then placed over 2 to 30 s; status reads the operation object kept from ActivePaste right after
/// the start, so its counts survive the paste's end. undo is BlueprintCommands.Undo, the bpundo console command,
/// whose console output a dedicated server does not show. Every BlueprintMod member comes from GameMembers. Host only.
/// </summary>
internal static class PasteBlueprintApi
{
    private const string ModMissingCode = "mod_missing";
    private const string PasteRefusedCode = "paste_refused";
    private const string BlueprintFailedCode = "blueprint_failed";
    private const int AnchorAxes = 3;

    private static TrackedPaste? _last;

    internal static object Handle(Args args)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host pastes blueprints.");
        }

        RequireBlueprintMod();
        if (args.OptionalBool("status") == true)
        {
            args.Reject("status", "name", "anchor", "rotation", "undo");
            return Status();
        }

        if (args.OptionalBool("undo") == true)
        {
            args.Reject("undo", "name", "anchor", "rotation", "status");
            return new BlueprintUndoView(
                Call(GameMembers.BlueprintUndo, new object?[] { Array.Empty<string>() }) as string ?? string.Empty);
        }

        return Paste(args);
    }

    private static BlueprintPasteStartedView Paste(Args args)
    {
        string path = PathOf(args.String("name"));
        Metres anchor = BuildArgs.PositionOf(
            args.Optional("anchor") ?? throw ApiErrors.InvalidArgument("Argument 'anchor' is required."), "anchor");
        int degrees = args.OptionalInt("rotation", 0, 270) ?? 0;
        PasteRotation rotation = PasteRotation.FromDegrees(degrees) ??
                                 throw ApiErrors.InvalidArgument("Argument 'rotation' must be 0, 90, 180 or 270.");

        object data = Call(GameMembers.BlueprintLoad, new object?[] { path }) ??
                      throw ApiErrors.Refused(BlueprintFailedCode, $"BlueprintMod read nothing from '{path}'.");
        float copyYAngle = (float)GameMembers.BlueprintCopyYAngle.GetValue(data);
        int entries = GameMembers.BlueprintEntries.GetValue(data) is ICollection list ? list.Count : 0;
        if (entries == 0)
        {
            throw ApiErrors.InvalidArgument($"The blueprint '{path}' has no entries.");
        }

        Vector3 position = new Vector3((float)anchor.X, (float)anchor.Y, (float)anchor.Z);
        object? refusal = Call(GameMembers.BlueprintStartPaste,
            new object?[] { data, position, copyYAngle, (float)rotation.Degrees });
        if (refusal is string message)
        {
            throw ApiErrors.Refused(PasteRefusedCode, message);
        }

        BlueprintFileView file = new BlueprintFileView(path, entries);
        _last = new TrackedPaste(file, ActivePaste());
        return new BlueprintPasteStartedView(file, new[] { anchor.X, anchor.Y, anchor.Z }, rotation.Degrees,
            copyYAngle, BlueprintFiles.ExpectedDurationSeconds(entries));
    }

    private static BlueprintPasteStatusView Status()
    {
        object? active = ActivePaste();
        TrackedPaste? last = _last;
        if (last == null)
        {
            return BlueprintPasteStatusView.Unknown(active != null);
        }

        object? operation = last.Operation;
        bool ours = operation != null && ReferenceEquals(active, operation);
        return BlueprintPasteStatusView.Of(last.File, ours, operation == null ? null : Counts(operation),
            active != null && !ours);
    }

    private static BlueprintPasteCountsView Counts(object operation) =>
        new BlueprintPasteCountsView(
            GameMembers.PasteComplete.GetValue(operation) is bool complete && complete,
            GameMembers.PasteCancelled.GetValue(operation) is bool cancelled && cancelled,
            GameMembers.PasteCreated.GetValue(operation) is int created ? created : 0,
            GameMembers.PasteFailed.GetValue(operation) is int failed ? failed : 0,
            GameMembers.PasteSkipped.GetValue(operation) is int skipped ? skipped : 0,
            GameMembers.PasteThings.GetValue(operation) is ICollection things ? things.Count : 0,
            GameMembers.PasteFingerprint.GetValue(operation) as string);

    private static object? ActivePaste() => Call(GameMembers.BlueprintActivePaste, Array.Empty<object?>());

    private static string PathOf(string name)
    {
        string? resolved = BlueprintFiles.Resolve(name, GameMembers.BlueprintDirectory.GetValue(null) as string);
        if (resolved == null)
        {
            throw ApiErrors.Refused(ModMissingCode,
                "BlueprintMod has not set up its Blueprints folder yet; pass an absolute path.");
        }

        string full;
        try
        {
            full = Path.GetFullPath(resolved);
        }
        catch (Exception error) when (error is ArgumentException || error is NotSupportedException ||
                                      error is PathTooLongException)
        {
            throw ApiErrors.InvalidArgument($"'{resolved}' is not a usable file path: {error.Message}");
        }

        if (!File.Exists(full))
        {
            throw ApiErrors.InvalidArgument($"No blueprint file at '{full}'.");
        }

        return full;
    }

    /// <summary>
    /// Refuses before anything is read or started: mod_missing when BlueprintMod is not loaded, game_changed naming
    /// the first member it no longer has.
    /// </summary>
    private static void RequireBlueprintMod()
    {
        if (GameMembers.BlueprintCommands.OrNull == null)
        {
            throw ApiErrors.Refused(ModMissingCode, "BlueprintMod is not loaded.");
        }

        GameMember[] needed =
        {
            GameMembers.BlueprintSerializer, GameMembers.BlueprintModPlugin, GameMembers.BlueprintData,
            GameMembers.BlueprintPasteOperation, GameMembers.BlueprintStartPaste, GameMembers.BlueprintUndo,
            GameMembers.BlueprintActivePaste, GameMembers.BlueprintLoad, GameMembers.BlueprintDirectory,
            GameMembers.BlueprintCopyYAngle, GameMembers.BlueprintEntries, GameMembers.PasteThings,
            GameMembers.PasteCreated, GameMembers.PasteFailed, GameMembers.PasteSkipped, GameMembers.PasteFingerprint,
            GameMembers.PasteCancelled, GameMembers.PasteComplete
        };
        foreach (GameMember member in needed)
        {
            if (!member.TryResolve())
            {
                throw new GameChangedException(member.Name);
            }
        }
    }

    /// <summary>
    /// One BlueprintMod call. What it throws comes back as blueprint_failed with the exception's own type and message,
    /// not reflection's wrapper.
    /// </summary>
    private static object? Call(GameMethod method, object?[] arguments)
    {
        try
        {
            return method.Invoke(null, arguments);
        }
        catch (TargetInvocationException wrapper) when (wrapper.InnerException != null)
        {
            Exception error = wrapper.InnerException;
            StationGodMod.LogWarning($"{method.Name} threw: {error}");
            throw ApiErrors.Refused(BlueprintFailedCode,
                $"{method.Name} threw {error.GetType().FullName}: {error.Message}");
        }
    }

    /// <summary>A paste this tool started: its file, and BlueprintMod's operation object if one was left.</summary>
    private sealed class TrackedPaste
    {
        internal TrackedPaste(BlueprintFileView file, object? operation)
        {
            File = file;
            Operation = operation;
        }

        internal BlueprintFileView File { get; }

        internal object? Operation { get; }
    }
}
