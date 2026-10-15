#nullable enable

using System;

namespace StationGodMCP.Pure.Facts;

/// <summary>How a fact a reply carries is kept true (cost design A2).</summary>
internal enum FactRule
{
    /// <summary>Read from the game when asked; cheap per item.</summary>
    Live,

    /// <summary>Kept current by the game's change points; recomputed only where something changed.</summary>
    Followed,

    /// <summary>No change point exists: a background sweep re-reads a little each frame; replies carry as_of.</summary>
    Refreshed
}

/// <summary>
/// A fact's rule, declared next to its reader: on every verb handler (the reader of its call's reply), one per kind of
/// fact it carries, and on every keeper of a followed or refreshed fact. A followed or refreshed fact on a handler names
/// its Keeper: a mod type declaring the same rule, or the game type whose own lists keep it. The source rules
/// (CostSourceRulesTests) check the declarations; writes never read a followed or refreshed keeper.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
internal sealed class ReplyFactAttribute : Attribute
{
    internal ReplyFactAttribute(FactRule rule, string what)
    {
        Rule = rule;
        What = what;
    }

    internal FactRule Rule { get; }

    /// <summary>The facts, in words.</summary>
    internal string What { get; }

    /// <summary>Who keeps a followed or refreshed fact; null for a live one and on a keeper itself.</summary>
    public Type? Keeper { get; set; }

    /// <summary>
    /// On a keeper: writes may read it, because every answer it gives is the game's own (the world index checks each
    /// thing against the game's list) or cannot change within a world (the prefab catalogues). A keeper without it is
    /// never read by a write: plans read the game again when they act.
    /// </summary>
    public bool WritesMayRead { get; set; }
}
