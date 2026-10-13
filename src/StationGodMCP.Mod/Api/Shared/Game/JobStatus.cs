#nullable enable

using System.Globalization;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// One job's status as inspect job reads it, picked by the kind its id names: "paste" the last blueprint paste
/// (BlueprintPastes.Status), a number a gas move (GasMoves.Outcome: queued until applied), anything else a held-tick
/// job (HeldTickJobs.Status: build, removal, run, swap, upgrade, undo), in brief (no preflight, and no final check
/// unless refused) unless verbose. Read only.
/// </summary>
internal static class JobStatus
{
    internal const string Paste = "paste";

    internal static object Of(string id, bool verbose)
    {
        string trimmed = id.Trim();
        if (string.Equals(trimmed, Paste, System.StringComparison.OrdinalIgnoreCase))
        {
            return BlueprintPastes.Status();
        }

        if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long transfer))
        {
            return GasMoves.Outcome(transfer);
        }

        object polled = HeldTickJobs.Status(trimmed);
        return verbose
            ? polled
            : polled switch
            {
                BuildJobView build => BuildJobView.Brief(build, null),
                RunJobView run => RunJobView.Brief(run, null),
                _ => JobBrief.Poll(polled)
            };
    }
}
