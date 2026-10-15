#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Assets.Scripts;
using Assets.Scripts.Networking;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// The blueprint paste: BlueprintMod's own BlueprintSerializer.Load(file), then BlueprintCommands.StartStaggeredPaste(
/// data, anchor, data.CopyYAngle, turn), the call the D.B.P.U. makes (CODE BlueprintMod 1.8.0) instead of the console
/// bppaste, which takes its anchor and turn from a local player a dedicated server has not got. StartStaggeredPaste
/// starts a coroutine on BlueprintMod's own behaviour, which this service may do because every request runs on the main
/// thread (StationGodRequestDispatcher, from the mod's Update); the pieces are then placed over 2 to 30 s, and Status
/// reads the operation kept from the start, so its counts survive the paste's end. Undo is BlueprintCommands.Undo, the
/// bpundo console command, whose console output a dedicated server does not show.
/// <para>
/// A report (Describe) reads the loaded file's own pieces through GameMembers and starts nothing, so it never touches
/// BlueprintMod's parse or paste geometry; BlueprintMod's own checks (the same blueprint already there, DeanamicMatter
/// in survival) run only inside the start, so a report cannot show them. Host only, with BlueprintMod.
/// </para>
/// </summary>
internal static class BlueprintPastes
{
    private const string ModMissingCode = "mod_missing";
    private const string BlueprintFailedCode = "blueprint_failed";
    private const double DegreesToRadians = Math.PI / 180.0;

    private static TrackedPaste? _last;

    /// <summary>The world was left: status no longer reports the last world's paste.</summary>
    internal static void ForgetWorld() => _last = null;

    /// <summary>BlueprintMod's own undo of the newest paste (undo id "paste"), with its own message.</summary>
    internal static BlueprintUndoView Undo()
    {
        RequireHost();
        RequireBlueprintMod();
        return new BlueprintUndoView(
            Call(GameMembers.BlueprintUndo, new object?[] { Array.Empty<string>() }) as string ?? string.Empty);
    }

    /// <summary>
    /// Every check a paste passes before BlueprintMod starts it: host, BlueprintMod, the file it reads and the pieces
    /// that file holds, and no paste still placing. Nothing is started and nothing changes.
    /// </summary>
    internal static PastePlan Check(PasteOrder order)
    {
        RequireHost();
        RequireBlueprintMod();
        string path = PathOf(order.Blueprint);
        object data = Call(GameMembers.BlueprintLoad, new object?[] { path }) ??
                      throw ApiErrors.Refused(BlueprintFailedCode, $"BlueprintMod read nothing from '{path}'.");
        float copyYAngle = (float)GameMembers.BlueprintCopyYAngle.GetValue(data);
        ICollection entries = GameMembers.BlueprintEntries.GetValue(data) as ICollection ?? Array.Empty<object>();
        if (entries.Count == 0)
        {
            throw ApiErrors.InvalidArgument($"The blueprint '{path}' has no pieces.");
        }

        RefuseWhilePlacing();
        return new PastePlan(data, new BlueprintFileView(path, entries.Count), copyYAngle, entries);
    }

    /// <summary>
    /// The paste itself: BlueprintMod starts its coroutine and the reply is what it took and how long it takes. A
    /// refusal of its own (the same blueprint already there, not enough DeanamicMatter) comes back as paste_refused.
    /// </summary>
    internal static VerbOutcome Start(PastePlan plan, PasteOrder order)
    {
        Vector3 position = new Vector3((float)order.At.X, (float)order.At.Y, (float)order.At.Z);
        object? refusal = Call(GameMembers.BlueprintStartPaste,
            new object?[] { plan.Data, position, plan.CopyYAngle, (float)order.Turn.Degrees });
        if (refusal is string message)
        {
            throw ApiErrors.Refused(PasteGate.RefusedCode, message);
        }

        _last = new TrackedPaste(plan.File, ActivePaste());
        BlueprintPasteStartedView started = new BlueprintPasteStartedView(plan.File,
            new[] { order.At.X, order.At.Y, order.At.Z }, order.Turn.Degrees, plan.CopyYAngle,
            BlueprintFiles.ExpectedDurationSeconds(plan.File.Entries));
        return new VerbOutcome.Answered(started);
    }

    /// <summary>
    /// The paste's report: one target per piece the file holds, at the cell BlueprintMod's own paste geometry puts it,
    /// and the call's own body. Nothing is started.
    /// </summary>
    internal static ReportReply Describe(PastePlan plan, PasteOrder order)
    {
        Vec3 offset = VectorOf(GameMembers.BlueprintPlayerLocalOffset, plan.Data);
        List<PastePiece> pieces = new List<PastePiece>(plan.Entries.Count);
        foreach (object? entry in plan.Entries)
        {
            if (entry != null)
            {
                pieces.Add(Piece(entry, order, offset));
            }
        }

        BlueprintPasteReportView body = new BlueprintPasteReportView(plan.File,
            new[] { order.At.X, order.At.Y, order.At.Z }, order.Turn.Degrees, plan.CopyYAngle,
            BlueprintFiles.ExpectedDurationSeconds(plan.File.Entries));
        return PasteReplies.Reported(pieces, body, order.Mark);
    }

