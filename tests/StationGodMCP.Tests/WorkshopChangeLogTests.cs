#nullable enable

using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The Steam change note (About.xml ChangeLog) holds every version since the last Workshop publish (none yet, so every
/// version in CHANGELOG.md), each with its heading and at least one line, and stays under the 8,000 characters the
/// Workshop and LaunchPad accept (build.ps1 refuses 8,000 or more).
/// </summary>
public sealed class WorkshopChangeLogTests
{
    private const int WorkshopLimit = 8000;

    private static string ChangeLog() =>
        XDocument.Load(Path.Combine(ToolArgumentsFileTests.RepositoryRoot(), "About", "About.xml")).Root!
            .Element("ChangeLog")!.Value;

    private static List<string> ChangelogVersions()
    {
        string text = File.ReadAllText(Path.Combine(ToolArgumentsFileTests.RepositoryRoot(), "CHANGELOG.md"));
        List<string> versions = new List<string>();
        foreach (Match heading in Regex.Matches(text, @"^## (\d+\.\d+\.\d+)\s*$", RegexOptions.Multiline))
        {
            versions.Add(heading.Groups[1].Value);
        }

        return versions;
    }

    [Fact]
    public void FitsTheWorkshopLimit()
    {
        int length = ChangeLog().Length;
        Assert.True(length < WorkshopLimit, $"About.xml ChangeLog is {length} characters; it must stay under {WorkshopLimit}.");
    }

    [Fact]
    public void EveryVersionHasAHeadingAndALine()
    {
        string[] lines = ChangeLog().Replace("\r\n", "\n").Split('\n');
        List<string> versions = ChangelogVersions();
        Assert.NotEmpty(versions);
        foreach (string version in versions)
        {
            int heading = System.Array.IndexOf(lines, "v" + version);
            Assert.True(heading >= 0, $"CHANGELOG.md has {version}, the Workshop ChangeLog has no v{version} heading.");
            Assert.True(heading + 1 < lines.Length && lines[heading + 1].Trim().Length > 0 &&
                        !lines[heading + 1].StartsWith("v", System.StringComparison.Ordinal),
                $"v{version} in the Workshop ChangeLog has no line under its heading.");
        }
    }

    [Fact]
    public void NewestVersionIsTheModVersion()
    {
        XElement about = XDocument.Load(Path.Combine(ToolArgumentsFileTests.RepositoryRoot(), "About", "About.xml")).Root!;
        string version = about.Element("Version")!.Value;

        Assert.Equal(version, ChangelogVersions()[0]);
        Assert.StartsWith("v" + version + "\n", ChangeLog().Replace("\r\n", "\n").TrimStart());
    }
}
