#nullable enable

using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StationGodMCP.Client;
using Xunit;

namespace StationGodMCP.Tests.Sidecar;

/// <summary>One connection on its own: the welcome's limits and how a write ended.</summary>
public sealed class GameConnectionTests
{
    [Theory]
    [InlineData("""{"limits":{"max_in_flight":2},"features":[]}""", 2, 2)]
    [InlineData("""{"limits":{"max_in_flight":2},"features":["streams"]}""", 2, 256)]
    [InlineData("""{"limits":{"max_in_flight":16,"max_in_flight_per_stream":4,"max_in_flight_per_connection":64},"features":["streams"]}""", 4, 64)]
    [InlineData("""{"features":["streams"]}""", 16, 256)]
    public void TheLimitsComeFromTheWelcomeWithTheirFallbacks(string welcome, int perStream, int perConnection)
    {
        GameConnection connection = Connection(new MemoryStream(), welcome);

        Assert.Equal(perStream, connection.PerStreamLimit);
        Assert.Equal(perConnection, connection.PerConnectionLimit);
    }

    [Fact]
    public async Task ALineOnAClosedConnectionIsNotWritten()
    {
        GameConnection connection = Connection(new MemoryStream(), "{}");
        connection.Close();

        Assert.IsType<Sending.NotWritten>(await connection.SendAsync("{}"));
    }

    [Fact]
    public async Task ClosingDuringAWriteBreaksItAndTheLineWaitingBehindItIsNotWritten()
    {
        StuckStream stream = new();
        GameConnection connection = Connection(stream, "{}");
        Task<Sending> first = connection.SendAsync("{\"n\":1}");
        await stream.Writing.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<Sending> second = connection.SendAsync("{\"n\":2}");

        connection.Close();

        Assert.IsType<Sending.Broken>(await first);
        Assert.IsType<Sending.NotWritten>(await second);
    }

    private static GameConnection Connection(Stream stream, string welcome)
    {
        using JsonDocument document = JsonDocument.Parse(welcome);
        return new GameConnection(new GameTarget.Pipe("unused"), new LineChannel(stream), document.RootElement.Clone());
    }

    // A stream whose writes never finish until cancelled.
    private sealed class StuckStream : MemoryStream
    {
        public TaskCompletionSource Writing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writing.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }
}
