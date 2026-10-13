#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Catalogue;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// The verbs the mod answers as methods, in catalogue order, each with its whats and their options: the verb model of
/// catalogue/verbs, generated into the options classes under Generated (VerbSurface.Generated.cs lists them).
/// run_script is the hub's own and is not here.
/// </summary>
internal static partial class VerbSurface
{
    /// <summary>The key of a whatless verb's one what (undo, help).</summary>
    internal const string WhatlessKey = "-";

    private static readonly Lazy<Table> Built = new Lazy<Table>(() => new Table(Generated()));

    internal static IReadOnlyList<VerbSpec> Verbs => Built.Value.Verbs;

    /// <summary>The verb names, in catalogue order.</summary>
    internal static IReadOnlyList<string> VerbNames => Built.Value.Names;

    internal static VerbSpec? Verb(string? name) =>
        name != null && Built.Value.ByName.TryGetValue(name, out VerbSpec? verb) ? verb : null;

    private sealed class Table
    {
        internal Table(IReadOnlyList<VerbSpec> verbs)
        {
            Verbs = verbs;
            List<string> names = new List<string>(verbs.Count);
            foreach (VerbSpec verb in verbs)
            {
                ByName.Add(verb.Name, verb);
                names.Add(verb.Name);
            }

            Names = names;
        }

        internal IReadOnlyList<VerbSpec> Verbs { get; }

        internal IReadOnlyList<string> Names { get; }

        internal Dictionary<string, VerbSpec> ByName { get; } = new Dictionary<string, VerbSpec>(StringComparer.Ordinal);
    }
}

/// <summary>One verb: whether it only reads, and its whats in catalogue order (a whatless verb has the one what "-").</summary>
internal sealed class VerbSpec
{
    private readonly Dictionary<string, WhatSpec> _whats = new Dictionary<string, WhatSpec>(StringComparer.Ordinal);

    internal VerbSpec(string name, bool readOnly, params WhatSpec[] whats)
    {
        Name = name;
        ReadOnly = readOnly;
        Whats = whats;
        List<string> names = new List<string>(whats.Length);
        foreach (WhatSpec what in whats)
        {
            _whats.Add(what.Name, what);
            names.Add(what.Name);
        }

        WhatNames = names;
        HasWhats = !(whats.Length == 1 && whats[0].Name == VerbSurface.WhatlessKey);
    }

    internal string Name { get; }

    internal bool ReadOnly { get; }

    internal bool HasWhats { get; }

    internal IReadOnlyList<WhatSpec> Whats { get; }

    internal IReadOnlyList<string> WhatNames { get; }

    /// <summary>Help takes no shaping options; every other verb takes fields, omit and list_limits.</summary>
    internal bool TakesShaping => Name != "help";

    /// <summary>The what a call names: by name, or the one what of a whatless verb; null when there is none.</summary>
    internal WhatSpec? What(string? name) =>
        _whats.TryGetValue(HasWhats ? name ?? string.Empty : VerbSurface.WhatlessKey, out WhatSpec? what) ? what : null;
}

/// <summary>
/// One what: its options in catalogue order, the reply's main list, the list option an id list fills (id_list), its
/// cost (x-cost), and for a write its write rules.
/// </summary>
internal sealed class WhatSpec
{
    private readonly Dictionary<string, OptionSpec> _options = new Dictionary<string, OptionSpec>(StringComparer.Ordinal);

    internal WhatSpec(string verb, string name, string? mainList, string? idList, WriteSpec? write, WhatCost? cost,
        params OptionSpec[] options)
    {
        Verb = verb;
        Name = name;
        MainList = mainList;
        IdList = idList;
        Write = write;
        Cost = cost ?? WhatCost.Unstated;
        List<string> names = new List<string>(options.Length);
        foreach (OptionSpec option in options)
        {
            _options.Add(option.Name, option);
            names.Add(option.Name);
        }

        OptionNames = names;
        Label = name == VerbSurface.WhatlessKey ? verb : verb + " " + name;
    }

    internal string Verb { get; }

    /// <summary>The what's name; "-" for a whatless verb.</summary>
    internal string Name { get; }

    /// <summary>Its name in messages: read logic, or undo.</summary>
    internal string Label { get; }

    internal IReadOnlyList<string> OptionNames { get; }

    internal string? MainList { get; }

    internal WriteSpec? Write { get; }

    /// <summary>How the scheduler counts a call of this what (x-cost); bounded, one item, where the catalogue states none.</summary>
    internal WhatCost Cost { get; }

    /// <summary>The what's name in stats, the cost predictor and profiling: find.thing, or undo for a whatless verb.</summary>
    internal string Key => Name == VerbSurface.WhatlessKey ? Verb : Verb + "." + Name;

    /// <summary>The list option whose entries an id list becomes (move item's moves); null where it answers rows.</summary>
    internal string? IdList { get; }

    internal bool Takes(string option) => _options.ContainsKey(option);

    internal OptionSpec? Option(string name) => _options.TryGetValue(name, out OptionSpec? option) ? option : null;

    /// <summary>True when the id option takes a list of ids.</summary>
    internal bool IdTakesList => Option("id")?.Takes("array") == true;

    /// <summary>True when an id list answers one row per id: the id takes a list and no id_list folds it.</summary>
    internal bool TakesIdList => IdTakesList && IdList == null;
}

