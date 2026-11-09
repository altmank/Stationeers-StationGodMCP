#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Pictures;

/// <summary>
/// Changes to the scene made for one render only (show picture's neutral lighting: the directional lights off, a key
/// light on, a flat ambient), each with the action that puts it back. Run applies and renders inside try/finally, so
/// every change made so far is put back whatever throws, in the apply or the render; Restore puts back in reverse
/// order, each change once, and a restorer that throws does not stop the others (the first failure is rethrown after
/// all have run). Main thread only.
/// </summary>
internal sealed class SceneSwap
{
    private readonly List<Action> _restore = new List<Action>();

    /// <summary>Changes still to be put back.</summary>
    internal int Held => _restore.Count;

    /// <summary>Records how to put a change back; call it just before (or after) making the change.</summary>
    internal void Hold(Action restore) => _restore.Add(restore ?? throw new ArgumentNullException(nameof(restore)));

    /// <summary>Sets a value through set, remembering get's value to set back.</summary>
    internal void Set<T>(Func<T> get, Action<T> set, T value)
    {
        T before = get();
        Hold(() => set(before));
        set(value);
    }

    /// <summary>Puts every held change back, newest first, each once.</summary>
    internal void Restore()
    {
        Exception? first = null;
        for (int i = _restore.Count - 1; i >= 0; i--)
        {
            Action restore = _restore[i];
            _restore.RemoveAt(i);
            try
            {
                restore();
            }
            catch (Exception failure)
            {
                first ??= failure;
            }
        }

        if (first != null)
        {
            throw new InvalidOperationException("A scene change could not be put back: " + first.Message, first);
        }
    }

    /// <summary>apply(this), then render, then Restore, in try/finally.</summary>
    internal static void Run(Action<SceneSwap> apply, Action render)
    {
        SceneSwap swap = new SceneSwap();
        try
        {
            apply(swap);
            render();
        }
        finally
        {
            swap.Restore();
        }
    }
}
