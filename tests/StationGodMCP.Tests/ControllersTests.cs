#nullable enable

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Views;
using StationGodMCP.Protocol;
using StationGodMCP.Pure.Catalogue;
using StationGodMCP.Pure.Controllers;
using StationGodMCP.Pure.Lint;
using StationGodMCP.Pure.Scheduling;
using StationGodMCP.Pure.Verbs;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Controllers (inspect_controllers, driven_by): what can write a device, from the chips, logic writers, batch writers
/// and mirrors on its data networks. The parse and the match are pure and tested here against synthetic networks; the
/// game side (Controllers, ControllerScan: reading DataDeviceList, holders, pins and writer targets) is compile-only.
/// </summary>
public sealed class ControllersTests
{
    private const long Housing = 100;
    private const long Door = 200;
    private const long PumpA = 300;
    private const long PumpB = 301;
    private const long Writer = 400;
    private const long Far = 500;

    private static readonly int PumpHash = LintChipPrograms.HashOf("StructureVolumePump");

    // ---- chips ----

    [Fact]
    public void AChipWritingDbControlsItsOwnHousingAtThatLine()
    {
        Net network = Network(Chip("# set point\nmove r0 5\ns db Setting r0"));

        Controller found = Assert.Single(For(network, Housing, 1));

        Assert.Equal(ControllerKinds.Chip, found.Kind);
        Assert.Equal(Housing, found.ReferenceId);
        Assert.Equal("Setting", found.LogicType);
        Assert.Equal("db", found.Via);
        Assert.Equal(2, found.Line);
    }

    [Fact]
    public void AChipWritingD0ThroughAnAliasControlsThePinnedDevice()
    {
        Net network = Network(Chip("alias Airlock d0\nalias Outer Airlock\n\ns Outer Open 0", Door));

        Controller found = Assert.Single(For(network, Door, 2));

        Assert.Equal("d0", found.Via);
        Assert.Equal("Open", found.LogicType);
        Assert.Equal(3, found.Line);
        Assert.Empty(For(network, PumpA, PumpHash));
    }

    [Fact]
    public void AnAliasSetTwiceReachesBothDevices()
    {
        Net network = Network(Chip("alias Target d0\ns Target On 1\nalias Target d1\n", Door, PumpA));

        Assert.Equal("d0", Assert.Single(For(network, Door, 2)).Via);
        Assert.Equal("d1", Assert.Single(For(network, PumpA, PumpHash)).Via);
    }

    [Fact]
    public void ABatchWriteByPrefabHashReachesEveryDeviceOfThatPrefabOnItsNetwork()
    {
        Net network = Network(Chip("define Pumps HASH(\"StructureVolumePump\")\nsb Pumps On 1"));

        Controller a = Assert.Single(For(network, PumpA, PumpHash));
        Controller b = Assert.Single(For(network, PumpB, PumpHash, "Pump B"));

        Assert.Equal("sb", a.Via);
        Assert.Equal("On", a.LogicType);
        Assert.Equal(1, a.Line);
        Assert.Equal(Housing, b.ReferenceId);
        Assert.Empty(For(network, Door, 2));
    }

    [Fact]
    public void ABatchWriteByNameHashReachesOnlyTheDeviceWithThatName()
    {
        Net network = Network(Chip($"sbn {PumpHash} HASH(\"Pump A\") On 0"));

        Assert.Equal("sbn", Assert.Single(For(network, PumpA, PumpHash, "Pump A")).Via);
        Assert.Empty(For(network, PumpB, PumpHash, "Pump B"));
    }

    [Fact]
    public void ADeviceOnAnotherNetworkIsNotReachedByItsBatches()
    {
        Net chips = Network(Chip("sb HASH(\"StructureVolumePump\") On 1"));
        Net other = new Net(new HashSet<long> { Far }, new List<IControllerSource>());

        Assert.Empty(For(chips, Far, PumpHash));
        Assert.Empty(For(other, Far, PumpHash));
    }

    [Fact]
    public void ASlotWriteIsListedAsSlotAndOneControllerWritingTwiceIsListedOnce()
    {
        Net network = Network(Chip("s d0 On 1\nyield\ns d0 On 0\nss d0 0 On 1", Door));

        List<Controller> found = For(network, Door, 2);

        Assert.Equal(2, found.Count);
        Assert.Equal(0, found[0].Line);
        Assert.False(found[0].Slot);
        Assert.True(found[1].Slot);
    }

