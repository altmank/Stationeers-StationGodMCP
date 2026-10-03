using System.Globalization;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// The sidecar's command line (docs/configuration.md, Sidecar options): --pipe (else STATIONGODMCP_PIPE_NAME, else
/// StationGodMCP), or --host (else STATIONGODMCP_HOST) with --port (else STATIONGODMCP_PORT, else 8765) for TCP;
/// --secret-env names the variable of the TCP shared secret (STATIONGODMCP_SECRET);
/// --client names this sidecar in hello; --output-dir (else STATIONGODMCP_OUTPUT_DIR) is where output files go;
/// --inline-limit-kb is the reply size above which a reply goes to a file on its own (200, 0 for never).
/// </summary>
internal sealed record SidecarOptions(ClientOptions Client, OutputFolder Output, int InlineLimitBytes)
{
    internal const int DefaultInlineLimitKb = 200;

    private const int LargestInlineLimitKb = 1024 * 1024;

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

        ClientOptions client = new(target)
        {
            ClientName = Argument(args, "--client"),
            SecretVariable = Argument(args, "--secret-env") ?? ClientOptions.DefaultSecretVariable,
            ClientVersion = Program.ServerVersion,
            ReadEnvironment = environment
        };
        OutputFolder output = OutputFolder.From(Argument(args, "--output-dir"), environment(OutputFolder.EnvironmentVariable));
        return new Parsed.Valid(new SidecarOptions(client, output, limitKb * 1024));
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

    internal abstract record Parsed
    {
        private Parsed()
        {
        }

        internal sealed record Valid(SidecarOptions Options) : Parsed;

        internal sealed record Invalid(string Message) : Parsed;
    }
}
