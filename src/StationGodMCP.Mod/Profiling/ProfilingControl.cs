#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure.Profiling;

namespace StationGodMCP;

/// <summary>
/// The profiling switch on the mod's side, main thread only: at load it finds the [Profiled] methods (once, by
/// reflection over the mod's assembly) and starts profiling when [Performance] Profiling is true. On starts a
/// ProfileRecorder, wraps every [Profiled] method with ProfiledTiming's prefix and finalizer under its own Harmony id
/// and, when asked, starts the CSV; off stops recording, removes exactly those wrappers and stops the CSV. A method
/// that cannot be wrapped is logged and left out. Profiling turned off by an internal failure (Prof.Fail) is cleaned
/// up the same way on the next Update (Tick).
/// </summary>
internal static class ProfilingControl
{
    private const string HarmonyId = StationGodMod.ModId + ".profiling";

    private static readonly List<MethodInfo> Tagged = new List<MethodInfo>();
    private static readonly List<string> TaggedNames = new List<string>();
    private static readonly List<MethodInfo> Wrapped = new List<MethodInfo>();
    private static readonly List<string> Untimed = new List<string>();
    private static Harmony? _harmony;
    private static bool _active;

    /// <summary>
    /// At load, on the main thread: finds the [Profiled] methods, and starts profiling when configured. A failure here
    /// is logged and leaves the methods untimed (scopes still work); the rest of the mod loads.
    /// </summary>
    internal static void Load(Assembly assembly, bool startOn)
    {
        Prof.MainThreadId = Environment.CurrentManagedThreadId;
        Prof.WarningSink = StationGodMod.LogWarning;
        try
        {
            Collect(assembly);
        }
        catch (Exception exception)
        {
            // Reflection over the mod's own types (a type this game build cannot load): profiling times no method.
            Tagged.Clear();
            TaggedNames.Clear();
            StationGodMod.LogWarning($"Profiling cannot time handlers or game hooks: {exception.Message}");
        }

        if (startOn)
        {
            try
            {
                TurnOn(null, null);
                StationGodMod.Log($"Profiling is on ([Performance] Profiling); {Wrapped.Count} methods timed.");
            }
            catch (Exception exception)
            {
                // Starting the session at load: profiling stays off, the mod goes on.
                Shutdown();
                StationGodMod.LogWarning($"Profiling could not start at load: {exception.Message}");
            }
        }
    }

    /// <summary>The mod is unloading: profiling off, its wrappers removed and its CSV closed; never throws.</summary>
    internal static void Shutdown()
    {
        try
        {
            TurnOff();
        }
        catch (Exception exception)
        {
            // Unloading: whatever profiling still holds is let go with the process.
            Prof.Stop();
            _active = false;
            StationGodMod.LogWarning($"Profiling did not shut down cleanly: {exception.Message}");
        }
    }

    /// <summary>Every frame, first: after an internal failure turned profiling off, its wrappers and CSV are removed.</summary>
    internal static void Tick()
    {
        if (_active && !Prof.Enabled)
        {
            TurnOff();
        }
    }

    /// <summary>The profiling method.</summary>
    internal static ProfilingView Run(ProfilingRequest request)
    {
        switch (request.Action)
        {
            case ProfilingAction.On:
                TurnOn(request.SlowFrameMs, request.Csv);
                break;
            case ProfilingAction.Off:
                TurnOff();
                break;
            case ProfilingAction.Reset:
                if (Prof.Enabled)
                {
                    Prof.Recorder?.Reset();
                }
                else
                {
                    Prof.Forget();
                }

                break;
        }

        return View();
    }

    /// <summary>mod_info's runtime.profiling: present only while profiling is on.</summary>
    internal static ProfilingSummaryView? Summary()
    {
        ProfileRecorder? recorder = Prof.Recorder;
        return Prof.Enabled && recorder != null ? new ProfilingSummaryView(recorder.Report(true)) : null;
    }

    private static ProfilingView View()
    {
        bool enabled = Prof.Enabled;
        return new ProfilingView(Prof.Recorder?.Report(enabled), enabled, Wrapped.Count, new List<string>(Untimed),
            Prof.Failure);
    }

