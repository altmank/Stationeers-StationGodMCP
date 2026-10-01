#nullable enable

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace StationGodMCP.Pure.Lint;

internal delegate LintValue LintEval(LintFrame frame);

/// <summary>The values an evaluation recorded: each member read, call and named value, in first-seen order.</summary>
internal sealed class LintTrace
{
    internal const int ValuesPerExpression = 4;
    internal const int Expressions = 64;

    private readonly Dictionary<string, List<LintValue>> _values = new Dictionary<string, List<LintValue>>(StringComparer.Ordinal);

    internal List<string> Order { get; } = new List<string>();

    internal IReadOnlyList<LintValue> ValuesOf(string expression) => _values[expression];

    internal void Record(string expression, LintValue value)
    {
        if (!_values.TryGetValue(expression, out List<LintValue> values))
        {
            if (Order.Count >= Expressions)
            {
                return;
            }

            values = new List<LintValue>(1);
            _values[expression] = values;
            Order.Add(expression);
        }

        if (values.Count < ValuesPerExpression)
        {
            values.Add(value);
        }
    }
}

/// <summary>The variables of one evaluation: parameters, lets, and rule lets worked out on first read.</summary>
internal sealed class LintFrame
{
    internal LintFrame(LintContext context, int slots, LintEval?[] lazy, LintTrace? trace = null)
    {
        Context = context;
        Slots = new LintValue[slots];
        Ready = new bool[slots];
        Lazy = lazy;
        Trace = trace;
    }

    internal LintContext Context { get; }

    internal LintValue[] Slots { get; }

    internal bool[] Ready { get; }

    internal LintEval?[] Lazy { get; }

    internal LintTrace? Trace { get; }

    internal void Set(int slot, LintValue value)
    {
        Slots[slot] = value;
        Ready[slot] = true;
    }

    internal LintValue Read(int slot)
    {
        if (!Ready[slot])
        {
            LintEval lazy = Lazy[slot] ?? throw new LintEvaluationException("a value was read before it was set");
            Slots[slot] = lazy(this);
            Ready[slot] = true;
        }

        return Slots[slot];
    }
}

/// <summary>A compiled expression: its evaluator and its type.</summary>
internal sealed class LintCompiled
{
    internal LintCompiled(LintEval eval, LintType type)
    {
        Eval = eval;
        Type = type;
    }

    internal LintEval Eval { get; }

    internal LintType Type { get; }
}

/// <summary>Names in scope while compiling: variables in slots, and the subject whose fields bare names read.</summary>
internal sealed class LintScope
{
    private readonly Dictionary<string, (int Slot, LintType Type)> _names =
        new Dictionary<string, (int Slot, LintType Type)>(StringComparer.Ordinal);

    internal LintScope(LintScope? parent, int implicitSlot = -1, ObjectType? implicitType = null)
    {
        Parent = parent;
        ImplicitSlot = implicitSlot >= 0 ? implicitSlot : parent?.ImplicitSlot ?? -1;
        ImplicitType = implicitType ?? parent?.ImplicitType;
    }

    internal LintScope? Parent { get; }

    internal int ImplicitSlot { get; }

    internal ObjectType? ImplicitType { get; }

    internal LintScope With(string name, int slot, LintType type)
    {
        _names[name] = (slot, type);
        return this;
    }

    internal bool TryFind(string name, out int slot, out LintType type)
    {
        for (LintScope? scope = this; scope != null; scope = scope.Parent)
        {
            if (scope._names.TryGetValue(name, out (int Slot, LintType Type) entry))
            {
                slot = entry.Slot;
                type = entry.Type;
                return true;
            }
        }

        slot = -1;
        type = LintType.Any;
        return false;
    }
}

/// <summary>
/// Type-checks a parsed expression and turns it into an evaluator, once, at load. A nullable value must be guarded
/// (?., ??, has(x) or x != null on the left of an and, in an if) before it is read; a field, function or name that
/// does not exist, or a type that does not fit, is an error with its position.
/// </summary>
internal sealed class LintCompiler
{
    private static readonly HashSet<string> Forms = new HashSet<string>(StringComparer.Ordinal)
        { "has", "if", "any", "all", "count", "sum", "min", "max", "first", "map", "filter", "flat_map", "sort_by", "distinct" };