    /// <summary>The last paste's status, as inspect job id "paste" reads it (host only, with BlueprintMod).</summary>
    internal static BlueprintPasteStatusView Status()
    {
        RequireHost();
        RequireBlueprintMod();
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

    // One entry as a report row: where BlueprintMod puts it, anchor + Euler(0, turn, 0) * (RelativePosition -
    // PlayerLocalOffset) (ComputePastePositions; the yAngle this service passes is CopyYAngle, so the yaw is the turn
    // alone), and the prefab it creates.
    private static PastePiece Piece(object entry, PasteOrder order, Vec3 offset)
    {
        string prefab = GameMembers.BlueprintEntryPrefabName.GetValue(entry) as string ?? string.Empty;
        Vec3 relative = VectorOf(GameMembers.BlueprintEntryRelativePosition, entry);
        double yaw = order.Turn.Degrees * DegreesToRadians;
        double cos = Math.Cos(yaw);
        double sin = Math.Sin(yaw);
        double x = relative.X - offset.X;
        double y = relative.Y - offset.Y;
        double z = relative.Z - offset.Z;
        return new PastePiece(prefab, new Vec3(order.At.X + x * cos + z * sin, order.At.Y + y,
            order.At.Z - x * sin + z * cos));
    }

    // A BlueprintMod SerializableVector3, whose X, Y and Z are public fields.
    private static Vec3 VectorOf(GameField field, object target)
    {
        object value = field.GetValue(target)!;
        return new Vec3((float)GameMembers.BlueprintVectorX.GetValue(value)!,
            (float)GameMembers.BlueprintVectorY.GetValue(value)!,
            (float)GameMembers.BlueprintVectorZ.GetValue(value)!);
    }

    private static BlueprintPasteCountsView Counts(object operation) =>
        new BlueprintPasteCountsView(
            GameMembers.PasteComplete.GetValue(operation) is bool complete && complete,
            GameMembers.PasteCancelled.GetValue(operation) is bool cancelled && cancelled,
            GameMembers.PasteCreated.GetValue(operation) is int created ? created : 0,
            GameMembers.PasteFailed.GetValue(operation) is int failed ? failed : 0,
            GameMembers.PasteSkipped.GetValue(operation) is int skipped ? skipped : 0,
            GameMembers.PasteThings.GetValue(operation) is ICollection things ? things.Count : 0,
            Standing(GameMembers.PasteThings.GetValue(operation) as ICollection),
            GameMembers.PasteFingerprint.GetValue(operation) as string);

    // The pasted things that still exist and are not on their way out.
    private static int Standing(ICollection? things)
    {
        int standing = 0;
        foreach (object? item in things ?? Array.Empty<object>())
        {
            standing += item is Thing thing && thing != null && !thing.IsBeingDestroyed ? 1 : 0;
        }

        return standing;
    }

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

    private static void RequireHost()
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host pastes blueprints.");
        }
    }

    // A paste still placing (anyone's) refuses a new one: BlueprintMod answered a second start as started and pasted
    // nothing (structures-38).
    private static void RefuseWhilePlacing()
    {
        object? active = ActivePaste();
        if (active != null)
        {
            PasteGate.Require(true, GameMembers.PasteComplete.GetValue(active) is bool complete && complete,
                GameMembers.PasteCancelled.GetValue(active) is bool cancelled && cancelled,
                GameMembers.PasteCreated.GetValue(active) is int created ? created : 0);
        }
    }

    /// <summary>
    /// Refuses before anything is read or started: mod_missing when BlueprintMod is not loaded, game_changed naming
    /// the first member it no longer has (the pieces a report lists are read from BlueprintEntry).
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
            GameMembers.BlueprintCopyYAngle, GameMembers.BlueprintEntries, GameMembers.BlueprintEntry,
            GameMembers.BlueprintVector, GameMembers.BlueprintEntryPrefabName, GameMembers.BlueprintEntryRelativePosition,
            GameMembers.BlueprintPlayerLocalOffset, GameMembers.BlueprintVectorX, GameMembers.BlueprintVectorY,
            GameMembers.BlueprintVectorZ, GameMembers.PasteThings, GameMembers.PasteCreated, GameMembers.PasteFailed,
            GameMembers.PasteSkipped, GameMembers.PasteFingerprint, GameMembers.PasteCancelled, GameMembers.PasteComplete
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

    /// <summary>A checked paste: BlueprintMod's BlueprintData, the file it read and the angle it was copied at.</summary>
    internal sealed class PastePlan
    {
        internal PastePlan(object data, BlueprintFileView file, float copyYAngle, ICollection entries)
        {
            Data = data;
            File = file;
            CopyYAngle = copyYAngle;
            Entries = entries;
        }

        /// <summary>BlueprintMod's BlueprintData, as BlueprintSerializer.Load read it.</summary>
        internal object Data { get; }

        internal BlueprintFileView File { get; }

        internal float CopyYAngle { get; }

        /// <summary>Its BlueprintEntry objects, one report target each.</summary>
        internal ICollection Entries { get; }
    }

    /// <summary>A paste this service started: its file, and BlueprintMod's operation object if one was left.</summary>
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
