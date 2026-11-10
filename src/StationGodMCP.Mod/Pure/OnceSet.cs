#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// The keys OnceLog has logged: each is accepted once; at the cap the first refusal says so (so the silence after it
/// is not a mystery) and later new keys are refused quietly. Clear starts afresh (a world change).
/// </summary>
internal sealed class OnceSet
{
    internal enum Verdict
    {
        Log,
        Repeat,
        CapReached,
        Suppressed
    }

    private readonly HashSet<string> _keys = new HashSet<string>();
    private readonly int _maximum;
    private bool _capSaid;

    internal OnceSet(int maximum) => _maximum = maximum;

    internal Verdict Offer(string key)
    {
        if (_keys.Contains(key))
        {
            return Verdict.Repeat;
        }

        if (_keys.Count >= _maximum)
        {
            if (_capSaid)
            {
                return Verdict.Suppressed;
            }

            _capSaid = true;
            return Verdict.CapReached;
        }

        _keys.Add(key);
        return Verdict.Log;
    }

    internal void Clear()
    {
        _keys.Clear();
        _capSaid = false;
    }
}