    private static readonly HashSet<string> NoFacts = new HashSet<string>(StringComparer.Ordinal);

    private readonly LintLibrary _library;
    private readonly List<LintEval?> _lazy = new List<LintEval?>();

    internal LintCompiler(LintLibrary library)
    {
        _library = library;
    }

    internal static IReadOnlyCollection<string> FormNames => Forms;

    internal static bool IsForm(string name) => Forms.Contains(name);

    internal int SlotCount => _lazy.Count;

    /// <summary>The lazy evaluators by slot, for frames of this unit.</summary>
    internal LintEval?[] LazySlots => _lazy.ToArray();

    internal int NewSlot(LintEval? lazy = null)
    {
        _lazy.Add(lazy);
        return _lazy.Count - 1;
    }

    internal void SetLazy(int slot, LintEval lazy) => _lazy[slot] = lazy;

    internal LintCompiled Compile(LintNode node, LintScope scope) => Compile(node, scope, NoFacts);

    internal LintCompiled CompileBool(LintNode node, LintScope scope)
    {
        LintCompiled compiled = Compile(node, scope);
        Require(compiled.Type, LintType.Bool, node);
        return compiled;
    }

    private LintCompiled Compile(LintNode node, LintScope scope, HashSet<string> facts)
    {
        switch (node)
        {
            case LiteralNode literal:
                LintValue constant = literal.Value;
                return new LintCompiled(_ => constant, literal.Type);
            case NameNode name:
                return CompileName(name, scope, facts);
            case MemberNode member:
                return CompileMember(member, scope, facts);
            case IndexNode index:
                return CompileIndex(index, scope, facts);
            case CallNode call:
                return Forms.Contains(call.Name) ? CompileForm(call, scope, facts) : CompileCall(call, scope, facts);
            case UnaryNode unary:
                return CompileUnary(unary, scope, facts);
            case BinaryNode binary:
                return CompileBinary(binary, scope, facts);
            case LetNode let:
                return CompileLet(let, scope, facts);
            case ListNode list:
                return CompileList(list, scope, facts);
            case LambdaNode lambda:
                throw new LintSyntaxException("a lambda (x => ...) only goes into a collection form such as any or map",
                    lambda.Position);
            default:
                throw new LintSyntaxException("unknown expression", node.Position);
        }
    }

    private static LintType Narrow(LintType type, LintNode node, HashSet<string> facts) =>
        type.IsNullable && facts.Contains(node.Text) ? type.NonNull : type;

    private LintCompiled CompileName(NameNode name, LintScope scope, HashSet<string> facts)
    {
        string text = name.Text;
        if (scope.TryFind(name.Name, out int slot, out LintType type))
        {
            return new LintCompiled(frame =>
            {
                LintValue value = frame.Read(slot);
                frame.Trace?.Record(text, value);
                return value;
            }, Narrow(type, name, facts));
        }

        if (scope.ImplicitType?.Find(name.Name) is LintField field)
        {
            int subject = scope.ImplicitSlot;
            return new LintCompiled(frame =>
            {
                LintValue value = frame.Read(subject).AsObject.Get(field);
                frame.Trace?.Record(text, value);
                return value;
            }, Narrow(field.Type, name, facts));
        }

        throw new LintSyntaxException(
            scope.ImplicitType != null
                ? $"no name or {scope.ImplicitType.Name} field '{name.Name}'"
                : $"no name '{name.Name}' (pair rules read a and b)", name.Position);
    }

