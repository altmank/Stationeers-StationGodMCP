#nullable enable

namespace StationGodMCP.Api.Shared;

/// <summary>
/// How much a tool lists when the caller does not say: every default page size and list length in one place, the
/// handlers' and the reply budget test's (ReplyBudgetTests holds each tool's default reply on a large world to a size).
/// </summary>
internal static class ReplyDefaults
{
    internal const int ConnectionMembers = 30;
    internal const int AreaOpenEnds = 25;
    internal const int ScreenElements = 25;
    internal const int NetworkOverviewLists = 10;
    internal const int DeepMinerSpots = 5;
    internal const int FindItems = 10;
    internal const int FindSpots = 5;
    internal const int FindThings = 8;
    internal const int IcStackWindow = 64;
    internal const int LuaLogLines = 5;
    internal const int GridSurveyCells = 8;
    internal const int ItemTotals = 12;
    internal const int ItemTotalHolders = 1;
    internal const int LintFindings = 25;
    internal const int Containers = 12;
    internal const int OuterFrames = 20;
    internal const int ConsoleLines = 40;
    internal const int ConsoleOutputLines = 100;
    internal const int FlightLogRows = 12;
    internal const int HealthThings = 8;
    internal const int BrokenNeighbours = 1;
    internal const int UpgradeListed = 5;
    internal const int SwapListed = 5;
    internal const int RunListed = 5;
    internal const int VaultDepositItems = 256;
    internal const int SnapshotDevices = 2;
    internal const int RuntimeMethods = 10;

    /// <summary>The largest list limit of the upgrade and clean reports.</summary>
    internal const int ReportListMaximum = 4096;

    internal const int LuaLogLinesMaximum = 200;
    internal const int ConsoleLinesMaximum = 500;
}
