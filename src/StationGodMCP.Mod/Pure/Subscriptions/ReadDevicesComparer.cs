#nullable enable

using System;
using System.Collections.Generic;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Pure.Subscriptions;

/// <summary>
/// Compares two read_devices readings value by value, without building JSON: numbers as numbers (NaN equals NaN),
/// strings ordinally, every error by its code, lists in order. The clock is left out: it changes every sample and is
/// never a change by itself. Derived counts are covered by comparing the items they count.
/// </summary>
internal sealed class ReadDevicesComparer : IReadingComparer<ReadDevicesView>
{
    internal static readonly ReadDevicesComparer Instance = new ReadDevicesComparer();

    private ReadDevicesComparer()
    {
    }

    public bool SameValues(ReadDevicesView previous, ReadDevicesView next)
    {
        if (!string.Equals(previous.GatewayId, next.GatewayId, StringComparison.Ordinal) ||
            previous.Results.Count != next.Results.Count)
        {
            return false;
        }

        for (int index = 0; index < previous.Results.Count; index++)
        {
            if (!SameItem(previous.Results[index], next.Results[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameItem(BatchItemView previous, BatchItemView next)
    {
        if (previous.Index != next.Index || previous.Ok != next.Ok)
        {
            return false;
        }

        return (previous, next) switch
        {
            (DeviceReadItemView a, DeviceReadItemView b) => SameParts(a, b),
            (DeviceReadFailedView a, DeviceReadFailedView b) =>
                a.ReferenceId.Equals(b.ReferenceId) && SameError(a.Error, b.Error),
            (BatchErrorView a, BatchErrorView b) => SameError(a.Error, b.Error),
            _ => false,
        };
    }

    private static bool SameParts(DeviceReadItemView a, DeviceReadItemView b) =>
        a.ReferenceId.Equals(b.ReferenceId) &&
        SameNumbers(a.Logic, b.Logic) &&
        SameErrors(a.LogicErrors, b.LogicErrors) &&
        SameSlots(a.Slots, b.Slots) &&
        SameAtmosphere(a.Atmosphere, b.Atmosphere) &&
        SameReagents(a.Reagents, b.Reagents) &&
        SameErrors(a.Errors, b.Errors);

    private static bool SameSlots(List<SlotReadView>? a, List<SlotReadView>? b)
    {
        if (a == null || b == null)
        {
            return a == b;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        for (int index = 0; index < a.Count; index++)
        {
            SlotReadView x = a[index];
            SlotReadView y = b[index];
            if (x.Index != y.Index || !SameNumbers(x.Logic, y.Logic) || !SameErrors(x.LogicErrors, y.LogicErrors) ||
                !SameOptionalError(x.Error, y.Error))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameAtmosphere(AtmosphereReadView? a, AtmosphereReadView? b)
    {
        if (a == null || b == null)
        {
            return a == b;
        }

        if (!string.Equals(a.Source, b.Source, StringComparison.Ordinal) ||
            !a.AtmosphereId.Equals(b.AtmosphereId) ||
            a.NetworkId.HasValue != b.NetworkId.HasValue ||
            a.NetworkId.HasValue && !a.NetworkId.GetValueOrDefault().Equals(b.NetworkId.GetValueOrDefault()) ||
            !ReadingNumbers.SameNumber(a.VolumeL, b.VolumeL) ||
            !ReadingNumbers.SameNumber(a.PressureKpa, b.PressureKpa) ||
            !ReadingNumbers.SameNumber(a.TemperatureK, b.TemperatureK) ||
            !ReadingNumbers.SameNumber(a.TotalMol, b.TotalMol) ||
            !ReadingNumbers.SameNumber(a.LiquidVolumeL, b.LiquidVolumeL) ||
            a.Contents.Count != b.Contents.Count)
        {
            return false;
        }

        for (int index = 0; index < a.Contents.Count; index++)
        {
            CompactGasView x = a.Contents[index];
            CompactGasView y = b.Contents[index];
            if (!string.Equals(x.Gas, y.Gas, StringComparison.Ordinal) ||
                !string.Equals(x.State, y.State, StringComparison.Ordinal) ||
                !ReadingNumbers.SameNumber(x.AmountMol, y.AmountMol) ||
                !ReadingNumbers.SameOptionalNumber(x.LiquidL, y.LiquidL))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameReagents(ReagentsReadView? a, ReagentsReadView? b)
    {
        if (a == null || b == null)
        {
            return a == b;
        }

        if (!ReadingNumbers.SameNumber(a.Total, b.Total) || a.Reagents.Count != b.Reagents.Count)
        {
            return false;
        }

        for (int index = 0; index < a.Reagents.Count; index++)
        {
            ReagentView x = a.Reagents[index];
            ReagentView y = b.Reagents[index];
            if (!string.Equals(x.Reagent, y.Reagent, StringComparison.Ordinal) ||
                !string.Equals(x.Name, y.Name, StringComparison.Ordinal) ||
                !ReadingNumbers.SameNumber(x.Quantity, y.Quantity) ||
                !string.Equals(x.Unit, y.Unit, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameNumbers(Dictionary<string, double>? a, Dictionary<string, double>? b)
    {
        if (a == null || b == null)
        {
            return a == b;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        foreach (KeyValuePair<string, double> entry in a)
        {
            if (!b.TryGetValue(entry.Key, out double other) || !ReadingNumbers.SameNumber(entry.Value, other))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameErrors(Dictionary<string, ErrorView>? a, Dictionary<string, ErrorView>? b)
    {
        int countA = a?.Count ?? 0;
        int countB = b?.Count ?? 0;
        if (countA != countB)
        {
            return false;
        }

        if (countA == 0)
        {
            return true;
        }

        foreach (KeyValuePair<string, ErrorView> entry in a!)
        {
            if (!b!.TryGetValue(entry.Key, out ErrorView? other) || !SameError(entry.Value, other))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameOptionalError(ErrorView? a, ErrorView? b) =>
        a == null || b == null ? a == b : SameError(a, b);

    private static bool SameError(ErrorView a, ErrorView b) => string.Equals(a.Code, b.Code, StringComparison.Ordinal);
}

/// <summary>How readings compare numbers.</summary>
internal static class ReadingNumbers
{
    /// <summary>Equal as numbers: NaN equals NaN (both read "NaN" on the wire), and 0 equals -0.</summary>
    internal static bool SameNumber(double a, double b) => a.Equals(b);

    internal static bool SameOptionalNumber(double? a, double? b) =>
        a.HasValue == b.HasValue && (!a.HasValue || SameNumber(a.GetValueOrDefault(), b.GetValueOrDefault()));
}
