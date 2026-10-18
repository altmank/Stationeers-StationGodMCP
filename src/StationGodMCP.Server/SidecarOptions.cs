using System.Globalization;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// The sidecar's command line (docs/configuration.md, Sidecar options): --pipe (else STATIONGODMCP_PIPE_NAME, else
/// StationGodMCP), or --host (else STATIONGODMCP_HOST) with --port (else STATIONGODMCP_PORT, else 8765) for TCP;
/// --secret-env names the variable of the TCP shared secret (STATIONGODMCP_SECRET);
/// --client names this sidecar in hello; --output-dir (else STATIONGODMCP_OUTPUT_DIR) is where output files go;
/// --inline-limit-kb is the reply size above which a reply goes to a file on its own (200, 0 for never); --record appends
/// the recording (docs/architecture/hub.md, Recording) to a file. The hub's options (hub.md, Modes): --hub serves the hub
/// protocol on stdio instead of MCP; --confirmed-ceiling-s caps a confirmed run's reply wait (30, 0 for none);
/// --journal-dir is where journals live; --python is the Python for script workers; --scripts-dir is where the saved
/// scripts live (the four script whats and run_script {name}). --as-player (else STATIONGODMCP_PLAYER) names the player
/// this sidecar's agent acts for on a multiplayer server: every verb call that names none gets it as as_player.
/// </summary>
internal sealed class SidecarOptions
{
    internal const int DefaultInlineLimitKb = 200;

    internal const int DefaultConfirmedCeilingSeconds = 30;

    private const int LargestInlineLimitKb = 1024 * 1024;

    // A day: a confirmed run's reply that would wait longer is a mistake in the option, not a wish.
    private const int LargestConfirmedCeilingSeconds = 24 * 60 * 60;

    // The options that take a value: the word after one is its value, never an option of its own.
    private static readonly string[] ValueOptions =
    [
        "--pipe", "--host", "--port", "--secret-env", "--client", "--inline-limit-kb", "--output-dir", "--record",
        "--confirmed-ceiling-s", "--journal-dir", "--python", "--scripts-dir", "--as-player"
    ];

    internal SidecarOptions(ClientOptions client, OutputFolder output, int inlineLimitBytes, string? recordPath = null)
    {
        Client = client;
        Output = output;
        InlineLimitBytes = inlineLimitBytes;
        RecordPath = recordPath;
    }

    /// <summary>How the game is reached and what this sidecar says in hello.</summary>
    internal ClientOptions Client { get; }

    /// <summary>Where output files go.</summary>
    internal OutputFolder Output { get; }

    /// <summary>The reply size above which a reply goes to a file on its own; 0 for never.</summary>
    internal int InlineLimitBytes { get; }

    /// <summary>The recording's file (hub.md, Recording); null for none.</summary>
    internal string? RecordPath { get; }

    /// <summary>The hub protocol on stdio for one program (hub.md, Modes) instead of MCP.</summary>
    internal bool Hub { get; init; }

    /// <summary>The longest a confirmed run's reply waits, whatever max_wait says; 0 is no ceiling.</summary>
    internal int ConfirmedCeilingSeconds { get; init; } = DefaultConfirmedCeilingSeconds;

    /// <summary>Where journals live (hub.md, The journal).</summary>
    internal string JournalDirectory { get; init; } = DefaultJournalDirectory(Environment.GetEnvironmentVariable);

    /// <summary>Where the saved scripts live (SPEC-NOTES 5b): shared/ and world/&lt;world id&gt;/.</summary>
    internal string ScriptsDirectory { get; init; } = DefaultScriptsDirectory(Environment.GetEnvironmentVariable);

    /// <summary>The Python for script workers: the program, then the arguments that come before the worker's own.</summary>
    internal IReadOnlyList<string> Python { get; init; } = DefaultPython;

    /// <summary>The player every verb call that names none speaks for (as_player); null for the game's own rule.</summary>
    internal string? AsPlayer { get; init; }

    /// <summary>
    /// Whether this hub adopts, at start, the journals other hubs left (hub.md, The journal): a hub serving an MCP client
    /// does; a --hub child never does, since the MCP client's hub is the one that reports them.
    /// </summary>
    internal bool AdoptsJournals => !Hub;

    private static IReadOnlyList<string> DefaultPython => OperatingSystem.IsWindows() ? ["py", "-3"] : ["python3"];

    internal static Parsed Parse(string[] args, Func<string, string?> environment)
    {
        string? pipeVariable = environment("STATIONGODMCP_PIPE_NAME");
        string pipe = Argument(args, "--pipe") ?? (string.IsNullOrWhiteSpace(pipeVariable) ? "StationGodMCP" : pipeVariable.Trim());
        string? host = Argument(args, "--host") ?? environment("STATIONGODMCP_HOST");
        GameTarget target = new GameTarget.Pipe(pipe);
        if (!string.IsNullOrWhiteSpace(host))
        {
            string portText = Argument(args, "--port") ?? environment("STATIONGODMCP_PORT") ?? "8765";
            if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is < 1 or > 65535)
            {
                return new Parsed.Invalid($"StationGodMCP remote port '{portText}' must be between 1 and 65535.");
            }

            target = new GameTarget.Tcp(host.Trim(), port);
        }

