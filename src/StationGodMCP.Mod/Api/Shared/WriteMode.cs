#nullable enable

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
            throw ApiErrors.InvalidArgument("confirm: true needs dry_run: false; nothing was changed.");
        }

        if (!dryRun && !confirm)
        {
            throw ApiErrors.Refused("confirm_required",
                "A real run needs dry_run: false and confirm: true; nothing was changed.");
        }

        return dryRun;
    }
}
