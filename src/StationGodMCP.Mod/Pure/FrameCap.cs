#nullable enable

namespace StationGodMCP.Pure;

/// <summary>The opt-in frame cap: [Rig] MaxFps, overridden by the launch argument -sgfps N. Off (null) unless above 0.</summary>
internal static class FrameCap
{
    internal const int DefaultMaxFps = 0;

    internal const string Argument = "-sgfps";

    /// <summary>The cap to apply: the argument's value when the command line has a valid one, else the key's; null = none.</summary>
    internal static int? Choose(int keyValue, string[]? args)
    {
        int value = keyValue;
        for (int i = 0; args != null && i + 1 < args.Length; i++)
        {
            if (string.Equals(args[i], Argument, System.StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int fromArgument))
            {
                value = fromArgument;
            }
        }

        return value > 0 ? value : null;
    }
}
