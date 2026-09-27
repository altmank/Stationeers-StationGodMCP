#nullable enable

using Assets.Scripts.Objects;

namespace StationGodMCP.Api.Shared.Game;

/// <summary>
/// What a thing is, by the game's own class tree: entity (players, animals), item, dynamic (any other DynamicThing: a
/// portable tank or canister, crate, portable generator, rover, lander), structure (anything built: tanks, lockers,
/// pipes, frames; devices are structures) or other.
/// </summary>
internal static class ThingKinds
{
    internal const string EntityKind = "entity";
    internal const string ItemKind = "item";
    internal const string DynamicKind = "dynamic";
    internal const string StructureKind = "structure";
    internal const string OtherKind = "other";

    internal static string Of(Thing thing) =>
        thing switch
        {
            Entity => EntityKind,
            Item => ItemKind,
            DynamicThing => DynamicKind,
            Structure => StructureKind,
            _ => OtherKind
        };

    internal static bool IsKind(string word) =>
        word == EntityKind || word == ItemKind || word == DynamicKind || word == StructureKind || word == OtherKind;
}
