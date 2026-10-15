#nullable enable

using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// The world index (Pure/WorldIndex), by prefab and by class, against a model of the game's two master lists, and the prefabs argument
/// (PrefabMatch.OfNames, PrefabMatch.ForIndex). The index must answer what a walk of a list answers, whatever order
/// things arrive, leave, change name or get cleared in.
/// </summary>
public sealed class WorldIndexTests
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

        // Class bits; the index reads them once, when it first files the thing.
        internal int Classes { get; set; }

        internal int Id { get; }

        // Unity objects override equality; the index must not depend on it.
        public override bool Equals(object? obj) => obj is Thing other && other.Name == Name;

        public override int GetHashCode() => Name?.GetHashCode() ?? 0;
    }

    private sealed class World
    {
        internal readonly List<Thing> All = new List<Thing>();
        internal readonly List<Thing> Dynamic = new List<Thing>();
        internal readonly WorldIndex<Thing> Index =
            new WorldIndex<Thing>(static thing => thing.Name, static thing => thing.Classes);
        internal int Seeds;

        // As the game: a thing is added, then told; removed, then told.
        internal Thing Add(string name, bool dynamic = false, bool tell = true, int classes = 0)
        {
            Thing thing = new Thing(name, All.Count + Dynamic.Count + 1) { Classes = classes };
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

        // A frame's Warm, as ThingIndex.Tick calls it.
        internal void Warm(int budget) => Index.Warm(Seed, thing => InAll(thing) || InDynamic(thing), budget);

        internal List<Thing> Find(PrefabMatch match, bool dynamic = false) =>
            Find(match, dynamic, out _);

        internal List<Thing> Find(PrefabMatch match, bool dynamic, out int examined) =>
            Index.Find(match, Seed, thing => InAll(thing) || InDynamic(thing), dynamic ? InDynamic : InAll,
                out examined);

        // What a walk of the list answers.
        internal List<Thing> Walk(PrefabMatch match, bool dynamic = false) =>
            (dynamic ? Dynamic : All).FindAll(thing => match.Keeps(thing.Name));

        // A class's members: things of the class its list (the dynamic one, as items are AllDynamicThings') holds.
        internal List<Thing> OfClass(int classNumber, bool dynamic = false) =>
            Index.OfClass(classNumber, Seed, thing => InAll(thing) || InDynamic(thing), dynamic ? InDynamic : InAll);

        internal List<Thing> WalkClass(int classNumber, bool dynamic) =>
            (dynamic ? Dynamic : All).FindAll(thing => (thing.Classes & (1 << classNumber)) != 0);
    }

    private static List<int> Ids(List<Thing> things)
    {
        List<int> ids = things.ConvertAll(static thing => thing.Id);
        ids.Sort();
        return ids;
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
    public void AWarmFilesOnlyTheArrivalsToldBeforeItsPreviousCall()
    {
        // A thing's name is set in the frame it arrives in; Warm runs once a frame, so it files a frame late.
        World world = new World();
        world.Warm(10);
        Thing thing = world.Add("Template", dynamic: true);
        world.Warm(10);
        Assert.Equal(0, world.Index.Count);

        thing.Name = "ItemKitWall";
        world.Warm(10);

        Assert.Equal(1, world.Index.Count);
        Assert.Single(world.Find(new PrefabMatch("ItemKitWall", null), dynamic: true));
        Assert.Empty(world.Find(new PrefabMatch("Template", null), dynamic: true));
    }

    [Fact]
    public void AWarmFilesTheSeedASliceAtATimeAndAQueryTheRest()
    {
        World world = new World();
        for (int index = 0; index < 5; index++)
        {
            world.Add("ItemIronOre", tell: false);
        }

        world.Warm(2);
        Assert.Equal(2, world.Index.Count);
        world.Warm(2);
        Assert.Equal(4, world.Index.Count);

        Assert.Equal(5, world.Find(new PrefabMatch("ItemIronOre", null)).Count);
        Assert.Equal(1, world.Seeds);
    }

    [Fact]
    public void AWarmDropsTheLeavesOnceEveryArrivalIsFiled()
    {
        World world = new World();
        world.Warm(10);
        Thing first = world.Add("ItemIronOre");
        world.Add("ItemIronOre");
        world.Warm(10);
        world.Warm(10);
        Assert.Equal(2, world.Index.Count);

        world.Remove(first);
        world.Warm(10);

        Assert.Equal(1, world.Index.Count);
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
        for (int index = 0; index <= WorldIndex<Thing>.MaximumBacklog; index++)
        {
            world.Index.Left(churn);
        }

        world.Add("ItemIronOre");

        Assert.Equal(2, world.Find(ore).Count);
        Assert.Equal(2, world.Seeds);
    }

    // ---- classes ----

    private const int FrameClass = 0;
    private const int ItemClass = 3;

    [Fact]
    public void AClassQueryAnswersTheThingsOfThatClassItsListHolds()
    {
        World world = new World();
        Thing frame = world.Add("StructureFrame", classes: 1 << FrameClass);
        Thing corner = world.Add("StructureFrameCorner", classes: 1 << FrameClass);
        Thing ore = world.Add("ItemIronOre", dynamic: true, classes: 1 << ItemClass);
        world.Add("StructureWall");

        Assert.Equal(new[] { frame.Id, corner.Id }, Ids(world.OfClass(FrameClass)));
        Assert.Equal(new[] { ore.Id }, Ids(world.OfClass(ItemClass, dynamic: true)));
        Assert.Equal(2, world.Index.ClassCount(FrameClass));
    }

    [Fact]
    public void AClassIsReadOnceWhenTheThingIsFirstFiled()
    {
        World world = new World();
        Thing frame = world.Add("StructureFrame", classes: 1 << FrameClass);
        world.OfClass(FrameClass);

        frame.Classes = 1 << ItemClass;
        frame.Name = "StructureFrameIron";
        world.Index.Arrived(frame);

        Assert.Equal(new[] { frame.Id }, Ids(world.OfClass(FrameClass)));
        Assert.Empty(world.OfClass(ItemClass));
    }

    [Fact]
    public void AThingInNoListLeavesItsClassBucket()
    {
        World world = new World();
        Thing frame = world.Add("StructureFrame", classes: 1 << FrameClass);
        world.Add("StructureFrame", classes: 1 << FrameClass);
        world.OfClass(FrameClass);

        world.Remove(frame, tell: false);

        Assert.Single(world.OfClass(FrameClass));
        Assert.Equal(1, world.Index.ClassCount(FrameClass));
    }

    [Fact]
    public void AThingItsClassListDoesNotHoldIsNotAnsweredButStaysFiled()
    {
        World world = new World();
        Thing item = world.Add("ItemIronOre", classes: 1 << ItemClass);

        Assert.Empty(world.OfClass(ItemClass, dynamic: true));
        world.Dynamic.Add(item);
        Assert.Equal(new[] { item.Id }, Ids(world.OfClass(ItemClass, dynamic: true)));
    }

    [Fact]
    public void AClassOutsideTheBitsIsRefused()
    {
        World world = new World();

        Assert.Throws<ArgumentOutOfRangeException>(() => world.OfClass(WorldIndex<Thing>.MaximumClasses));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.OfClass(-1));
    }

    [Fact]
    public void RandomClassQueriesAlwaysMatchAWalk()
    {
        string[] names = { "ItemIronOre", "StructureFrame", "StructureWall" };
        Random random = new Random(20261014);
        World world = new World();
        for (int step = 0; step < 4000; step++)
        {
            int roll = random.Next(100);
            if (roll < 45 || world.All.Count == 0)
            {
                world.Add(names[random.Next(names.Length)], dynamic: random.Next(2) == 0, classes: random.Next(16));
            }
            else if (roll < 80)
            {
                world.Remove(world.All[random.Next(world.All.Count)], tell: random.Next(10) != 0);
            }
            else if (roll < 85)
            {
                // Renamed and told again (Awake, then Register): its class stays.
                Thing renamed = world.All[random.Next(world.All.Count)];
                renamed.Name = names[random.Next(names.Length)];
                world.Index.Arrived(renamed);
            }
            else if (roll < 86)
            {
                world.Clear();
            }
            else if (roll < 90)
            {
                world.Warm(random.Next(1, 8));
            }
            else
            {
                int classNumber = random.Next(4);
                bool dynamic = random.Next(2) == 0;
                Assert.Equal(Ids(world.WalkClass(classNumber, dynamic)), Ids(world.OfClass(classNumber, dynamic)));
            }
        }
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
            else if (roll < 90)
            {
                world.Warm(random.Next(1, 8));
            }
            else
            {
                PrefabMatch match = matches[random.Next(matches.Length)];
                bool dynamic = random.Next(2) == 0;
                SameThings(world.Walk(match, dynamic), world.Find(match, dynamic), match);
            }
        }
    }

    // ---- the verbs' prefab filter, as the index reads it ----

    [Fact]
    public void ListedPrefabsKeepAnyOfTheirNamesInAnyCase()
    {
        PrefabMatch match = PrefabMatch.ForIndex(Prefab("""{"prefab": [" ItemIronIngot ", "itemsteelingot"]}"""));

        Assert.True(match.Keeps("ItemIronIngot"));
        Assert.True(match.Keeps("ItemSteelIngot"));
        Assert.False(match.Keeps("ItemCopperIngot"));
        Assert.False(match.Keeps(null));
        Assert.Equal(2, match.Exact!.Count);
    }

    [Fact]
    public void AListedNameAndAPartAreWalkedNotIndexed()
    {
        PrefabFilter mixed = Prefab("""{"prefab": ["ItemIronIngot", "*Ore*"]}""")!;

        Assert.False(PrefabMatch.ForIndex(mixed).IsActive);
        Assert.True(mixed.Keeps("ItemIronOre", 0));
        Assert.True(mixed.Keeps("ItemIronIngot", 0));
    }

    [Theory]
    [InlineData("""{"prefab": ["ItemIronIngot", ""]}""", "prefab[1]")]
    [InlineData("""{"prefab": ["ItemIronIngot", true]}""", "prefab[1]")]
    [InlineData("""{"prefab": "*"}""", "prefab")]
    public void ABadPrefabIsAProblemAtItsPlace(string options, string path)
    {
        OptionReader reader = new OptionReader(FindItemOptions.Spec, JObject.Parse(options));
        PrefabFilter.Bind(reader);

        Assert.Equal(path, Assert.Single(reader.Problems).Path);
    }

    [Fact]
    public void NoPrefabFilterIsNotIndexed()
    {
        Assert.False(PrefabMatch.ForIndex(Prefab("""{"name": "Iron"}""")).IsActive);
        Assert.True(PrefabMatch.ForIndex(Prefab("""{"prefab": "*Iron*"}""")).IsActive);
    }

    private static PrefabFilter? Prefab(string options) =>
        PrefabFilter.Bind(new OptionReader(FindItemOptions.Spec, JObject.Parse(options)));
}