    private LintCompiled CompileMember(MemberNode member, LintScope scope, HashSet<string> facts)
    {
        LintCompiled target = Compile(member.Target, scope, facts);
        if (target.Type.IsNullable && !member.NullSafe)
        {
            throw new LintSyntaxException(
                $"{member.Target.Text} may be null: read it with ?. or guard it with has({member.Target.Text})",
                member.Target.Position);
        }

        LintEval read = target.Eval;
        string text = member.Text;
        bool nullSafe = member.NullSafe;
        LintType owner = target.Type.NonNull;
        LintType result;
        LintEval eval;
        if (owner is ObjectType objectType)
        {
            LintField field = objectType.Find(member.Name) ??
                              throw new LintSyntaxException(
                                  $"{objectType.Name} has no field '{member.Name}' ({FieldList(objectType)})", member.Position);
            result = field.Type;
            eval = frame =>
            {
                LintValue value = read(frame);
                LintValue got = value.IsNull
                    ? nullSafe ? LintValue.Null : throw new LintEvaluationException($"{member.Target.Text} is null")
                    : value.AsObject.Get(field);
                frame.Trace?.Record(text, got);
                return got;
            };
        }
        else if (ReferenceEquals(owner, LintType.Vec) && (member.Name == "x" || member.Name == "y" ||
                                                          member.Name == "z" || member.Name == "length"))
        {
            string axis = member.Name;
            result = LintType.Number;
            eval = frame =>
            {
                LintValue value = read(frame);
                if (value.IsNull)
                {
                    return nullSafe ? LintValue.Null : throw new LintEvaluationException($"{member.Target.Text} is null");
                }

                Vec3 v = value.AsVec;
                return LintValue.Of(axis == "x" ? v.X : axis == "y" ? v.Y : axis == "z" ? v.Z : v.Length);
            };
        }
        else
        {
            throw new LintSyntaxException($"a {owner.Name} has no field '{member.Name}'", member.Position);
        }

        if (nullSafe && target.Type.IsNullable)
        {
            result = LintType.Nullable(result);
        }

        return new LintCompiled(eval, Narrow(result, member, facts));
    }

    private static string FieldList(ObjectType type)
    {
        List<string> names = new List<string>(type.Fields.Count);
        foreach (LintField field in type.Fields)
        {
            names.Add(field.Name);
        }

        return string.Join(", ", names);
    }

    private LintCompiled CompileIndex(IndexNode node, LintScope scope, HashSet<string> facts)
    {
        LintCompiled target = Compile(node.Target, scope, facts);
        LintCompiled index = Compile(node.Index, scope, facts);
        NotNull(target.Type, node.Target);
        string text = node.Text;
        LintEval read = target.Eval, key = index.Eval;
        if (target.Type.NonNull is ListType list)
        {
            Require(index.Type, LintType.Number, node.Index);
            return new LintCompiled(frame =>
            {
                IReadOnlyList<LintValue> items = read(frame).AsList;
                double at = key(frame).AsNumber;
                int position = (int)at;
                if (position < 0 || position >= items.Count || position != at)
                {
                    throw new LintEvaluationException($"{text}: no item {LintValue.NumberText(at)} in {items.Count}");
                }

                return items[position];
            }, list.Element);
        }

        if (target.Type.NonNull is MapType map)
        {
            Require(index.Type, LintType.String, node.Index);
            bool zero = ReferenceEquals(map.Value, LintType.Number);
            return new LintCompiled(frame =>
            {
                LintValue got = read(frame).AsMap.TryGetValue(key(frame).AsString, out LintValue value) ? value
                    : zero ? LintValue.Of(0.0) : LintValue.Null;
                frame.Trace?.Record(text, got);
                return got;
            }, zero ? LintType.Number : Narrow(LintType.Nullable(map.Value), node, facts));
        }

        throw new LintSyntaxException($"a {target.Type.Name} cannot be indexed", node.Position);
    }

