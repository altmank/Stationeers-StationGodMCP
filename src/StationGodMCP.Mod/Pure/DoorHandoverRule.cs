#nullable enable

namespace StationGodMCP.Pure;

/// <summary>
/// What a door swap (swap door, 2.8.0) does with each thing that points at the old door. A pointer held as an object
/// is repointed to the new door: a board's linked door, an airlock board's exterior or interior door, an IC housing's
/// pin, a logic writer's output or a mirror's device. A chip's program can name the door's reference id (sd id,
/// write_id), and the swap cannot rewrite a program, so that still points at the old id. A batch write (sb, sbn, a
/// batch writer) reaches the new door only when it has the old one's prefab: same prefab, nothing to do; another
/// prefab, it still points at the old prefab hash.
/// </summary>
internal static class DoorHandoverRule
{
    internal const string Repoint = "repoint";

    internal const string Repointed = "repointed";

    internal const string Keep = "keep";

    internal const string Restored = "restored";

    internal const string StillPoints = "still_points";

    internal const string Failed = "failed";

    /// <summary>
    /// What becomes of a controller (Controllers: via db, d0..d5, id, output, mirror, prefab_hash or a batch
    /// instruction) that writes the old door: null when an object pointer it holds is repointed (pins, output, mirror)
    /// or it reaches the new door anyway (a batch of the same prefab); else the note saying why it still points at the
    /// old door.
    /// </summary>
    internal static string? StillPointing(string via, long oldId, bool samePrefab)
    {
        if (IsPin(via) || via == "output" || via == "mirror" || via == "db")
        {
            return null;
        }

        if (via == "id")
        {
            return $"Its program names the old door's id {oldId}; change it to the new door's id.";
        }

        return samePrefab
            ? null
            : $"Its batch write ({via}) reaches the old door's prefab; the new door is another prefab, so change the " +
              "prefab hash it writes.";
    }

    /// <summary>A housing pin, d0 to d5.</summary>
    internal static bool IsPin(string via) =>
        via.Length == 2 && via[0] == 'd' && via[1] >= '0' && via[1] <= '5';
}
