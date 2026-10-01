#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure.Lint;

/// <summary>
/// The collection forms over a list, with an optional lambda: any, all, count, sum, min, max, first, map, filter,
/// flat_map, sort_by, distinct. Lists are walked with plain loops.
/// </summary>
internal static class LintForms
{
    internal static readonly Dictionary<string, string> Docs = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["has"] = "has(x) -> bool: x is not null. In 'has(x) and ...', 'if(has(x), ...)' x reads as not null.",
        ["if"] = "if(condition, then, else) -> T: only the branch taken is evaluated.",
        ["any"] = "any(xs: list<T>, x => bool) -> bool",
        ["all"] = "all(xs: list<T>, x => bool) -> bool (true for an empty list)",
        ["count"] = "count(xs: list<T>, x => bool?) -> number: items, or items the lambda holds for",
        ["sum"] = "sum(xs: list<T>, x => number?) -> number (0 for an empty list)",
        ["min"] = "min(xs: list<T>, x => number?) -> number? (null for an empty list); min(a, b, ...) -> number",
        ["max"] = "max(xs: list<T>, x => number?) -> number? (null for an empty list); max(a, b, ...) -> number",
        ["first"] = "first(xs: list<T>, x => bool?) -> T?: the first item, or the first the lambda holds for",
        ["map"] = "map(xs: list<T>, x => U) -> list<U>",
        ["filter"] = "filter(xs: list<T>, x => bool) -> list<T>",
        ["flat_map"] = "flat_map(xs: list<T>, x => list<U>) -> list<U>",
        ["sort_by"] = "sort_by(xs: list<T>, x => number or string) -> list<T>, smallest first",
        ["distinct"] = "distinct(xs: list<T>) -> list<T>: each item once (objects by identity)"
    };

    internal static LintCompiled Build(CallNode call, LintType element, LintEval items, int slot, LintEval? body,
        LintType bodyType)
    {
        LintNode? lambda = call.Args.Count > 1 ? ((LambdaNode)call.Args[1]).Body : null;
        switch (call.Name)
        {
            case "any":
            case "all":
                RequireBody(call, lambda, bodyType, LintType.Bool);
                bool all = call.Name == "all";
                return new LintCompiled(frame =>
                {
                    foreach (LintValue item in items(frame).AsList)
                    {
                        frame.Set(slot, item);
                        if (body!(frame).AsBool != all)
                        {
                            return LintValue.Of(!all);
                        }
                    }

                    return LintValue.Of(all);
                }, LintType.Bool);
            case "count":
                if (body == null)
                {
                    return new LintCompiled(frame => LintValue.Of(items(frame).AsList.Count), LintType.Number);
                }

                RequireBody(call, lambda, bodyType, LintType.Bool);
                return new LintCompiled(frame =>
                {
                    int count = 0;
                    foreach (LintValue item in items(frame).AsList)
                    {
                        frame.Set(slot, item);
                        count += body(frame).AsBool ? 1 : 0;
                    }

                    return LintValue.Of(count);
                }, LintType.Number);
            case "sum":
            case "min":
            case "max":
                return Aggregate(call, element, items, slot, body, bodyType, lambda);
            case "first":
                if (body != null)
                {
                    RequireBody(call, lambda, bodyType, LintType.Bool);
                }

                return new LintCompiled(frame =>
                {
                    foreach (LintValue item in items(frame).AsList)
                    {
                        if (body == null)
                        {
                            return item;
                        }

                        frame.Set(slot, item);
                        if (body(frame).AsBool)
                        {
                            return item;
                        }
                    }

                    return LintValue.Null;
                }, LintType.Nullable(element));
            case "map":
                return new LintCompiled(frame =>
                {
                    IReadOnlyList<LintValue> list = items(frame).AsList;
                    LintValue[] mapped = new LintValue[list.Count];
                    for (int index = 0; index < mapped.Length; index++)
                    {
                        frame.Set(slot, list[index]);
                        mapped[index] = body!(frame);
                    }

                    return LintValue.Of(mapped);
                }, LintType.ListOf(bodyType));
            case "filter":
                RequireBody(call, lambda, bodyType, LintType.Bool);
                return new LintCompiled(frame =>
                {
                    IReadOnlyList<LintValue> list = items(frame).AsList;
                    List<LintValue> kept = new List<LintValue>(list.Count);
                    foreach (LintValue item in list)
                    {
                        frame.Set(slot, item);
                        if (body!(frame).AsBool)
                        {
                            kept.Add(item);
                        }
                    }

                    return LintValue.Of(kept);
                }, LintType.ListOf(element));
            case "flat_map":
                if (bodyType.IsNullable || !(bodyType is ListType inner))
                {
                    throw new LintSyntaxException($"flat_map's lambda gives a list, not a {bodyType.Name}", lambda!.Position);
                }

                return new LintCompiled(frame =>
                {
                    List<LintValue> joined = new List<LintValue>();
                    foreach (LintValue item in items(frame).AsList)
                    {
                        frame.Set(slot, item);
                        foreach (LintValue each in body!(frame).AsList)
                        {
                            joined.Add(each);
                        }
                    }

                    return LintValue.Of(joined);
                }, LintType.ListOf(inner.Element));
            case "sort_by":
                bool text = ReferenceEquals(bodyType, LintType.String);
                if (!text)
                {
                    RequireBody(call, lambda, bodyType, LintType.Number);
                }

                return new LintCompiled(frame =>
                {
                    IReadOnlyList<LintValue> list = items(frame).AsList;
                    List<(LintValue Key, LintValue Item, int Order)> keyed = new List<(LintValue, LintValue, int)>(list.Count);
                    for (int index = 0; index < list.Count; index++)
                    {
                        frame.Set(slot, list[index]);
                        keyed.Add((body!(frame), list[index], index));
                    }

                    keyed.Sort((a, b) =>
                    {
                        int order = text ? string.CompareOrdinal(a.Key.AsString, b.Key.AsString)
                            : a.Key.AsNumber.CompareTo(b.Key.AsNumber);
                        return order != 0 ? order : a.Order.CompareTo(b.Order);
                    });
                    LintValue[] sorted = new LintValue[keyed.Count];
                    for (int index = 0; index < sorted.Length; index++)
                    {
                        sorted[index] = keyed[index].Item;
                    }

                    return LintValue.Of(sorted);
                }, LintType.ListOf(element));
            default:
                return new LintCompiled(frame =>
                {
                    List<LintValue> unique = new List<LintValue>();
                    HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (LintValue item in items(frame).AsList)
                    {
                        string key = item.Kind == LintKind.Object ? item.AsObject.Key : item.Kind + ":" + item.ToText();
                        if (seen.Add(key))
                        {
                            unique.Add(item);
                        }
                    }

                    return LintValue.Of(unique);
                }, LintType.ListOf(element));
        }
    }

    private static LintCompiled Aggregate(CallNode call, LintType element, LintEval items, int slot, LintEval? body,
        LintType bodyType, LintNode? lambda)
    {
        if (body != null)
        {
            RequireBody(call, lambda, bodyType, LintType.Number);
        }
        else
        {
            LintCompiler.Require(element, LintType.Number, call.Args[0]);
        }

        string name = call.Name;
        return new LintCompiled(frame =>
        {
            double total = 0;
            double best = name == "min" ? double.PositiveInfinity : double.NegativeInfinity;
            int count = 0;
            foreach (LintValue item in items(frame).AsList)
            {
                double value;
                if (body != null)
                {
                    frame.Set(slot, item);
                    value = body(frame).AsNumber;
                }
                else
                {
                    value = item.AsNumber;
                }

                total += value;
                best = name == "min" ? Math.Min(best, value) : Math.Max(best, value);
                count++;
            }

            return name == "sum" ? LintValue.Of(total) : count == 0 ? LintValue.Null : LintValue.Of(best);
        }, name == "sum" ? LintType.Number : LintType.Nullable(LintType.Number));
    }

    private static void RequireBody(CallNode call, LintNode? lambda, LintType bodyType, LintType wanted)
    {
        if (lambda == null)
        {
            throw new LintSyntaxException($"{call.Name} needs a lambda: {call.Name}(xs, x => ...)", call.Position);
        }

        LintCompiler.Require(bodyType, wanted, lambda);
    }
}
