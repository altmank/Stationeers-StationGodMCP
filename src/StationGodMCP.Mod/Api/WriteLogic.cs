#nullable enable

using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// write_logic: one logic value written to one device, as a chip's s instruction writes it
/// (ILogicable.SetLogicValue), with the value read before and after when the type also reads. Writes. A write that
/// opens a vent, valve, pump or regulator runs the write lint rules first (LogicOps.Check); a problem refuses it
/// (lint_refused) unless allow_lint. dry_run reports the write from the same checks and writes nothing (ReportOnly).
/// </summary>
internal static class WriteLogicApi
{
    internal static object Handle(Args args)
    {
        bool report = ReportOnly.Asked(args);
        bool allowLint = args.OptionalBool(WriteLint.AllowArgument) ?? false;
        LogicWriteArgs write = LogicWriteArgs.Read(args);
        return WriteCheck<LogicWritePlan>.Run(() => LogicOps.Check(Devices.Scope(args), write, allowLint, null))
            .Answer(report, Write, Describe);
    }

    private static object Write(LogicWritePlan plan) => new LogicWriteView(plan.GatewayId, LogicOps.Apply(plan));

    private static object Describe(LogicWritePlan plan) => new LogicWriteReportView(plan.GatewayId, plan.Checked);
}
