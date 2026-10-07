#nullable enable

namespace StationGodMCP.Pure.Profiling;

/// <summary>
/// The profiler's manual scopes on the main thread. Each value indexes the recorder's preallocated arrays; the
/// methods timed through [Profiled] take the slots after Count.
/// </summary>
internal enum ProfId
{
    WorldStores,
    SubscriptionsBeginFrame,
    PublishFacts,
    ObserveGameState,
    RemoteViews,
    RunFrame,
    SubscriptionLane,
    CallExecute,
    CallSerialize,
    HeldTickJobs,
    JobStep,
    JobApply,
    JobGasOpen,
    JobGasSettle,
    JobGasClose,
    AtmosphereWait,
    Previews,
    Highlights,
    RocketFlightRecorder
}

/// <summary>Every scope's name as the reports write it, by ProfId.</summary>
internal static class ProfIds
{
    internal const int Count = (int)ProfId.RocketFlightRecorder + 1;

    /// <summary>The frame time no scope covers: the rest of StationGod's Update.</summary>
    internal const string Unscoped = "unscoped";

    private static readonly string[] Names =
    {
        "world_stores", "subscriptions_begin_frame", "publish_facts", "observe_game_state", "remote_views",
        "run_frame", "subscription_lane", "call_execute", "call_serialize", "held_tick_jobs", "job_step", "job_apply",
        "job_gas_open", "job_gas_settle", "job_gas_close", "atmosphere_wait", "previews", "highlights",
        "rocket_flight_recorder"
    };

    internal static string Name(ProfId id) => Names[(int)id];
}
