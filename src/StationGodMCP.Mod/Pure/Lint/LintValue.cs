#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StationGodMCP.Pure.Lint;

internal enum LintKind : byte
{
    Null,
    Bool,
    Number,
    String,
    Vec,
    Object,
    List,
    Map
}

/// <summary>A value of the lint expression language. Lists and maps are read-only.</summary>
internal readonly struct LintValue
{
    internal static readonly LintValue Null = default;
    internal static readonly LintValue True = new LintValue(LintKind.Bool, 1, null);
    internal static readonly LintValue False = new LintValue(LintKind.Bool, 0, null);
    internal static readonly IReadOnlyList<LintValue> NoValues = new LintValue[0];
    internal static readonly LintValue EmptyList = new LintValue(LintKind.List, 0, NoValues);

    private readonly double _number;
    private readonly object? _ref;

    private LintValue(LintKind kind, double number, object? reference)
    {
        Kind = kind;
        _number = number;
        _ref = reference;
    }

    internal LintKind Kind { get; }

    internal bool IsNull => Kind == LintKind.Null;

    internal static LintValue Of(bool value) => value ? True : False;

    internal static LintValue Of(double value) => new LintValue(LintKind.Number, value, null);

    internal static LintValue Of(double? value) => value.HasValue ? Of(value.Value) : Null;

    internal static LintValue Of(string? value) => value == null ? Null : new LintValue(LintKind.String, 0, value);

    internal static LintValue Of(Vec3 value) => new LintValue(LintKind.Vec, 0, value);

    internal static LintValue Of(Vec3? value) => value.HasValue ? Of(value.Value) : Null;

    internal static LintValue Of(ILintObject? value) => value == null ? Null : new LintValue(LintKind.Object, 0, value);

    internal static LintValue Of(IReadOnlyList<LintValue> values) => new LintValue(LintKind.List, 0, values);

    internal static LintValue Of(IReadOnlyDictionary<string, LintValue> values) => new LintValue(LintKind.Map, 0, values);

    internal static LintValue Objects<T>(IReadOnlyList<T> objects) where T : ILintObject
    {
        LintValue[] values = new LintValue[objects.Count];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = Of(objects[index]);
        }

        return Of(values);
    }

    internal static LintValue Strings(IReadOnlyList<string> strings)
    {
        LintValue[] values = new LintValue[strings.Count];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = Of(strings[index]);
        }

        return Of(values);
    }

    internal bool AsBool => Kind == LintKind.Bool ? _number != 0 : throw Mismatch("bool");

    internal double AsNumber => Kind == LintKind.Number ? _number : throw Mismatch("number");

    internal string AsString => Kind == LintKind.String ? (string)_ref! : throw Mismatch("string");

    internal Vec3 AsVec => Kind == LintKind.Vec ? (Vec3)_ref! : throw Mismatch("vec");

    internal ILintObject AsObject => Kind == LintKind.Object ? (ILintObject)_ref! : throw Mismatch("object");

    internal IReadOnlyList<LintValue> AsList => Kind == LintKind.List ? (IReadOnlyList<LintValue>)_ref! : throw Mismatch("list");

    internal IReadOnlyDictionary<string, LintValue> AsMap =>
        Kind == LintKind.Map ? (IReadOnlyDictionary<string, LintValue>)_ref! : throw Mismatch("map");

    private LintEvaluationException Mismatch(string wanted) =>
        new LintEvaluationException(IsNull ? $"a null where a {wanted} is needed" : $"a {Kind} where a {wanted} is needed");

    /// <summary>Equality as == compares: numbers by value, strings ordinally, objects by identity key, lists by items.</summary>
    internal static bool AreEqual(LintValue a, LintValue b)
    {
        if (a.Kind != b.Kind)
        {
            return false;
        }

        switch (a.Kind)
        {
            case LintKind.Null:
                return true;
            case LintKind.Bool:
            case LintKind.Number:
                return a._number.Equals(b._number);
            case LintKind.String:
                return string.Equals((string)a._ref!, (string)b._ref!, StringComparison.Ordinal);
            case LintKind.Vec:
                return a.AsVec.Equals(b.AsVec);
            case LintKind.Object:
                return ReferenceEquals(a._ref, b._ref) ||
                       string.Equals(a.AsObject.Key, b.AsObject.Key, StringComparison.Ordinal);
            case LintKind.List:
                IReadOnlyList<LintValue> left = a.AsList, right = b.AsList;
                if (left.Count != right.Count)
                {
                    return false;
                }

                for (int index = 0; index < left.Count; index++)
                {
                    if (!AreEqual(left[index], right[index]))
                    {
                        return false;
                    }
                }

                return true;
            default:
                return ReferenceEquals(a._ref, b._ref);
        }
    }

    /// <summary>The value as a message shows it.</summary>
    internal string ToText()
    {
        switch (Kind)
        {
            case LintKind.Null:
                return "null";
            case LintKind.Bool:
                return _number != 0 ? "true" : "false";
            case LintKind.Number:
                return NumberText(_number);
            case LintKind.String:
                return (string)_ref!;
            case LintKind.Vec:
                return AsVec.ToString();
            case LintKind.Object:
                return AsObject.Describe;
            case LintKind.List:
                StringBuilder text = new StringBuilder("[");
                IReadOnlyList<LintValue> items = AsList;
                for (int index = 0; index < items.Count && index < 20; index++)
                {
                    text.Append(index > 0 ? ", " : string.Empty).Append(items[index].ToText());
                }

                return text.Append(items.Count > 20 ? ", ...]" : "]").ToString();
            default:
                StringBuilder map = new StringBuilder("{");
                bool first = true;
                foreach (KeyValuePair<string, LintValue> entry in AsMap)
                {
                    map.Append(first ? string.Empty : ", ").Append(entry.Key).Append(": ").Append(entry.Value.ToText());
                    first = false;
                }

                return map.Append('}').ToString();
        }
    }

    internal static string NumberText(double number) =>
        double.IsPositiveInfinity(number) ? "infinity"
        : double.IsNegativeInfinity(number) ? "-infinity"
        : Math.Abs(number - Math.Round(number)) < 1e-9 && Math.Abs(number) < 1e15
            ? ((long)Math.Round(number)).ToString(CultureInfo.InvariantCulture)
            : number.ToString("0.###", CultureInfo.InvariantCulture);

    public override string ToString() => ToText();
}

/// <summary>A rule could not be evaluated on a subject: reported as a rule_error finding, never thrown past the engine.</summary>
internal sealed class LintEvaluationException : Exception
{
    internal LintEvaluationException(string message)
        : base(message)
    {
    }
}
