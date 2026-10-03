#nullable enable

namespace StationGodMCP.Pure.RemoteView;

/// <summary>
/// What a client's StationGod tells the server about its player's camera: the cursor's ray (eye, look direction and
/// the camera's up), the camera mode, where the player stands, the thing and interactable the game's cursor is on, and
/// the first surface the ray hits within ViewWire.ReachM. A session (new each time the game starts) and a sequence
/// number order the reports of one client. Sealed classes, not records: netstandard2.1 has no IsExternalInit.
/// </summary>
internal sealed class ViewReport
{
    internal ViewReport(int session, int sequence, Vec3 eye, Vec3 forward, Vec3 up, bool thirdPerson, bool seated,
        Vec3 playerPosition, ViewTarget? target, ViewHit? hit)
    {
        Session = session;
        Sequence = sequence;
        Eye = eye;
        Forward = forward;
        Up = up;
        ThirdPerson = thirdPerson;
        Seated = seated;
        PlayerPosition = playerPosition;
        Target = target;
        Hit = hit;
    }

    internal int Session { get; }

    internal int Sequence { get; }

    internal Vec3 Eye { get; }

    internal Vec3 Forward { get; }

    internal Vec3 Up { get; }

    internal bool ThirdPerson { get; }

    internal bool Seated { get; }

    /// <summary>The player's own position on the client when the report was taken.</summary>
    internal Vec3 PlayerPosition { get; }

    /// <summary>The thing the game's cursor is on (CursorManager.FoundThing, within 3 m); null on none.</summary>
    internal ViewTarget? Target { get; }

    /// <summary>The first surface along the ray within ViewWire.ReachM; null when it hits nothing.</summary>
    internal ViewHit? Hit { get; }

    /// <summary>The same view under another sequence number (the reporter numbers what it sends).</summary>
    internal ViewReport Numbered(int sequence) =>
        new ViewReport(Session, sequence, Eye, Forward, Up, ThirdPerson, Seated, PlayerPosition, Target, Hit);
}

/// <summary>The cursor's thing by reference id, and the interactable on it by its index (Interactable.InteractableId).</summary>
internal sealed class ViewTarget
{
    internal ViewTarget(long thingId, int? interactableId)
    {
        ThingId = thingId;
        InteractableId = interactableId;
    }

    internal long ThingId { get; }

    /// <summary>The index in Thing.Interactables, as the game's own InteractionMessage names one; null on none.</summary>
    internal int? InteractableId { get; }
}

/// <summary>Where the ray hit: the point, the surface normal, the distance from the eye and the thing hit (0 terrain).</summary>
internal sealed class ViewHit
{
    internal ViewHit(Vec3 point, Vec3 normal, double distanceM, long thingId)
    {
        Point = point;
        Normal = normal;
        DistanceM = distanceM;
        ThingId = thingId;
    }

    internal Vec3 Point { get; }

    internal Vec3 Normal { get; }

    internal double DistanceM { get; }

    /// <summary>The reference id of the thing hit; 0 for terrain or anything that is no thing.</summary>
    internal long ThingId { get; }
}
