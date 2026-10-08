#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The prefab index (Pure/PrefabIndex) against a model of the game's two master lists, and the prefabs argument
/// (PrefabMatch.OfNames, PrefabMatches.Parse). The index must answer what a walk of a list answers, whatever order
/// things arrive, leave, change name or get cleared in.
/// </summary>
public sealed class PrefabIndexTests
{
    // ---- a model of the game's lists ----

    private sealed class Thing
    {
        internal Thing(string name, int id)
        {
            Name = name;
            Id = id;
        }

        internal string? Name { get; set; }

        internal int Id { get; }

        // Unity objects override equality; the index must not depend on it.
        public override bool Equals(object? obj) => obj is Thing other && other.Name == Name;

        public override int GetHashCode() => Name?.GetHashCode() ?? 0;
    }

    private sealed class World
    {
        internal readonly List<Thing> All = new List<Thing>();
        internal readonly List<Thing> Dynamic = new List<Thing>();
        internal readonly PrefabIndex<Thing> Index = new PrefabIndex<Thing>(static thing => thing.Name);
        internal int Seeds;

        // As the game: a thing is added, then told; removed, then told.
        internal Thing Add(string name, bool dynamic = false, bool tell = true)
        {
            Thing thing = new Thing(name, All.Count + Dynamic.Count + 1);
            All.Add(thing);
            if (dynamic)
            {
                Dynamic.Add(thing);
            }

            if (tell)
            {
                Index.Arrived(thing);
            }

            return thing;
        }

        internal void Remove(Thing thing, bool tell = true)
        {
            All.RemoveAll(other => ReferenceEquals(other, thing));
            Dynamic.RemoveAll(other => ReferenceEquals(other, thing));
            if (tell)
            {
                Index.Left(thing);
            }
        }

        internal void Clear()
        {
            All.Clear();
            Dynamic.Clear();
            Index.Reset();
        }

        private bool InAll(Thing thing) => All.Exists(other => ReferenceEquals(other, thing));

        private bool InDynamic(Thing thing) => Dynamic.Exists(other => ReferenceEquals(other, thing));

        private IEnumerable<Thing> Seed()
        {
            Seeds++;
            List<Thing> all = new List<Thing>(All);
            all.AddRange(Dynamic);
            return all;
        }

        internal List<Thing> Find(PrefabMatch match, bool dynamic = false) =>
            Find(match, dynamic, out _);

        internal List<Thing> Find(PrefabMatch match, bool dynamic, out int examined) =>
            Index.Find(match, Seed, thing => InAll(thing) || InDynamic(thing), dynamic ? InDynamic : InAll,
                out examined);

        // What a walk of the list answers.
        internal List<Thing> Walk(PrefabMatch match, bool dynamic = false) =>
            (dynamic ? Dynamic : All).FindAll(thing => match.Keeps(thing.Name));
    }

    private static void SameThings(List<Thing> expected, List<Thing> indexed, PrefabMatch match)
    {
        List<int> want = expected.ConvertAll(static thing => thing.Id);
        List<int> got = indexed.FindAll(thing => match.Keeps(thing.Name)).ConvertAll(static thing => thing.Id);
        want.Sort();
        got.Sort();
        Assert.Equal(want, got);
    }

    // ---- the index ----

    [Fact]
    public void AnExactNameFindsThatPrefabOnlyInAnyCase()
    {
        World world = new World();
        world.Add("StructureFrame");
        world.Add("StructureFrameCorner");
        world.Add("structureframe");
        PrefabMatch frame = new PrefabMatch("StructureFrame", null);

        List<Thing> found = world.Find(frame);

        Assert.Equal(2, found.Count);
        SameThings(world.Walk(frame), found, frame);
    }

    [Fact]
    public void SeveralNamesFindEachOnce()
    {
        World world = new World();
        world.Add("ItemIronIngot");
        world.Add("ItemSteelIngot");
        world.Add("ItemCopperIngot");
        PrefabMatch two = PrefabMatch.OfNames(new[] { "ItemIronIngot", "itemironingot", "ItemSteelIngot" }, null);

        List<Thing> found = world.Find(two);

        Assert.Equal(2, found.Count);
        SameThings(world.Walk(two), found, two);
    }

