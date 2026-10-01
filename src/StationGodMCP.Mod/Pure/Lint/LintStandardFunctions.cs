#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace StationGodMCP.Pure.Lint;

/// <summary>
/// The library functions that need nothing but the lint model: text, numbers, geometry, flow through devices, chip
/// programs and gases. Each is one registration.
/// </summary>
internal static class LintStandardFunctions
{
    internal static void Register(LintLibrary library)
    {
        Text(library);
        Numbers(library);
        Geometry(library);
        LintFlow.Register(library);
        LintChipPrograms.Register(library);
        LintGases.Register(library);
    }

    private static void Text(LintLibrary library)
    {
        library
            .Add(new LintFunction("len", "(s: string) -> number", "Characters in a string.",
                call => LintValue.Of(call[0].AsString.Length)))
            .Add(new LintFunction("contains", "(s: string, part: string) -> bool", "The string holds part (case-sensitive).",
                call => LintValue.Of(call[0].AsString.IndexOf(call[1].AsString, StringComparison.Ordinal) >= 0)))
            .Add(new LintFunction("starts_with", "(s: string, part: string) -> bool", "The string starts with part.",
                call => LintValue.Of(call[0].AsString.StartsWith(call[1].AsString, StringComparison.Ordinal))))
            .Add(new LintFunction("ends_with", "(s: string, part: string) -> bool", "The string ends with part.",
                call => LintValue.Of(call[0].AsString.EndsWith(call[1].AsString, StringComparison.Ordinal))))
            .Add(new LintFunction("lower", "(s: string) -> string", "Lower case.",
                call => LintValue.Of(call[0].AsString.ToLowerInvariant())))
            .Add(new LintFunction("upper", "(s: string) -> string", "Upper case.",
                call => LintValue.Of(call[0].AsString.ToUpperInvariant())))
            .Add(new LintFunction("trim_end", "(s: string, chars?: string) -> string",
                "The string without trailing characters from chars (default white space).",
                call => LintValue.Of(call.Count > 1 ? call[0].AsString.TrimEnd(call[1].AsString.ToCharArray())
                    : call[0].AsString.TrimEnd())))
            .Add(new LintFunction("str", "(value: any) -> string", "Any value as text, as a message shows it.",
                call => LintValue.Of(call[0].ToText())))
            .Add(new LintFunction("format", "(n: number, pattern: string) -> string",
                "A number with a .NET format pattern, e.g. format(d, \"0.00\").",
                call => LintValue.Of(call[0].AsNumber.ToString(call[1].AsString, CultureInfo.InvariantCulture))))
            .Add(new LintFunction("join", "(items: list<any>, separator?: string) -> string",
                "Items as text, joined by the separator (default \", \").",
                call =>
                {
                    StringBuilder text = new StringBuilder();
                    string separator = call.Count > 1 ? call[1].AsString : ", ";
                    IReadOnlyList<LintValue> items = call[0].AsList;
                    for (int index = 0; index < items.Count; index++)
                    {
                        text.Append(index > 0 ? separator : string.Empty).Append(items[index].ToText());
                    }

                    return LintValue.Of(text.ToString());
                }))
            .Add(new LintFunction("keys", "(m: map<any>) -> list<string>", "A map's keys, sorted.",
                call =>
                {
                    List<string> keys = new List<string>(call[0].AsMap.Keys);
                    keys.Sort(StringComparer.Ordinal);
                    return LintValue.Strings(keys);
                }));
    }

    private static void Numbers(LintLibrary library)
    {
        library
            .Add(new LintFunction("abs", "(n: number) -> number", "Absolute value.", call => LintValue.Of(Math.Abs(call[0].AsNumber))))
            .Add(new LintFunction("round", "(n: number, digits?: number) -> number", "Rounded (half away from zero).",
                call => LintValue.Of(Math.Round(call[0].AsNumber, call.Count > 1 ? (int)call[1].AsNumber : 0,
                    MidpointRounding.AwayFromZero))))
            .Add(new LintFunction("floor", "(n: number) -> number", "Rounded down.", call => LintValue.Of(Math.Floor(call[0].AsNumber))))
            .Add(new LintFunction("ceil", "(n: number) -> number", "Rounded up.", call => LintValue.Of(Math.Ceiling(call[0].AsNumber))))
            .Add(new LintFunction("sqrt", "(n: number) -> number", "Square root.", call => LintValue.Of(Math.Sqrt(call[0].AsNumber))))
            .Add(new LintFunction("is_infinite", "(n: number) -> bool", "Infinite (mesh_overlap of one box inside another).",
                call => LintValue.Of(double.IsInfinity(call[0].AsNumber))));
    }

