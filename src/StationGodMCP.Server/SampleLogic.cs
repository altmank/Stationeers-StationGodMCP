using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using StationGodMCP.Client;

namespace StationGodMCP.Server;

/// <summary>
/// sample_logic while the catalogue says it runs in the sidecar (x-runs-in: sidecar): read_logic_many at an interval on
/// the real clock, keeping each reading that changed, with elapsed real seconds. A mod whose catalogue no longer says so
/// answers it itself, and the sidecar forwards it like any other tool.
/// </summary>
internal static class SampleLogic
{
    private const int MaximumTargets = 32;
    private const int MaximumSamples = 120;

    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    /// <summary>The samples, invalid_argument for arguments out of range, or the first read's own failure.</summary>
    internal static async Task<CallOutcome> RunAsync(JsonElement arguments,
        Func<JsonElement, Task<CallOutcome>> readLogicMany, CancellationToken cancellation)
    {
        Parsed parsed = Request.Of(arguments);
        if (parsed is Parsed.Invalid invalid)
        {
            return CallOutcome.Refused.Of(ToolFailure.InvalidArgument, invalid.Problem);
        }

        Request request = ((Parsed.Valid)parsed).Request;

        string? gatewayId = request.GatewayId;
        JsonElement readArguments = JsonSerializer.SerializeToElement(new { gateway_id = gatewayId, reads = request.Targets }, Json);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        Stopwatch stopwatch = new();
        Dictionary<int, string> previous = new();
        List<object> changes = [];
        int sampleCount = 0;

        while (true)
        {
            CallOutcome read = await readLogicMany(readArguments).ConfigureAwait(false);
            if (read is not CallOutcome.Answered { Result: var result })
            {
                return read;
            }

            if (result.TryGetProperty("gateway_id", out JsonElement scope) && scope.ValueKind == JsonValueKind.String)
            {
                gatewayId = scope.GetString();
            }

            List<JsonElement> changed = [];
            if (result.TryGetProperty("results", out JsonElement results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement reading in results.EnumerateArray())
                {
                    int index = reading.GetProperty("index").GetInt32();
                    string serialized = reading.GetRawText();
                    if (!previous.TryGetValue(index, out string? prior) || prior != serialized)
                    {
                        previous[index] = serialized;
                        changed.Add(reading.Clone());
                    }
                }
            }

            if (changed.Count > 0)
            {
                changes.Add(new
                {
                    elapsed_seconds = sampleCount == 0 ? 0d : Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
                    readings = changed
                });
            }

            sampleCount++;
            if (!stopwatch.IsRunning)
            {
                stopwatch.Start();
            }

            double remaining = request.DurationSeconds - stopwatch.Elapsed.TotalSeconds;
            if (remaining <= 0d)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromSeconds(Math.Min(request.IntervalSeconds, remaining)), cancellation).ConfigureAwait(false);
        }

        return new CallOutcome.Answered(JsonSerializer.SerializeToElement(new
        {
            gateway_id = gatewayId,
            started_at_utc = startedAt,
            duration_seconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
            interval_seconds = request.IntervalSeconds,
            sample_count = sampleCount,
            change_count = changes.Count,
            changes
        }, Json), Shaped: false);
    }

    private sealed record Request(string? GatewayId, JsonElement[] Targets, double DurationSeconds, double IntervalSeconds)
    {
        // gateway_id may be omitted: the mod then reaches the whole world.
        internal static Parsed Of(JsonElement arguments)
        {
            string? gatewayId = null;
            if (arguments.TryGetProperty("gateway_id", out JsonElement gateway) && gateway.ValueKind != JsonValueKind.Null)
            {
                if (gateway.ValueKind != JsonValueKind.String)
                {
                    return new Parsed.Invalid("Argument 'gateway_id' must be a string.");
                }

                gatewayId = string.IsNullOrWhiteSpace(gateway.GetString()) ? null : gateway.GetString();
            }

            if (!arguments.TryGetProperty("targets", out JsonElement targets) || targets.ValueKind != JsonValueKind.Array ||
                targets.GetArrayLength() is 0 or > MaximumTargets)
            {
                return new Parsed.Invalid($"Argument 'targets' must be an array of 1 to {MaximumTargets} entries.");
            }

            if (Number(arguments, "duration_seconds", 5d) is not { } duration)
            {
                return new Parsed.Invalid("Argument 'duration_seconds' must be a finite number.");
            }

            if (Number(arguments, "interval_seconds", 0.5d) is not { } interval)
            {
                return new Parsed.Invalid("Argument 'interval_seconds' must be a finite number.");
            }

            if (duration is < 0.1d or > 30d)
            {
                return new Parsed.Invalid("Argument 'duration_seconds' must be from 0.1 to 30.");
            }

            if (interval is < 0.05d or > 5d)
            {
                return new Parsed.Invalid("Argument 'interval_seconds' must be from 0.05 to 5.");
            }

            int planned = (int)Math.Ceiling(duration / interval) + 1;
            return planned > MaximumSamples
                ? new Parsed.Invalid(string.Create(CultureInfo.InvariantCulture,
                    $"duration_seconds {duration} at interval_seconds {interval} asks for {planned} samples (the first at 0 s included); at most {MaximumSamples}."))
                : new Parsed.Valid(new Request(gatewayId, targets.EnumerateArray().Select(target => target.Clone()).ToArray(), duration, interval));
        }

        // The argument, its default when absent, null when it is not a finite number.
        private static double? Number(JsonElement arguments, string name, double fallback) =>
            !arguments.TryGetProperty(name, out JsonElement value) ? fallback
            : value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number) ? number
            : null;
    }

    private abstract record Parsed
    {
        private Parsed()
        {
        }

        internal sealed record Valid(Request Request) : Parsed;

        internal sealed record Invalid(string Problem) : Parsed;
    }
}