    private LintCompiled CompileCall(CallNode call, LintScope scope, HashSet<string> facts)
    {
        LintFunction function = _library.Find(call.Name) ??
                                throw new LintSyntaxException($"no function '{call.Name}' (lint_rules functions lists them)",
                                    call.Position);
        if (call.Named.Count > 0)
        {
            throw new LintSyntaxException($"{call.Name} takes no named arguments", call.Position);
        }

        int required = 0;
        foreach (LintParameter parameter in function.Parameters)
        {
            required += parameter.Optional ? 0 : 1;
        }

        if (call.Args.Count < required || call.Args.Count > function.Parameters.Count)
        {
            throw new LintSyntaxException($"{function.Signature} takes {required} to {function.Parameters.Count} arguments, not {call.Args.Count}",
                call.Position);
        }

        LintEval[] args = new LintEval[call.Args.Count];
        bool nullSafe = call.NullSafeReceiver;
        bool receiverNullable = false;
        for (int index = 0; index < args.Length; index++)
        {
            LintCompiled arg = Compile(call.Args[index], scope, facts);
            LintType type = arg.Type;
            if (index == 0 && nullSafe && type.IsNullable)
            {
                receiverNullable = true;
                type = type.NonNull;
            }

            if (!function.Parameters[index].Type.Accepts(type))
            {
                throw new LintSyntaxException(
                    $"{function.Name}'s {function.Parameters[index].Name} is a {function.Parameters[index].Type.Name}, not a {arg.Type.Name}" +
                    (arg.Type.IsNullable ? $": guard it with has({call.Args[index].Text}) or ??" : string.Empty),
                    call.Args[index].Position);
            }

            args[index] = arg.Eval;
        }

        string text = call.Text;
        LintEval eval = frame =>
        {
            LintValue[] values = new LintValue[args.Length];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = args[index](frame);
            }

            if (nullSafe && values.Length > 0 && values[0].IsNull)
            {
                return LintValue.Null;
            }

            LintValue result = frame.Context.Invoke(function, values);
            frame.Trace?.Record(text, result);
            return result;
        };
        LintType returns = receiverNullable ? LintType.Nullable(function.Returns) : function.Returns;
        return new LintCompiled(eval, Narrow(returns, call, facts));
    }

    private LintCompiled CompileUnary(UnaryNode unary, LintScope scope, HashSet<string> facts)
    {
        LintCompiled operand = Compile(unary.Operand, scope, facts);
        LintEval read = operand.Eval;
        if (unary.Op == "not")
        {
            Require(operand.Type, LintType.Bool, unary.Operand);
            return new LintCompiled(frame => LintValue.Of(!read(frame).AsBool), LintType.Bool);
        }

        if (ReferenceEquals(operand.Type, LintType.Vec))
        {
            return new LintCompiled(frame => LintValue.Of(read(frame).AsVec * -1), LintType.Vec);
        }

        Require(operand.Type, LintType.Number, unary.Operand);
        return new LintCompiled(frame => LintValue.Of(-read(frame).AsNumber), LintType.Number);
    }

    private LintCompiled CompileBinary(BinaryNode node, LintScope scope, HashSet<string> facts)
    {
        if (node.Op == "and" || node.Op == "or")
        {
            LintCompiled left = Compile(node.Left, scope, facts);
            Require(left.Type, LintType.Bool, node.Left);
            HashSet<string> more = node.Op == "and" ? With(facts, WhenTrue(node.Left)) : With(facts, WhenFalse(node.Left));
            LintCompiled right = Compile(node.Right, scope, more);
            Require(right.Type, LintType.Bool, node.Right);
            LintEval l = left.Eval, r = right.Eval;
            return node.Op == "and"
                ? new LintCompiled(frame => l(frame).AsBool ? r(frame) : LintValue.False, LintType.Bool)
                : new LintCompiled(frame => l(frame).AsBool ? LintValue.True : r(frame), LintType.Bool);
        }

        LintCompiled a = Compile(node.Left, scope, facts);
        LintCompiled b = Compile(node.Right, scope, facts);
        LintEval x = a.Eval, y = b.Eval;
        switch (node.Op)
        {
            case "==":
            case "!=":
                if (!Comparable(a.Type, b.Type))
                {
                    throw new LintSyntaxException($"a {a.Type.Name} is never equal to a {b.Type.Name}", node.Position);
                }

                bool equal = node.Op == "==";
                return new LintCompiled(frame => LintValue.Of(LintValue.AreEqual(x(frame), y(frame)) == equal), LintType.Bool);
            case "<":
            case "<=":
            case ">":
            case ">=":
                return CompileOrder(node, a, b);
            case "in":
                return CompileIn(node, a, b);
            case "matches":
            case "matches_regex":
                return CompileMatch(node, a, b);
            case "??":
                LintType joined = Unify(a.Type.NonNull, b.Type, node);
                return new LintCompiled(frame =>
                {
                    LintValue value = x(frame);
                    return value.IsNull ? y(frame) : value;
                }, joined);
            default:
                return CompileArithmetic(node, a, b);
        }
    }

    private static LintCompiled CompileOrder(BinaryNode node, LintCompiled a, LintCompiled b)
    {
        bool text = ReferenceEquals(a.Type, LintType.String) && ReferenceEquals(b.Type, LintType.String);
        if (!text)
        {
            Require(a.Type, LintType.Number, node.Left);
            Require(b.Type, LintType.Number, node.Right);
        }

        LintEval x = a.Eval, y = b.Eval;
        string op = node.Op;
        return new LintCompiled(frame =>
        {
            LintValue left = x(frame), right = y(frame);
            int order = text
                ? string.CompareOrdinal(left.AsString, right.AsString)
                : left.AsNumber.CompareTo(right.AsNumber);
            bool result = op == "<" ? order < 0 : op == "<=" ? order <= 0 : op == ">" ? order > 0 : order >= 0;
            return LintValue.Of(result);
        }, LintType.Bool);
    }

    private static LintCompiled CompileIn(BinaryNode node, LintCompiled a, LintCompiled b)
    {
        LintEval x = a.Eval, y = b.Eval;
        if (ReferenceEquals(b.Type, LintType.String))
        {
            Require(a.Type, LintType.String, node.Left);
            return new LintCompiled(frame => LintValue.Of(y(frame).AsString.IndexOf(x(frame).AsString, StringComparison.Ordinal) >= 0),
                LintType.Bool);
        }

        NotNull(b.Type, node.Right);
        if (!(b.Type is ListType list) || !Comparable(a.Type, list.Element))
        {
            throw new LintSyntaxException($"'in' needs a list of {a.Type.Name} on its right, not a {b.Type.Name}", node.Position);
        }

        return new LintCompiled(frame =>
        {
            LintValue item = x(frame);
            foreach (LintValue candidate in y(frame).AsList)
            {
                if (LintValue.AreEqual(item, candidate))
                {
                    return LintValue.True;
                }
            }

            return LintValue.False;
        }, LintType.Bool);
    }

    private static LintCompiled CompileMatch(BinaryNode node, LintCompiled a, LintCompiled b)
    {
        Require(a.Type, LintType.String, node.Left);
        Require(b.Type, LintType.String, node.Right);
        bool glob = node.Op == "matches";
        LintEval x = a.Eval, y = b.Eval;
        if (node.Right is LiteralNode literal)
        {
            Regex fixedPattern;
            try
            {
                fixedPattern = glob ? LintPatterns.Glob(literal.Value.AsString) : LintPatterns.Regex(literal.Value.AsString);
            }
            catch (ArgumentException error)
            {
                throw new LintSyntaxException($"bad pattern: {error.Message}", node.Right.Position);
            }

            return new LintCompiled(frame => LintValue.Of(fixedPattern.IsMatch(x(frame).AsString)), LintType.Bool);
        }

        return new LintCompiled(frame =>
        {
            string pattern = y(frame).AsString;
            Regex regex;
            try
            {
                regex = glob ? LintPatterns.Glob(pattern) : LintPatterns.Regex(pattern);
            }
            catch (ArgumentException error)
            {
                throw new LintEvaluationException($"bad pattern {pattern}: {error.Message}");
            }

            return LintValue.Of(regex.IsMatch(x(frame).AsString));
        }, LintType.Bool);
    }

    private static LintCompiled CompileArithmetic(BinaryNode node, LintCompiled a, LintCompiled b)
    {
        NotNull(a.Type, node.Left);
        NotNull(b.Type, node.Right);
        LintEval x = a.Eval, y = b.Eval;
        string op = node.Op;
        if (op == "+" && ReferenceEquals(a.Type, LintType.String) && ReferenceEquals(b.Type, LintType.String))
        {
            return new LintCompiled(frame => LintValue.Of(x(frame).AsString + y(frame).AsString), LintType.String);
        }

        if (op == "+" && a.Type is ListType && b.Type is ListType)
        {
            LintType joined = Unify(a.Type, b.Type, node);
            return new LintCompiled(frame =>
            {
                IReadOnlyList<LintValue> left = x(frame).AsList, right = y(frame).AsList;
                LintValue[] all = new LintValue[left.Count + right.Count];
                for (int index = 0; index < left.Count; index++)
                {
                    all[index] = left[index];
                }

                for (int index = 0; index < right.Count; index++)
                {
                    all[left.Count + index] = right[index];
                }

                return LintValue.Of(all);
            }, joined);
        }

        if (ReferenceEquals(a.Type, LintType.Vec) && ReferenceEquals(b.Type, LintType.Vec) && (op == "+" || op == "-"))
        {
            return new LintCompiled(frame => LintValue.Of(op == "+" ? x(frame).AsVec + y(frame).AsVec : x(frame).AsVec - y(frame).AsVec),
                LintType.Vec);
        }

        if (ReferenceEquals(a.Type, LintType.Vec) && ReferenceEquals(b.Type, LintType.Number) && op == "*")
        {
            return new LintCompiled(frame => LintValue.Of(x(frame).AsVec * y(frame).AsNumber), LintType.Vec);
        }

        Require(a.Type, LintType.Number, node.Left);
        Require(b.Type, LintType.Number, node.Right);
        return new LintCompiled(frame =>
        {
            double left = x(frame).AsNumber, right = y(frame).AsNumber;
            return LintValue.Of(op == "+" ? left + right
                : op == "-" ? left - right
                : op == "*" ? left * right
                : op == "/" ? left / right
                : left % right);
        }, LintType.Number);
    }

    private LintCompiled CompileLet(LetNode let, LintScope scope, HashSet<string> facts)
    {
        LintCompiled value = Compile(let.Value, scope, facts);
        int slot = NewSlot();
        LintScope inner = new LintScope(scope).With(let.Name, slot, value.Type);
        LintCompiled body = Compile(let.Body, inner, facts);
        LintEval compute = value.Eval, rest = body.Eval;
        string name = let.Name;
        return new LintCompiled(frame =>
        {
            LintValue bound = compute(frame);
            frame.Set(slot, bound);
            frame.Trace?.Record(name, bound);
            return rest(frame);
        }, body.Type);
    }

    private LintCompiled CompileList(ListNode list, LintScope scope, HashSet<string> facts)
    {
        LintType element = LintType.Any;
        LintEval[] items = new LintEval[list.Items.Count];
        for (int index = 0; index < items.Length; index++)
        {
            LintCompiled item = Compile(list.Items[index], scope, facts);
            element = index == 0 ? item.Type : Unify(element, item.Type, list.Items[index]);
            items[index] = item.Eval;
        }

        return new LintCompiled(frame =>
        {
            LintValue[] values = new LintValue[items.Length];
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = items[index](frame);
            }

            return LintValue.Of(values);
        }, LintType.ListOf(element));
    }

    private LintCompiled CompileForm(CallNode call, LintScope scope, HashSet<string> facts)
    {
        if (call.Named.Count > 0)
        {
            throw new LintSyntaxException($"{call.Name} takes no named arguments", call.Position);
        }

        List<LintNode> args = call.Args;
        switch (call.Name)
        {
            case "has":
                Arity(call, 1, 1);
                LintEval value = Compile(args[0], scope, facts).Eval;
                return new LintCompiled(frame => LintValue.Of(!value(frame).IsNull), LintType.Bool);
            case "if":
                Arity(call, 3, 3);
                LintCompiled condition = Compile(args[0], scope, facts);
                Require(condition.Type, LintType.Bool, args[0]);
                LintCompiled then = Compile(args[1], scope, With(facts, WhenTrue(args[0])));
                LintCompiled otherwise = Compile(args[2], scope, With(facts, WhenFalse(args[0])));
                LintEval test = condition.Eval, yes = then.Eval, no = otherwise.Eval;
                return new LintCompiled(frame => test(frame).AsBool ? yes(frame) : no(frame),
                    Unify(then.Type, otherwise.Type, call));
            case "min":
            case "max":
                if (args.Count >= 2 && !(args[1] is LambdaNode))
                {
                    return CompileScalarExtreme(call, scope, facts);
                }

                break;
        }

        switch (call.Name)
        {
            case "any":
            case "all":
            case "map":
            case "filter":
            case "flat_map":
            case "sort_by":
                Arity(call, 2, 2);
                break;
            case "distinct":
                Arity(call, 1, 1);
                break;
            default:
                Arity(call, 1, 2);
                break;
        }

        LintCompiled source = Compile(args[0], scope, facts);
        if (call.NullSafeReceiver && source.Type.IsNullable)
        {
            throw new LintSyntaxException($"{call.Name} over a list that may be null: use ({args[0].Text} ?? [])", call.Position);
        }

        NotNull(source.Type, args[0]);
        if (!(source.Type is ListType list))
        {
            throw new LintSyntaxException($"{call.Name} needs a list, not a {source.Type.Name}", args[0].Position);
        }

        LintEval items = source.Eval;
        int slot = -1;
        LintEval? body = null;
        LintType bodyType = list.Element;
        if (args.Count > 1)
        {
            if (!(args[1] is LambdaNode lambda))
            {
                throw new LintSyntaxException($"{call.Name}'s second argument is a lambda: x => ...", args[1].Position);
            }

            slot = NewSlot();
            LintScope inner = new LintScope(scope).With(lambda.Parameter, slot, list.Element);
            LintCompiled compiled = Compile(lambda.Body, inner, facts);
            body = compiled.Eval;
            bodyType = compiled.Type;
        }

        string text = call.Text;
        LintCompiled result = LintForms.Build(call, list.Element, items, slot, body, bodyType);
        LintEval inner2 = result.Eval;
        return new LintCompiled(frame =>
        {
            LintValue got = inner2(frame);
            frame.Trace?.Record(text, got);
            return got;
        }, Narrow(result.Type, call, facts));
    }

    private LintCompiled CompileScalarExtreme(CallNode call, LintScope scope, HashSet<string> facts)
    {
        LintEval[] values = new LintEval[call.Args.Count];
        for (int index = 0; index < values.Length; index++)
        {
            LintCompiled arg = Compile(call.Args[index], scope, facts);
            Require(arg.Type, LintType.Number, call.Args[index]);
            values[index] = arg.Eval;
        }

        bool max = call.Name == "max";
        return new LintCompiled(frame =>
        {
            double best = values[0](frame).AsNumber;
            for (int index = 1; index < values.Length; index++)
            {
                double next = values[index](frame).AsNumber;
                best = max ? Math.Max(best, next) : Math.Min(best, next);
            }

            return LintValue.Of(best);
        }, LintType.Number);
    }

    private static void Arity(CallNode call, int min, int max)
    {
        if (call.Args.Count < min || call.Args.Count > max)
        {
            throw new LintSyntaxException(
                min == max ? $"{call.Name} takes {min} argument{(min == 1 ? string.Empty : "s")}"
                    : $"{call.Name} takes {min} to {max} arguments", call.Position);
        }
    }

    internal static void Require(LintType actual, LintType wanted, LintNode node)
    {
        if (!wanted.Accepts(actual) || (actual.IsNullable && !wanted.IsNullable) || ReferenceEquals(actual, LintType.Null))
        {
            throw new LintSyntaxException(
                actual.IsNullable
                    ? $"{node.Text} may be null where a {wanted.Name} is needed: guard it with has({node.Text}) or ??"
                    : $"{node.Text} is a {actual.Name}, not a {wanted.Name}", node.Position);
        }
    }

    private static void NotNull(LintType type, LintNode node)
    {
        if (type.IsNullable || ReferenceEquals(type, LintType.Null))
        {
            throw new LintSyntaxException($"{node.Text} may be null: guard it with has({node.Text}) or ??", node.Position);
        }
    }

    private static bool Comparable(LintType a, LintType b) =>
        ReferenceEquals(a, LintType.Null) || ReferenceEquals(b, LintType.Null) ||
        a.NonNull.Accepts(b.NonNull) || b.NonNull.Accepts(a.NonNull);

    internal static LintType Unify(LintType a, LintType b, LintNode node)
    {
        if (ReferenceEquals(a, LintType.Null))
        {
            return LintType.Nullable(b);
        }

        if (ReferenceEquals(b, LintType.Null))
        {
            return LintType.Nullable(a);
        }

        if (a.NonNull.SameAs(b.NonNull) || ReferenceEquals(a.NonNull, LintType.Any) || ReferenceEquals(b.NonNull, LintType.Any))
        {
            LintType core = ReferenceEquals(a.NonNull, LintType.Any) ? b.NonNull : a.NonNull;
            if (core is ListType listA && listA.Element == LintType.Any && b.NonNull is ListType)
            {
                core = b.NonNull;
            }

            return a.IsNullable || b.IsNullable ? LintType.Nullable(core) : core;
        }

        if (a.NonNull is ListType la && b.NonNull is ListType lb &&
            (ReferenceEquals(la.Element, LintType.Any) || ReferenceEquals(lb.Element, LintType.Any)))
        {
            LintType core = ReferenceEquals(la.Element, LintType.Any) ? lb : la;
            return a.IsNullable || b.IsNullable ? LintType.Nullable(core) : core;
        }

        throw new LintSyntaxException($"a {a.Name} and a {b.Name} do not mix here", node.Position);
    }

    private static HashSet<string> With(HashSet<string> facts, HashSet<string> more)
    {
        if (more.Count == 0)
        {
            return facts;
        }

        HashSet<string> joined = new HashSet<string>(facts, StringComparer.Ordinal);
        joined.UnionWith(more);
        return joined;
    }

    // What is known not null when the condition is true: has(e), e != null, both sides of an and.
    private static HashSet<string> WhenTrue(LintNode node)
    {
        switch (node)
        {
            case CallNode call when call.Name == "has" && call.Args.Count == 1:
                return new HashSet<string>(StringComparer.Ordinal) { call.Args[0].Text };
            case BinaryNode { Op: "!=" } binary:
                return NullTest(binary);
            case BinaryNode { Op: "and" } binary:
                HashSet<string> both = WhenTrue(binary.Left);
                both.UnionWith(WhenTrue(binary.Right));
                return both;
            case UnaryNode { Op: "not" } unary:
                return WhenFalse(unary.Operand);
            default:
                return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    // What is known not null when the condition is false: e == null, not has(e), both sides of an or.
    private static HashSet<string> WhenFalse(LintNode node)
    {
        switch (node)
        {
            case BinaryNode { Op: "==" } binary:
                return NullTest(binary);
            case BinaryNode { Op: "or" } binary:
                HashSet<string> both = WhenFalse(binary.Left);
                both.UnionWith(WhenFalse(binary.Right));
                return both;
            case UnaryNode { Op: "not" } unary:
                return WhenTrue(unary.Operand);
            default:
                return new HashSet<string>(StringComparer.Ordinal);
        }
    }

    private static HashSet<string> NullTest(BinaryNode binary)
    {
        HashSet<string> facts = new HashSet<string>(StringComparer.Ordinal);
        if (binary.Right is LiteralNode { Value.IsNull: true })
        {
            facts.Add(binary.Left.Text);
        }
        else if (binary.Left is LiteralNode { Value.IsNull: true })
        {
            facts.Add(binary.Right.Text);
        }

        return facts;
    }
}

/// <summary>Glob and regular-expression patterns, compiled once per text.</summary>
internal static class LintPatterns
{
    private static readonly Dictionary<string, Regex> Known = new Dictionary<string, Regex>(StringComparer.Ordinal);

    /// <summary>* any run of characters, ? one character; case-insensitive; the whole text must match.</summary>
    internal static Regex Glob(string pattern) =>
        Cached("g:" + pattern, () => new Regex("^" + System.Text.RegularExpressions.Regex.Escape(pattern)
            .Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

    /// <summary>.NET regular expression, matched anywhere unless anchored.</summary>
    internal static Regex Regex(string pattern) =>
        Cached("r:" + pattern, () => new Regex(pattern, RegexOptions.CultureInvariant));

    private static Regex Cached(string key, Func<Regex> make)
    {
        lock (Known)
        {
            if (!Known.TryGetValue(key, out Regex regex))
            {
                regex = make();
                if (Known.Count < 512)
                {
                    Known[key] = regex;
                }
            }

            return regex;
        }
    }
}
