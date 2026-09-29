#nullable enable

using System.Collections.Generic;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using Networks;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// Chute runs: Kit (Chute) straights, corners and junctions; items travel from the run's first cell to its last. A
/// device's chute ports bridge (its input and output on one network send items round in a loop). A junction's output
/// is turned to where the flow says items must go (ChuteFlowCheck), and every edit is checked for items meeting
/// head-on, entering a piece through its output, or falling out of an open end. A piece with an item riding in it is
/// never removed or replaced.
/// </summary>
internal sealed class ChuteRunKind : RunKind
{
    private const int MaximumListedItems = 32;

    private readonly ChuteFamily _family = new ChuteFamily();

    internal override UpgradeFamily Family => _family;

    internal override string PlaceTool => "place_chutes";

    internal override string RemoveTool => "remove_chutes";

    internal override string PlanTool => "plan_chute_route";

    internal override string[] GradeNames => new[] { "chute" };

    internal override string? DefaultGrade => "chute";

    // Chutes are built from Kit (Chute), not a coil.
    internal override string ShortageCode => "not_enough_kits";

    internal override Grade? GradeOf(string name) => name == "chute" ? ChuteFamily.Chute : null;

    internal override string NameOf(Grade grade) => "chute";

    internal override int EndType(Grade grade) => (int)NetworkType.Chute;

    internal override int AnyEndType => (int)NetworkType.Chute;

    internal override PipeContent? ContentOf(Grade grade) => null;

    internal override SmallGrid? SlotOf(SmallCell cell) => cell.Chute;

    internal override bool Bridges(Connection end) => (end.ConnectionType & NetworkType.Chute) != NetworkType.None;

    internal override object Summary(IReferencable network)
    {
        ChuteNetwork chutes = (ChuteNetwork)network;
        List<SmallGrid> pieces = new List<SmallGrid>();
        foreach (INetworkedStructure member in RunNetworks.Copy(chutes.StructureList))
        {
            if (member.GetAsThing is SmallGrid grid)
            {
                pieces.Add(grid);
            }
        }

        List<RunChuteItemView> items = new List<RunChuteItemView>();
        int riding = 0;
        foreach (SmallGrid piece in pieces)
        {
            DynamicThing? item = ChuteFamily.ItemIn(piece);
            if (item == null)
            {
                continue;
            }

            riding++;
            if (items.Count < MaximumListedItems)
            {
                items.Add(new RunChuteItemView(new ThingId(piece.ReferenceId), GameLookup.ViewOf(item)));
            }
        }

        return new RunChuteNetworkView(new ThingId(chutes.ReferenceId), pieces.Count, riding, items,
            RunNetworks.Views(RunNetworks.Copy(chutes.DeviceList)));
    }

    internal override List<Device> DevicesOf(IReferencable network) =>
        RunNetworks.Copy(((ChuteNetwork)network).DeviceList);

    internal override KindGuard GuardOf(ForecastNetwork after, RunNetworkContext context) =>
        new KindGuard(null, null, null);

    internal override LayoutIssue? RemovalProblem(Forecast forecast, RunNetworkContext context) => null;

    internal override string? Holding(SmallGrid piece)
    {
        DynamicThing? item = ChuteFamily.ItemIn(piece);
        return item != null
            ? $"{Names.Of(item)} ({item.ReferenceId}) rides in it and would be lost with it (a chute does not drop " +
              "what it carries when destroyed); let it pass or take it out first"
            : null;
    }

    internal override Dictionary<GridCell, RunChoice> Orient(RunPlan plan, List<OrientableCell> cells) =>
        ChuteFlowCheck.Orient(plan, cells);

    internal override void CheckEdit(RunPlan plan) => ChuteFlowCheck.Check(plan);

    internal override RerouteSegment Ordered(RerouteSegment segment) => ChuteFlowCheck.Ordered(segment);

    internal override List<string> Notes => new List<string>
    {
        "Items travel along a run from its first cell to its last: start at the source (a device's chute Output " +
        "port, a chute bin, an open end items come from) and end at the sink (a device's chute Input port). A " +
        "straight or corner passes an item out of the end it did not come in by; a junction merges its two inputs " +
        "into its output, which is turned to face downstream. flow_reversed, flow_conflict and flow_ambiguous " +
        "refuse; drops_items warns where items would fall out of an open end.",
        "Chutes block cables, pipes, devices and other chutes in the same small cell (SmallCollisionType); frames and " +
        "walls never block them. Pieces come from Kit (Chute): a straight or corner costs 1, a junction 2; " +
        "valves, overflows, splitters, bins, inlets and outlets are never placed or replaced by these tools.",
        "Chute networks take new ids after any removal or change (Chute.OnDestroy rebuilds its neighbours' network)."
    };
}
