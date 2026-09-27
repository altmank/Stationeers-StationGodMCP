#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// mod_info: the running mod's identity and the pipe it listens on, every method with its counters since the mod
/// loaded (MethodStats), and every game member it reaches by reflection or patches, with whether this build of the
/// game has it (GameMembers.Report). Read only.
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
        return new ModInfoView(identity, methods, reflection.Members, reflection.Missing);
    }
}
