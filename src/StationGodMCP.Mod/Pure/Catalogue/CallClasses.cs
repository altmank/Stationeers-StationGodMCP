#nullable enable

namespace StationGodMCP.Pure.Catalogue;

/// <summary>A call's permission class (protocol.md, Sign-in and permissions).</summary>
internal enum MethodClass
{
    Read,
    Write,
    Cheat
}

/// <summary>The cost class the scheduler starts from (docs/architecture/scheduling.md, Cost classes).</summary>
internal enum CostClass
{
    Instant,
    Bounded,
    World,
    Plan,
    Job,
    Stream
}

/// <summary>A cost at the arguments given: the class, and the item count a per-list rule scales it by (1 otherwise).</summary>
internal readonly struct CallCost
{
    internal CallCost(CostClass cost, int items)
    {
        Cost = cost;
        Items = items;
    }

    internal CostClass Cost { get; }

    internal int Items { get; }
}
