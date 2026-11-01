#nullable enable

using System;
using System.Collections.Generic;
using System.Reflection;

namespace StationGodMCP.Pure;

/// <summary>
/// The mod's own types that reflection may read in this game. The mod references Lattice's contract assembly, optional
/// at run time, and only the types under StationGodMCP.LatticeLink name its types (LatticeIsolationTests). In a game
/// without Lattice, Assembly.GetTypes() throws ReflectionTypeLoadException for those types, and listing the members of
/// one that did load gives method objects that crash the game's Mono natively when read (IsGenericMethod; rig sgt5,
/// 2.16.0), where no catch reaches. So without Lattice those types are left out before any member is read.
/// </summary>
internal static class LoadableTypes
{
    /// <summary>The namespace (with the ones under it) whose types may name Lattice's.</summary>
    internal const string LatticeNamespace = "StationGodMCP.LatticeLink";

    /// <summary>The types getTypes returns, or, when it throws ReflectionTypeLoadException, the ones that loaded.</summary>
    internal static Type[] Of(Func<Type[]> getTypes)
    {
        try
        {
            return getTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            List<Type> loaded = new List<Type>(exception.Types.Length);
            foreach (Type? type in exception.Types)
            {
                if (type != null)
                {
                    loaded.Add(type);
                }
            }

            return loaded.ToArray();
        }
    }

    /// <summary>
    /// The loadable types of an assembly whose members are safe to read: without Lattice (latticeLoaded false),
    /// every type that may name Lattice's is left out.
    /// </summary>
    internal static Type[] Readable(Assembly assembly, bool latticeLoaded) =>
        Readable(() => assembly.GetTypes(), latticeLoaded);

    /// <summary>As Readable(assembly, latticeLoaded), from getTypes.</summary>
    internal static Type[] Readable(Func<Type[]> getTypes, bool latticeLoaded)
    {
        Type[] types = Of(getTypes);
        if (latticeLoaded)
        {
            return types;
        }

        List<Type> readable = new List<Type>(types.Length);
        foreach (Type type in types)
        {
            if (!NamesLattice(type))
            {
                readable.Add(type);
            }
        }

        return readable.ToArray();
    }

    /// <summary>Whether a type may name Lattice's: it, or the type it is nested in, is in LatticeNamespace.</summary>
    internal static bool NamesLattice(Type type)
    {
        Type outer = type;
        while (outer.DeclaringType != null)
        {
            outer = outer.DeclaringType;
        }

        return InLatticeNamespace(outer.Namespace);
    }

    /// <summary>Whether a namespace is LatticeNamespace or one under it.</summary>
    internal static bool InLatticeNamespace(string? ns) =>
        ns != null && (ns == LatticeNamespace || ns.StartsWith(LatticeNamespace + ".", StringComparison.Ordinal));
}
