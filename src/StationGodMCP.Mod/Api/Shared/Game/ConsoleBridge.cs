#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Assets.Scripts;
using HarmonyLib;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Util.Commands;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// Runs Stationeers console commands and reads the console's printed lines.
/// Every append to ConsoleWindow's ring buffer funnels through ConsoleLine.Set or
/// ConsoleLine.SetSegments, so a Harmony postfix on those two sees each new line exactly once.
/// A capture snapshots the line's text as it is appended rather than reading the ring buffer
/// afterwards: the buffer shifts by value (ConsoleLine.Apply) on every print, and
/// Application.logMessageReceivedThreaded mirrors errors into it from worker threads with no
/// main-thread hop (ConsoleWindow.LogMessage), so index arithmetic over the buffer is a race.
/// Appends that arrive on another thread during a capture are counted, not captured, so they
/// can never be passed off as the command's own output.
/// A dedicated (batch-mode) server keeps no buffer: ConsoleWindow.Print writes the line to its system
/// console and returns before any ConsoleLine is set, and PrintBlock, PrintSegmentedBlock and
/// PrintSegmentedBlockRaw fall through to Print there (ConsoleWindow.cs, IsBatchMode branches). A
/// Harmony prefix on that Print sees each batch-mode line, captures it as above, and keeps it in
/// BatchLines, which read_console reads on such a server.
/// </summary>
internal static class ConsoleBridge
{
    internal const int MaximumConsoleLines = 500;

    /// <summary>Managed thread id that armed the current capture; 0 when nothing is capturing.</summary>
    private static int _captureThreadId;

    /// <summary>Appends seen on other threads during the current capture.</summary>
    private static long _concurrentWrites;

    /// <summary>Touched only by the capturing thread.</summary>
    private static readonly List<ConsoleLineView> Captured = new List<ConsoleLineView>(64);
    private static int _capturedTotal;

