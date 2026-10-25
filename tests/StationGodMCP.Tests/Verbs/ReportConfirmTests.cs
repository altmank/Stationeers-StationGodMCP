#nullable enable

using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests.Verbs;

/// <summary>
/// build structure's check_only as the mod decides it: a report the call asked for whatever its confirm said, with the
/// rows it asked for kept.
/// </summary>
public sealed class ReportConfirmTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReportOnlyNeverActs(bool confirm)
    {
        ReportConfirm checking = new ReportConfirm(takesConfirm: true, confirm, rows: 12).ReportOnly();

        Assert.False(checking.Acts);
        Assert.True(checking.Asked);
        Assert.Equal(12, checking.Rows);
    }
}
