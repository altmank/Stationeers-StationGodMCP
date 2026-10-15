#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Lint;

/// <summary>
/// Chip programs parsed once (LintChipPrograms.Ic10 and Lua) and kept by their language and exact source text, so a
/// chip is parsed again only when its source changes. Bounded: past Capacity programs it is emptied and starts over,
/// and it is emptied when a world is left (WorldStores, chip_programs). The lint engine's chip functions and the
/// controller index (inspect_controllers, driven_by) share one (LintChipPrograms.Programs). Safe from any thread.
/// </summary>
internal sealed class ChipProgramCache
{
    internal const int DefaultCapacity = 1024;

    private readonly Dictionary<string, List<ChipOperation>> _ic10 = new Dictionary<string, List<ChipOperation>>(StringComparer.Ordinal);
    private readonly Dictionary<string, List<ChipOperation>> _lua = new Dictionary<string, List<ChipOperation>>(StringComparer.Ordinal);
    private readonly Dictionary<long, HolderProgram> _byHolder = new Dictionary<long, HolderProgram>();
    private readonly object _gate = new object();
    private readonly int _capacity;
    private long _parses;

    internal ChipProgramCache(int capacity = DefaultCapacity)
    {
        _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    }

    /// <summary>Programs held parsed.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _ic10.Count + _lua.Count;
            }
        }
    }

    /// <summary>Programs parsed since this cache was made: a lookup that found its program parses nothing.</summary>
    internal long Parses
    {
        get
        {
            lock (_gate)
            {
                return _parses;
            }
        }
    }

    /// <summary>The device operations of a program in that language ("lua", else IC10), parsed at most once per source.</summary>
    internal List<ChipOperation> Of(string language, string source)
    {
        bool lua = language == "lua";
        Dictionary<string, List<ChipOperation>> parsed = lua ? _lua : _ic10;
        lock (_gate)
        {
            if (parsed.TryGetValue(source, out List<ChipOperation> known))
            {
                return known;
            }
        }

        List<ChipOperation> operations = lua ? LintChipPrograms.Lua(source) : LintChipPrograms.Ic10(source);
        lock (_gate)
        {
            _parses++;
            if (_ic10.Count + _lua.Count >= _capacity)
            {
                _ic10.Clear();
                _lua.Clear();
            }

            parsed[source] = operations;
        }

        return operations;
    }

    /// <summary>
    /// A holder's program (Of), found first by the holder and the very source string it gave last time, so a holder
    /// whose source is the same string object costs a lookup, not a hash of its whole text (review D, R-D-9: the
    /// controllers scan of every write logic); a new string is looked up by its text as Of does.
    /// </summary>
    internal List<ChipOperation> OfHolder(long holder, string language, string source)
    {
        bool lua = language == "lua";
        lock (_gate)
        {
            if (_byHolder.TryGetValue(holder, out HolderProgram? kept) && ReferenceEquals(kept.Source, source) &&
                kept.Lua == lua)
            {
                return kept.Operations;
            }
        }

        List<ChipOperation> operations = Of(language, source);
        lock (_gate)
        {
            if (_byHolder.Count >= _capacity)
            {
                _byHolder.Clear();
            }

            _byHolder[holder] = new HolderProgram(source, lua, operations);
        }

        return operations;
    }

    /// <summary>Forgets every parsed program.</summary>
    internal void Clear()
    {
        lock (_gate)
        {
            _ic10.Clear();
            _lua.Clear();
            _byHolder.Clear();
        }
    }

    // A holder's last source string and its program.
    private sealed class HolderProgram
    {
        internal HolderProgram(string source, bool lua, List<ChipOperation> operations)
        {
            Source = source;
            Lua = lua;
            Operations = operations;
        }

        internal string Source { get; }

        internal bool Lua { get; }

        internal List<ChipOperation> Operations { get; }
    }
}