    [Fact]
    public void AnIdWriteAndALuaPinWriteAreListed()
    {
        Net network = Network(
            Chip($"sd {Door} Lock 1"),
            new ProgramSource(101, "StructureConsole", new long[] { PumpA }, LintChipPrograms.Lua(
                "function tick(dt)\n  ic.write(0, LogicType.On, 1)\n  io.write(\"x\")\nend")));

        Assert.Equal("id", Assert.Single(For(network, Door, 2)).Via);
        Controller lua = Assert.Single(For(network, PumpA, PumpHash));
        Assert.Equal(101, lua.ReferenceId);
        Assert.Equal("d0", lua.Via);
        Assert.Equal("On", lua.LogicType);
        Assert.Equal(1, lua.Line);
    }

    // ---- logic units ----

    [Fact]
    public void ALogicWriterControlsItsOutputDevice()
    {
        Net network = Network(
            new WiredSource(ControllerKinds.LogicWriter, Writer, "StructureLogicWriter", Door, "Open"),
            new BatchWriterSource(401, "StructureLogicBatchWriter", PumpHash, "Setting"),
            new WiredSource(ControllerKinds.Mirror, 402, "StructureLogicMirror", PumpA, null));

        Controller writer = Assert.Single(For(network, Door, 2));
        List<Controller> pump = For(network, PumpA, PumpHash);

        Assert.Equal(ControllerKinds.LogicWriter, writer.Kind);
        Assert.Equal("output", writer.Via);
        Assert.Equal("Open", writer.LogicType);
        Assert.Null(writer.Line);
        Assert.Equal(2, pump.Count);
        Assert.Contains(pump, c => c.Kind == ControllerKinds.BatchWriter && c.Via == "prefab_hash");
        Assert.Contains(pump, c => c.Kind == ControllerKinds.Mirror && c.LogicType == null);
    }

    [Fact]
    public void ADeviceWithNoControllerHasNone()
    {
        Net network = Network(Chip("l r0 d0 Temperature\nsb HASH(\"StructureWallLight\") On 1", Door));

        Assert.Empty(For(network, Door, 2));
    }

    // ---- driven_by ----

    [Fact]
    public void DrivenByListsOnlyControllersOfTheSameLogicType()
    {
        Net network = Network(Chip("s d0 On 1\nss d0 0 Setting 1", Door),
            new WiredSource(ControllerKinds.Mirror, 402, "StructureLogicMirror", Door, null));
        List<Controller> controllers = For(network, Door, 2);

        List<Controller>? on = DrivenByMatch.Writing(controllers, "on");

        Assert.Equal("On", Assert.Single(on!).LogicType);
        Assert.Null(DrivenByMatch.Writing(controllers, "Setting"));
        Assert.Null(DrivenByMatch.Writing(controllers, "Open"));
    }

    [Fact]
    public void AWriteReplyCarriesDrivenByOnlyWhenAControllerWritesThatType()
    {
        LogicRead requested = new LogicRead(new ThingId(Door), new LogicTypeView(28, "On"), 1);
        Controller chip = new Controller(ControllerKinds.Chip, Housing, "StructureCircuitHousing", "On", "d0", 4, false);

        JObject plain = Acted(new LogicWrite(requested, 0, 1), false);
        JObject driven = Acted(new LogicWrite(requested, 0, 1, ControllerView.ListOrNull(new List<Controller> { chip })),
            false);
        JObject item = (JObject)Acted(new LogicWrite(requested, 0, 1,
            ControllerView.ListOrNull(new List<Controller> { chip })), true)["results"]![0]!;

        Assert.Null(plain["driven_by"]);
        JObject listed = (JObject)driven["driven_by"]![0]!;
        Assert.Equal(new JObject
        {
            ["kind"] = "chip", ["reference_id"] = "100", ["prefab_name"] = "StructureCircuitHousing",
            ["logic_type"] = "On", ["via"] = "d0", ["line"] = 4
        }, listed);
        Assert.Equal(listed, item["driven_by"]![0]);
        Assert.Null(ControllerView.ListOrNull(new List<Controller>()));
    }

