#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// resolve_ic_selectors: what a chip's pins d0..dN reach now (IcTarget.GetPins), its aliases, and the prefab and name
/// hash a batch instruction (lb, lbn, sb, sbn) selects a device by (ILogicable.GetNameHash). Batch instructions walk
/// only the holder's batch list (ICircuitHolder.GetBatchOutput: an IC Housing's data network; null without one, and
/// the chip then fails with DeviceListNull), so selectors default to the devices in that list the scope shows, and
/// uniqueness is counted over that list. A named target outside it is listed with reachable false. Read only.
/// </summary>
internal static class ResolveIcSelectorsApi
{
    private const int MaximumTargets = 256;

    internal static IcSelectorsView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        List<ThingId>? targetIds = args.Has("target_reference_ids")
            ? args.ThingIds("target_reference_ids", MaximumTargets)
            : null;
        IcTarget ic = Devices.RequireCircuitHolder(scope, args);
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
