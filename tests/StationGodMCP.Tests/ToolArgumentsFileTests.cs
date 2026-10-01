#nullable enable

using System;
using System.IO;
using StationGodMCP.Server;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// tool-arguments.json, the argument names the mod holds pipe clients to, is the sidecar's schemas as they stand.
/// Set STATIONGOD_WRITE_TOOL_ARGUMENTS=1 and run this test to write it after a schema change.
/// </summary>
public sealed class ToolArgumentsFileTests
{
    [Fact]
    public void TheModsArgumentFileMatchesTheSidecarsSchemas()
    {
        string path = Path.Combine(RepositoryRoot(), "tool-arguments.json");
        string expected = ToolArguments.FileText();
        if (Environment.GetEnvironmentVariable("STATIONGOD_WRITE_TOOL_ARGUMENTS") == "1")
        {
            File.WriteAllText(path, expected);
        }

        Assert.True(File.Exists(path) && File.ReadAllText(path).Replace("\r\n", "\n") == expected,
            "tool-arguments.json differs from the sidecar's schemas: run this test with STATIONGOD_WRITE_TOOL_ARGUMENTS=1.");
    }

    [Fact]
    public void TheSidecarsOwnArgumentsNeverReachTheModsList()
    {
        foreach (string[] names in ToolArguments.Declared().Values)
        {
            Assert.DoesNotContain(ReplyShaping.OutputFileArgument, names);
            Assert.DoesNotContain(ReplyShaping.FieldsArgument, names);
        }
    }

    internal static string RepositoryRoot()
    {
        for (DirectoryInfo? folder = new DirectoryInfo(AppContext.BaseDirectory); folder != null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "StationGodMCP.sln")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("No StationGodMCP.sln above the test folder.");
    }
}