    [Fact]
    public void AReportCarriesDrivenByFromTheSameCheckedWriteAsTheReply()
    {
        LogicRead requested = new LogicRead(new ThingId(Door), new LogicTypeView(28, "On"), 1);
        Controller chip = new Controller(ControllerKinds.Chip, Housing, "StructureCircuitHousing", "On", "d0", 4, false);
        LogicWriteChecked driven = WriteLint.Checked(requested, 0, OpeningVerdict.Closed, false, null,
            ControllerView.ListOrNull(new List<Controller> { chip }));
        LogicWriteChecked plain = WriteLint.Checked(requested, 0, OpeningVerdict.Closed, false, null, null);

        JObject report = Reported(driven);
        JObject written = Acted(new LogicWrite(driven, 1), false);

        Assert.True((bool)report["report_only"]!);
        JObject listed = (JObject)report["targets"]![0]!["driven_by"]![0]!;
        Assert.Equal("chip", (string?)listed["kind"]);
        Assert.Equal("100", (string?)listed["reference_id"]);
        Assert.Equal(listed, written["driven_by"]![0]);
        Assert.Null(Reported(plain)["targets"]![0]!["driven_by"]);
    }

    // ---- the parse cache ----

    [Fact]
    public void AProgramIsParsedOnceUntilItsSourceChanges()
    {
        ChipProgramCache cache = new ChipProgramCache();

        List<ChipOperation> first = cache.Of("ic10", "s d0 On 1");
        List<ChipOperation> again = cache.Of("ic10", "s d0 On 1");
        List<ChipOperation> changed = cache.Of("ic10", "s d0 On 0");
        List<ChipOperation> lua = cache.Of("lua", "s d0 On 0");

        Assert.Same(first, again);
        Assert.NotSame(first, changed);
        Assert.Empty(lua);
        Assert.Equal(3, cache.Parses);
        Assert.Equal(3, cache.Count);
    }

    [Fact]
    public void TheCacheIsBoundedAndForgetsOnClear()
    {
        ChipProgramCache cache = new ChipProgramCache(capacity: 2);
        cache.Of("ic10", "s d0 On 1");
        cache.Of("ic10", "s d0 On 2");

        cache.Of("ic10", "s d0 On 3");

        Assert.Equal(1, cache.Count);
        cache.Clear();
        Assert.Equal(0, cache.Count);
    }

    // ---- the parse ----

    [Fact]
    public void LuaWritesAndGlobalBatchFormsAreReadWithTheirLines()
    {
        List<ChipOperation> ops = LintChipPrograms.Lua(
            "logic_batch_write(hash(\"StructureVolumePump\"), LogicType.On, 1)\n" +
            "ic.logic.write_id(4612280, LogicType.Lock, 1)\n" +
            "ic.write(device, LogicType.On, 1)\n" +
            "ic.disk.write(0, 1)");

        Assert.Equal(2, ops.Count);
        Assert.Equal("batch_write", ops[0].Op);
        Assert.Equal(PumpHash, ops[0].PrefabHash);
        Assert.Equal(0, ops[0].Line);
        Assert.Equal(4612280L, ops[1].ReferenceId);
        Assert.Equal("Lock", ops[1].Logic);
        Assert.Equal(1, ops[1].Line);
    }

    [Fact]
    public void Ic10UnresolvedDevicesAreLeftOut()
    {
        List<ChipOperation> ops = LintChipPrograms.Ic10("s dr0 On 1\nsd r1 On 1\nsb r2 On 1\ns d0:0 Channel0 1");

        ChipOperation batch = Assert.Single(ops);
        Assert.Null(batch.PrefabHash);
    }

    // ---- inspect controllers: one entry per controller ----

    [Fact]
    public void AControllersWritesAreGroupedPerControllerAndHowWithItsFirstLine()
    {
        List<Controller> found = new List<Controller>
        {
            new Controller(ControllerKinds.Chip, Housing, "StructureCircuitHousing", "Open", "d0", 7, false),
            new Controller(ControllerKinds.Chip, Housing, "StructureCircuitHousing", "Lock", "d0", 3, false),
            new Controller(ControllerKinds.Chip, Housing, "StructureCircuitHousing", "open", "d0", 9, false),
            new Controller(ControllerKinds.Chip, Housing, "StructureCircuitHousing", "Open", "sb", 12, false),
            new Controller(ControllerKinds.Mirror, 900, "StructureLogicMirror", null, "mirror", null, false)
        };

        List<ControllerGroup> groups = ControllerGroups.Of(found);

        Assert.Equal(new[] { "d0", "sb", "mirror" }, groups.Select(group => group.How));
        Assert.Equal(new[] { "Open", "Lock" }, groups[0].LogicTypes);
        Assert.Equal(3, groups[0].Line);
        Assert.Empty(groups[2].LogicTypes);
        Assert.Null(groups[2].Line);
    }

