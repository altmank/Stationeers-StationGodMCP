#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// A circuit holder's chip as inspect chip reads it, one part at a time. Status: the chip (line, errors, language;
/// without its source), the holder's power state, its pins (IcPins), for an IC10 chip its runtime state (IcRuntime)
/// with a window of its stack, and for a Lua chip StationeersLua's runtime with the last lines of its print() log; a
/// holder with no chip reports its pins only. Source: the chip's source (ICircuitHolder.GetSourceCode, decompressed
/// for a Lua chip), its line and language. Selectors: what pins d0..dN reach now (IcTarget.GetPins), its aliases, and
/// the prefab and name hash a batch instruction (lb, lbn, sb, sbn) selects a device by (ILogicable.GetNameHash).
/// Batch instructions walk only the holder's batch list (ICircuitHolder.GetBatchOutput: an IC Housing's data network;
/// null without one), so selectors default to the devices in that list the scope shows, and uniqueness is counted
/// over that list; a named target outside it is listed with reachable false. Read only.
/// </summary>
internal static class ChipReads
{
    /// <summary>The status part: the stack window from stackStart, stackCount values, logLines of a Lua log.</summary>
    internal static object Status(DeviceScope scope, IcTarget ic, int stackStart, int stackCount, int logLines)
    {
        IcPlace place = IcRuntime.PlaceOf(scope, ic);
        ProgrammableChip? chip = ic.Chip();
        if (chip == null)
        {
            return new IcNoChipView(place, IcPins.Describe(ic));
        }

        ChipProgram program = ChipProgram.Of(chip);
        IcRuntimeParts parts = new IcRuntimeParts(IcPins.Describe(ic),
            program.Language == IcChip.Ic10 ? IcRuntime.Capture(chip, ic.HolderId, stackStart, stackCount) : null);
        IcChip described = program.Describe(ic, chip, logLines);
        return new IcStatusView(place, described.WithoutSource(), IcRuntime.State(chip), ic.StateView(), parts);
    }

    /// <summary>The source part; refused when the holder has no chip.</summary>
    internal static IcSourceView Source(DeviceScope scope, IcTarget ic)
    {
        ProgrammableChip chip = ic.RequireChip();
        IcChip described = ChipProgram.Of(chip).Describe(ic, chip, 0);
        return new IcSourceView(IcRuntime.PlaceOf(scope, ic), described, chip.LineNumber);
    }

    /// <summary>The selectors part: the targets named (null: the batch list's devices the scope shows).</summary>
    internal static IcSelectorsView Selectors(DeviceScope scope, IcTarget ic, List<ThingId>? targetIds)
    {
        ProgrammableChip chip = ic.RequireChip();
        List<PinTargetView> pins = new List<PinTargetView>();
        IList<ILogicable?> devices = ic.GetPins();
        for (int index = 0; index < devices.Count; index++)
        {
            // A pin with no device carries nothing: left out, the selector of each pin listed names it.
            if (devices[index] == null)
            {
                continue;
            }

            ScopedTarget? target = ScopedTarget.From(devices[index]);
            pins.Add(new PinTargetView(index, devices[index] != null,
                target == null ? null : Devices.ViewOf(target, scope)));
        }

        List<ScopedTarget>? batch = BatchList(ic);
        BatchSelectors reach = new BatchSelectors(Keys(batch ?? new List<ScopedTarget>()));
        List<ScopedTarget> listed = targetIds != null
            ? Targets(scope, targetIds)
            : Visible(scope, batch ?? new List<ScopedTarget>());
        List<StableSelectorView> selectors = listed.ConvertAll(device => Selector(device, reach));

        return new IcSelectorsView(IcRuntime.PlaceOf(scope, ic), Devices.ViewOf(ic.Target, scope), pins,
            IcRuntime.Aliases(chip), selectors, batch == null ? (int?)null : reach.DeviceCount);
    }

    // The devices the chip's batch instructions walk, as the chip gets them; null when the holder has no list.
    private static List<ScopedTarget>? BatchList(IcTarget ic)
    {
        List<ILogicable>? output = ic.Holder.GetBatchOutput();
        if (output == null)
        {
            return null;
        }

        List<ScopedTarget> batch = new List<ScopedTarget>(output.Count);
        foreach (ILogicable logicable in output)
        {
            if (ScopedTarget.From(logicable) is { } device)
            {
                batch.Add(device);
            }
        }

        return batch;
    }

    private static IEnumerable<(long, int, int?)> Keys(List<ScopedTarget> batch)
    {
        foreach (ScopedTarget device in batch)
        {
            yield return (device.ReferenceId, device.PrefabHash, NameHash(device));
        }
    }

    // Every target named must be a device the scope shows, as on every other device tool.
    private static List<ScopedTarget> Targets(DeviceScope scope, List<ThingId> ids)
    {
        HashSet<long> seen = new HashSet<long>();
        List<ScopedTarget> targets = new List<ScopedTarget>(ids.Count);
        foreach (ThingId id in ids)
        {
            if (seen.Add(id.Value))
            {
                targets.Add(Devices.Require(scope, id));
            }
        }

        return targets;
    }

    // The batch devices the scope shows, by reference id; the world shows them all.
    private static List<ScopedTarget> Visible(DeviceScope scope, List<ScopedTarget> batch)
    {
        HashSet<long> seen = new HashSet<long>();
        List<ScopedTarget> visible = batch.FindAll(device =>
            seen.Add(device.ReferenceId) && (scope.IsWorld || Devices.Find(scope, device.ReferenceId) != null));
        visible.Sort(static (a, b) => a.ReferenceId.CompareTo(b.ReferenceId));
        return visible;
    }

    private static StableSelectorView Selector(ScopedTarget device, BatchSelectors reach)
    {
        int? nameHash = NameHash(device);
        return new StableSelectorView(
            new ThingView(new ThingId(device.ReferenceId), device.PrefabName, device.DisplayName),
            device.PrefabHash, nameHash, reach.CountOf(device.PrefabHash, nameHash),
            reach.Reaches(device.ReferenceId));
    }

    private static int? NameHash(ScopedTarget device)
    {
        try
        {
            return device.GetNameHash();
        }
        catch (Exception)
        {
            // ILogicable.GetNameHash on a device with no name yet: it has no name selector.
            return null;
        }
    }
}
