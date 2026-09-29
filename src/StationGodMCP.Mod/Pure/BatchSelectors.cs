#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// How many of the devices a chip's batch instructions reach share each prefab and name hash pair. lb, lbn, sb and sbn
/// walk only the holder's batch list (ICircuitHolder.GetBatchOutput: an IC Housing's data network), so a pair is a
/// unique selector for that chip when exactly one device in that list has it, whatever the rest of the world holds.
/// </summary>
internal sealed class BatchSelectors
{
    private readonly Dictionary<long, int> _counts = new Dictionary<long, int>();
    private readonly HashSet<long> _reached = new HashSet<long>();

    /// <param name="batch">Each device in the batch list: reference id, prefab hash, and name hash (null: none).</param>
    internal BatchSelectors(IEnumerable<(long ReferenceId, int PrefabHash, int? NameHash)> batch)
    {
        foreach ((long referenceId, int prefabHash, int? nameHash) in batch)
        {
            if (!_reached.Add(referenceId) || !nameHash.HasValue)
            {
                continue;
            }

            long pair = Pair(prefabHash, nameHash.Value);
            _counts[pair] = _counts.TryGetValue(pair, out int count) ? count + 1 : 1;
        }
    }

    internal int DeviceCount => _reached.Count;

    internal bool Reaches(long referenceId) => _reached.Contains(referenceId);

    /// <summary>How many reached devices a batch instruction with this pair selects.</summary>
    internal int CountOf(int prefabHash, int? nameHash) =>
        nameHash.HasValue && _counts.TryGetValue(Pair(prefabHash, nameHash.Value), out int count) ? count : 0;

    // The two 32-bit hashes as one key.
    private static long Pair(int prefabHash, int nameHash) => ((long)prefabHash << 32) | (uint)nameHash;
}
