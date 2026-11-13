#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>A slot of a prefab template as the rule reads it: its class (None or a named class) and the prefabs it names.</summary>
internal sealed record PrefabSlotFacts(bool ClassIsNone, IReadOnlyList<int> SpecificPrefabHashes);

/// <summary>
/// Which slots of a prefab take any item: a general slot. A slot with a class (a battery, a gas canister, a tool...)
/// takes only that class, and a slot that names specific prefabs takes only those, so neither is general.
/// </summary>
internal static class PrefabSlotRule
{
    internal static bool IsGeneral(PrefabSlotFacts slot) =>
        slot.ClassIsNone && slot.SpecificPrefabHashes.Count == 0;

    internal static int GeneralCount(IEnumerable<PrefabSlotFacts> slots)
    {
        int count = 0;
        foreach (PrefabSlotFacts slot in slots)
        {
            if (IsGeneral(slot))
            {
                count++;
            }
        }

        return count;
    }
}
