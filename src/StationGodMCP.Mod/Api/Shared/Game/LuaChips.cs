#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using Assets.Scripts.Objects.Electrical;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// StationeersLua's Lua chips, read by name through GameMembers so the mod is optional: without it no chip is a Lua
/// chip. Every call here runs on the main thread; the runtime manager's own locks cover its worker threads.
/// </summary>
internal static class LuaChips
{
    /// <summary>
    /// The largest Lua source set_ic_source takes, in characters. StationeersLua sets none; the chip's stored form
    /// (gzip and base64) goes to clients in one message and a client reads it into a stack buffer
    /// (RocketBinaryReader.ReadAscii), so the tool keeps it well below that.
    /// </summary>
    internal const int MaximumSourceLength = 262144;

    internal const int DefaultLogLines = 20;
    internal const int MaximumLogLines = 200;

    internal static bool IsInstalled => GameMembers.LuaRuntimeManager.OrNull != null;

    /// <summary>LuaChipIds.IsLuaProgrammableChip: an IntegratedCircuitLua, or a thing with its prefab name.</summary>
    internal static bool IsLua(ProgrammableChip chip)
    {
        if (!GameMembers.LuaIsLuaChip.TryResolve())
        {
            return false;
        }

        return GameMembers.LuaIsLuaChip.Invoke(null, chip) is bool isLua && isLua;
    }

    internal static void RequireSourceSize(string source)
    {
        if (source.Length > MaximumSourceLength)
        {
            throw ApiErrors.Refused("source_too_large",
                $"The Lua source is {source.Length.ToString(CultureInfo.InvariantCulture)} characters; the limit is " +
                $"{MaximumSourceLength.ToString(CultureInfo.InvariantCulture)}. Split it into library chips " +
                "(--@module) and require them.");
        }
    }

    /// <summary>
    /// LuaChipRuntimeManager.ClearFailedSourceHash, so the next SetSourceCode compiles even an unchanged source that
    /// failed before. Required for restart, which does nothing without it; best effort before a write.
    /// </summary>
    internal static void ForgetFailedSource(ProgrammableChip chip, bool required)
    {
        if (!GameMembers.LuaClearFailedSource.TryResolve())
        {
            if (required)
            {
                throw Changed(GameMembers.LuaClearFailedSource);
            }

            return;
        }

        GameMembers.LuaClearFailedSource.Invoke(null, chip.ReferenceId);
    }

    /// <summary>
    /// The chip's runtime (LuaChipRuntimeManager.IsCompilationInProgress, IsChipInitComplete, GetSourceVersion,
    /// TryGetDebugSnapshot for the runtime, library flag and last error) and, when logLines is above 0, the tail of its
    /// print() log (GetChipLogText). A member StationeersLua no longer has is named in unavailable.
    /// </summary>
    internal static LuaStateView State(ProgrammableChip chip, int logLines)
    {
        List<string> missing = new List<string>();
        long id = chip.ReferenceId;
        bool compiling = Read(GameMembers.LuaIsCompiling, missing, id) is bool isCompiling && isCompiling;
        bool initComplete = Read(GameMembers.LuaIsInitComplete, missing, chip) is bool isInit && isInit;
        long? sourceVersion = Read(GameMembers.LuaSourceVersion, missing, id) as long?;
        Snapshot snapshot = Snapshot.Read(chip, missing);
        LuaLogView? log = logLines > 0 ? Log(Read(GameMembers.LuaLogText, missing, id) as string, logLines) : null;
        LuaRuntimeFlags flags = new LuaRuntimeFlags(compiling, snapshot.HasRuntime, initComplete, snapshot.Library);
        string? unavailable = missing.Count == 0
            ? null
            : "StationeersLua has changed; could not read " + string.Join(", ", missing);
        return new LuaStateView(flags, sourceVersion, snapshot.LastError, log, unavailable);
    }

    private static object? Read(GameMethod member, List<string> missing, params object[] arguments)
    {
        if (!member.TryResolve())
        {
            missing.Add(member.Name);
            return null;
        }

