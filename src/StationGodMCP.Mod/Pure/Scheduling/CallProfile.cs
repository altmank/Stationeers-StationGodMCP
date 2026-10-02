#nullable enable

using System;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Catalogue;

namespace StationGodMCP.Pure.Scheduling;

/// <summary>The lane a call is sorted into when it arrives (scheduling.md, Lanes).</summary>
internal enum Lane
{
    Light,
    Heavy
}

/// <summary>
/// What the scheduler needs to know about one call: its method, its effective class at its arguments (which decides
/// its order on its connection) and its catalogue cost at its arguments (which decides its lane).
/// </summary>
internal readonly struct CallProfile
{
    internal CallProfile(string method, MethodClass callClass, CallCost cost)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Class = callClass;
        Cost = cost.Cost;
        Items = Math.Max(cost.Items, 1);
    }

    internal string Method { get; }

    internal MethodClass Class { get; }

    internal CostClass Cost { get; }

    /// <summary>The per_item count of the cost rule that matched, at least 1.</summary>
    internal int Items { get; }

    /// <summary>
    /// protocol.md, Order on one connection: a write or cheat call starts only once every earlier call on its
    /// connection is answered, and no later call starts before it is.
    /// </summary>
    internal bool IsOrdered => Class != MethodClass.Read;

    /// <summary>The profile the catalogue gives a call with these arguments.</summary>
    internal static CallProfile Of(CatalogueMethod method, JObject? arguments) =>
        new CallProfile(method.Name, method.ClassAt(arguments), method.CostAt(arguments));
}
