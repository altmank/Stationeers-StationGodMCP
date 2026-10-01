#nullable enable

using System;
using System.Collections.Generic;
using System.Text;

namespace StationGodMCP.Pure.Lint;

/// <summary>One parameter of a library function.</summary>
internal sealed class LintParameter
{
    internal LintParameter(string name, LintType type, bool optional)
    {
        Name = name;
        Type = type;
        Optional = optional;
    }

    internal string Name { get; }

    internal LintType Type { get; }

    internal bool Optional { get; }

    public override string ToString() => $"{Name}{(Optional ? "?" : string.Empty)}: {Type.Name}";
}

/// <summary>The arguments of one call, and the lint call it runs in.</summary>
internal sealed class LintCall
{
    internal LintCall(LintValue[] args, LintContext context)
    {
        Args = args;
        Context = context;
    }

    internal LintValue[] Args { get; }

    internal LintContext Context { get; }

    internal ILintWorld World => Context.World;

    internal LintValue this[int index] => index < Args.Length ? Args[index] : LintValue.Null;

    internal int Count => Args.Length;
}

/// <summary>
/// A library function: a name, typed parameters, a return type, what it does, and its body. Registering one
/// (LintLibrary.Add) is all a new function needs: rule files can call it, lint_rules lists it.
/// </summary>
internal sealed class LintFunction
{
    /// <param name="signature">"(thing, number?) -> bool": parameter types (a trailing ? on a name makes it
    /// optional: "axis?: string"), then the return type.</param>
    internal LintFunction(string name, string signature, string doc, Func<LintCall, LintValue> body,
        bool cached = false)
    {
        Name = name;
        Doc = doc;
        Body = body;
        Cached = cached;
        (Parameters, Returns) = ParseSignature(name, signature);
        Signature = $"{name}({string.Join(", ", ParameterTexts(Parameters))}) -> {Returns.Name}";
    }

    internal string Name { get; }

    internal string Doc { get; }

    internal IReadOnlyList<LintParameter> Parameters { get; }

    internal LintType Returns { get; }

    internal string Signature { get; }

    /// <summary>The result is kept per lint call for the same arguments (functions that ask the game for much).</summary>
    internal bool Cached { get; }

    internal Func<LintCall, LintValue> Body { get; }

    private static List<string> ParameterTexts(IReadOnlyList<LintParameter> parameters)
    {
        List<string> texts = new List<string>(parameters.Count);
        foreach (LintParameter parameter in parameters)
        {
            texts.Add(parameter.ToString());
        }

        return texts;
    }

    private static (IReadOnlyList<LintParameter>, LintType) ParseSignature(string name, string signature)
    {
        int arrow = signature.LastIndexOf("->", StringComparison.Ordinal);
        string inside = arrow < 0 ? string.Empty : signature.Substring(0, arrow).Trim();
        if (arrow < 0 || !inside.StartsWith("(", StringComparison.Ordinal) || !inside.EndsWith(")", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{name}: a signature reads \"(a: type, b?: type) -> type\".", nameof(signature));
        }

        List<LintParameter> parameters = new List<LintParameter>();
        foreach (string part in SplitTop(inside.Substring(1, inside.Length - 2)))
        {
            int colon = part.IndexOf(':');
            string label = colon < 0 ? $"arg{parameters.Count + 1}" : part.Substring(0, colon).Trim();
            string type = colon < 0 ? part : part.Substring(colon + 1);
            bool optional = label.EndsWith("?", StringComparison.Ordinal);
            parameters.Add(new LintParameter(optional ? label.TrimEnd('?') : label, LintModel.ParseType(type), optional));
        }

        return (parameters, LintModel.ParseType(signature.Substring(arrow + 2)));
    }

    // Splits "a: list<x>, b: map<y>" at the commas outside angle brackets.
    private static List<string> SplitTop(string text)
    {
        List<string> parts = new List<string>();
        int depth = 0, start = 0;
        for (int index = 0; index < text.Length; index++)
        {
            depth += text[index] == '<' ? 1 : text[index] == '>' ? -1 : 0;
            if (text[index] == ',' && depth == 0)
            {
                parts.Add(text.Substring(start, index - start));
                start = index + 1;
            }
        }

        if (text.Trim().Length > 0)
        {
            parts.Add(text.Substring(start));
        }

        return parts;
    }
}

/// <summary>
/// The functions rule files can call. The collection forms (any, all, count, sum, min, max, first, map, filter,
/// flat_map, sort_by, distinct), if and has are part of the language (LintCompiler.Forms); everything else is a
/// registered function: the pure ones here, the ones that ask the game added by the game side.
/// </summary>
internal sealed class LintLibrary
{
    private readonly Dictionary<string, LintFunction> _functions = new Dictionary<string, LintFunction>(StringComparer.Ordinal);

    internal IReadOnlyCollection<LintFunction> Functions => _functions.Values;

    internal LintFunction? Find(string name) => _functions.TryGetValue(name, out LintFunction function) ? function : null;

    internal LintLibrary Add(LintFunction function)
    {
        if (_functions.ContainsKey(function.Name) || LintCompiler.IsForm(function.Name))
        {
            throw new ArgumentException($"A lint function {function.Name} is already registered.", nameof(function));
        }

        _functions[function.Name] = function;
        return this;
    }

    /// <summary>The pure library: text, numbers, geometry, flow through devices, chip programs, gases.</summary>
    internal static LintLibrary Standard()
    {
        LintLibrary library = new LintLibrary();
        LintStandardFunctions.Register(library);
        return library;
    }
}

/// <summary>One lint call: the world it reads, results kept for cached functions, and the trace explain asks for.</summary>
internal sealed class LintContext
{
    private readonly Dictionary<string, LintValue> _cache = new Dictionary<string, LintValue>(StringComparer.Ordinal);

    internal LintContext(ILintWorld world)
    {
        World = world;
    }

    internal ILintWorld World { get; }

    internal LintValue Invoke(LintFunction function, LintValue[] args)
    {
        if (args.Length > 0 && args[0].Kind == LintKind.Object && args[0].AsObject is LintRecord record &&
            Stubbed(record, function.Name, args, out LintValue stub))
        {
            return stub;
        }

        if (!function.Cached)
        {
            return function.Body(new LintCall(args, this));
        }

        string key = KeyOf(function.Name, args);
        if (!_cache.TryGetValue(key, out LintValue value))
        {
            value = function.Body(new LintCall(args, this));
            _cache[key] = value;
        }

        return value;
    }

    // An example fixes a function's result on its object: "name(arg2, ...)" first, then "name".
    private static bool Stubbed(LintRecord record, string name, LintValue[] args, out LintValue value)
    {
        if (args.Length > 1)
        {
            StringBuilder call = new StringBuilder(name).Append('(');
            for (int index = 1; index < args.Length; index++)
            {
                call.Append(index > 1 ? "," : string.Empty).Append(args[index].ToText());
            }

            if (record.TryStub(call.Append(')').ToString(), out value))
            {
                return true;
            }
        }

        return record.TryStub(name, out value);
    }

    private static string KeyOf(string name, LintValue[] args)
    {
        StringBuilder key = new StringBuilder(name);
        foreach (LintValue arg in args)
        {
            key.Append('|').Append(arg.Kind == LintKind.Object ? arg.AsObject.Key : arg.ToText());
        }

        return key.ToString();
    }
}
