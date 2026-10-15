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
/// cost (x-cost) and what a report call of it counts as (x-cost-report), the lists the writer cuts when the call does
/// not say (x-default-limits), and for a write its write rules.
/// </summary>
internal sealed class WhatSpec
{
    private static readonly IReadOnlyDictionary<string, int> NoLimits = new Dictionary<string, int>(StringComparer.Ordinal);

    private readonly Dictionary<string, OptionSpec> _options = new Dictionary<string, OptionSpec>(StringComparer.Ordinal);

    internal WhatSpec(string verb, string name, string? mainList, string? idList, WriteSpec? write, WhatCost? cost,
        WhatCost? reportCost, IReadOnlyDictionary<string, int>? defaultLimits, params OptionSpec[] options)
    {
        DefaultLimits = defaultLimits ?? NoLimits;
        Verb = verb;
        Name = name;
        MainList = mainList;
        IdList = idList;
        Write = write;
        Cost = cost ?? WhatCost.Unstated;
        ReportCost = reportCost;
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

    /// <summary>What the scheduler counts a report call of this what as (x-cost-report); null where the catalogue states none.</summary>
    internal WhatCost? ReportCost { get; }

    /// <summary>
    /// The most entries the writer keeps of each named top-level list when the call's shape does not limit it
    /// (x-default-limits, 1.33.2's caps); empty where the catalogue states none. The hub lifts them on the script and
    /// program faces.
    /// </summary>
    internal IReadOnlyDictionary<string, int> DefaultLimits { get; }

    /// <summary>
    /// The cost a call of this what is scheduled by: an acting call counts as x-cost; a confirm-route call without
    /// confirm: true is a report and counts as x-cost-report (x-cost where the what states none), reading confirm as
    /// ReportConfirm does.
    /// </summary>
    internal WhatCost CostFor(JObject? parameters) =>
        Write?.Route == "confirm" && !Confirms(parameters) ? ReportCost ?? Cost : Cost;

    // Whether the call says confirm: true, read from the raw params before the options are checked.
    private static bool Confirms(JObject? parameters) =>
        parameters?["confirm"] is { Type: JTokenType.Boolean } confirm && (bool)confirm;

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
internal sealed class WriteSpec
{
    internal WriteSpec(string route, string undo, string undoMark)
    {
        Route = route;
        Undo = undo;
        UndoMark = undoMark;
    }

    public string Route { get; }

    public string Undo { get; }

    public string UndoMark { get; }
}

/// <summary>
/// A what's cost (x-cost, or x-cost-report for a report call): its class (world, plan and job are heavy by nature), the
/// options whose list lengths count its items (per), and the option that sets how long it runs (duration).
/// </summary>
internal sealed class WhatCost
{
    internal WhatCost(CostClass @class, IReadOnlyList<string>? per = null, CostDuration? duration = null)
    {
        Class = @class;
        Per = per;
        Duration = duration;
    }

    public CostClass Class { get; }

    public IReadOnlyList<string>? Per { get; }

    public CostDuration? Duration { get; }

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
internal sealed class CostDuration
{
    internal CostDuration(string option, double maxSeconds)
    {
        Option = option;
        MaxSeconds = maxSeconds;
    }

    public string Option { get; }

    public double MaxSeconds { get; }

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
internal sealed class DefaultForm
{
    internal DefaultForm(JObject when, string @default)
    {
        When = when;
        Default = @default;
    }

    public JObject When { get; }

    public string Default { get; }

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
