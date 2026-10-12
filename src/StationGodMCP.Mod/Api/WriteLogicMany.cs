#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// write_logic_many: up to 256 logic writes in order, each as write_logic, each failing alone: a write the lint rules
/// refuse (lint_refused) fails its entry and the rest still run. allow_lint covers every entry. Writes. dry_run reports
/// each write from the same checks and writes nothing (ReportOnly).
/// </summary>
internal static class WriteLogicManyApi
{
    private const int MaximumWrites = 256;

    internal static object Handle(Args args)
    {
        bool report = ReportOnly.Asked(args);
        bool allowLint = args.OptionalBool(WriteLint.AllowArgument) ?? false;
        List<Args?> items = args.Objects("writes", MaximumWrites);
        return WriteCheck<DeviceScope>.Run(() => Devices.Scope(args))
            .Answer(report, scope => Writes(scope, items, allowLint, false),
                scope => Writes(scope, items, allowLint, true));
    }

    private static object Writes(DeviceScope scope, List<Args?> items, bool allowLint, bool report)
    {
        List<BuildIssueView> warnings = new List<BuildIssueView>();
        BatchResultView results = LogicOps.WriteMany(scope, items, allowLint, report, warnings);
        return report
            ? new LogicBatchReportView(scope.Id, results, warnings)
            : new LogicWriteBatchView(scope.Id, results, warnings);
    }
}