    [Fact]
    public void PrefabContainsMatchesTheNamesFirst()
    {
        World world = new World();
        world.Add("ItemCableCoil");
        world.Add("ItemCableCoilHeavy");
        world.Add("StructureCable");
        PrefabMatch coil = new PrefabMatch(null, "cablecoil");

        List<Thing> found = world.Find(coil, false, out int examined);

        Assert.Equal(2, found.Count);
        Assert.Equal(2, examined);
        SameThings(world.Walk(coil), found, coil);
    }

    [Fact]
    public void AThingIsFiledUnderTheNameItHasAtTheQuery()
    {
        // The game names a new thing after its first registration (DynamicThing.Awake runs inside Instantiate).
        World world = new World();
        Thing thing = world.Add("Template", dynamic: true);
        thing.Name = "ItemKitWall";

        Assert.Single(world.Find(new PrefabMatch("ItemKitWall", null), dynamic: true));
        Assert.Empty(world.Find(new PrefabMatch("Template", null), dynamic: true));
    }

    [Fact]
    public void ALeftThingIsDropped()
    {
        World world = new World();
        Thing first = world.Add("ItemIronOre");
        world.Add("ItemIronOre");
        PrefabMatch ore = new PrefabMatch("ItemIronOre", null);
        Assert.Equal(2, world.Find(ore).Count);

        world.Remove(first);

        Assert.Single(world.Find(ore));
        Assert.Equal(1, world.Index.Count);
    }

    [Fact]
    public void AThingTheListsLetGoUntoldIsNeverAnswered()
    {
        World world = new World();
        Thing first = world.Add("ItemIronOre");
        world.Add("ItemIronOre");
        PrefabMatch ore = new PrefabMatch("ItemIronOre", null);
        world.Find(ore);

        world.Remove(first, tell: false);

        Assert.Single(world.Find(ore));
        Assert.Equal(1, world.Index.Count);
    }

    [Fact]
    public void ThingsInTheListsBeforeTheIndexHeardOfThemAreSeeded()
    {
        World world = new World();
        world.Add("ItemIronOre", tell: false);
        world.Add("ItemIronOre", dynamic: true, tell: false);
        PrefabMatch ore = new PrefabMatch("ItemIronOre", null);

        Assert.Equal(2, world.Find(ore).Count);
        Assert.Equal(2, world.Find(ore).Count);
        Assert.Equal(1, world.Seeds);
    }

    [Fact]
    public void AResetSeedsAgainFromTheLists()
    {
        World world = new World();
        world.Add("ItemIronOre");
        PrefabMatch ore = new PrefabMatch("ItemIronOre", null);
        world.Find(ore);

        world.Clear();
        world.Add("ItemIronOre");
        world.Add("ItemIronOre", tell: false);

        Assert.Equal(2, world.Find(ore).Count);
        Assert.Equal(2, world.Seeds);
    }

    [Fact]
    public void EachListAnswersItsOwnMembers()
    {
        World world = new World();
        world.Add("ItemIronOre");
        world.Add("ItemIronOre", dynamic: true);
        Thing onlyDynamic = new Thing("ItemIronOre", 99);
        world.Dynamic.Add(onlyDynamic);
        world.Index.Arrived(onlyDynamic);
        PrefabMatch ore = new PrefabMatch("ItemIronOre", null);

        SameThings(world.Walk(ore), world.Find(ore), ore);
        SameThings(world.Walk(ore, dynamic: true), world.Find(ore, dynamic: true), ore);
        Assert.Equal(3, world.Index.Count);
    }

    [Fact]
    public void ARepeatedArrivalIsFiledOnce()
    {
        World world = new World();
        Thing thing = world.Add("ItemIronOre", dynamic: true);
        world.Index.Arrived(thing);
        world.Index.Arrived(thing);

        Assert.Single(world.Find(new PrefabMatch("ItemIronOre", null)));
    }

    [Fact]
    public void ABackloggedIndexSeedsAgainFromTheLists()
    {
        World world = new World();
        PrefabMatch ore = new PrefabMatch("ItemIronOre", null);
        world.Add("ItemIronOre");
        world.Find(ore);
        Thing churn = new Thing("ItemIronOre", -1);
        for (int index = 0; index <= PrefabIndex<Thing>.MaximumBacklog; index++)
        {
            world.Index.Left(churn);
        }

        world.Add("ItemIronOre");

        Assert.Equal(2, world.Find(ore).Count);
        Assert.Equal(2, world.Seeds);
    }

