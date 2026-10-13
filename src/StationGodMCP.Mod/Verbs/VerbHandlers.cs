#nullable enable

using StationGodMCP.Pure.Verbs;

namespace StationGodMCP.Verbs;

/// <summary>
/// The game's (verb, what) handler table: one verb-native handler per what, each binding its generated options class
/// (VerbHandler.For(XOptions.Spec, XOptions.Bind, run)). A verb with no handler here answers method_not_found.
/// </summary>
internal static class VerbHandlers
{
    internal static VerbDispatcher Dispatcher { get; } = new VerbDispatcher(new VerbHandler[0]);
}
