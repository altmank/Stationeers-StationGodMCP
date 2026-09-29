#nullable enable

using System.Collections.Generic;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Networks;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// What differs between laying cable, pipe and chute runs: the family (pieces, networks, kits), the grade names, which
/// cell slot a piece takes, which device ends a run joins and which of them bridge, the network summaries and the
/// kind's own guards on the networks an edit would leave. The survey, the route search, the layout, the forecast, the
/// job and the check are shared.
/// </summary>
internal abstract class RunKind
{
    internal abstract UpgradeFamily Family { get; }

    /// <summary>cable or pipe.</summary>
    internal string Noun => Family.NetworkKind;

    internal abstract string PlaceTool { get; }

    internal abstract string RemoveTool { get; }

    internal abstract string PlanTool { get; }

    /// <summary>The grade names the tools take, in the order the description lists them.</summary>
    internal abstract string[] GradeNames { get; }

    /// <summary>The grade when the request names none; null when it must be named.</summary>
    internal abstract string? DefaultGrade { get; }

    /// <summary>The family's grade for a name; null for an unknown one.</summary>
    internal abstract Grade? GradeOf(string name);

    /// <summary>The tools' name for a piece's grade (heavy, insulated_gas, ...).</summary>
    internal abstract string NameOf(Grade grade);

    /// <summary>The run's NetworkType bits for the grade (PowerAndData for cables, Pipe or PipeLiquid).</summary>
    internal abstract int EndType(Grade grade);

    /// <summary>Every NetworkType bit a piece of the kind may join by (for removals, which have no grade).</summary>
    internal abstract int AnyEndType { get; }

    /// <summary>What a run of the grade carries (pipes); null for cables.</summary>
    internal abstract PipeContent? ContentOf(Grade grade);

    /// <summary>The family's piece in the cell's slot for it (SmallCell.Cable or SmallCell.Pipe).</summary>
    internal abstract SmallGrid? SlotOf(SmallCell cell);

    /// <summary>
    /// What is left of a piece the game destroyed (a burnt cable): on no network, built by no coil, removed by the
    /// remove tool for nothing and cleared by the place tool's remove_ids. None for pipes and chutes.
    /// </summary>
    internal virtual bool IsDebris(Thing thing) => false;

    /// <summary>The debris standing in the cell, if any (a burnt cable takes SmallCell.Other).</summary>
    internal virtual SmallGrid? DebrisIn(SmallCell cell) => null;

    /// <summary>
    /// Whether two ports of one device on one network of this kind is a bridge: a power port (cables), any pipe port
    /// of the kind (pipes: a pump's or regulator's two sides).
    /// </summary>
    internal abstract bool Bridges(Connection end);

    /// <summary>A report entry for a network as it is now.</summary>
    internal abstract object Summary(IReferencable network);

    /// <summary>The devices on a network now.</summary>
    internal abstract List<Device> DevicesOf(IReferencable network);

    /// <summary>
    /// The kind's guard for one network the edit leaves: its view and whether it refuses (overload for cables,
    /// burst for pipes), from the networks before it holds and its new pieces.
    /// </summary>
    internal abstract KindGuard GuardOf(ForecastNetwork after, RunNetworkContext context);

    /// <summary>
    /// A problem when the removals would lose or move a network's contents (pipes): null when removing is safe.
    /// </summary>
    internal abstract LayoutIssue? RemovalProblem(Forecast forecast, RunNetworkContext context);

    /// <summary>The problem code when the source holds too few of the kit or coil a run is built from.</summary>
    internal virtual string ShortageCode => "not_enough_coils";

    /// <summary>A kind's own notes for the report.</summary>
    internal abstract List<string> Notes { get; }

    /// <summary>
    /// Why the piece may be neither removed nor replaced because of what it holds (an item riding in a chute); null
    /// when nothing stops it.
    /// </summary>
    internal virtual string? Holding(SmallGrid piece) => null;

    /// <summary>
    /// The piece for each cell whose ends several turns of the kit fit, each sending items out a different way (a
    /// chute junction); the first turn of each where direction means nothing.
    /// </summary>
    internal virtual Dictionary<GridCell, RunChoice> Orient(RunPlan plan, List<OrientableCell> cells)
    {
        Dictionary<GridCell, RunChoice> picked = new Dictionary<GridCell, RunChoice>();
        foreach (OrientableCell cell in cells)
        {
            picked[cell.Layout.Cell] = cell.Options[0];
        }

        return picked;
    }