    private static void Geometry(LintLibrary library)
    {
        library
            .Add(new LintFunction("distance", "(a: any, b: any) -> number",
                "Metres between two places: vecs, or things, cells, ports, networks (their positions).",
                call => LintValue.Of((PointOf(call[0]) - PointOf(call[1])).Length)))
            .Add(new LintFunction("cells_of", "(x: any) -> list<cell>",
                "The cells of a thing (its cells), a port (its joining cell), a network (its members' cells), a cell (itself).",
                CellsOf))
            .Add(new LintFunction("neighbors", "(c: cell, axis?: string) -> list<cell>",
                "The cells beside a cell, one cell size away: all six, or along one axis (x, y, z: two) or one side (+x ... -z).",
                Neighbors))
            .Add(new LintFunction("mesh_overlap", "(a: thing, b: thing) -> number",
                "How deep two things' mesh boxes run into each other on the shallowest axis, metres; infinite when one " +
                "holds the other, 0 or less when they only touch or are apart, 0 when either has no mesh box. A thin " +
                "panel wholly inside the other's extent on an axis clashes however thin it is (Box3.ClashDepth).",
                MeshOverlap))
            .Add(new LintFunction("shares_cell", "(a: thing, b: thing) -> bool", "The two register in a common cell (a device on a pipe).",
                SharesCell))
            .Add(new LintFunction("seam_sections", "(x: thing) -> number?",
                "How many 2 m wall sections a face-mounted thing's mesh rectangle spans; null when it is not face-mounted.",
                call =>
                {
                    LintValue mount = call[0].AsObject.Get(LintModel.Thing["mounted"]);
                    return mount.IsNull ? LintValue.Null : mount.AsObject.Get(LintModel.Mount["sections"]);
                }));
    }

    internal static Vec3 PointOf(LintValue value)
    {
        if (value.Kind == LintKind.Vec)
        {
            return value.AsVec;
        }

        if (value.Kind == LintKind.Object && value.AsObject.Position is Vec3 at)
        {
            return at;
        }

        throw new LintEvaluationException($"{value.ToText()} has no position");
    }

    private static LintValue CellsOf(LintCall call)
    {
        ILintObject x = call[0].Kind == LintKind.Object
            ? call[0].AsObject
            : throw new LintEvaluationException("cells_of needs a thing, port, network or cell");
        if (ReferenceEquals(x.Type, LintModel.Thing))
        {
            return x.Get(LintModel.Thing["cells"]);
        }

        if (ReferenceEquals(x.Type, LintModel.Cell))
        {
            return LintValue.Of(new[] { call[0] });
        }

        if (ReferenceEquals(x.Type, LintModel.Port))
        {
            return LintValue.Of(new[] { x.Get(LintModel.Port["joining_cell"]) });
        }

        if (ReferenceEquals(x.Type, LintModel.Network))
        {
            List<LintValue> cells = new List<LintValue>();
            foreach (LintValue member in x.Get(LintModel.Network["members"]).AsList)
            {
                foreach (LintValue cell in member.AsObject.Get(LintModel.Thing["cells"]).AsList)
                {
                    cells.Add(cell);
                }
            }

            return LintValue.Of(cells);
        }

        throw new LintEvaluationException($"cells_of does not take a {x.Type.Name}");
    }

    private static LintValue Neighbors(LintCall call)
    {
        ILintObject cell = call[0].AsObject;
        Vec3 centre = cell.Get(LintModel.Cell["position"]).AsVec;
        double size = cell.Get(LintModel.Cell["size"]).AsNumber;
        string? axis = call.Count > 1 ? call[1].AsString : null;
        List<LintValue> found = new List<LintValue>(6);
        foreach (GridStep step in GridStep.All)
        {
            bool wanted = axis == null || axis == step.Name ||
                          (axis.Length == 1 && "xyz".IndexOf(axis[0]) == step.Axis);
            if (!wanted)
            {
                continue;
            }

            if (call.World.CellAt(centre + Vec3.Of(step) * size, size) is ILintObject next)
            {
                found.Add(LintValue.Of(next));
            }
        }

        if (axis != null && found.Count == 0 && !IsAxis(axis))
        {
            throw new LintEvaluationException($"neighbors: axis is x, y, z or a side +x ... -z, not {axis}");
        }

        return LintValue.Of(found);
    }

    private static bool IsAxis(string axis)
    {
        if (axis == "x" || axis == "y" || axis == "z")
        {
            return true;
        }

        foreach (GridStep step in GridStep.All)
        {
            if (step.Name == axis)
            {
                return true;
            }
        }

        return false;
    }

    private static LintValue MeshOverlap(LintCall call)
    {
        Box3? a = BoxOf(call[0].AsObject), b = BoxOf(call[1].AsObject);
        return LintValue.Of(a.HasValue && b.HasValue ? VisualClash.Depth(a.Value, b.Value) : 0.0);
    }

    internal static Box3? BoxOf(ILintObject thing)
    {
        LintValue box = thing.Get(LintModel.Thing["mesh_box"]);
        return box.IsNull
            ? (Box3?)null
            : new Box3(box.AsObject.Get(LintModel.Box["min"]).AsVec, box.AsObject.Get(LintModel.Box["max"]).AsVec);
    }

    private static LintValue SharesCell(LintCall call)
    {
        HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (LintValue cell in call[0].AsObject.Get(LintModel.Thing["cells"]).AsList)
        {
            keys.Add(cell.AsObject.Key);
        }

        foreach (LintValue cell in call[1].AsObject.Get(LintModel.Thing["cells"]).AsList)
        {
            if (keys.Contains(cell.AsObject.Key))
            {
                return LintValue.True;
            }
        }

        return LintValue.False;
    }
}
