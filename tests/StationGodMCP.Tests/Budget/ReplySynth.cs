#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using StationGodMCP.Api.Shared;

namespace StationGodMCP.Tests.Budget;

/// <summary>
/// Builds a reply view the way a large world fills it, without the game: every field of the view and of everything
/// it holds is set (so every optional key is written), each list and dictionary gets the length its declaration in a
/// ReplyShape gives, and a list nobody declared gets UndeclaredLength entries, enough that a list of real entries
/// breaks the budget until its bound is declared. Fields are filled behind the constructors
/// (RuntimeHelpers.GetUninitializedObject, then every instance field), so computed properties (has_more, a note
/// constant) serialise as they would. A field is named Type.Member, with an auto-property's backing field named after
/// its property.
/// </summary>
internal sealed class ReplySynth
{
    /// <summary>Entries in a list or dictionary whose bound no ReplyShape declares.</summary>
    internal const int UndeclaredLength = 64;

    private const int MaximumDepth = 9;
    private const string Word = "Abcdefghijklmnopqrst";

    private static readonly Assembly Mod = typeof(ApiJson).Assembly;

    private readonly ReplyShape _shape;
    private readonly HashSet<string> _undeclared = new HashSet<string>(StringComparer.Ordinal);
    private readonly HashSet<string> _used = new HashSet<string>(StringComparer.Ordinal);
    private long _nextId = 100000;

    internal ReplySynth(ReplyShape shape) => _shape = shape;

    /// <summary>Lists and dictionaries the shape does not bound, by Type.Member: candidates that grow with the world.</summary>
    internal IReadOnlyCollection<string> Undeclared => _undeclared;

    /// <summary>The declarations the build consulted, by Type.Member.</summary>
    internal IReadOnlyCollection<string> Used => _used;

    internal object Make(Type type) => Value(type, type.Name, 0)!;

    private object? Value(Type type, string member, int depth)
    {
        Type? underlying = Nullable.GetUnderlyingType(type);
        if (underlying != null)
        {
            return Value(underlying, member, depth);
        }

        if (_shape.Factory(type) is { } factory)
        {
            return factory();
        }

        if (type == typeof(string))
        {
            if (_shape.TextOf(member) is { } text)
            {
                _used.Add(member);
                return text;
            }

            return Word;
        }

