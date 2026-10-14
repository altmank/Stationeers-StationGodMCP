#nullable enable

using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using StationGodMCP.Protocol;
using StationGodMCP.Server;
using StationGodMCP.Tests.Hub;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// T-B-15's invariant at the process level: the clients the gate kills are processes, not handles it closes in
/// process. A killed hub must free its mod pipe instance like any abrupt end; the other clients (a subscription held,
/// a call in flight, a half-written line, a bye) are pinned in SubscriptionWireTests and PipeProtocolTests.
/// </summary>
public sealed class ConnectionLifetimeTests
{
    [Fact]
    public async Task AKilledHubProcessFreesItsModPipeInstance()
    {
        using PipeRig rig = new(maxConnections: 1);
        using Process hub = StartHub(rig.Name, out StringBuilder errors);
        try
        {
            await hub.StandardInput.WriteLineAsync(HubCaller.Hello);
            await hub.StandardInput.FlushAsync();
            string? welcome = await hub.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.NotNull(welcome);
            Connection connection = rig.Host.Open.Single();

            hub.Kill(entireProcessTree: false);
            hub.WaitForExit(5000);

            Assert.True(connection.Stopped.WaitOne(5000),
                $"the killed hub's pipe instance stayed taken; hub stderr: {errors}");
        }
        finally
        {
            if (!hub.HasExited)
            {
                hub.Kill(entireProcessTree: true);
            }
        }
    }

    private static Process StartHub(string pipe, out StringBuilder errors)
    {
        StringBuilder collected = new();
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardOutputEncoding = new UTF8Encoding(false)
        };
        start.ArgumentList.Add(typeof(HubSession).Assembly.Location);
        start.ArgumentList.Add("--hub");
        start.ArgumentList.Add("--pipe");
        start.ArgumentList.Add(pipe);
        Process hub = Process.Start(start)!;
        hub.ErrorDataReceived += (_, line) =>
        {
            lock (collected)
            {
                collected.AppendLine(line.Data);
            }
        };
        hub.BeginErrorReadLine();
        errors = collected;
        return hub;
    }
}