        string limitText = Argument(args, "--inline-limit-kb") ?? DefaultInlineLimitKb.ToString(CultureInfo.InvariantCulture);
        if (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out int limitKb) ||
            limitKb > LargestInlineLimitKb)
        {
            return new Parsed.Invalid(
                $"--inline-limit-kb '{limitText}' must be a whole number of KB from 0 (never) to {LargestInlineLimitKb}.");
        }

        string ceilingText = Argument(args, "--confirmed-ceiling-s") ??
                             DefaultConfirmedCeilingSeconds.ToString(CultureInfo.InvariantCulture);
        if (!int.TryParse(ceilingText, NumberStyles.None, CultureInfo.InvariantCulture, out int ceiling) ||
            ceiling > LargestConfirmedCeilingSeconds)
        {
            return new Parsed.Invalid(
                $"--confirmed-ceiling-s '{ceilingText}' must be a whole number of seconds from 0 (no ceiling) to " +
                $"{LargestConfirmedCeilingSeconds}.");
        }

        string? journal = Argument(args, "--journal-dir");
        if (journal != null && string.IsNullOrWhiteSpace(journal))
        {
            return new Parsed.Invalid("--journal-dir must name a folder.");
        }

        string? python = Argument(args, "--python");
        if (python != null && string.IsNullOrWhiteSpace(python))
        {
            return new Parsed.Invalid("--python must name the Python program for script workers.");
        }

        string? scripts = Argument(args, "--scripts-dir");
        if (scripts != null && string.IsNullOrWhiteSpace(scripts))
        {
            return new Parsed.Invalid("--scripts-dir must name a folder.");
        }

        string? asPlayer = Argument(args, "--as-player") ?? environment("STATIONGODMCP_PLAYER");
        ClientOptions client = new(target)
        {
            ClientName = Argument(args, "--client"),
            SecretVariable = Argument(args, "--secret-env") ?? ClientOptions.DefaultSecretVariable,
            ClientVersion = Program.ServerVersion,
            ReadEnvironment = environment
        };
        OutputFolder output = OutputFolder.From(Argument(args, "--output-dir"), environment(OutputFolder.EnvironmentVariable));
        string? record = Argument(args, "--record");
        return new Parsed.Valid(new SidecarOptions(client, output, limitKb * 1024,
            string.IsNullOrWhiteSpace(record) ? null : record.Trim())
        {
            Hub = Flag(args, "--hub"),
            ConfirmedCeilingSeconds = ceiling,
            JournalDirectory = journal != null ? Path.GetFullPath(journal.Trim()) : DefaultJournalDirectory(environment),
            ScriptsDirectory = scripts != null ? Path.GetFullPath(scripts.Trim()) : DefaultScriptsDirectory(environment),
            Python = python != null ? [python.Trim()] : DefaultPython,
            AsPlayer = string.IsNullOrWhiteSpace(asPlayer) ? null : asPlayer.Trim()
        });
    }

    /// <summary>%LOCALAPPDATA%\StationGodMCP\journal on Windows, else $XDG_STATE_HOME/stationgodmcp/journal.</summary>
    internal static string DefaultJournalDirectory(Func<string, string?> environment)
    {
        if (OperatingSystem.IsWindows())
        {
            string local = environment("LOCALAPPDATA") is { Length: > 0 } given
                ? given
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "StationGodMCP", "journal");
        }

        // XDG's own default when the variable is unset or empty.
        string state = environment("XDG_STATE_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(environment("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "state");
        return Path.Combine(state, "stationgodmcp", "journal");
    }

    /// <summary>%LOCALAPPDATA%\StationGodMCP\scripts on Windows, else $XDG_DATA_HOME/stationgodmcp/scripts.</summary>
    internal static string DefaultScriptsDirectory(Func<string, string?> environment)
    {
        if (OperatingSystem.IsWindows())
        {
            string local = environment("LOCALAPPDATA") is { Length: > 0 } given
                ? given
                : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(local, "StationGodMCP", "scripts");
        }

        string data = environment("XDG_DATA_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(environment("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share");
        return Path.Combine(data, "stationgodmcp", "scripts");
    }

    private static string? Argument(string[] args, string name)
    {
        for (int index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    // A flag given on its own: the value of an option that takes one ("--client --hub") is skipped.
    private static bool Flag(string[] args, string name)
    {
        for (int index = 0; index < args.Length; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            foreach (string option in ValueOptions)
            {
                if (args[index].Equals(option, StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                    break;
                }
            }
        }

        return false;
    }

    internal abstract class Parsed
    {
        private Parsed()
        {
        }

        internal sealed class Valid(SidecarOptions options) : Parsed
        {
            internal SidecarOptions Options { get; } = options;
        }

        internal sealed class Invalid(string message) : Parsed
        {
            internal string Message { get; } = message;
        }
    }
}