    /// <summary>
    /// Command types that start a UniTask and Forget() it, so CommandBase.Execute returns before
    /// the work is done and CommandLine.Process returns before a single result line is printed.
    /// Nothing can be captured for them within the call, so the tool must not report their empty
    /// output as "printed nothing".
    ///
    /// <see cref="DetectAsynchrony"/> finds most of these without a list, because an async method
    /// declared on the command type carries [AsyncStateMachine]. It is not sufficient on its own:
    /// ExportWorldCommand and SteamCommand call an async helper declared in another type, so they
    /// have no async method of their own. The two mechanisms are therefore unioned, and the whole
    /// audited set is named here so a reflection surprise cannot silently reinstate the bug.
    ///
    /// Re-derive after a game update, against the decompiled Assembly-CSharp:
    ///     grep -l "Forget()" Util.Commands/*.cs
    /// Keys are resolved from the types, so aliases (load/loadgame, new/newgame) and subclasses
    /// (LoadLatestCommand) follow automatically.
    /// </summary>
    private static readonly HashSet<string> AsynchronousCommandTypeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "DifficultySettingsCommand", // difficulty      WaitExecute(args).Forget()
        "ExportWorldCommand",        // exportworld     WorldSettingData.SaveNewScenarioWorld(...).Forget()
        "FileCommand",               // file            NewGameTask/NewSaveTask/SaveTask/SaveAsTask/QuickSaveTask
        "LoadGameCommand",           // load, loadgame, loadlatest (subclass)  LoadGame(...).Forget()
        "LogCommand",                // log             LogToFile(args).Forget()
        "NewGameCommand",            // new, newgame    ExecuteAsync(...).Forget()
        "RestartCommand",            // reset           RestartApplication().Forget()
        "SaveCommand",               // save            SaveTask/NewSaveTask(...).Forget()
        "SteamCommand",              // steam           Achievements.Steam.TryForceAchievementUpdate().Forget()
        "UpnpCommand"                // upnp            UPnP().Forget()
    };

    private static readonly Dictionary<Type, bool> AsynchronyByType = new Dictionary<Type, bool>();

    /// <summary>The lines a batch-mode server printed, newest last.</summary>
    private static readonly ConsoleRing<ConsoleLineView> BatchLines =
        new ConsoleRing<ConsoleLineView>(MaximumConsoleLines);

    /// <summary>False on a network client, where several commands only queue a request.</summary>
    internal static bool RunSimulation => GameManager.RunSimulation;

    internal static void NoteAppend(ConsoleLine line)
    {
        if (!CapturingHere())
        {
            return;
        }

        try
        {
            Captured.Add(LineSummary(line));
        }
        catch (Exception)
        {
            // ConsoleWindow.GetConsoleColor and the line's text, read inside the game's own print path
            // (ConsoleLine.Set): losing a captured line is survivable; throwing out of the postfix is not.
        }
    }

    /// <summary>A line ConsoleWindow.Print is about to write to a batch-mode server's system console.</summary>
    internal static void NoteBatchPrint(string? output, ConsoleColor color)
    {
        ConsoleLineView line = new ConsoleLineView(DateTime.Now.ToString("HH:mm:ss"), color.ToString(), output);
        BatchLines.Add(line);
        if (CapturingHere())
        {
            Captured.Add(line);
        }
    }

    // Counts a line printed during a capture: on another thread as concurrent, on this one as the command's own.
    // True when the line is to be kept: on this thread, below the line cap.
    private static bool CapturingHere()
    {
        int owner = Volatile.Read(ref _captureThreadId);
        if (owner == 0)
        {
            return false;
        }

        if (Thread.CurrentThread.ManagedThreadId != owner)
        {
            Interlocked.Increment(ref _concurrentWrites);
            return false;
        }

        _capturedTotal++;
        return Captured.Count < MaximumConsoleLines;
    }

    /// <summary>Resolves the registry key exactly the way CommandLine.Process does.</summary>
    internal static string? ResolveCommandKey(string command)
    {
        string? first = null;
        foreach (string part in CmdLineParser.SplitCommandLine(command))
        {
            first = part;
            break;
        }

        if (string.IsNullOrEmpty(first))
        {
            return null;
        }

        // CommandLine.Process uses ToLower, not ToLowerInvariant. Under a Turkish locale the two
        // disagree on 'I', which would make this lookup and the game's disagree about the key.
        return first!.TrimStart('-').ToLower();
    }

    /// <summary>
    /// True when the command can return before its result is printed, so an empty capture says
    /// nothing about whether it worked. Cached per type; main thread only.
    /// </summary>
    internal static bool CompletesAsynchronously(CommandBase? command)
    {
        if (command == null)
        {
            return false;
        }

        Type type = command.GetType();
        if (AsynchronyByType.TryGetValue(type, out bool known))
        {
            return known;
        }

        bool asynchronous = DetectAsynchrony(type);
        AsynchronyByType[type] = asynchronous;
        return asynchronous;
    }

    private static bool DetectAsynchrony(Type type)
    {
        for (Type current = type; current != null && current != typeof(CommandBase); current = current.BaseType)
        {
            if (AsynchronousCommandTypeNames.Contains(current.Name))
            {
                return true;
            }

            try
            {
                MethodInfo[] methods = current.GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly);
                foreach (MethodInfo method in methods)
                {
                    if (method.IsDefined(typeof(AsyncStateMachineAttribute), false))
                    {
                        return true;
                    }
                }
            }
            catch (Exception)
            {
                // Type.GetMethods on a command type from a mod assembly that cannot load: reflection is the
                // forward-looking half only; the named set above still applies.
            }
        }

        return false;
    }

    internal static CommandBase? FindCommand(string? commandKey)
    {
        if (string.IsNullOrEmpty(commandKey) || CommandLine.CommandsMap == null)
        {
            return null;
        }

        return CommandLine.CommandsMap.TryGetValue(commandKey, out CommandBase command) ? command : null;
    }

    /// <summary>
    /// True when CommandLine.ExecuteCommand would append the command to _postLaunchCommands
    /// instead of running it, to be fired later by GameManager during its one-time init. The
    /// caller would see an empty, successful-looking result for a command that had not run.
    /// Unreachable in practice - the pipe server only starts once NetworkManager.IsServer, which
    /// is well after GameManager.Start sets IsInitialized - so this is a guard, not a fix.
    /// </summary>
    internal static bool WouldBeDeferredToLaunchQueue(CommandBase? command)
    {
        return command != null && command.RequiresGameManagerIsInitialized && !GameManager.IsInitialized;
    }

    /// <summary>
    /// Whether printed lines can be read: on a batch-mode server through the Print prefix, once it is patched;
    /// otherwise from the console's buffer, once it has one.
    /// </summary>
    internal static bool CanCapture()
    {
        return GameManager.IsBatchMode
            ? ConsoleBatchPrintPatch.Patched
            : ConsoleWindow.IsInitialised && BufferLength() > 0;
    }

    internal static void Echo(string command)
    {
        ConsoleWindow.Print("mcp> " + command, ConsoleColor.Cyan);
    }

    /// <summary>
    /// Runs <paramref name="command"/> and returns the lines it printed on this thread, oldest
    /// first. Truncation keeps the oldest, because an error prints its message before its stack
    /// trace. <paramref name="concurrentLines"/> counts lines another thread printed into the
    /// console during the same window; they are excluded from the result.
    /// </summary>
    internal static List<ConsoleLineView> Execute(
        string command,
        int maximumLines,
        out int totalLines,
        out bool truncated,
        out long concurrentLines)
    {
        Captured.Clear();
        _capturedTotal = 0;
        Interlocked.Exchange(ref _concurrentWrites, 0L);
        Volatile.Write(ref _captureThreadId, Thread.CurrentThread.ManagedThreadId);
        try
        {
            CommandLine.Process(command);
        }
        finally
        {
            Volatile.Write(ref _captureThreadId, 0);
        }

        concurrentLines = Interlocked.Read(ref _concurrentWrites);
        totalLines = _capturedTotal;
        truncated = totalLines > maximumLines || Captured.Count < totalLines;

        List<ConsoleLineView> lines = new List<ConsoleLineView>(Math.Min(maximumLines, Captured.Count));
        for (int index = 0; index < Captured.Count && index < maximumLines; index++)
        {
            lines.Add(Captured[index]);
        }

        Captured.Clear();
        return lines;
    }

    /// <summary>
    /// The most recent lines the console printed, oldest first. Read straight out of the ring
    /// buffer, so a line another thread is appending right now may be seen half-written.
    /// </summary>
    internal static List<ConsoleLineView> ReadRecent(int maximumLines)
    {
        if (GameManager.IsBatchMode)
        {
            return BatchLines.Recent(maximumLines);
        }

        List<ConsoleLineView> lines = new List<ConsoleLineView>();
        ConsoleLine[]? buffer = ConsoleWindow.ConsoleBuffer;
        if (buffer == null)
        {
            return lines;
        }

        int limit = Math.Min(maximumLines, buffer.Length);
        for (int index = limit - 1; index >= 0; index--)
        {
            ConsoleLine? line = buffer[index];
            if (line == null || string.IsNullOrEmpty(line.Time))
            {
                continue;
            }

            lines.Add(LineSummary(line));
        }

        return lines;
    }

    private static int BufferLength()
    {
        ConsoleLine[]? buffer = ConsoleWindow.ConsoleBuffer;
        return buffer == null ? 0 : buffer.Length;
    }

    private static ConsoleLineView LineSummary(ConsoleLine line) =>
        new ConsoleLineView(line.Time, LevelName(line.Color), FlattenText(line));

    private static string FlattenText(ConsoleLine line)
    {
        if (line.SegmentLines != null)
        {
            StringBuilder segments = new StringBuilder();
            for (int row = 0; row < line.SegmentLines.Length; row++)
            {
                if (row > 0)
                {
                    segments.Append('\n');
                }

                ConsoleSegment[]? cells = line.SegmentLines[row];
                if (cells == null)
                {
                    continue;
                }

                for (int cell = 0; cell < cells.Length; cell++)
                {
                    segments.Append(cells[cell].Text);
                }
            }

            return segments.ToString();
        }

        if (line.Continuations == null || line.Continuations.Length == 0)
        {
            return line.Text;
        }

        return line.Text + "\n" + string.Join("\n", line.Continuations);
    }

    /// <summary>
    /// The level name of a console colour, through the game's private ConsoleWindow.GetConsoleColor(uint). A missing
    /// member is game_changed; a colour the game itself cannot map is null.
    /// </summary>
    private static string? LevelName(uint color)
    {
        MethodInfo map = GameMembers.ConsoleColorOf.Info;
        try
        {
            return map.Invoke(null, new object[] { color })?.ToString();
        }
        catch (TargetInvocationException)
        {
            // ConsoleWindow.GetConsoleColor threw for a colour it cannot map.
            return null;
        }
    }
}

