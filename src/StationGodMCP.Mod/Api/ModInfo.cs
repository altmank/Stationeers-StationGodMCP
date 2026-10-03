#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Protocol;
using StationGodMCP.Pure;
using UnityEngine.Profiling;

namespace StationGodMCP.Api;

/// <summary>
/// mod_info: the running mod's identity and the pipe it listens on, every method with its counters since the mod
/// loaded (MethodStats), and every game member it reaches by reflection or patches, with whether this build of the
/// game has it (GameMembers.Report), and the runtime section: what the mod costs the game (per-method handler,
/// serialisation, queue wait and reply size; per-frame load; the collector and the Mono heap). Read only.
/// </summary>
internal static class ModInfoApi
{
    internal static ModInfoView Handle(Args args)
    {
        Assembly assembly = typeof(ModInfoApi).Assembly;
        AssemblyInformationalVersionAttribute? informational =
            Attribute.GetCustomAttribute(assembly, typeof(AssemblyInformationalVersionAttribute))
                as AssemblyInformationalVersionAttribute;
        ModIdentity identity = new ModIdentity(StationGodMod.ModId, StationGodMod.Version,
            assembly.GetName().Version?.ToString(), informational?.InformationalVersion, StationGodMod.Pipe.Value);

        List<string> names = new List<string>(ApiHost.Methods.Keys);
        names.Sort(StringComparer.Ordinal);
        List<MethodStatsView> methods = new List<MethodStatsView>(names.Count);
        foreach (string name in names)
        {
            methods.Add(MethodStats.ViewOf(name));
        }

        ReflectionReport reflection = GameMembers.Report();
        return new ModInfoView(identity, methods, reflection.Members, reflection.Missing, Runtime());
    }

    private static RuntimeView Runtime() =>
        new RuntimeView(StationGodMod.SinceLoad.Elapsed.TotalSeconds, WorldStores.Epoch,
            FrameBudget.For(PerformanceSettings.RequestBudgetMs, false), StationGodRequestDispatcher.Stats.Snapshot(),
            Memory(), MethodStats.Called(), ArgumentDrift.Counts.Snapshot(), Connections());

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
                session?.Protocol, session?.InFlight ?? 0, connection.ServedCount, connection.BytesSent));
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
