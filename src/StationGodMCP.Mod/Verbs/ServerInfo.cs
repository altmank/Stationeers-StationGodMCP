#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using StationGodMCP.Api;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Protocol;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Jobs;
using StationGodMCP.Pure.Lint;
using StationGodMCP.Pure.Profiling;
using StationGodMCP.Pure.Verbs;
using UnityEngine.Profiling;

namespace StationGodMCP.Verbs;

/// <summary>
/// server info (catalogue server.json): the running mod's identity and the pipe it listens on, every verb what with its
/// counters since the mod loaded (MethodStats), and every game member it reaches by reflection or patches, with whether
/// this build of the game has it (GameMembers.Report), and the runtime section: what the mod costs the game (per-method
/// handler, serialisation, queue wait and reply size; per-frame load; the collector and the Mono heap; and, with
/// include: ["counters"], the counters of the mod's game-side work). The methods list is the whats with a handler
/// (VerbHandlers.Dispatcher.Keys): a retired per-tool method is not listed. Read only.
/// </summary>
internal static class ServerInfo
{
    internal static VerbOutcome Run(ServerInfoOptions options)
    {
        Assembly assembly = typeof(ServerInfo).Assembly;
        AssemblyInformationalVersionAttribute? informational =
            Attribute.GetCustomAttribute(assembly, typeof(AssemblyInformationalVersionAttribute))
                as AssemblyInformationalVersionAttribute;
        ModIdentity identity = new ModIdentity(StationGodMod.ModId, StationGodMod.Version,
            assembly.GetName().Version?.ToString(), informational?.InformationalVersion, StationGodMod.Pipe.Value);

        List<string> names = new List<string>(VerbHandlers.Dispatcher.Keys);
        List<MethodStatsView> methods = new List<MethodStatsView>(names.Count);
        foreach (string name in names)
        {
            methods.Add(MethodStats.ViewOf(name));
        }

        ReflectionReport reflection = GameMembers.Report();
        RuntimeView runtime = Runtime(options.Fields.Includes("counters"));
        runtime.NoteTruncations("runtime.");
        return Answer.Of(new ModInfoView(identity, methods, reflection.Members, reflection.Missing, runtime));
    }

    private static RuntimeView Runtime(bool counters) =>
        new RuntimeView(StationGodMod.SinceLoad.Elapsed.TotalSeconds, WorldStores.Epoch,
            FrameBudget.For(PerformanceSettings.RequestBudgetMs, false), StationGodRequestDispatcher.Stats.Snapshot(),
            Memory(), MethodStats.Called(), Connections(), ProfilingControl.Summary(),
            new JobSettlesView(SettleGate.Run, SettleGate.Skipped, SettleGate.Unchecked), ThingIndex.View(counters),
            counters ? Counters() : null);

    private static GameCountersView Counters() =>
        new GameCountersView(new JobHoldsView(HeldTickJobs.HoldCounts()),
            new PrintLogView(Prints.Log.Count, Prints.Log.Capacity), LintChipPrograms.ParsedCount,
            new BatchConsoleView(ConsoleBridge.BatchLineRate.Total,
                ConsoleBridge.BatchLineRate.PerMinute(ProfileClock.System.Timestamp())), Restores(),
            JobStoresView.Of(KeptJobs.Main));

    private static RestoreCountsView Restores()
    {
        HeldRestores? restores = StationGodMod.Restores;
        return restores == null
            ? new RestoreCountsView(0, 0, 0)
            : new RestoreCountsView(restores.Applied, restores.Skipped, restores.Dropped);
    }

    private static List<ConnectionView>? Connections()
    {
        if (StationGodMod.Connections == null)
        {
            return null;
        }

        List<ConnectionView> views = new List<ConnectionView>();
        foreach (Connection connection in StationGodMod.AllConnections())
        {
            Session? session = connection.Session;
            views.Add(new ConnectionView(connection.ClientId, session?.Client, session?.Label, connection.Transport,
                session?.Protocol, session?.InFlight ?? 0, connection.ServedCount, connection.BytesSent,
                connection.Calls, StationGodMod.Hub?.CountOf(connection.Owner) ?? 0));
        }

        views.Sort(static (left, right) => string.CompareOrdinal(left.ClientId, right.ClientId));
        return views;
    }

    // Unity's Mono (Boehm collector): one generation; the Profiler sizes answer 0 where the build does not report them.
    private static MemoryView Memory()
    {
        long used = Profiler.GetMonoUsedSizeLong();
        long heap = Profiler.GetMonoHeapSizeLong();
        return new MemoryView(GC.CollectionCount(0), GC.MaxGeneration, GC.GetTotalMemory(false),
            used > 0 ? used : null, heap > 0 ? heap : null);
    }
}
