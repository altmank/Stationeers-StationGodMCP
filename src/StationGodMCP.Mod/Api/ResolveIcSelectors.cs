#nullable enable

using System;
using System.Collections.Generic;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;

namespace StationGodMCP.Api;

/// <summary>
/// resolve_ic_selectors: what a chip's pins d0..dN reach now (IcTarget.GetPins), its aliases, and for each device in
/// the scope the prefab and name hash a batch instruction (lbn, sbn) selects it by (ILogicable.GetNameHash), with how
/// many visible devices share that pair. Read only.
/// </summary>
internal static class ResolveIcSelectorsApi
{
    private const int MaximumTargets = 256;

    internal static IcSelectorsView Handle(Args args)
    {
        DeviceScope scope = Devices.Scope(args);
        IcTarget ic = Devices.RequireCircuitHolder(scope, args);
        HashSet<long>? targets = args.Has("target_reference_ids")
            ? IdSet(args.ThingIds("target_reference_ids", MaximumTargets))
            : null;
        ProgrammableChip chip = ic.RequireChip();

        List<PinTargetView> pins = new List<PinTargetView>();
        IList<ILogicable?> devices = ic.GetPins();
        for (int index = 0; index < devices.Count; index++)
        {
            ScopedTarget? target = ScopedTarget.From(devices[index]);
            pins.Add(new PinTargetView(index, devices[index] != null,
                target == null ? null : Devices.ViewOf(target, scope)));
        }

        return new IcSelectorsView(IcRuntime.PlaceOf(scope, ic), Devices.ViewOf(ic.Target, scope), pins,
            IcRuntime.Aliases(chip), Selectors(scope.SortedDevices(), targets));
    }

    private static List<StableSelectorView> Selectors(List<ScopedTarget> devices, HashSet<long>? targets)
    {
        Dictionary<long, int> pairCounts = new Dictionary<long, int>();
        foreach (ScopedTarget device in devices)
        {
            int? nameHash = NameHash(device);
            if (nameHash.HasValue)
            {
                long pair = Pair(device.PrefabHash, nameHash.Value);
                pairCounts[pair] = pairCounts.TryGetValue(pair, out int count) ? count + 1 : 1;
            }
        }

        List<StableSelectorView> selectors = new List<StableSelectorView>();
        foreach (ScopedTarget device in devices)
        {
            if (targets != null && !targets.Contains(device.ReferenceId))
            {
                continue;
            }

            int? nameHash = NameHash(device);
            int collisions = nameHash.HasValue &&
                             pairCounts.TryGetValue(Pair(device.PrefabHash, nameHash.Value), out int count)
                ? count
                : 0;
            selectors.Add(new StableSelectorView(
                new ThingView(new ThingId(device.ReferenceId), device.PrefabName, device.DisplayName),
                device.PrefabHash, nameHash, collisions));
        }

        return selectors;
    }

    // The two 32-bit hashes as one key.
    private static long Pair(int prefabHash, int nameHash) => ((long)prefabHash << 32) | (uint)nameHash;

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

    private static HashSet<long> IdSet(List<ThingId> ids)
    {
        HashSet<long> set = new HashSet<long>();
        foreach (ThingId id in ids)
        {
            set.Add(id.Value);
        }

        return set;
    }
}