/// <summary>
/// A write what's rules: route (acts: typed ids act at once; confirm: reports until confirm), undo class (undo, cannot,
/// mixed) and the undo mark its report rows carry (x-undo-mark).
/// </summary>
internal sealed record WriteSpec(string Route, string Undo, string UndoMark);

/// <summary>
/// A what's cost (x-cost): its class (world, plan and job are heavy by nature), the options whose list lengths count
/// its items (per), and the option that sets how long it runs (duration).
/// </summary>
internal sealed record WhatCost(CostClass Class, IReadOnlyList<string>? Per = null, CostDuration? Duration = null)
{
    internal static WhatCost Unstated { get; } = new WhatCost(CostClass.Bounded);

    /// <summary>The cost at these options: the class, with the per options' list lengths added (at least 1).</summary>
    internal CallCost At(JObject? options)
    {
        int items = 0;
        foreach (string option in Per ?? Array.Empty<string>())
        {
            items += options?[option] is JArray list ? list.Count : 0;
        }

        return new CallCost(Class, Math.Max(items, 1));
    }

    /// <summary>The milliseconds a call runs for, which its deadline adds; 0 for a what with no duration.</summary>
    internal int DurationMs(JObject? options) => Duration?.Ms(options) ?? 0;
}

/// <summary>
/// The option that sets how many seconds a call runs (x-cost duration), and its most: the option as given, at most
/// max_s; max_s when it is left out.
/// </summary>
internal sealed record CostDuration(string Option, double MaxSeconds)
{
    internal int Ms(JObject? options)
    {
        double seconds = OptionReader.NumberOf(options?[Option]) is { } given
            ? Math.Max(0.0, Math.Min(given, MaxSeconds))
            : MaxSeconds;
        return (int)Math.Ceiling(seconds * 1000.0);
    }
}

/// <summary>
/// One option: its JSON schema, whether it is required, the catalogue's default text when it gives one, and the forms
/// that give another default when other options say so (x-default-when, in order: the first that applies).
/// </summary>
internal sealed class OptionSpec
{
    private readonly Lazy<JObject> _schema;
    private readonly Lazy<IReadOnlyList<string>> _types;
    private readonly Lazy<IReadOnlyList<DefaultForm>> _forms;

    internal OptionSpec(string name, string schema, bool required = false, string? defaultText = null,
        string? defaultWhen = null)
    {
        Name = name;
        Required = required;
        Default = defaultText;
        _schema = new Lazy<JObject>(() => JObject.Parse(schema));
        _types = new Lazy<IReadOnlyList<string>>(() => SchemaTypes.Of(_schema.Value));
        _forms = new Lazy<IReadOnlyList<DefaultForm>>(() => DefaultForm.Parse(defaultWhen));
    }

    /// <summary>The x-default-when forms, in catalogue order; empty for most options.</summary>
    internal IReadOnlyList<DefaultForm> DefaultForms => _forms.Value;

    internal string Name { get; }

    internal JObject Schema => _schema.Value;

    internal bool Required { get; }

    /// <summary>The catalogue's default, as text ("0", "nearest"); null when it gives none.</summary>
    internal string? Default { get; }

    /// <summary>The schema's types, in order; empty when it names none.</summary>
    internal IReadOnlyList<string> Types => _types.Value;

    /// <summary>The option's default as a number; null when it has none or it is no number.</summary>
    internal double? DefaultNumber =>
        double.TryParse(Default, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture,
            out double number)
            ? number
            : null;

    internal bool Takes(string type)
    {
        foreach (string listed in Types)
        {
            if (listed == type)
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// One x-default-when form: the default it gives (as text) when every condition holds. A condition is an option and
/// either {"present": true} (the option is given) or a value the option equals, as given or else by its own default.
/// </summary>
internal sealed record DefaultForm(JObject When, string Default)
{
    internal static IReadOnlyList<DefaultForm> Parse(string? forms)
    {
        if (forms == null)
        {
            return Array.Empty<DefaultForm>();
        }

        List<DefaultForm> parsed = new List<DefaultForm>();
        foreach (JToken form in JArray.Parse(forms))
        {
            parsed.Add(new DefaultForm((JObject)form["when"]!, (string)form["default"]!));
        }

        return parsed;
    }

    /// <summary>True when every condition holds for these options: given names an option's value as given (null when absent).</summary>
    internal bool Applies(Func<string, JToken?> given, Func<string, JToken?> defaulted)
    {
        foreach (JProperty condition in When.Properties())
        {
            JToken? value = given(condition.Name);
            bool holds = condition.Value is JObject test && test["present"] is { Type: JTokenType.Boolean } present
                ? (value != null) == (bool)present
                : JToken.DeepEquals(value ?? defaulted(condition.Name), condition.Value);
            if (!holds)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The type names a JSON schema lists in type (one, or a list).</summary>
internal static class SchemaTypes
{
    internal static IReadOnlyList<string> Of(JObject schema)
    {
        List<string> types = new List<string>(2);
        switch (schema["type"])
        {
            case JValue single when single.Type == JTokenType.String:
                types.Add((string)single!);
                break;
            case JArray several:
                foreach (JToken type in several)
                {
                    if (type.Type == JTokenType.String)
                    {
                        types.Add((string)type!);
                    }
                }

                break;
        }

        return types;
    }
}
