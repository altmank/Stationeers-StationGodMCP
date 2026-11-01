#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Assets.Scripts.Objects;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// inspect prefab include data (2.8.0): a prefab's own game data, read generically from every field Unity serializes on
/// it. That is every public or [SerializeField] instance field, from Thing down to the prefab's own class, that is not
/// [NonSerialized], readonly or static. Examples: maxPressureDelta (-1: no limit), flashpointTemperature,
/// autoignitionTemperature, MaxQuantity, UsedPower, and BuildStates with each state's tool and its cost.
/// <list type="bullet">
/// <item>Numbers, strings and booleans are written as they are, and enums by name.</item>
/// <item>A Thing a field holds (a kit's constructables, a build state's tool) is written as its prefab name. Any other
/// Unity object (a mesh, a transform, a sound) is left out.</item>
/// <item>Lists and nested serializable values are read down to MaximumDepth levels, at most ListCap entries per
/// list.</item>
/// <item>A dynamic thing also gets mass_kg from its Rigidbody.</item>
/// </list>
/// Field values are read without running the game's code. Main thread only.
/// </summary>
internal static class PrefabData
{
    internal const int MaximumDepth = 3;

    internal const int ListCap = 32;

    internal const string MassKey = "mass_kg";

    private const BindingFlags Declared =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    private static readonly Dictionary<Type, List<FieldInfo>> Serialized = new Dictionary<Type, List<FieldInfo>>();

    private static readonly Dictionary<Type, MethodInfo?> ToDoubles = new Dictionary<Type, MethodInfo?>();

    /// <summary>The prefab's serialized fields, base class first, and its mass.</summary>
    internal static JObject Of(Thing prefab)
    {
        JObject data = Fields(prefab, 0);
        if (prefab is DynamicThing dynamic && dynamic.RigidBody != null)
        {
            data[MassKey] = Number(dynamic.RigidBody.mass);
        }

        return data;
    }

    private static JObject Fields(object owner, int depth)
    {
        JObject data = new JObject();
        foreach (FieldInfo field in FieldsOf(owner.GetType()))
        {
            object? value;
            try
            {
                value = field.GetValue(owner);
            }
            catch (Exception error) when (error is FieldAccessException || error is TargetException ||
                                          error is NotSupportedException)
            {
                // FieldInfo.GetValue on a field the runtime will not read (a ref struct, a closed generic); left out.
                continue;
            }

            JToken? token = Value(value, depth);
            if (token != null && !data.ContainsKey(field.Name))
            {
                data[field.Name] = token;
            }
        }

        return data;
    }

    // The fields Unity serializes on the type, base class first, cached per type. A Thing's walk stops at
    // MonoBehaviour: the engine's own members are no game data.
    private static List<FieldInfo> FieldsOf(Type type)
    {
        if (Serialized.TryGetValue(type, out List<FieldInfo> cached))
        {
            return cached;
        }

        List<Type> chain = new List<Type>();
        for (Type? at = type; at != null && at != typeof(MonoBehaviour) && at != typeof(object) &&
                              at != typeof(ValueType); at = at.BaseType)
        {
            chain.Add(at);
        }

        List<FieldInfo> fields = new List<FieldInfo>();
        for (int index = chain.Count - 1; index >= 0; index--)
        {
            foreach (FieldInfo field in chain[index].GetFields(Declared))
            {
                if (IsSerialized(field))
                {
                    fields.Add(field);
                }
            }
        }

        Serialized[type] = fields;
        return fields;
    }

    private static bool IsSerialized(FieldInfo field) =>
        !field.IsStatic && !field.IsInitOnly && !field.IsLiteral && !field.IsNotSerialized &&
        (field.IsPublic || field.IsDefined(typeof(SerializeField), false)) &&
        !typeof(Delegate).IsAssignableFrom(field.FieldType);

    // A value as data, or null when it is left out (null, a Unity object that is no Thing, too deep, nothing in it).
    private static JToken? Value(object? value, int depth)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return text;
            case bool flag:
                return flag;
            case float single:
                return Number(single);
            case double number:
                return Number(number);
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                return JToken.FromObject(value);
            case Enum named:
                return named.ToString();
            case Thing thing:
                return thing != null ? thing.PrefabName : null;
            case UnityEngine.Object:
                return null;
        }

        if (depth >= MaximumDepth)
        {
            return null;
        }

        if (value is IList list)
        {
            return List(list, depth);
        }

        Type type = value.GetType();
        if (value is IEnumerable || !(type.IsValueType || type.IsSerializable))
        {
            // Dictionaries, sets and plain runtime classes: Unity does not serialize them.
            return null;
        }

        JObject nested = Fields(value, depth + 1);
        if (nested.Count > 0)
        {
            return nested;
        }

        // A unit struct (PressurekPa, TemperatureKelvin) keeps its number in a private field: its ToDouble.
        return type.IsValueType && ToDoubleOf(type) is { } toDouble ? Number((double)toDouble.Invoke(value, null)) : null;
    }

    private static JToken? List(IList list, int depth)
    {
        JArray items = new JArray();
        int count = list.Count;
        for (int index = 0; index < count && items.Count < ListCap; index++)
        {
            JToken? item = Value(list[index], depth + 1);
            if (item != null)
            {
                items.Add(item);
            }
        }

        if (count > ListCap)
        {
            items.Add($"+{count - ListCap} more");
        }

        return items.Count > 0 ? items : null;
    }

    private static MethodInfo? ToDoubleOf(Type type)
    {
        if (!ToDoubles.TryGetValue(type, out MethodInfo? method))
        {
            method = type.GetMethod("ToDouble", BindingFlags.Instance | BindingFlags.Public, null, Type.EmptyTypes,
                null);
            ToDoubles[type] = method != null && method.ReturnType == typeof(double) ? method : null;
        }

        return ToDoubles[type];
    }

    // NaN and infinities are no JSON numbers: they are written as their names.
    private static JToken Number(double value) =>
        double.IsNaN(value) || double.IsInfinity(value) ? (JToken)value.ToString() : Math.Round(value, 6);
}