    [Fact]
    public void AnIndexQueryNeedsAPrefabFilter()
    {
        World world = new World();

        Assert.Throws<ArgumentException>(() => world.Find(PrefabMatch.Any));
    }

    [Fact]
    public void RandomArrivalsLeavesRenamesAndClearsAlwaysMatchAWalk()
    {
        string[] names = { "ItemIronOre", "ItemIronIngot", "StructureFrame", "StructureFrameCorner", "ItemKitFrame" };
        PrefabMatch[] matches =
        {
            new PrefabMatch("ItemIronOre", null), new PrefabMatch(null, "Frame"), new PrefabMatch(null, "iron"),
            PrefabMatch.OfNames(new[] { "StructureFrame", "ItemIronIngot" }, null),
            PrefabMatch.OfNames(new[] { "StructureFrameCorner", "ItemKitFrame" }, "Kit"),
        };
        Random random = new Random(20261007);
        World world = new World();
        for (int step = 0; step < 4000; step++)
        {
            int roll = random.Next(100);
            if (roll < 45 || world.All.Count == 0)
            {
                world.Add(names[random.Next(names.Length)], dynamic: random.Next(2) == 0);
            }
            else if (roll < 80)
            {
                world.Remove(world.All[random.Next(world.All.Count)], tell: random.Next(10) != 0);
            }
            else if (roll < 85)
            {
                // A thing named after its first arrival, then told again (Awake, then Register in Thing.Create).
                Thing renamed = world.All[random.Next(world.All.Count)];
                renamed.Name = names[random.Next(names.Length)];
                world.Index.Arrived(renamed);
            }
            else if (roll < 86)
            {
                world.Clear();
            }
            else
            {
                PrefabMatch match = matches[random.Next(matches.Length)];
                bool dynamic = random.Next(2) == 0;
                SameThings(world.Walk(match, dynamic), world.Find(match, dynamic), match);
            }
        }
    }

    // ---- prefabs ----

    [Fact]
    public void PrefabsKeepsAnyOfItsNamesInAnyCase()
    {
        PrefabMatch match = PrefabMatches.Parse(new Args(JObject.Parse(
            """{"prefabs": [" ItemIronIngot ", "itemsteelingot"]}""")));

        Assert.True(match.Keeps("ItemIronIngot"));
        Assert.True(match.Keeps("ItemSteelIngot"));
        Assert.False(match.Keeps("ItemCopperIngot"));
        Assert.False(match.Keeps(null));
        Assert.Equal(2, match.Exact!.Count);
    }

    [Fact]
    public void PrefabsWithPrefabContainsMustBothMatch()
    {
        PrefabMatch match = PrefabMatches.Parse(new Args(JObject.Parse(
            """{"prefabs": ["ItemIronIngot", "ItemIronOre"], "prefab_contains": "Ore"}""")));

        Assert.True(match.Keeps("ItemIronOre"));
        Assert.False(match.Keeps("ItemIronIngot"));
    }

    [Theory]
    [InlineData("""{"prefabs": ["ItemIronIngot"], "prefab": "ItemIronIngot"}""", "not both")]
    [InlineData("""{"prefabs": []}""", "1 to 200")]
    [InlineData("""{"prefabs": "ItemIronIngot"}""", "1 to 200")]
    [InlineData("""{"prefabs": ["ItemIronIngot", ""]}""", "prefabs[1]")]
    [InlineData("""{"prefabs": ["ItemIronIngot", 5]}""", "prefabs[1]")]
    public void ABadPrefabsListIsRefused(string arguments, string reason)
    {
        ApiException refused = Assert.Throws<ApiException>(() => PrefabMatches.Parse(new Args(JObject.Parse(arguments))));

        Assert.Equal(ApiErrors.InvalidArgumentCode, refused.Code);
        Assert.Contains(reason, refused.Message);
    }

    [Fact]
    public void NoPrefabFilterIsNotIndexed()
    {
        Assert.False(PrefabMatches.Parse(new Args(JObject.Parse("""{"name_contains": "Iron"}"""))).IsActive);
        Assert.True(PrefabMatches.Parse(new Args(JObject.Parse("""{"prefab_contains": "Iron"}"""))).IsActive);
    }
}
