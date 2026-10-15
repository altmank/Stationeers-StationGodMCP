#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using StationGodMCP.Api.Shared.Game.Lint;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP.WorldModel;

/// <summary>
/// The static data built once per load (cost design A1 and A3), built at load rather than in the first call that needs
/// it: once the world runs and the game's placement cursors exist, each server frame builds one piece of it (the build
/// catalogue, the swap targets, each run family's kits, prefabs by name, ingots by reagent, the lint rule set), so no
/// call's frame pays for a whole catalogue. A rebuild of the prefab data (Prefabs.Generation) starts the list again.
/// Then the mod's own code is compiled (RuntimeHelpers.PrepareMethod: compiled, never run), CompileMsAFrame of each
/// frame, type by type, so the first call of a kind after a load does not pay for compiling its handler, its reports
/// and the planners and job states it reaches. Generic methods and types are left to their first call. Main thread.
/// </summary>
internal static class LoadWarmup
{
    /// <summary>How long compiling may take of one frame.</summary>
    internal const double CompileMsAFrame = 1.0;

    private static readonly Action[] Pieces =
    {
        static () => _ = Prefabs.Build,
        static () => _ = Prefabs.Targets,
        static () => _ = Prefabs.KitsOf(new CableFamily()),
        static () => _ = Prefabs.KitsOf(new PipeFamily()),
        static () => _ = Prefabs.KitsOf(new ChuteFamily()),
        static () => _ = Prefabs.NamedIgnoringCase(string.Empty),
        static () => _ = Prefabs.IngotsByReagent,
        static () => _ = LintRuleFiles.Current()
    };

    private static long _generation = -1;
    private static int _next;

    // The mod's types, compiled once while the mod runs (compiled code outlives a load): _type the next to compile,
    // _member the next of its methods.
    private static Type[]? _types;
    private static int _type;
    private static MethodBase[]? _members;
    private static int _member;
    private static int _compiled;
    private static int _failed;
    private static double _compileMs;

    /// <summary>Whether the mod's code has been compiled.</summary>
    internal static bool Compiled => _types != null && _type >= _types.Length;

    /// <summary>Each server frame: the next piece of the static data, then of compiling, while any is left.</summary>
    internal static void Tick()
    {
        if (GameManager.GameState != GameState.Running || !World.PrefabsLoaded || !Prefabs.CursorsMade)
        {
            return;
        }

        long generation = Prefabs.Generation;
        if (generation != _generation)
        {
            _generation = generation;
            _next = 0;
        }

        if (_next >= Pieces.Length)
        {
            if (!Compiled)
            {
                using ProfScope compiling = Prof.Scope(ProfId.LoadWarmup);
                Compile();
            }

            return;
        }

        using ProfScope scope = Prof.Scope(ProfId.LoadWarmup);
        try
        {
            Pieces[_next]();
        }
        catch (Exception exception)
        {
            // A piece that fails here fails the same way in the call that needs it, which answers for it.
            StationGodMod.LogWarning($"Building the load's static data (piece {_next}) failed: {exception.Message}");
        }

        _next++;
    }

    // Compiles methods until CompileMsAFrame has passed (at least one), going on where the last frame stopped.
    private static void Compile()
    {
        long started = Stopwatch.GetTimestamp();
        _types ??= typeof(LoadWarmup).Assembly.GetTypes();
        while (_type < _types.Length)
        {
            if (_members == null)
            {
                _members = MembersOf(_types[_type]);
                _member = 0;
            }

            while (_member < _members.Length)
            {
                Prepare(_members[_member++]);
                if (Elapsed(started) >= CompileMsAFrame)
                {
                    _compileMs += Elapsed(started);
                    return;
                }
            }

            _members = null;
            _type++;
        }

        _compileMs += Elapsed(started);
        StationGodMod.Log($"Compiled StationGod's code at load: {_compiled} methods in {_compileMs:F0} ms of frames" +
                          (_failed > 0 ? $", {_failed} left to their first call" : string.Empty) + ".");
    }

    private static void Prepare(MethodBase method)
    {
        try
        {
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
            _compiled++;
        }
        catch (Exception)
        {
            // A method this game's build cannot compile (a member it lacks) fails in its first call as it would have.
            _failed++;
        }
    }

    // The methods and constructors of a type that can be compiled ahead: with a body, and with no generic parameter.
    private static MethodBase[] MembersOf(Type type)
    {
        if (type.ContainsGenericParameters)
        {
            return Array.Empty<MethodBase>();
        }

        const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                      BindingFlags.Static | BindingFlags.DeclaredOnly;
        MethodInfo[] methods = type.GetMethods(Declared);
        ConstructorInfo[] constructors = type.GetConstructors(Declared);
        List<MethodBase> members = new List<MethodBase>(methods.Length + constructors.Length);
        foreach (MethodInfo method in methods)
        {
            if (Compilable(method))
            {
                members.Add(method);
            }
        }

        foreach (ConstructorInfo constructor in constructors)
        {
            if (Compilable(constructor))
            {
                members.Add(constructor);
            }
        }

        return members.ToArray();
    }

    private static bool Compilable(MethodBase method) =>
        !method.IsAbstract && !method.ContainsGenericParameters &&
        (method.MethodImplementationFlags & (MethodImplAttributes.InternalCall | MethodImplAttributes.Runtime)) == 0 &&
        (method.Attributes & MethodAttributes.PinvokeImpl) == 0;

    private static double Elapsed(long started) => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
}