/// <summary>
/// ConsoleWindow.Print(string, ConsoleColor, bool, bool, bool): on a batch-mode server, every console line, before
/// the game timestamps it and writes it to the system console.
/// </summary>
[HarmonyPatch(typeof(ConsoleWindow), nameof(ConsoleWindow.Print),
    new[] { typeof(string), typeof(ConsoleColor), typeof(bool), typeof(bool), typeof(bool) })]
internal static class ConsoleBatchPrintPatch
{
    /// <summary>Set once Harmony has prepared the prefix for its target.</summary>
    internal static bool Patched { get; private set; }

    private static void Prepare(MethodBase? original)
    {
        if (original != null)
        {
            Patched = true;
        }
    }

    // Harmony passes the exception that stopped the patch, if any: then nothing is captured.
    private static Exception? Cleanup(MethodBase? original, Exception? exception)
    {
        if (exception != null)
        {
            Patched = false;
        }

        return exception;
    }

    private static void Prefix(string output, ConsoleColor color)
    {
        if (!GameManager.IsBatchMode)
        {
            return;
        }

        try
        {
            ConsoleBridge.NoteBatchPrint(output, color);
        }
        catch (Exception)
        {
            // A line lost to the capture is survivable; an exception out of the game's print path is not.
        }
    }
}

[HarmonyPatch(typeof(ConsoleLine), nameof(ConsoleLine.Set))]
internal static class ConsoleLineSetPatch
{
    private static void Postfix(ConsoleLine __instance)
    {
        ConsoleBridge.NoteAppend(__instance);
    }
}

[HarmonyPatch(typeof(ConsoleLine), nameof(ConsoleLine.SetSegments))]
internal static class ConsoleLineSetSegmentsPatch
{
    private static void Postfix(ConsoleLine __instance)
    {
        ConsoleBridge.NoteAppend(__instance);
    }
}
