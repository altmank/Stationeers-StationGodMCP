#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure;

/// <summary>
/// Where move_player lands a player next to something: beside its body on the side facing where the player stands
/// now, clear of it by ClearanceM, at the height of the body's base. Level only: the side is chosen in x and z, so a
/// player above or below lands beside it on its floor, not on top.
/// </summary>
internal static class Landing
{
    /// <summary>The gap between the body's side and the landing point.</summary>
    internal const double ClearanceM = 0.5;

    /// <summary>A standing player's body: half its width and its height above the feet.</summary>
    internal const double PlayerHalfWidthM = 0.3;

    internal const double PlayerHeightM = 1.8;

    private const double Level = 1e-6;

    /// <summary>The point beside the body nearest the side facing from; +x when from is straight above or below.</summary>
    internal static Vec3 Beside(Box3 body, Vec3 from)
    {
        Vec3 centre = body.Centre;
        Vec3 level = new Vec3(from.X - centre.X, 0.0, from.Z - centre.Z);
        Vec3 direction = level.Length > Level ? level.Normalized : new Vec3(1.0, 0.0, 0.0);
        double reach = ToSide(body, direction) + ClearanceM;
        return new Vec3(centre.X + direction.X * reach, body.Min.Y, centre.Z + direction.Z * reach);
    }

    /// <summary>The body of a player standing with their feet at a point.</summary>
    internal static Box3 PlayerAt(Vec3 feet) =>
        new Box3(new Vec3(feet.X - PlayerHalfWidthM, feet.Y, feet.Z - PlayerHalfWidthM),
            new Vec3(feet.X + PlayerHalfWidthM, feet.Y + PlayerHeightM, feet.Z + PlayerHalfWidthM));

    // From the box's centre to its side along a level unit direction.
    private static double ToSide(Box3 body, Vec3 direction)
    {
        double alongX = Math.Abs(direction.X) > Level ? body.Size.X / 2.0 / Math.Abs(direction.X) : double.MaxValue;
        double alongZ = Math.Abs(direction.Z) > Level ? body.Size.Z / 2.0 / Math.Abs(direction.Z) : double.MaxValue;
        return Math.Min(alongX, alongZ);
    }
}

/// <summary>
/// safe_ground in the low-orbit area above the planet (Rocket.LowOrbitPlayableBounds). The game's safe point
/// (SpawnPoint.GetSafePoint) starts from the terrain surface at that x and z (EstimateTerrainSurface), which would drop
/// the player 2 km to the planet. Up there there is no gravity (WorldManager.HasGravityAtHeight: only below
/// PlanetaryAtmosphereSimulation.SpaceHeight), so nothing falls: the player stands on the highest structure surface in
/// that column (a launch mount's deck, a station's roof), clear of it by ClearanceM, or stays at the point when no
/// structure is there.
/// </summary>
internal static class OrbitLanding
{
    /// <summary>The gap between the surface and the player's feet.</summary>
    internal const double ClearanceM = 0.5;

    /// <summary>Where the player goes: on the highest structure the probe hit, else the point itself.</summary>
    internal static Vec3 Safe(Vec3 point, IEnumerable<OrbitSurface> hits)
    {
        double? top = null;
        foreach (OrbitSurface hit in hits)
        {
            if (hit.IsStructure && (top == null || hit.Y > top.Value))
            {
                top = hit.Y;
            }
        }

        return top.HasValue ? new Vec3(point.X, top.Value + ClearanceM, point.Z) : point;
    }
}

/// <summary>One thing a downward probe hit: its surface height, and whether it is a structure (not an item).</summary>
internal readonly struct OrbitSurface
{
    internal OrbitSurface(double y, bool isStructure)
    {
        Y = y;
        IsStructure = isStructure;
    }

    internal double Y { get; }

    internal bool IsStructure { get; }
}

/// <summary>A player as move_player finds them: the body, its reference id and the name players see.</summary>
internal sealed class NamedPlayer<T> where T : class
{
    internal NamedPlayer(T body, long referenceId, string name)
    {
        Body = body;
        ReferenceId = referenceId;
        Name = name;
    }

    internal T Body { get; }

    internal long ReferenceId { get; }

    internal string Name { get; }
}

/// <summary>
/// Which player a name or reference id means. A decimal string that is a player's reference id is that player; else
/// the name, ignoring case: one exact match wins, else one name containing it. None or several is no match, with the
/// names of every player to choose from.
/// </summary>
internal static class PlayerMatch
{
    internal static PlayerFound<T> Find<T>(IReadOnlyList<NamedPlayer<T>> players, string wanted) where T : class
    {
        string text = wanted.Trim();
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long id))
        {
            foreach (NamedPlayer<T> player in players)
            {
                if (player.ReferenceId == id)
                {
                    return new PlayerFound<T>.One(player);
                }
            }
        }

        List<NamedPlayer<T>> exact = Matching(players, name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase));
        if (exact.Count > 0)
        {
            return OneOf(exact, players, text);
        }

        List<NamedPlayer<T>> partial = Matching(players,
            name => text.Length > 0 && name.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
        return OneOf(partial, players, text);
    }

    private static List<NamedPlayer<T>> Matching<T>(IReadOnlyList<NamedPlayer<T>> players, Func<string, bool> test)
        where T : class
    {
        List<NamedPlayer<T>> found = new List<NamedPlayer<T>>();
        foreach (NamedPlayer<T> player in players)
        {
            if (test(player.Name))
            {
                found.Add(player);
            }
        }

        return found;
    }

    private static PlayerFound<T> OneOf<T>(List<NamedPlayer<T>> found, IReadOnlyList<NamedPlayer<T>> players,
        string text) where T : class =>
        found.Count switch
        {
            1 => new PlayerFound<T>.One(found[0]),
            0 => new PlayerFound<T>.None(text, Describe(players)),
            _ => new PlayerFound<T>.Several(text, Describe(found))
        };

    private static string Describe<T>(IReadOnlyList<NamedPlayer<T>> players) where T : class
    {
        if (players.Count == 0)
        {
            return "none";
        }

        List<string> named = new List<string>(players.Count);
        foreach (NamedPlayer<T> player in players)
        {
            named.Add($"{player.Name} ({player.ReferenceId.ToString(CultureInfo.InvariantCulture)})");
        }

        return string.Join(", ", named);
    }
}

/// <summary>The player a name or id means, or why none is.</summary>
internal abstract class PlayerFound<T> where T : class
{
    private PlayerFound()
    {
    }

    internal sealed class One : PlayerFound<T>
    {
        internal One(NamedPlayer<T> player)
        {
            Player = player;
        }

        internal NamedPlayer<T> Player { get; }
    }

    internal sealed class None : PlayerFound<T>
    {
        internal None(string wanted, string players)
        {
            Reason = $"No player is named or has the reference id '{wanted}'. Players: {players}.";
        }

        internal string Reason { get; }
    }

    internal sealed class Several : PlayerFound<T>
    {
        internal Several(string wanted, string players)
        {
            Reason = $"'{wanted}' matches several players: {players}. Give a whole name or a reference id.";
        }

        internal string Reason { get; }
    }
}
