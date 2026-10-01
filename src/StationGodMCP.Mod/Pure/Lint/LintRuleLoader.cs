#nullable enable

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace StationGodMCP.Pure.Lint;

/// <summary>A rule file's text, where it came from, and its name for messages.</summary>
internal sealed class LintRuleText
{
    internal LintRuleText(string file, string text, LintRuleOrigin origin)
    {
        File = file;
        Text = text;
        Origin = origin;
    }

    internal string File { get; }

    internal string Text { get; }

    internal LintRuleOrigin Origin { get; }
}

/// <summary>The effective rule set: the mod's rules with the save's file applied, and every error found loading them.</summary>
internal sealed class LintRuleSet
{
    internal LintRuleSet(List<LintRule> rules, List<LintRuleError> errors, List<string> files, List<string> disabled)
    {
        Rules = rules;
        Errors = errors;
        Files = files;
        Disabled = disabled;
    }

    internal IReadOnlyList<LintRule> Rules { get; }

    internal IReadOnlyList<LintRuleError> Errors { get; }

    /// <summary>The files read, mod first.</summary>
    internal IReadOnlyList<string> Files { get; }

    /// <summary>Rule ids turned off with "enabled": false.</summary>
    internal IReadOnlyList<string> Disabled { get; }

    internal LintRule? Find(string id)
    {
        foreach (LintRule rule in Rules)
        {
            if (rule.Id == id)
            {
                return rule;
            }
        }

        return null;
    }

    /// <summary>A rule's place in the set: the report order of its findings.</summary>
    internal int IndexOf(string id)
    {
        for (int index = 0; index < Rules.Count; index++)
        {
            if (Rules[index].Id == id)
            {
                return index;
            }
        }

        return Rules.Count;
    }
}

/// <summary>
/// Reads lint-rules.json files and builds the effective set. Format: {"schema_version": 1, "rules": [...]}. The save's
/// file (next to the save) takes priority: a rule with an id the mod's file has replaces it when it carries select or
/// assert; otherwise its enabled, level, level_when, on, message and description patch the mod's rule ("enabled":
/// false turns it off). Rules with new ids are added after the mod's. A rule that does not compile is left out and
/// reported; the rest still run.
/// </summary>
internal static class LintRuleLoader
{
    internal const int SchemaVersion = 1;

    private static readonly Regex IdPattern = new Regex("^[a-z][a-z0-9_]*$", RegexOptions.CultureInvariant);

