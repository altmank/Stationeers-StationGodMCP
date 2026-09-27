#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// The local named pipe's name, the part after \\.\pipe\. Not empty, trimmed, and free of \, / and :, which Windows
/// reads as path or namespace separators in a pipe path.
/// </summary>
internal sealed class PipeName
{
    internal const string DefaultValue = "StationGodMCP";

    internal static readonly PipeName Default = new PipeName(DefaultValue);

    private static readonly char[] Forbidden = { '\\', '/', ':' };

    private PipeName(string value)
    {
        Value = value;
    }

    internal string Value { get; }

    /// <summary>The name, or null when the text is empty, only whitespace, or contains \, / or :.</summary>
    internal static PipeName? TryParse(string? text)
    {
        string trimmed = (text ?? string.Empty).Trim();
        return trimmed.Length == 0 || trimmed.IndexOfAny(Forbidden) >= 0 ? null : new PipeName(trimmed);
    }

    /// <summary>
    /// The name the mod listens on: the environment variable when it is set (not empty), else the config value. An
    /// invalid choice falls back to the default, with a warning naming where it came from.
    /// </summary>
    internal static PipeNameChoice Choose(string? environment, string environmentName, string? configured)
    {
        bool fromEnvironment = !string.IsNullOrEmpty(environment);
        string? text = fromEnvironment ? environment : configured;
        PipeName? parsed = TryParse(text);
        if (parsed != null)
        {
            return new PipeNameChoice(parsed, null);
        }

        string source = fromEnvironment ? environmentName : "[Pipe] Name";
        return new PipeNameChoice(Default,
            $"Ignoring invalid pipe name '{text}' from {source}: it must not be empty or contain \\, / or :. " +
            $"Using '{DefaultValue}'.");
    }

    public override string ToString() => Value;
}

/// <summary>The pipe name chosen, and the warning to log when the requested one was refused.</summary>
internal sealed class PipeNameChoice
{
    internal PipeNameChoice(PipeName name, string? warning)
    {
        Name = name;
        Warning = warning;
    }

    internal PipeName Name { get; }

    internal string? Warning { get; }
}