        try
        {
            return member.Invoke(null, arguments);
        }
        catch (TargetInvocationException error)
        {
            // Another mod's method threw; report it and read the rest.
            missing.Add($"{member.Name} ({(error.InnerException ?? error).Message})");
            return null;
        }
    }

    private static LuaLogView Log(string? text, int lineCount)
    {
        string[] lines = string.IsNullOrEmpty(text)
            ? Array.Empty<string>()
            : text!.Split('\n');
        int start = Math.Max(0, lines.Length - lineCount);
        List<string> tail = new List<string>(lines.Length - start);
        for (int index = start; index < lines.Length; index++)
        {
            tail.Add(lines[index].TrimEnd('\r'));
        }

        return new LuaLogView(tail, lines.Length);
    }

    private static ApiException Changed(GameMember member) =>
        ApiErrors.Refused(IsInstalled ? "lua_changed" : "lua_not_installed", IsInstalled
            ? $"StationeersLua has changed: {member.Name} is gone, so this StationGod MCP build cannot do this."
            : "StationeersLua is not loaded, so there are no Lua chips.");

    /// <summary>The fields of LuaChipRuntimeManager.TryGetDebugSnapshot's LuaChipDebugSnapshot the tools report.</summary>
    private readonly struct Snapshot
    {
        private Snapshot(bool hasRuntime, bool library, LuaErrorView? lastError)
        {
            HasRuntime = hasRuntime;
            Library = library;
            LastError = lastError;
        }

        internal bool HasRuntime { get; }

        internal bool Library { get; }

        internal LuaErrorView? LastError { get; }

        internal static Snapshot Read(ProgrammableChip chip, List<string> missing)
        {
            GameMember? absent = FirstMissing();
            if (absent != null)
            {
                missing.Add(absent.Name);
                return default;
            }

            // TryGetDebugSnapshot(chip, memoryStart, memoryCount, out snapshot, out error): no memory window.
            object?[] arguments = { chip, 0, 0, null, null };
            object? found;
            try
            {
                found = GameMembers.LuaDebugSnapshot.Invoke(null, arguments);
            }
            catch (TargetInvocationException error)
            {
                // Another mod's method threw; report it and read the rest.
                missing.Add($"{GameMembers.LuaDebugSnapshot.Name} ({(error.InnerException ?? error).Message})");
                return default;
            }

            if (!(found is bool ok) || !ok || arguments[3] == null)
            {
                return default;
            }

            object boxed = arguments[3]!;
            bool hasRuntime = GameMembers.LuaSnapshotHasRuntime.GetValue(boxed) is bool runtime && runtime;
            bool library = GameMembers.LuaSnapshotIsLibrary.GetValue(boxed) is bool isLibrary && isLibrary;
            return new Snapshot(hasRuntime, library, Error(boxed));
        }

        private static LuaErrorView? Error(object boxed)
        {
            string? kind = GameMembers.LuaSnapshotErrorKind.GetValue(boxed) as string;
            if (string.IsNullOrEmpty(kind))
            {
                return null;
            }

            int line = GameMembers.LuaSnapshotErrorLine.GetValue(boxed) is ushort number ? number : 0;
            return new LuaErrorView(kind!, line,
                GameMembers.LuaSnapshotErrorMessage.GetValue(boxed) as string ?? string.Empty,
                GameMembers.LuaSnapshotErrorTraceback.GetValue(boxed) as string);
        }

        private static GameMember? FirstMissing()
        {
            GameMember[] needed =
            {
                GameMembers.LuaDebugSnapshot, GameMembers.LuaSnapshotHasRuntime, GameMembers.LuaSnapshotIsLibrary,
                GameMembers.LuaSnapshotErrorKind, GameMembers.LuaSnapshotErrorLine, GameMembers.LuaSnapshotErrorMessage,
                GameMembers.LuaSnapshotErrorTraceback
            };
            foreach (GameMember member in needed)
            {
                if (!member.TryResolve())
                {
                    return member;
                }
            }

            return null;
        }
    }
}