    /// <summary>The kind's own checks of the whole edit once every piece is chosen (the chute flow); none by default.</summary>
    internal virtual void CheckEdit(RunPlan plan)
    {
    }

    /// <summary>The old run a reroute replaces, its ends ordered as the kind needs (upstream first for chutes).</summary>
    internal virtual RerouteSegment Ordered(RerouteSegment segment) => segment;
}

/// <summary>A layout cell several turns of its kit's pieces fit, with those turns, the piece there now and its id.</summary>
internal sealed class OrientableCell
{
    internal OrientableCell(LayoutCell layout, RunCatalogue catalogue, List<RunChoice> options, SmallGrid? existing,
        long id, SmallGrid? splitFrom)
    {
        SplitFrom = splitFrom;
        Layout = layout;
        Catalogue = catalogue;
        Options = options;
        Existing = existing;
        Id = id;
    }

    internal LayoutCell Layout { get; }

    internal RunCatalogue Catalogue { get; }

    internal List<RunChoice> Options { get; }

    internal SmallGrid? Existing { get; }

    /// <summary>The id the piece goes by in the forecast: the old piece's, or a negative number for a new one.</summary>
    internal long Id { get; }

    /// <summary>The long straight a new piece is a single of (PlannedCell.SplitFrom); null otherwise.</summary>
    internal SmallGrid? SplitFrom { get; }
}

/// <summary>The kind's guard numbers for one network after an edit, its refusal if any, and its warning if any.</summary>
internal sealed class KindGuard
{
    internal KindGuard(object? view, string? code, string? message, LayoutIssue? warning = null)
    {
        View = view;
        Code = code;
        Message = message;
        Warning = warning;
    }

    internal object? View { get; }

    internal string? Code { get; }

    internal string? Message { get; }

    /// <summary>A risk the edit does not cause by itself (would_overload_when_on); null for none.</summary>
    internal LayoutIssue? Warning { get; }
}

/// <summary>Cable runs: normal, heavy or super heavy coil; power ports bridge; the flow must fit the weakest cable.</summary>
internal sealed class CableRunKind : RunKind
{
    private readonly CableFamily _family = new CableFamily();

    internal override UpgradeFamily Family => _family;

    internal override string PlaceTool => "place_cables";

    internal override string RemoveTool => "remove_cables";

    internal override string PlanTool => "plan_cable_route";

    internal override string[] GradeNames => new[] { "normal", "heavy", "super_heavy" };

    internal override string? DefaultGrade => "heavy";

    internal override Grade? GradeOf(string name) =>
        name switch
        {
            "normal" => new Grade((int)Cable.Type.normal, 0, CableFamily.NameOf(Cable.Type.normal)),
            "heavy" => new Grade((int)Cable.Type.heavy, 0, CableFamily.NameOf(Cable.Type.heavy)),
            "super_heavy" => new Grade((int)Cable.Type.superHeavy, 0, CableFamily.NameOf(Cable.Type.superHeavy)),
            _ => null
        };

    internal override string NameOf(Grade grade) => CableFamily.NameOf((Cable.Type)grade.Level);

    internal override int EndType(Grade grade) => (int)NetworkType.PowerAndData;

    internal override int AnyEndType => (int)NetworkType.PowerAndData;

    internal override PipeContent? ContentOf(Grade grade) => null;

    internal override SmallGrid? SlotOf(SmallCell cell) => cell.Cable;

    // Cable.Break (CODE) destroys the cable and spawns its RupturedPrefab, a CableRuptured
    // (StructureCableStraightBurnt, StructureCableStraightHBurnt, ...).
    internal override bool IsDebris(Thing thing) => thing is CableRuptured;

    internal override SmallGrid? DebrisIn(SmallCell cell) => cell.Other as CableRuptured;

    internal override bool Bridges(Connection end) => (end.ConnectionType & NetworkType.Power) != NetworkType.None;

    internal override object Summary(IReferencable network) => RunNetworks.CableSummary((CableNetwork)network);

    internal override List<Device> DevicesOf(IReferencable network) =>
        RunNetworks.Copy(((CableNetwork)network).DeviceList);

    internal override KindGuard GuardOf(ForecastNetwork after, RunNetworkContext context) =>
        RunNetworks.PowerGuard(after, context);

    internal override LayoutIssue? RemovalProblem(Forecast forecast, RunNetworkContext context) => null;

