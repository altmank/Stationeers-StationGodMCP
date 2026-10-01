#nullable enable

using System;
using System.Collections.Concurrent;

namespace StationGodMCP.Pure;

/// <summary>
/// Enum.GetName, asked once per value and remembered: the hot read tools name a logic type, slot type, slot class or
/// gas on every read, and Enum.GetName looks the value up by reflection each time. The name is whatever Enum.GetName
/// answers on this runtime (also for values that share a number, and null for a value with no name), so the wire is
/// unchanged. Safe from any thread.
/// </summary>
internal static class EnumNames<TEnum> where TEnum : struct, Enum
{
    private static readonly ConcurrentDictionary<TEnum, string?> Known = new ConcurrentDictionary<TEnum, string?>();

    private static readonly Func<TEnum, string?> Ask = static value => Enum.GetName(typeof(TEnum), value);

    internal static string? Of(TEnum value) => Known.GetOrAdd(value, Ask);
}
