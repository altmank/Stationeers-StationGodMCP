#nullable enable

using System;
using System.IO;
using System.Reflection;
using Assets.Scripts.Serialization;
using StationGodMCP.Pure.Lint;

namespace StationGodMCP.Api.Shared.Game.Lint;

/// <summary>
/// The rule files a lint call uses: the mod's rules, the copy of lint-rules.json built into this DLL, and
/// lint-rules.json in the loaded save's folder (saves\&lt;station&gt;\), which takes priority. The lint-rules.json in
/// the mod's folder is a copy to read and is never loaded: the rules always match the code that runs them, even when
/// the DLL sits next to another version's file (whose rules can lack a phase, such as "write", this code relies on).
/// The save's file is read again whenever its time or size changes; otherwise the compiled set is kept.
/// </summary>
internal static class LintRuleFiles
{
    internal const string FileName = "lint-rules.json";

    /// <summary>The name the mod's rules go by in rule_source and in their errors.</summary>
    internal const string BuiltInName = FileName + " (built in)";

    private const string Resource = "StationGodMCP.lint-rules.json";

    private static string? _key;
    private static LintRuleSet? _set;
    private static string? _builtIn;

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

    /// <summary>The effective rule set, compiled once per change of the save's file.</summary>
    internal static LintRuleSet Current()
    {
        string? save = SaveFile;
        string key = save != null ? Stamp(save) : "-";
        if (_set != null && key == _key)
        {
            return _set;
        }

        _set = Load(ModText(), save != null && File.Exists(save) ? Text(save, LintRuleOrigin.Save) : null);
        _key = key;
        return _set;
    }

    /// <summary>The set a save file's text would give with the mod's rules: check rules validate on text not yet saved.</summary>
    internal static LintRuleSet With(string saveText, string name) =>
        Load(ModText(), new LintRuleText(name, saveText, LintRuleOrigin.Save));

    private static LintRuleSet Load(LintRuleText mod, LintRuleText? save) =>
        LintRuleLoader.Load(LintGameLibrary.Library, mod, save);

    private static LintRuleText ModText() => new LintRuleText(BuiltInName, _builtIn ??= BuiltIn(), LintRuleOrigin.Mod);

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
