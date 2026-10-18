#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure.Rockets;

/// <summary>
/// The flight log a read or stop or clear names, before any live rocket is looked for: a log outlives its rocket (a
/// crash keeps the samples), so the logs are asked first. Given an id, the one log of that rocket id, else the one log
/// of that rocket name (any case); left out, the only log there is. Null when that is not exactly one log: the call
/// then names a live rocket, as write flight_log start does.
/// </summary>
internal static class FlightLogPick
{
    internal static int? Of(IReadOnlyList<(long RocketId, string RocketName)> logs, string? given)
    {
        if (given == null)
        {
            return logs.Count == 1 ? 0 : null;
        }

        string text = given.Trim();
        int? byId = Single(logs, log => log.RocketId.ToString(CultureInfo.InvariantCulture) == text);
        return byId ?? Single(logs, log => string.Equals(log.RocketName, text, StringComparison.OrdinalIgnoreCase));
    }

    private static int? Single(IReadOnlyList<(long RocketId, string RocketName)> logs,
        Func<(long RocketId, string RocketName), bool> matches)
    {
        int? found = null;
        for (int index = 0; index < logs.Count; index++)
        {
            if (!matches(logs[index]))
            {
                continue;
            }

            if (found.HasValue)
            {
                return null;
            }

            found = index;
        }

        return found;
    }
}