    private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.Ordinal)
    {
        "id", "level", "level_when", "message", "on", "select", "assert", "let", "other", "examples", "description",
        "enabled"
    };

    private static readonly string[] PatchFields = { "enabled", "level", "level_when", "on", "message", "description" };

    internal static LintRuleSet Load(LintLibrary library, LintRuleText mod, LintRuleText? save)
    {
        List<LintRuleError> errors = new List<LintRuleError>();
        List<string> files = new List<string> { mod.File };
        List<(JObject Rule, LintRuleOrigin Origin, string File)> merged = new List<(JObject, LintRuleOrigin, string)>();
        Dictionary<string, int> byId = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (JObject rule in RulesOf(mod, errors))
        {
            string? id = IdOf(rule, mod.File, errors);
            if (id == null)
            {
                continue;
            }

            if (byId.ContainsKey(id))
            {
                errors.Add(ErrorAt(mod.File, id, "id", rule, $"a second rule with id {id}"));
                continue;
            }

            byId[id] = merged.Count;
            merged.Add((rule, LintRuleOrigin.Mod, mod.File));
        }

        if (save != null)
        {
            files.Add(save.File);
            foreach (JObject rule in RulesOf(save, errors))
            {
                string? id = IdOf(rule, save.File, errors);
                if (id == null)
                {
                    continue;
                }

                bool replaces = rule["select"] != null || rule["assert"] != null;
                if (byId.TryGetValue(id, out int at))
                {
                    merged[at] = replaces ? (rule, LintRuleOrigin.Save, save.File) : (Patched(merged[at].Rule, rule), merged[at].Origin, merged[at].File);
                }
                else if (replaces)
                {
                    byId[id] = merged.Count;
                    merged.Add((rule, LintRuleOrigin.Save, save.File));
                }
                else
                {
                    errors.Add(ErrorAt(save.File, id, "id", rule,
                        $"no rule {id} to change: a new rule needs select, assert and message"));
                }
            }
        }

        List<LintRule> rules = new List<LintRule>(merged.Count);
        List<string> disabled = new List<string>();
        foreach ((JObject source, LintRuleOrigin origin, string file) in merged)
        {
            string id = (string)source["id"]!;
            if (source["enabled"] is JToken enabled && enabled.Type == JTokenType.Boolean && !(bool)enabled)
            {
                disabled.Add(id);
                continue;
            }

            LintRule? rule = Compile(library, source, origin, file, errors);
            if (rule != null)
            {
                rules.Add(rule);
            }
        }

        return new LintRuleSet(rules, errors, files, disabled);
    }

    private static JObject Patched(JObject rule, JObject patch)
    {
        JObject copy = (JObject)rule.DeepClone();
        foreach (string field in PatchFields)
        {
            if (patch[field] != null)
            {
                copy[field] = patch[field]!.DeepClone();
            }
        }

        return copy;
    }

    private static List<JObject> RulesOf(LintRuleText text, List<LintRuleError> errors)
    {
        List<JObject> rules = new List<JObject>();
        JObject root;
        try
        {
            using JsonTextReader reader = new JsonTextReader(new System.IO.StringReader(text.Text));
            root = JObject.Load(reader, new JsonLoadSettings
            {
                LineInfoHandling = LineInfoHandling.Load,
                CommentHandling = CommentHandling.Ignore,
                DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
            });
        }
        catch (JsonException error)
        {
            int line = error is JsonReaderException reader ? reader.LineNumber : 0;
            int column = error is JsonReaderException position ? position.LinePosition : 0;
            errors.Add(new LintRuleError(text.File, null, null, line, column, $"not valid JSON: {error.Message}"));
            return rules;
        }

        JToken? version = root["schema_version"];
        if (version == null || version.Type != JTokenType.Integer || (int)version != SchemaVersion)
        {
            errors.Add(ErrorAt(text.File, null, "schema_version", (JToken?)version ?? root,
                $"schema_version must be {SchemaVersion}"));
            return rules;
        }

        if (!(root["rules"] is JArray array))
        {
            errors.Add(ErrorAt(text.File, null, "rules", root, "rules must be a list"));
            return rules;
        }

        foreach (JToken item in array)
        {
            if (item is JObject rule)
            {
                rules.Add(rule);
            }
            else
            {
                errors.Add(ErrorAt(text.File, null, "rules", item, "each rule is an object"));
            }
        }

        return rules;
    }

    private static string? IdOf(JObject rule, string file, List<LintRuleError> errors)
    {
        JToken? id = rule["id"];
        if (id == null || id.Type != JTokenType.String || !IdPattern.IsMatch((string)id!))
        {
            errors.Add(ErrorAt(file, null, "id", (JToken?)id ?? rule, "id must be lower case letters, digits and _, starting with a letter"));
            return null;
        }

        return (string)id!;
    }

    internal static LintRule? Compile(LintLibrary library, JObject source, LintRuleOrigin origin, string file,
        List<LintRuleError> errors)
    {
        string id = (string)source["id"]!;
        int before = errors.Count;
        foreach (JProperty property in source.Properties())
        {
            if (!Known.Contains(property.Name))
            {
                errors.Add(ErrorAt(file, id, property.Name, property, $"unknown field {property.Name}"));
            }
        }

        string? levelText = Text(source, "level", file, id, errors, true);
        ConflictLevel level = ConflictLevel.Warning;
        if (levelText != null && !LintRule.TryLevel(levelText, out level))
        {
            errors.Add(ErrorAt(file, id, "level", source["level"]!, "level is problem, warning or info"));
        }

        List<string> on = new List<string>();
        if (source["on"] == null)
        {
            on.Add("audit");
        }
        else if (source["on"] is JArray phases)
        {
            foreach (JToken phase in phases)
            {
                if (phase.Type != JTokenType.String || Array.IndexOf(LintRule.Phases, (string)phase!) < 0)
                {
                    errors.Add(ErrorAt(file, id, "on", phase, "on lists audit and/or dry_run"));
                }
                else if (!on.Contains((string)phase!))
                {
                    on.Add((string)phase!);
                }
            }
        }
        else
        {
            errors.Add(ErrorAt(file, id, "on", source["on"]!, "on is a list: [\"audit\", \"dry_run\"]"));
        }

        string? selectText = Text(source, "select", file, id, errors, true);
        string? assertText = Text(source, "assert", file, id, errors, true);
        string? messageText = Text(source, "message", file, id, errors, true);
        string? description = Text(source, "description", file, id, errors, false);
        if (errors.Count > before || selectText == null || assertText == null || messageText == null)
        {
            return null;
        }

        SelectNode select;
        try
        {
            select = LintParser.Select(selectText);
        }
        catch (LintSyntaxException error)
        {
            errors.Add(ExpressionError(file, id, "select", source["select"]!, error));
            return null;
        }

        ObjectType element = LintModel.ObjectNamed(LintSets.ElementTypeOf(select.Set))!;
        LintCompiler compiler = new LintCompiler(library);
        int world = compiler.NewSlot(frame => LintValue.Of(frame.Context.World.World));
        LintScope scope;
        LintUnit unit;
        LintUnit? whereUnit = null;
        LintEval? where = null;
        if (select.IsPairs)
        {
            int a = compiler.NewSlot(), b = compiler.NewSlot();
            scope = new LintScope(null).With("a", a, element).With("b", b, element).With("world", world, LintModel.World);
            unit = new LintUnit(compiler, a, b);
            if (select.Where != null)
            {
                LintCompiler whereCompiler = new LintCompiler(library);
                int whereWorld = whereCompiler.NewSlot(frame => LintValue.Of(frame.Context.World.World));
                int x = whereCompiler.NewSlot();
                LintScope whereScope = new LintScope(null, x, element).With("x", x, element).With("world", whereWorld, LintModel.World);
                where = Expression(() => whereCompiler.CompileBool(select.Where, whereScope).Eval, file, id, "select", source["select"]!, errors);
                whereUnit = new LintUnit(whereCompiler, x, -1);
            }
        }
        else
        {
            int x = compiler.NewSlot();
            scope = new LintScope(null, x, element).With("x", x, element).With("world", world, LintModel.World);
            unit = new LintUnit(compiler, x, -1);
        }

        List<(string, string)> lets = new List<(string, string)>();
        if (source["let"] is JObject letObject)
        {
            foreach (JProperty let in letObject.Properties())
            {
                if (let.Value.Type != JTokenType.String)
                {
                    errors.Add(ErrorAt(file, id, "let." + let.Name, let.Value, "a let is an expression string"));
                    continue;
                }

                string letName = let.Name;
                int slot = compiler.NewSlot();
                LintScope letScope = scope;
                LintCompiled? compiled = Expression(() => compiler.Compile(LintParser.Expression((string)let.Value!), letScope),
                    file, id, "let." + letName, let.Value, errors);
                if (compiled != null)
                {
                    compiler.SetLazy(slot, compiled.Eval);
                    scope = new LintScope(scope).With(letName, slot, compiled.Type);
                    lets.Add((letName, (string)let.Value!));
                }
            }
        }
        else if (source["let"] != null)
        {
            errors.Add(ErrorAt(file, id, "let", source["let"]!, "let is an object: {\"name\": \"expression\"}"));
        }

        LintScope ruleScope = scope;
        if (!select.IsPairs && select.Where != null)
        {
            where = Expression(() => compiler.CompileBool(select.Where, ruleScope).Eval, file, id, "select", source["select"]!, errors);
        }

        LintEval? assert = Expression(() => compiler.CompileBool(LintParser.Expression(assertText), ruleScope).Eval,
            file, id, "assert", source["assert"]!, errors);
        LintTemplate? message = Expression(() => LintTemplate.Compile(messageText, compiler, ruleScope),
            file, id, "message", source["message"]!, errors);
        LintEval? other = null;
        if (Text(source, "other", file, id, errors, false) is string otherText)
        {
            other = Expression(() =>
            {
                LintCompiled compiled = compiler.Compile(LintParser.Expression(otherText), ruleScope);
                if (!LintType.Nullable(LintModel.Thing).Accepts(compiled.Type) &&
                    !LintType.Nullable(LintType.Number).Accepts(compiled.Type))
                {
                    throw new LintSyntaxException($"other gives a thing or an id, not a {compiled.Type.Name}", 0);
                }

                return compiled.Eval;
            }, file, id, "other", source["other"]!, errors);
        }

        LintEval? levelWhen = null;
        if (Text(source, "level_when", file, id, errors, false) is string levelWhenText)
        {
            levelWhen = Expression(() =>
            {
                LintCompiled compiled = compiler.Compile(LintParser.Expression(levelWhenText), ruleScope);
                LintCompiler.Require(compiled.Type, LintType.String, LintParser.Expression(levelWhenText));
                return compiled.Eval;
            }, file, id, "level_when", source["level_when"]!, errors);
        }

        if (source["examples"] != null)
        {
            LintExamples.CheckShape(source["examples"]!, file, id, select.IsPairs, errors);
        }

        if (errors.Count > before || assert == null || message == null)
        {
            return null;
        }

        return new LintRule(id, level, description, on, selectText, assertText, select, unit, where, whereUnit, assert,
            message, messageText, other, levelWhen, source, origin, file, lets);
    }

    private static T? Expression<T>(Func<T> compile, string file, string id, string field, JToken token,
        List<LintRuleError> errors) where T : class
    {
        try
        {
            return compile();
        }
        catch (LintSyntaxException error)
        {
            errors.Add(ExpressionError(file, id, field, token, error));
            return null;
        }
        catch (ArgumentException error)
        {
            errors.Add(ErrorAt(file, id, field, token, error.Message));
            return null;
        }
    }

    private static string? Text(JObject source, string field, string file, string id, List<LintRuleError> errors,
        bool required)
    {
        JToken? token = source[field];
        if (token == null)
        {
            if (required)
            {
                errors.Add(ErrorAt(file, id, field, source, $"{field} is missing"));
            }

            return null;
        }

        if (token.Type != JTokenType.String)
        {
            errors.Add(ErrorAt(file, id, field, token, $"{field} is a string"));
            return null;
        }

        return (string)token!;
    }

    private static LintRuleError ExpressionError(string file, string id, string field, JToken token,
        LintSyntaxException error)
    {
        IJsonLineInfo info = token;
        return new LintRuleError(file, id, field, info.HasLineInfo() ? info.LineNumber : 0,
            info.HasLineInfo() ? info.LinePosition : 0, error.Message, error.Position + 1);
    }

    internal static LintRuleError ErrorAt(string file, string? id, string? field, JToken token, string message)
    {
        IJsonLineInfo info = token;
        return new LintRuleError(file, id, field, info.HasLineInfo() ? info.LineNumber : 0,
            info.HasLineInfo() ? info.LinePosition : 0, message);
    }
}
