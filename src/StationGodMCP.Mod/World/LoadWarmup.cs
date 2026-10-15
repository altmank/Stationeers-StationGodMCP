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
/// Then the mod's own code is compiled (compiled, never run: RuntimeHelpers.PrepareMethod, and on the game's Mono the
/// method's function pointer, which compiles it), CompileMsAFrame of each
/// frame, type by type, so the first call of a kind after a load does not pay for compiling its handler, its reports
/// and the planners and job states it reaches; with them the methods they call (the game's, the runtime's, closed
/// generic ones), read from their IL (CalledMethods), and theirs, CalleeDepth calls deep; and every method of a closed
/// form of the mod's own generic types they name (a list page of frames, an iterator's state machine), which exists
/// only for those type arguments. Open generic methods are left to their first call. Main thread.
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
    private static readonly List<MethodBase> Called = new List<MethodBase>();
    // How far below the mod's own methods callees are compiled: a callee's callees, and theirs.
    private const int CalleeDepth = 3;

    private static readonly List<MethodBase> Callees = new List<MethodBase>();
    private static readonly List<int> CalleeDepths = new List<int>();
    private static readonly HashSet<MethodBase> Seen = new HashSet<MethodBase>();
    private static readonly HashSet<Type> SeenTypes = new HashSet<Type>();
    private static int _queued;
    private static int _callees;
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

    // Compiles methods until CompileMsAFrame has passed (at least one), going on where the last frame stopped: the
    // callees queued so far first, then the next of the mod's own methods, whose direct callees (the game's, the
    // runtime's, closed generic ones: CalledMethods) are queued as it is compiled.
    private static void Compile()
    {
        long started = Stopwatch.GetTimestamp();
        _types ??= typeof(LoadWarmup).Assembly.GetTypes();
        while (true)
        {
            if (_queued < Callees.Count)
            {
                MethodBase callee = Callees[_queued];
                int depth = CalleeDepths[_queued++];
                Prepare(callee);
                if (depth < CalleeDepth)
                {
                    Queue(callee, depth + 1);
                }

                _callees++;
            }
            else if (NextOwn() is { } own)
            {
                Prepare(own);
                Queue(own, 1);
            }
            else
            {
                break;
            }

            if (Elapsed(started) >= CompileMsAFrame)
            {
                _compileMs += Elapsed(started);
                return;
            }
        }

        _compileMs += Elapsed(started);
        Callees.Clear();
        CalleeDepths.Clear();
        Seen.Clear();
        SeenTypes.Clear();
        StationGodMod.Log($"Compiled StationGod's code at load: {_compiled - _callees} of its methods and {_callees} " +
                          $"they call in {_compileMs:F0} ms of frames" +
                          (_failed > 0 ? $", {_failed} left to their first call" : string.Empty) + ".");
    }

    // The next of the mod's own methods to compile; null once every type is done (Compiled).
    private static MethodBase? NextOwn()
    {
        while (_type < _types!.Length)
        {
            _members ??= MembersOf(_types[_type]);
            if (_member < _members.Length)
            {
                return _members[_member++];
            }

            _members = null;
            _member = 0;
            _type++;
        }

        return null;
    }

    // The method's direct callees not seen yet, queued to be compiled at depth (1: called by one of the mod's own).
    private static void Queue(MethodBase method, int depth)
    {
        try
        {
            CalledMethods.Of(method, Called);
        }
        catch (Exception)
        {
            // A body the runtime will not read: its callees compile in their first call.
        }

        foreach (MethodBase callee in Called)
        {
            Add(callee, depth);
            // A closed form of one of the mod's own generic types (a list page of frames, an iterator's state
            // machine): its methods are made for these type arguments only, so they are compiled with it.
            Type? owner = callee.DeclaringType;
            if (owner is { IsGenericType: true, ContainsGenericParameters: false } &&
                owner.Assembly == typeof(LoadWarmup).Assembly && SeenTypes.Add(owner))
            {
                foreach (MethodBase member in MembersOf(owner))
                {
                    Add(member, depth);
                }

                foreach (Type nested in owner.GetGenericTypeDefinition().GetNestedTypes(BindingFlags.NonPublic))
                {
                    if (nested.IsGenericTypeDefinition &&
                        nested.GetGenericArguments().Length == owner.GetGenericArguments().Length)
                    {
                        foreach (MethodBase member in MembersOf(nested.MakeGenericType(owner.GetGenericArguments())))
                        {
                            Add(member, depth);
                        }
                    }
                }
            }
        }

        Called.Clear();
    }

    private static void Add(MethodBase method, int depth)
    {
        if (Seen.Add(method))
        {
            Callees.Add(method);
            CalleeDepths.Add(depth);
        }
    }

    private static void Prepare(MethodBase method)
    {
        try
        {
            // PrepareMethod is the documented way; the game's Mono does nothing for it, and compiles a method when its
            // function pointer is asked for (mono_compile_method). Neither runs it.
            RuntimeMethodHandle handle = method.MethodHandle;
            RuntimeHelpers.PrepareMethod(handle);
            _ = handle.GetFunctionPointer();
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
            if (CalledMethods.Compilable(method))
            {
                members.Add(method);
            }
        }

        foreach (ConstructorInfo constructor in constructors)
        {
            if (CalledMethods.Compilable(constructor))
            {
                members.Add(constructor);
            }
        }

        return members.ToArray();
    }

    private static double Elapsed(long started) => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
}
