#nullable enable

using StationGodMCP.Pure.Protocol;

namespace StationGodMCP.Api.Shared;

/// <summary>Dry run by default; a real run needs dry_run false and confirm true, as the other write tools.</summary>
internal static class WriteMode
{
    internal static bool IsDryRun(Args args)
    {
        bool dryRun = args.OptionalBool("dry_run") ?? true;
        bool confirm = args.OptionalBool("confirm") ?? false;
        if (dryRun && confirm)
        {
            throw ApiErrors.InvalidArgument(Vocabulary.Text("{arg:confirm}: true needs {arg:dry_run}: false; nothing " +
                "was changed."));
        }

        if (!dryRun && !confirm)
        {
            throw ApiErrors.Refused("confirm_required",
                Vocabulary.Text("A real run needs {arg:dry_run}: false and {arg:confirm}: true; nothing was changed."));
        }

        return dryRun;
    }
}
