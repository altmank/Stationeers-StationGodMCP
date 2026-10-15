#nullable enable

using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure;

namespace StationGodMCP.Protocol;

/// <summary>
/// A job_ended event's job: the job's final view, written as its facts (JobEndFacts) by the thread that writes the
/// event, so reading the facts out of the view costs the game's thread nothing.
/// </summary>
[JsonConverter(typeof(JobEndedFactsConverter))]
internal sealed class JobEndedFacts
{
    internal JobEndedFacts(object finalView) => FinalView = finalView;

    internal object FinalView { get; }
}

internal sealed class JobEndedFactsConverter : JsonConverter
{
    public override bool CanRead => false;

    public override bool CanConvert(Type objectType) => objectType == typeof(JobEndedFacts);

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
        JobEndFacts.Of(JObject.FromObject(((JobEndedFacts)value!).FinalView, serializer)).WriteTo(writer);

    public override object ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer) =>
        throw new NotSupportedException("A job's facts are only written.");
}
