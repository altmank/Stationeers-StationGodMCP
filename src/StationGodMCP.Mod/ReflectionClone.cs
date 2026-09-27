#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace StationGodMCP;

/// <summary>
/// Field copies between game components, for PrefabRegistrar's gateway: the cloned Logic Memory's LogicMemory
/// component is replaced by a StationGodGateway that gets the same field values, and every reference to the old
/// component under the clone is pointed at the new one. A field that cannot be read or set is skipped.
/// </summary>
internal static class ReflectionClone
{
    private const BindingFlags DeclaredInstanceFields =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    // Plain objects are followed this many levels deep looking for references to the old component.
    private const int MaximumDepth = 5;

    internal static void CopyMatchingFields(Component source, Component destination)
    {
        Dictionary<string, FieldInfo> destinationFields = new Dictionary<string, FieldInfo>(StringComparer.Ordinal);
        for (Type? type = destination.GetType(); type != null && type != typeof(Component); type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(DeclaredInstanceFields))
            {
                destinationFields[field.Name] = field;
            }
        }

        for (Type? type = source.GetType(); type != null && type != typeof(Component); type = type.BaseType)
        {
            foreach (FieldInfo sourceField in type.GetFields(DeclaredInstanceFields))
            {
                if (destinationFields.TryGetValue(sourceField.Name, out FieldInfo destinationField) &&
                    destinationField.FieldType.IsAssignableFrom(sourceField.FieldType))
                {
                    TryCopy(sourceField, source, destinationField, destination);
                }
            }
        }
    }

    internal static void CopyAllFields(object source, object destination)
    {
        for (Type? type = source.GetType(); type != null && type != typeof(object); type = type.BaseType)
        {
            foreach (FieldInfo field in type.GetFields(DeclaredInstanceFields))
            {
                TryCopy(field, source, field, destination);
            }
        }
    }

    internal static void ReplaceComponentReferences(GameObject root, Component oldComponent, Component newComponent)
    {
        ReferenceSwap swap = new ReferenceSwap(oldComponent, newComponent);
        foreach (Component component in root.GetComponentsInChildren<Component>(true))
        {
            if (component != null && component != oldComponent)
            {
                swap.Visit(component, 0);
            }
        }
    }

    private static void TryCopy(FieldInfo from, object source, FieldInfo to, object destination)
    {
        try
        {
            to.SetValue(destination, from.GetValue(source));
        }
        catch (Exception)
        {
            // FieldInfo.GetValue or SetValue on a readonly or init-only game field: it keeps its own value.
        }
    }

    /// <summary>One walk from a clone's components, swapping each reference to the old component for the new.</summary>
    private sealed class ReferenceSwap
    {
        private readonly Component _oldComponent;
        private readonly Component _newComponent;
        private readonly HashSet<object> _visited = new HashSet<object>(ReferenceComparer.Instance);

        internal ReferenceSwap(Component oldComponent, Component newComponent)
        {
            _oldComponent = oldComponent;
            _newComponent = newComponent;
        }

        internal void Visit(object? value, int depth)
        {
            if (value == null || depth > MaximumDepth || !_visited.Add(value))
            {
                return;
            }

            Type type = value.GetType();
            if (type.IsPrimitive || type.IsEnum || type == typeof(string) || typeof(Delegate).IsAssignableFrom(type))
            {
                return;
            }

            if (value is IList list)
            {
                VisitList(list, depth);
                return;
            }

            for (Type? current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                foreach (FieldInfo field in current.GetFields(DeclaredInstanceFields))
                {
                    VisitField(field, value, depth);
                }
            }
        }

        private void VisitList(IList list, int depth)
        {
            for (int index = 0; index < list.Count; index++)
            {
                object? item = list[index];
                if (!ReferenceEquals(item, _oldComponent))
                {
                    Visit(item, depth + 1);
                    continue;
                }

                try
                {
                    list[index] = _newComponent;
                }
                catch (Exception)
                {
                    // IList's indexer on a fixed-size or typed list that cannot hold the new component: left as is.
                }
            }
        }

        private void VisitField(FieldInfo field, object owner, int depth)
        {
            object? fieldValue;
            try
            {
                fieldValue = field.GetValue(owner);
            }
            catch (Exception)
            {
                // FieldInfo.GetValue on a field whose getter throws: not followed.
                return;
            }

            if (ReferenceEquals(fieldValue, _oldComponent) && field.FieldType.IsAssignableFrom(_newComponent.GetType()))
            {
                TrySwap(field, owner);
            }
            else if (!(fieldValue is UnityEngine.Object))
            {
                Visit(fieldValue, depth + 1);
            }
        }

        private void TrySwap(FieldInfo field, object owner)
        {
            try
            {
                field.SetValue(owner, _newComponent);
            }
            catch (Exception)
            {
                // FieldInfo.SetValue on a readonly field: it keeps the old component.
            }
        }
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new ReferenceComparer();

        public new bool Equals(object? left, object? right) => ReferenceEquals(left, right);

        public int GetHashCode(object value) => RuntimeHelpers.GetHashCode(value);
    }
}
