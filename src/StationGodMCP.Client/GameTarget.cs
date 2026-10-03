using System.Globalization;
using System.IO.Pipes;
using System.Net.Sockets;

namespace StationGodMCP.Client;

/// <summary>
/// Where the game is: a named pipe on this machine or a TCP endpoint. Each knows how to open itself, how long to wait
/// for that, and how it is named in messages.
/// </summary>
public abstract record GameTarget
{
    private const string PipePrefix = @"\\.\pipe\";

    private GameTarget()
    {
    }

    /// <summary>"pipe" or "tcp".</summary>
    public abstract string Transport { get; }

    /// <summary>The target as messages name it: pipe 'StationGodMCP', TCP endpoint host:port.</summary>
    public abstract string Description { get; }

    /// <summary>1 second for the pipe, 3 seconds for TCP (clients.md, Connecting).</summary>
    public abstract TimeSpan DefaultConnectTimeout { get; }

    private protected abstract string EndpointName { get; }

    /// <summary>The open byte stream, or why no game answered.</summary>
    internal abstract Task<Opened> OpenAsync(TimeSpan timeout, CancellationToken cancellation);

    private static string Seconds(TimeSpan timeout) =>
        timeout.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

    /// <summary>The local named pipe \\.\pipe\&lt;Name&gt;, opened for overlapped I/O.</summary>
    public sealed record Pipe(string Name) : GameTarget
    {
        public override string Transport => "pipe";

        public override string Description => $"pipe '{Name}'";

        public override TimeSpan DefaultConnectTimeout => TimeSpan.FromSeconds(1);

        private protected override string EndpointName =>
            Name.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase) ? Name[PipePrefix.Length..] : Name;

        internal override async Task<Opened> OpenAsync(TimeSpan timeout, CancellationToken cancellation)
        {
            NamedPipeClientStream pipe = new(".", EndpointName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(timeout);
            try
            {
                await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
                return new Opened.Stream(pipe);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or
                                                  UnauthorizedAccessException or TimeoutException &&
                                              !cancellation.IsCancellationRequested)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return new Opened.Nothing(
                    $"No StationGodMCP pipe '{EndpointName}' answered within {Seconds(timeout)} s: the game is not running, is " +
                    "not hosting a loaded save, does not have the StationGodMCP mod loaded, or uses another pipe name (the " +
                    "mod's [Pipe] Name or STATIONGODMCP_PIPE_NAME; the sidecar's --pipe), or every instance of the pipe " +
                    "stayed taken that long.");
            }
        }
    }

    /// <summary>A TCP endpoint: the game's [Remote MCP] listener.</summary>
    public sealed record Tcp(string Host, int Port) : GameTarget
    {
        public override string Transport => "tcp";

        public override string Description => $"TCP endpoint {Host}:{Port}";

        public override TimeSpan DefaultConnectTimeout => TimeSpan.FromSeconds(3);

        private protected override string EndpointName => $"{Host}_{Port}";

        internal override async Task<Opened> OpenAsync(TimeSpan timeout, CancellationToken cancellation)
        {
            TcpClient client = new() { NoDelay = true };
            using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(timeout);
            try
            {
                await client.ConnectAsync(Host, Port, deadline.Token).ConfigureAwait(false);
                return new Opened.Stream(client.GetStream());
            }
            catch (Exception exception) when (exception is OperationCanceledException or SocketException or IOException &&
                                              !cancellation.IsCancellationRequested)
            {
                client.Dispose();
                return new Opened.Nothing(
                    $"No StationGodMCP bridge at {Host}:{Port} answered within {Seconds(timeout)} s ({exception.Message}).");
            }
        }
    }
}

/// <summary>Opening a target: a stream, or the message saying why nothing answered.</summary>
internal abstract record Opened
{
    private Opened()
    {
    }

    internal sealed record Stream(System.IO.Stream Value) : Opened;

    internal sealed record Nothing(string Message) : Opened;
}