    internal override List<string> Notes => new List<string>
    {
        "Cables may pass through frame cells and along wall faces: small-grid pieces never collide with frames or " +
        "walls (SmallGrid.CanConstruct checks the large grid only for DualRegister pieces). They collide with " +
        "devices and chutes in the same small cell, and with a pipe there whose ends lie along the same axis.",
        "Networks are rebuilt by the game as a player's building does (Cable.OnRegistered merges, Cable.OnDestroy " +
        "rebuilds from the neighbours), so merged and split networks get new ids."
    };
}

/// <summary>
/// Pipe runs: gas or liquid, normal or insulated kit; pipe ports bridge; merged contents must stay under the weakest
/// pipe, and a removal never loses or moves contents.
/// </summary>
internal sealed class PipeRunKind : RunKind
{
    private readonly PipeFamily _family = new PipeFamily();

    internal override UpgradeFamily Family => _family;

    internal override string PlaceTool => "place_pipes";

    internal override string RemoveTool => "remove_pipes";

    internal override string PlanTool => "plan_pipe_route";

    internal override string[] GradeNames => new[] { "gas", "liquid", "insulated_gas", "insulated_liquid" };

    internal override string? DefaultGrade => null;

    internal override Grade? GradeOf(string name) =>
        name switch
        {
            "gas" => Of(Piping.Type.normal, Pipe.ContentType.Gas),
            "liquid" => Of(Piping.Type.normal, Pipe.ContentType.Liquid),
            "insulated_gas" => Of(Piping.Type.Insulated, Pipe.ContentType.Gas),
            "insulated_liquid" => Of(Piping.Type.Insulated, Pipe.ContentType.Liquid),
            _ => null
        };

    internal override string NameOf(Grade grade)
    {
        string content = (Pipe.ContentType)grade.Content == Pipe.ContentType.Liquid ? "liquid" : "gas";
        return (Piping.Type)grade.Level switch
        {
            Piping.Type.normal => content,
            Piping.Type.Insulated => "insulated_" + content,
            _ => grade.Name
        };
    }

    // PipeFamily.GradeOf names grades "{PipeType} {ContentType}", lower case.
    private static Grade Of(Piping.Type type, Pipe.ContentType content) =>
        new Grade((int)type, (int)content, $"{type} {content}".ToLowerInvariant());

    internal override int EndType(Grade grade) =>
        (Pipe.ContentType)grade.Content == Pipe.ContentType.Liquid
            ? (int)NetworkType.PipeLiquid
            : (int)NetworkType.Pipe;

    internal override int AnyEndType => (int)(NetworkType.Pipe | NetworkType.PipeLiquid);

    internal override PipeContent? ContentOf(Grade grade) => new PipeContent(grade.Content, false);

    internal override SmallGrid? SlotOf(SmallCell cell) => cell.Pipe;

    internal override bool Bridges(Connection end) =>
        (end.ConnectionType & (NetworkType.Pipe | NetworkType.PipeLiquid)) != NetworkType.None;

    internal override object Summary(IReferencable network) => RunNetworks.PipeSummary((PipeNetwork)network);

    internal override List<Device> DevicesOf(IReferencable network) =>
        RunNetworks.Copy(((PipeNetwork)network).DeviceList);

    internal override KindGuard GuardOf(ForecastNetwork after, RunNetworkContext context) =>
        RunNetworks.PipeGuard(after, context);

    internal override LayoutIssue? RemovalProblem(Forecast forecast, RunNetworkContext context) =>
        RunNetworks.PipeRemovalProblem(forecast, context);

    internal override List<string> Notes => new List<string>
    {
        "Pipes may pass through frame cells and along wall faces (small-grid pieces never collide with frames or " +
        "walls). They collide with devices and chutes in the same small cell, and with a cable there whose ends lie " +
        "along the same axis.",
        "Placing pipes into a network adds their volume: the network's contents spread into it (moles and energy " +
        "unchanged, pressure falls). A merge pools the networks' contents (reported per network with its gases); " +
        "the pooled pressure must stay under the weakest pipe (would_burst).",
        "Removing pipes: each removed pipe leaves its network before it is destroyed, so its volume leaves and the " +
        "contents stay (pressure rises; would_burst). A removal that would split a network holding contents, or " +
        "empty one, is refused (contents_would_move, holds_contents): empty it first."
    };
}