    [Fact]
    public void AnEntryWritesTheDeclaredKeys()
    {
        ControllerGroup group = new ControllerGroup(ControllerKinds.Chip, Housing, "StructureCircuitHousing", "d0",
            new List<string> { "Open" }, 3);
        DeviceControllersView view = new DeviceControllersView(new ThingId(Door), "Door",
            new List<ControllerEntryView> { new ControllerEntryView(group, "IC Housing", true) });

        JObject wire = JObject.Parse(WireCheck.New(view));

        Assert.Equal(new[] { "reference_id", "display_name", "controllers", "count" },
            wire.Properties().Select(key => key.Name));
        Assert.Equal(new[] { "reference_id", "display_name", "kind", "how", "logic_types", "line", "on" },
            ((JObject)wire["controllers"]![0]!).Properties().Select(key => key.Name));
        Assert.Equal(1, (int)wire["count"]!);
        Assert.True((bool)wire["controllers"]![0]!["on"]!);
    }

    [Fact]
    public void ANetworkIsIndexedAfterEnoughLookupsAndAnswersAlike()
    {
        Net net = Network(Chip("sb HASH(\"StructureVolumePump\") On 1\ns d0 Open 1", Door));
        NetworkControllers controllers = new NetworkControllers(net.Members, net.Sources);
        List<Controller> first = new List<Controller>();

        controllers.AddFor(new ControlledDevice(PumpA, PumpHash, 0), first);
        for (int lookup = 1; lookup < NetworkControllers.IndexAfter; lookup++)
        {
            controllers.AddFor(new ControlledDevice(Door, 2, 0), new List<Controller>());
        }

        List<Controller> indexed = new List<Controller>();
        controllers.AddFor(new ControlledDevice(PumpA, PumpHash, 0), indexed);

        Assert.True(controllers.Indexed);
        Assert.Equal("sb", Assert.Single(first).Via);
        Assert.True(Assert.Single(indexed).SameAs(first[0]));
    }

    // ---- helpers ----

    // A network's members and sources, asked both ways: matched directly and through the index, which must agree.
    private sealed class Net
    {
        internal Net(HashSet<long> members, List<IControllerSource> sources)
        {
            Members = members;
            Sources = sources;
        }

        internal HashSet<long> Members { get; }

        internal List<IControllerSource> Sources { get; }

        internal List<Controller> For(ControlledDevice device)
        {
            List<Controller> direct = new List<Controller>();
            new NetworkControllers(Members, Sources).AddFor(device, direct);
            List<Controller> indexed = new List<Controller>();
            if (Members.Contains(device.ReferenceId))
            {
                ControllerIndex.Of(Sources).AddFor(device, indexed);
            }

            Assert.Equal(direct.Count, indexed.Count);
            foreach (Controller controller in direct)
            {
                Assert.Contains(indexed, other => other.SameAs(controller) && other.Line == controller.Line);
            }

            return direct;
        }
    }

    private static JObject Acted(LogicWrite write, bool rows) =>
        ((VerbOutcome.Answered)LogicWriteReplies.Acted("world",
            new LogicWriteResult[] { new LogicWriteResult.Written(0, "Door", write) }, rows)).Reply;

    private static JObject Reported(LogicWriteChecked write) =>
        LogicWriteReplies.Reported("world", new LogicWriteResult[] { new LogicWriteResult.Planned(0, "Door", write) });

    private static ProgramSource Chip(string source, params long[] pins) =>
        new ProgramSource(Housing, "StructureCircuitHousing", pins, LintChipPrograms.Ic10(source));

    // A data network holding the housing, the door, both pumps and the writer, with these controllers on it.
    private static Net Network(params IControllerSource[] sources) =>
        new Net(new HashSet<long> { Housing, 101, Door, PumpA, PumpB, Writer, 401, 402 },
            new List<IControllerSource>(sources));

    private static List<Controller> For(Net network, long id, int prefabHash, string name = "Pump A") =>
        network.For(new ControlledDevice(id, prefabHash, LintChipPrograms.HashOf(name)));
}