        if (type == typeof(bool))
        {
            return true;
        }

        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
        {
            return Convert.ChangeType(1234.5678, type, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (type == typeof(long) || type == typeof(ulong))
        {
            return Convert.ChangeType(_nextId++, type, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (type.IsPrimitive)
        {
            return Convert.ChangeType(12, type, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (type.IsEnum)
        {
            Array values = Enum.GetValues(type);
            return values.Length > 0 ? values.GetValue(values.Length - 1) : Activator.CreateInstance(type);
        }

        if (type == typeof(object))
        {
            string owner = member.EndsWith("[]", StringComparison.Ordinal) ? member.Substring(0, member.Length - 2) : member;
            if (Held(owner) is { } held)
            {
                return Value(held, held.Name, depth);
            }

            return new ThingView(new ThingId(_nextId++), "StructureAbcdefghijklm", Word);
        }

        if (type.IsArray)
        {
            int length = depth >= MaximumDepth ? 0 : Length(member);
            Type element = type.GetElementType()!;
            Array array = Array.CreateInstance(element, length);
            for (int index = 0; index < length; index++)
            {
                array.SetValue(Value(element, ElementName(element, member), depth + 1), index);
            }

            return array;
        }

        if (DictionaryTypes(type) is { } pair)
        {
            int length = depth >= MaximumDepth ? 0 : Length(member);
            IDictionary dictionary = (IDictionary)Activator.CreateInstance(type.IsClass && !type.IsAbstract
                ? type
                : typeof(Dictionary<,>).MakeGenericType(pair.Key, pair.Value))!;
            for (int index = 0; index < length; index++)
            {
                object key = pair.Key == typeof(string) ? "Key" + index : Value(pair.Key, member, depth + 1)!;
                if (!dictionary.Contains(key))
                {
                    dictionary.Add(key, Value(pair.Value, ElementName(pair.Value, member), depth + 1));
                }
            }

            return dictionary;
        }

        if (ListElement(type) is { } declaredElement)
        {
            Type elementType = Held(member) ?? declaredElement;
            int length = depth >= MaximumDepth ? 0 : Length(member);
            IList list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(declaredElement))!;
            for (int index = 0; index < length; index++)
            {
                list.Add(Value(elementType, elementType == declaredElement ? ElementName(elementType, member) : elementType.Name,
                    depth + 1));
            }

            return list;
        }

        if (type.IsInterface || type.IsAbstract)
        {
            Type? concrete = ConcreteOf(type);
            return concrete == null || depth >= MaximumDepth ? null : Value(concrete, member, depth);
        }

        if (depth >= MaximumDepth && !type.IsValueType)
        {
            return null;
        }

        return Fill(type, depth);
    }

    private object Fill(Type type, int depth)
    {
        object instance = RuntimeHelpers.GetUninitializedObject(type);
        for (Type? current = type; current != null && current != typeof(object) && current != typeof(ValueType);
             current = current.BaseType)
        {
            foreach (FieldInfo field in current.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                          BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                string member = type.Name + "." + MemberName(field);
                if (_shape.IsAbsent(member))
                {
                    _used.Add(member);
                    continue;
                }

                field.SetValue(instance, Value(field.FieldType, member, depth + 1));
            }
        }

        return instance;
    }

    // A top-level list of the reply view the mod cuts by default (x-default-limits); int.MaxValue otherwise.
    private int TopLimit(string member)
    {
        string prefix = _shape.View.Name + ".";
        if (!member.StartsWith(prefix, StringComparison.Ordinal))
        {
            return int.MaxValue;
        }

        string key = Snake.GetPropertyName(member.Substring(prefix.Length), false);
        return _shape.TopLimits.TryGetValue(key, out int limit) ? limit : int.MaxValue;
    }

    private static readonly Newtonsoft.Json.Serialization.SnakeCaseNamingStrategy Snake =
        new Newtonsoft.Json.Serialization.SnakeCaseNamingStrategy();

    // The type a member holds on the wire when its declared type is object or a base class.
    private Type? Held(string member)
    {
        Type? held = _shape.HeldBy(member);
        if (held != null)
        {
            _used.Add(member);
        }

        return held;
    }

    private int Length(string member)
    {
        if (_shape.LengthOf(member) is int declared)
        {
            _used.Add(member);
            return Math.Min(declared, TopLimit(member));
        }

        _undeclared.Add(member);
        return UndeclaredLength;
    }

    // An element inherits its list's name, so a list of strings or numbers can be declared by the list alone.
    private static string ElementName(Type element, string member) =>
        element == typeof(string) || element.IsPrimitive || element == typeof(object) ? member + "[]" : element.Name;

    private static string MemberName(FieldInfo field)
    {
        string name = field.Name;
        if (name.StartsWith("<", StringComparison.Ordinal))
        {
            int end = name.IndexOf('>');
            return end > 1 ? name.Substring(1, end - 1) : name;
        }

        return name.TrimStart('_');
    }

    private static KeyValuePair<Type, Type>? DictionaryTypes(Type type)
    {
        foreach (Type candidate in type.GetInterfaces().Prepend(type))
        {
            if (candidate.IsGenericType &&
                (candidate.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                 candidate.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)))
            {
                Type[] arguments = candidate.GetGenericArguments();
                return new KeyValuePair<Type, Type>(arguments[0], arguments[1]);
            }
        }

        return null;
    }

    private static Type? ListElement(Type type)
    {
        foreach (Type candidate in type.GetInterfaces().Prepend(type))
        {
            if (candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                return candidate.GetGenericArguments()[0];
            }
        }

        return null;
    }

    // The first concrete type of the mod's views (by name) that implements or derives from type.
    private static Type? ConcreteOf(Type type) =>
        Mod.GetTypes()
            .Where(candidate => candidate.IsClass && !candidate.IsAbstract && !candidate.IsGenericTypeDefinition &&
                                type.IsAssignableFrom(candidate))
            .OrderBy(candidate => candidate.Name, StringComparer.Ordinal)
            .FirstOrDefault();
}
