#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>Every key read goes through KeyGuard (P112): no other source file reads a key or the mouse buttons.</summary>
public class KeyReadScanTests
{
    private static readonly Regex Read = new Regex(
        @"\.IsDown\(|\.IsPressed\(|\.IsUp\(|Input\.GetKey|Input\.GetMouseButton|Input\.anyKey|Input\.inputString",
        RegexOptions.Compiled);

    [Fact]
    public void NoKeyIsReadOutsideKeyGuard()
    {
        string src = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Here())!, "..", "..", "src"));
        List<string> hits = new List<string>();
        foreach (string path in Directory.GetFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) ||
                path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar) ||
                Path.GetFileName(path) == "KeyGuard.cs")
            {
                continue;
            }

            string[] lines = File.ReadAllLines(path);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimStart();
                if (!line.StartsWith("//", StringComparison.Ordinal) && Read.IsMatch(line))
                {
                    string relative = Path.GetRelativePath(src, path).Replace(Path.DirectorySeparatorChar, '/');
                    hits.Add($"{relative}:{i + 1}: {line}");
                }
            }
        }

        Assert.True(hits.Count == 0, "Unguarded key reads (use KeyGuard):\n" + string.Join("\n", hits));
    }

    private static string Here([CallerFilePath] string here = "") => here;
}