    private static void TurnOn(double? slowFrameMs, bool? csv)
    {
        ProfileRecorder? recorder = Prof.Recorder;
        if (!Prof.Enabled || recorder == null)
        {
            TurnOff();
            recorder = new ProfileRecorder(slowFrameMs ?? SlowFrameLimits.DefaultMs, TaggedNames);
            Prof.Start(recorder);
            _active = true;
            Wrap();
        }
        else if (slowFrameMs.HasValue)
        {
            recorder.SlowFrameMs = slowFrameMs.Value;
        }

        if (csv == true && (recorder.Csv == null || !recorder.Csv.State().Writing))
        {
            StartCsv(recorder);
        }
        else if (csv == false)
        {
            recorder.Csv?.Stop();
        }
    }

    private static void TurnOff()
    {
        Prof.Stop();
        Unwrap();
        Prof.Recorder?.Csv?.Stop();
        _active = false;
    }

    private static void StartCsv(ProfileRecorder recorder)
    {
        recorder.Csv?.Stop();
        ProfileCsvWriter writer = new ProfileCsvWriter(Path.Combine(Paths.BepInExRootPath, "StationGodMCP"),
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
        writer.Start();
        recorder.Csv = writer;
    }

    private static void Collect(Assembly assembly)
    {
        Tagged.Clear();
        TaggedNames.Clear();
        Dictionary<MethodBase, int> slots = new Dictionary<MethodBase, int>();
        foreach (Type type in AccessTools.GetTypesFromAssembly(assembly))
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic |
                                                          BindingFlags.Static | BindingFlags.Instance |
                                                          BindingFlags.DeclaredOnly))
            {
                if (method.IsDefined(typeof(ProfiledAttribute), false))
                {
                    slots[method] = Tagged.Count;
                    Tagged.Add(method);
                    TaggedNames.Add(type.Name + "." + method.Name);
                }
            }
        }

        ProfiledTiming.Slots = slots;
    }

    // Each method on its own, so one this build cannot wrap costs only its own timing.
    private static void Wrap()
    {
        _harmony ??= new Harmony(HarmonyId);
        HarmonyMethod prefix = new HarmonyMethod(typeof(ProfiledTiming), nameof(ProfiledTiming.Prefix));
        HarmonyMethod finalizer = new HarmonyMethod(typeof(ProfiledTiming), nameof(ProfiledTiming.Finalizer));
        Untimed.Clear();
        for (int index = 0; index < Tagged.Count; index++)
        {
            try
            {
                _harmony.Patch(Tagged[index], prefix: prefix, finalizer: finalizer);
                Wrapped.Add(Tagged[index]);
            }
            catch (Exception exception)
            {
                // Harmony refusing one method (a body it cannot copy): that method is not timed, the rest are.
                StationGodMod.LogWarning($"Profiling cannot time {TaggedNames[index]}: {exception.Message}");
                Untimed.Add(TaggedNames[index]);
            }
        }
    }

    private static void Unwrap()
    {
        foreach (MethodInfo method in Wrapped)
        {
            try
            {
                _harmony!.Unpatch(method, HarmonyPatchType.All, HarmonyId);
            }
            catch (Exception exception)
            {
                // Harmony failing to restore one method: its timing stays, and records nothing while profiling is off.
                StationGodMod.LogWarning($"Profiling could not unwrap {method.DeclaringType?.Name}.{method.Name}: {exception.Message}");
            }
        }

        Wrapped.Clear();
    }
}

/// <summary>
/// The prefix and finalizer that time a [Profiled] method while profiling is on. The finalizer returns nothing, so the
/// method's own exception, if any, goes on unchanged; neither ever throws (Prof catches at its boundary).
/// </summary>
internal static class ProfiledTiming
{
    /// <summary>Each [Profiled] method's slot; written once at load, read only after.</summary>
    internal static Dictionary<MethodBase, int> Slots { get; set; } = new Dictionary<MethodBase, int>();

    internal static void Prefix(MethodBase __originalMethod, out long __state)
    {
        __state = Slots.TryGetValue(__originalMethod, out int slot) ? Prof.MethodEnter(slot) : 0;
    }

    internal static void Finalizer(MethodBase __originalMethod, long __state)
    {
        if (__state != 0 && Slots.TryGetValue(__originalMethod, out int slot))
        {
            Prof.MethodExit(slot, __state);
        }
    }
}
