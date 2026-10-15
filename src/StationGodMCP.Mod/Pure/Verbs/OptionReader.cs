#nullable enable

using System;
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
    private List<OptionProblem>? _problems;
    private Func<string, JToken?>? _raw;
    private Func<string, JToken?>? _baseDefault;

    internal OptionReader(WhatSpec what, JObject? given, ShapeRequest? shape = null)
    {
        What = what;
        _given = given ?? new JObject();
        Shape = shape;
    }

    internal WhatSpec What { get; }

    /// <summary>The call's shape: the wire shape with fields, omit and list_limits given as options folded in.</summary>
    internal ShapeRequest? Shape { get; }

    /// <summary>What the option kinds found wrong; made at the first problem.</summary>
    internal List<OptionProblem> Problems => _problems ??= new List<OptionProblem>();

    internal bool HasProblems => _problems is { Count: > 0 };

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
    internal string? DefaultText(string name) => DefaultOf(name)?.Text;

    // The default for this call, parsed once in the catalogue's spec: the first x-default-when form that applies, else
    // the option's own.
    private DefaultValue? DefaultOf(string name)
    {
        if (What.Option(name) is not { } option)
        {
            return null;
        }

        IReadOnlyList<DefaultForm> forms = option.DefaultForms;
        for (int index = 0; index < forms.Count; index++)
        {
            if (forms[index].Applies(_raw ??= Raw, _baseDefault ??= BaseDefault))
            {
                return forms[index].Value;
            }
        }

        return option.DefaultValue;
    }

    /// <summary>The words the catalogue gives as a list option's default ("[status]"), or null when it gives none.</summary>
    internal IReadOnlyList<string>? DefaultWords(string name) => DefaultOf(name)?.Words;

    /// <summary>The words of a default written as a list, "[a, b]".</summary>
    internal static IReadOnlyList<string> WordsOf(string text)
    {
        List<string> words = new List<string>();
        foreach (string word in text.Substring(1, text.Length - 2).Split(','))
        {
            if (word.Trim().Length > 0)
            {
                words.Add(word.Trim());
            }
        }

        return words;
    }

    /// <summary>The integer the catalogue gives as an option's default for this call, or null when it gives none.</summary>
    internal long? DefaultInteger(string name) => DefaultOf(name)?.Integer;

    /// <summary>The number the catalogue gives as an option's default for this call, or null when it gives none.</summary>
    internal double? DefaultNumber(string name) => DefaultOf(name)?.Number;

    /// <summary>The flag the catalogue gives as an option's default for this call, or null when it gives none.</summary>
    internal bool? DefaultFlag(string name) => DefaultOf(name)?.Flag;

    // An option's own default as a JSON value (a flag, a number or a word), for a form's condition on it.
    private JToken? BaseDefault(string name) => What.Option(name)?.DefaultValue?.Json;

    internal static double? NumberOf(JToken? token) =>
        token is { Type: JTokenType.Integer or JTokenType.Float } ? (double)token : null;
}
