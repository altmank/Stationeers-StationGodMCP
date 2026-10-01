#nullable enable

using System;
using System.IO;
using System.Reflection;
using Assets.Scripts.Serialization;
using StationGodMCP.Pure.Lint;

namespace StationGodMCP.Api.Shared.Game.Lint;

/// <summary>
/// The rule files a lint call uses: lint-rules.json in the mod's folder (the copy built into the DLL when that file is
/// missing), and lint-rules.json in the loaded save's folder (saves\&lt;station&gt;\), which takes priority. Read again
/// whenever either file's time or size changes; otherwise the compiled set is kept.
/// </summary>
internal static class LintRuleFiles
{
    internal const string FileName = "lint-rules.json";
    private const string Resource = "StationGodMCP.lint-rules.json";

    private static string? _key;
    private static LintRuleSet? _set;

    internal static string ModFile => Path.Combine(ModDirectory, FileName);

    /// <summary>The save's rule file path (it may not exist); null with no save loaded.</summary>
    internal static string? SaveFile
    {
        get
        {
            XmlSaveLoad? saves = XmlSaveLoad.Instance;
            string? station = saves != null ? saves.CurrentStationName : null;
            return string.IsNullOrEmpty(station)
                ? null
                : Path.Combine(StationSaveUtils.GetSavePathSavesSubDir().FullName, station, FileName);
        }
    }

    // The folder the mod's DLL was loaded from (LaunchPad loads mods with Assembly.LoadFrom); a DLL loaded from bytes
    // has no location, and then only the built-in rules apply.
    private static string ModDirectory
    {
        get
        {
            string location = typeof(LintRuleFiles).Assembly.Location;
            return string.IsNullOrEmpty(location)
                ? Path.Combine(Directory.GetCurrentDirectory(), "no-mod-folder")
                : Path.GetDirectoryName(location) ?? Directory.GetCurrentDirectory();
        }
    }

    /// <summary>The effective rule set, compiled once per change of either file.</summary>
    internal static LintRuleSet Current()
    {
        string? save = SaveFile;
        string key = Stamp(ModFile) + "|" + (save != null ? Stamp(save) : "-");
        if (_set != null && key == _key)
        {
            return _set;
        }

        _set = Load(ModText(), save != null && File.Exists(save) ? Text(save, LintRuleOrigin.Save) : null);
        _key = key;
        return _set;
    }

    /// <summary>The set a save file's text would give with the mod's rules: lint_rules validate on text not yet saved.</summary>
    internal static LintRuleSet With(string saveText, string name) =>
        Load(ModText(), new LintRuleText(name, saveText, LintRuleOrigin.Save));

    private static LintRuleSet Load(LintRuleText mod, LintRuleText? save) =>
        LintRuleLoader.Load(LintGameLibrary.Library, mod, save);

    private static LintRuleText ModText() =>
        File.Exists(ModFile) ? Text(ModFile, LintRuleOrigin.Mod) : new LintRuleText(FileName + " (built in)", BuiltIn(), LintRuleOrigin.Mod);

    private static LintRuleText Text(string path, LintRuleOrigin origin)
    {
        try
        {
            return new LintRuleText(path, File.ReadAllText(path), origin);
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
        {
            return new LintRuleText(path, $"unreadable: {error.Message}", origin);
        }
    }

    private static string BuiltIn()
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource);
        if (stream == null)
        {
            return "{\"schema_version\": 1, \"rules\": []}";
        }

        using StreamReader reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Stamp(string path)
    {
        try
        {
            FileInfo file = new FileInfo(path);
            return file.Exists ? $"{path}@{file.LastWriteTimeUtc.Ticks}/{file.Length}" : path + "@none";
        }
        catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
        {
            return path + "@unreadable";
        }
    }
}
