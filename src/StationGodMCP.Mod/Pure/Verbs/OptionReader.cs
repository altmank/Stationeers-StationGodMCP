#nullable enable

using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Shaping;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// A checked call's options as one what reads them: each by its verb name, typed, and only when the what takes it
/// (an option the what does not take reads as absent). JSON null is absent. What an option kind finds wrong with a
/// value it parses goes to Problems, so the call is refused with every problem at once.
/// </summary>
internal sealed class OptionReader
{
    private readonly JObject _given;

    internal OptionReader(WhatSpec what, JObject? given, ShapeRequest? shape = null)
    {
        What = what;
        _given = given ?? new JObject();
        Shape = shape;
    }

    internal WhatSpec What { get; }

    /// <summary>The call's shape: the wire shape with fields, omit and list_limits given as options folded in.</summary>
    internal ShapeRequest? Shape { get; }

    internal List<OptionProblem> Problems { get; } = new List<OptionProblem>();

    internal void Problem(string path, string problem) => Problems.Add(new OptionProblem(path, problem));

    /// <summary>The value as given, when the what takes the option; null when absent.</summary>
    internal JToken? Raw(string name) =>
        What.Takes(name) && _given[name] is { Type: not JTokenType.Null } value ? value : null;

    internal bool Has(string name) => Raw(name) != null;

    internal string? Text(string name) => Raw(name) is { Type: JTokenType.String } text ? (string)text! : null;

    internal bool? Flag(string name) => Raw(name) is { Type: JTokenType.Boolean } flag ? (bool)flag : null;

    internal double? Number(string name) => NumberOf(Raw(name));

    internal long? Integer(string name) =>
        Number(name) is { } number && number == System.Math.Floor(number) ? (long)number : null;

    /// <summary>A list of strings; a single string reads as a list of one.</summary>
    internal IReadOnlyList<string>? Words(string name)
    {
        switch (Raw(name))
        {
            case JValue { Type: JTokenType.String } one:
                return new[] { (string)one! };
            case JArray many:
                List<string> words = new List<string>(many.Count);
                foreach (JToken entry in many)
                {
                    if (entry.Type == JTokenType.String)
                    {
                        words.Add((string)entry!);
                    }
                    else if (entry.Type is JTokenType.Integer or JTokenType.Float)
                    {
                        words.Add(((double)entry).ToString(CultureInfo.InvariantCulture));
                    }
                }

                return words;
            default:
                return null;
        }
    }

    /// <summary>
    /// The default the catalogue gives an option for this call, as text: the first x-default-when form whose conditions
    /// these options meet, else the option's default; null when it gives none.
    /// </summary>
    internal string? DefaultText(string name)
    {
        if (What.Option(name) is not { } option)
        {
            return null;
        }

        foreach (DefaultForm form in option.DefaultForms)
        {
            if (form.Applies(Raw, BaseDefault))
            {
                return form.Default;
            }
        }

        return option.Default;
    }

    /// <summary>The integer the catalogue gives as an option's default for this call, or null when it gives none.</summary>
    internal long? DefaultInteger(string name) =>
        DefaultText(name) is { } text &&
        long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number)
            ? number
            : null;

    /// <summary>The number the catalogue gives as an option's default for this call, or null when it gives none.</summary>
    internal double? DefaultNumber(string name) =>
        DefaultText(name) is { } text &&
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            ? number
            : null;

    /// <summary>The flag the catalogue gives as an option's default for this call, or null when it gives none.</summary>
    internal bool? DefaultFlag(string name) => DefaultText(name) switch
    {
        "true" => true,
        "false" => false,
        _ => null
    };

    // An option's own default as a JSON value (a flag, a number or a word), for a form's condition on it.
    private JToken? BaseDefault(string name)
    {
        if (What.Option(name)?.Default is not { } text)
        {
            return null;
        }

        if (text is "true" or "false")
        {
            return text == "true";
        }

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            ? number == System.Math.Floor(number) ? new JValue((long)number) : new JValue(number)
            : new JValue(text);
    }

    internal static double? NumberOf(JToken? token) =>
        token is { Type: JTokenType.Integer or JTokenType.Float } ? (double)token : null;
}
