using System.Text;
using System.Text.Json;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

internal static class Program
{
    // Reported in the initialize response. build.ps1 checks it matches StationGodMCP.Server.csproj and the mod.
    internal const string ServerVersion = "1.28.2";

    public static async Task<int> Main(string[] args)
    {
        switch (SidecarOptions.Parse(args, Environment.GetEnvironmentVariable))
        {
            case SidecarOptions.Parsed.Valid valid:
                await ServeAsync(valid.Options, Console.OpenStandardInput(), Console.OpenStandardOutput());
                return 0;
            case SidecarOptions.Parsed.Invalid invalid:
                await Console.Error.WriteLineAsync($"[StationGodMCP] {invalid.Message}");
                return 2;
            default:
                return 2;
        }
    }

    /// <summary>
    /// MCP over newline-delimited JSON-RPC on the two streams until the input ends. Messages are handled as they come,
    /// several at once, so a slow tool call does not hold up the next; responses and notifications are written whole,
    /// one line at a time.
    /// </summary>
    internal static async Task ServeAsync(SidecarOptions options, Stream input, Stream output)
    {
        using StreamReader reader = new(input, new UTF8Encoding(false), true, 4096, leaveOpen: true);
        await using StreamWriter writer = new(output, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
        using SemaphoreSlim writing = new(1, 1);

        async Task WriteAsync(string line)
        {
            await writing.WaitAsync().ConfigureAwait(false);
            try
            {
                await writer.WriteLineAsync(line).ConfigureAwait(false);
            }
            finally
            {
                writing.Release();
            }
        }

        await using McpAdapter adapter = new(options, WriteAsync);
        List<Task> running = [];
        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            running.RemoveAll(task => task.IsCompleted);
            string message = line;
            running.Add(Task.Run(async () =>
            {
                if (await adapter.HandleAsync(message).ConfigureAwait(false) is { } response)
                {
                    await WriteAsync(response).ConfigureAwait(false);
                }
            }));
        }

        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>Every tool's input schema as the built-in tools/list publishes it, by tool name.</summary>
    internal static IReadOnlyDictionary<string, JsonElement> InputSchemas => ToolCatalogue.InputSchemas;

    /// <summary>One message through a sidecar of its own, closed afterwards.</summary>
    internal static async Task<string?> HandleMcpMessageAsync(string line, GameTransportSettings transport,
        OutputFolder? output = null)
    {
        await using McpAdapter adapter = new(new SidecarOptions(new ClientOptions(transport.Target),
            output ?? OutputFolder.Default, SidecarOptions.DefaultInlineLimitKb * 1024));
        return await adapter.HandleAsync(line);
    }

    /// <summary>Where a one-message sidecar finds the game.</summary>
    internal sealed record GameTransportSettings(GameTarget Target)
    {
        internal static GameTransportSettings ForPipe(string pipeName) => new(new GameTarget.Pipe(pipeName));
    }
}
