#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Networks;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Electrical;
using Assets.Scripts.Objects.Pipes;
using Networks;
using Objects.Pipes;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Upgrades;

/// <summary>
/// A grade of piece and the kit that places it: a cable's Cable.Type, or a pipe's Piping.Type and content. Two
/// pieces of one grade come from the same coil or kit.
/// </summary>
internal sealed class Grade
{
    internal Grade(int level, int content, string name)
    {
        Level = level;
        Content = content;
        Name = name;
    }

    /// <summary>Cable.Type or Piping.Type as an integer.</summary>
    internal int Level { get; }

    /// <summary>Pipe.ContentType as an integer; 0 for cables.</summary>
    internal int Content { get; }

    internal string Name { get; }

    internal bool SameAs(Grade other) => Level == other.Level && Content == other.Content;
}

/// <summary>
/// What differs between cable and pipe runs: which things are pieces, their grades, their network, and how a
/// replacement joins that network and the old piece leaves it. What a piece is replaced with is the goal's
/// (SwapGoal); everything else is shared.
/// </summary>
internal abstract class UpgradeFamily
{
    /// <summary>cable or pipe, as connections names networks.</summary>
    internal abstract string NetworkKind { get; }

    /// <summary>A thing this family's networks hold (Cable, or Pipe for pipes).</summary>
    internal abstract bool IsMember(Thing thing);

    /// <summary>
    /// A member that is a run piece (any Cable; Piping for pipes). Other pipe members (vents, drains, radiators) are
    /// devices on the run and stay as they are.
    /// </summary>
    internal abstract bool IsPiece(Thing thing);

    /// <summary>The members of one network, by id; network_not_found when there is none.</summary>
    internal abstract List<SmallGrid> NetworkMembers(ThingId networkId);

    /// <summary>
    /// The devices on one network (its DeviceList), which NetworkMembers does not hold; network_not_found when there is
    /// none.
    /// </summary>
    internal abstract List<Device> NetworkDevices(ThingId networkId);

    /// <summary>
    /// Whether the family's pieces come from this kind of kit: a coil or pipe kit merges into the piece it is used on
    /// (MultiMergeConstructor), so a plain MultiConstructor listing such pieces is some other kit.
    /// </summary>
    internal virtual bool BuildsWith(MultiConstructor kit) => kit is MultiMergeConstructor;

    /// <summary>A member's grade when a coil or kit of this family places it; null for special pieces.</summary>
    internal abstract Grade? GradeOf(Structure structure);

    /// <summary>
    /// A run piece's grade whether or not a kit places it (the long pipe straights are PipingLong, which no pipe kit
    /// lists as a merging piece); null for pieces of no grade.
    /// </summary>
    internal abstract Grade? RunGradeOf(SmallGrid piece);

    /// <summary>The network the piece is in; null when it is in none.</summary>
    internal abstract IReferencable? NetworkOf(SmallGrid piece);

    /// <summary>What the piece carries, for the pipe connection rule; null for cables.</summary>
    internal abstract PipeContent? ContentOf(SmallGrid piece);

    /// <summary>A device mounted on this family's pieces (a cable fuse or analyser, a pipe meter).</summary>
    internal abstract bool IsMountedOn(Device device);

    /// <summary>Whether the mounted device is attached to the piece now, by the game's own test.</summary>
    internal abstract bool MountedNow(Device device, SmallGrid piece);

    /// <summary>Puts the replacement into the old piece's network when placing it made a network of its own.</summary>
    internal abstract void Join(SmallGrid replacement, IReferencable network);

    /// <summary>
    /// Takes the old piece out of its network; every device registered through it is registered through the
    /// replacements it links to instead (ThroughWhich).
    /// </summary>
    internal abstract void Leave(SmallGrid old, List<SmallGrid> replacements, IReferencable network);

    /// <summary>The replacement's rating, for the report: MaxVoltage in W, or MaxPressure in kPa.</summary>
    internal abstract double RatingOf(Structure structure);

    /// <summary>Whether the cell's slot for this family's pieces holds the piece.</summary>
    internal abstract bool Holds(SmallCell cell, SmallGrid piece);

    /// <summary>The ids of this family's networks the device is on.</summary>
    internal abstract List<long> DeviceNetworks(Device device);

    /// <summary>The network's state before the swap, with what the swap would change.</summary>
    internal abstract NetworkRecord Record(IReferencable network, List<PlannedSwap> swaps);

    /// <summary>A report entry for one source prefab and what it becomes.</summary>
    internal abstract object Mapping(Api.Views.UpgradeMappingCount count, Structure source, Structure target);

    internal const string HoldsContents = "holds_contents";

    /// <summary>
    /// Removed pieces that must stay after all, with why: for pipes, every removed piece of a network the removal would
    /// empty while it still holds gas or liquid. Nothing for cables.
    /// </summary>
    internal virtual Dictionary<long, string> RemovalHolds(List<SmallGrid> removed) => new Dictionary<long, string>();

    /// <summary>What the clean tools keep for this family's networks, for their report's notes.</summary>
    internal abstract string CleanNote { get; }

    /// <summary>What does not change for devices when a piece of this family changes grade (CODE).</summary>
    internal abstract string DeviceNote { get; }

    protected static List<long> Ids<T>(List<T> networks) where T : class, IReferencable
    {
        List<long> ids = new List<long>(networks.Count);
        foreach (T network in networks)
        {
            if (network != null)
            {
                ids.Add(network.ReferenceId);
            }
        }

        ids.Sort();
        return ids;
    }

    protected static List<SmallGrid> NonNull<T>(List<T> members) where T : class
    {
        List<T> copy;
        lock (members)
        {
            copy = new List<T>(members);
        }

        List<SmallGrid> pieces = new List<SmallGrid>(copy.Count);
        foreach (T member in copy)
        {
            SmallGrid? grid = member is INetworkedStructure networked
                ? networked.GetAsThing as SmallGrid
                : member as SmallGrid;
            if (grid != null)
            {
                pieces.Add(grid);
            }
        }

        return pieces;
    }

    /// <summary>
    /// The replacements a device registered through the old piece is to be registered through: the one replacement
    /// of a one-for-one swap (the same ends, so the same devices); for the singles of a split long straight, those the
    /// device links to either way (SmallGrid.IsConnected on each other's ends); all of them if none does, so no device
    /// is ever left without a registration.
    /// </summary>
    protected static List<SmallGrid> ThroughWhich(Device device, List<SmallGrid> replacements)
    {
        if (replacements.Count == 1)
        {
            return replacements;
        }

        List<SmallGrid> linked = replacements.FindAll(replacement => Linked(replacement, device));
        return linked.Count > 0 ? linked : replacements;
    }

    private static bool Linked(SmallGrid piece, Device device)
    {
        foreach (Connection end in piece.OpenEnds)
        {
            if (end != null && device.IsConnected(end))
            {
                return true;
            }
        }

        foreach (Connection end in device.OpenEnds)
        {
            if (end != null && piece.IsConnected(end))
            {
                return true;
            }
        }

        return false;
    }

    protected static ApiException NetworkNotFound(string kind, ThingId id) =>
        ApiErrors.Refused("network_not_found", $"No {kind} network has id {id}.");
}

/// <summary>
/// Cables. The grade is Cable.CableType (normal, heavy, superHeavy); a plain Cable is a coil piece when it merges with
/// other cables or is a long straight segment (CablePieces). CableType is read by nothing but placement (Cable._IsCollision, Cable.CanReplace), merging and
/// the debug drawing; power and devices only read MaxVoltage (PowerTick). CODE.
/// </summary>
internal sealed class CableFamily : UpgradeFamily
{
    internal override string NetworkKind => "cable";

    internal override bool IsMember(Thing thing) => thing is Cable;

    internal override bool IsPiece(Thing thing) => thing is Cable;

    internal override List<SmallGrid> NetworkMembers(ThingId networkId)
    {
        CableNetwork network = Referencable.Find<CableNetwork>(networkId.Value) ??
                               throw NetworkNotFound(NetworkKind, networkId);
        return NonNull(network.CableList);
    }

    internal override List<Device> NetworkDevices(ThingId networkId)
    {
        CableNetwork network = Referencable.Find<CableNetwork>(networkId.Value) ??
                               throw NetworkNotFound(NetworkKind, networkId);
        return Runs.RunNetworks.Copy(network.DeviceList);
    }

    internal override Grade? GradeOf(Structure structure) =>
        structure is Cable cable && cable.GetType() == typeof(Cable) &&
        CablePieces.IsCoilPiece(cable.BlockMergeWithOtherCables, cable.IsStraight, cable.StraightUnitLength)
            ? new Grade((int)cable.CableType, 0, NameOf(cable.CableType))
            : null;

    internal override Grade? RunGradeOf(SmallGrid piece) =>
        piece is Cable cable && cable.GetType() == typeof(Cable)
            ? new Grade((int)cable.CableType, 0, NameOf(cable.CableType))
            : null;

    internal override IReferencable? NetworkOf(SmallGrid piece) => piece is Cable cable ? cable.CableNetwork : null;

    internal override PipeContent? ContentOf(SmallGrid piece) => null;

    internal override bool IsMountedOn(Device device) => device is DeviceCableMounted;

    // DeviceCableMounted.IsValidCable: the cable in its cell, with the same forward axis either way round.
    internal override bool MountedNow(Device device, SmallGrid piece) =>
        device is DeviceCableMounted mounted && mounted.SmallCell != null && mounted.SmallCell.Cable == piece &&
        mounted.IsValidCable();

    internal override void Join(SmallGrid replacement, IReferencable network)
    {
        if (replacement is Cable cable && network is CableNetwork cables && cable.CableNetwork != cables)
        {
            cables.Add(cable);
        }
    }

    // CableNetwork.Remove leaves the cable in DeviceRegister. Each device registered through the old cable is
    // registered through its replacements (ThroughWhich) before the old cable is dropped from its registration, so no
    // device ever has none and leaves the network.
    internal override void Leave(SmallGrid old, List<SmallGrid> replacements, IReferencable network)
    {
        if (!(old is Cable cable) || !(network is CableNetwork cables))
        {
            return;
        }

        cables.Remove(cable);
        List<Device> registered;
        lock (cables.DeviceList)
        {
            registered = new List<Device>(cables.DeviceList);
        }

        foreach (Device device in registered)
        {
            HashSet<Cable> through = cables.GetDeviceRegistration(device);
            if (through == null || !through.Contains(cable))
            {
                continue;
            }

            foreach (SmallGrid next in ThroughWhich(device, replacements))
            {
                if (next is Cable nextCable)
                {
                    cables.AddDevice(nextCable, device);
                }
            }

            cables.RemoveDevice(cable, device);
        }
    }

    internal override double RatingOf(Structure structure) => structure is Cable cable ? cable.MaxVoltage : 0.0;

    internal override bool Holds(SmallCell cell, SmallGrid piece) => cell.Cable == piece;

    internal override List<long> DeviceNetworks(Device device) => Ids(device.ConnectedCableNetworks);

    internal override NetworkRecord Record(IReferencable network, List<PlannedSwap> swaps) =>
        new CableNetworkRecord((CableNetwork)network, swaps);

    internal override object Mapping(Api.Views.UpgradeMappingCount count, Structure source, Structure target) =>
        new Api.Views.CableMappingView(count, RatingOf(source), RatingOf(target));

    internal override string CleanNote =>
        "Cable networks keep their ids; every device stays on the networks it was on (checked after the run).";

    internal override string DeviceNote =>
        "Devices, APCs, batteries, transformers and fuses connect to any cable type: CableType is read only when " +
        "placing and merging cables (Cable._IsCollision, Cable.CanReplace) and for the debug drawing. Power reads " +
        "each cable's MaxVoltage only, to burn one that carries more (PowerTick).";

    internal static string NameOf(Cable.Type type) =>
        type switch
        {
            Cable.Type.normal => "normal",
            Cable.Type.heavy => "heavy",
            Cable.Type.superHeavy => "super_heavy",
            _ => type.ToString()
        };
}

/// <summary>
/// Pipes. The grade is Piping.PipeType with Pipe.PipeContentType; a normal piece becomes insulated with the same
/// content (normal to Insulated, NormalLowVolume to InsulatedLowVolume). Gas and liquid never mix: the content is
/// part of the grade. PipeType is read by placement, merging, the debug drawing and Pipe.MaxPressure, which is the
/// same for normal and insulated. CODE.
/// </summary>
internal sealed class PipeFamily : UpgradeFamily
{
    internal override string NetworkKind => "pipe";

    internal override bool IsMember(Thing thing) => thing is Pipe;

    internal override bool IsPiece(Thing thing) => thing is Piping;

    internal override List<SmallGrid> NetworkMembers(ThingId networkId)
    {
        PipeNetwork network = Referencable.Find<PipeNetwork>(networkId.Value) ??
                              throw NetworkNotFound(NetworkKind, networkId);
        return NonNull(network.StructureList);
    }

    internal override List<Device> NetworkDevices(ThingId networkId)
    {
        PipeNetwork network = Referencable.Find<PipeNetwork>(networkId.Value) ??
                              throw NetworkNotFound(NetworkKind, networkId);
        return Runs.RunNetworks.Copy(network.DeviceList);
    }

    // Plain Piping and the long straights (PipingLong), so a kit that lists long pipes is found with them.
    internal override Grade? GradeOf(Structure structure) =>
        structure is Piping piping && (piping.GetType() == typeof(Piping) || piping is PipingLong)
            ? new Grade((int)piping.PipeType, (int)piping.PipeContentType,
                $"{piping.PipeType} {piping.PipeContentType}".ToLowerInvariant())
            : null;

    // Plain Piping and the long straights (PipingLong: Piping with a length of 3, 5 or 10 cells).
    internal override Grade? RunGradeOf(SmallGrid piece) =>
        piece is Piping piping && (piping.GetType() == typeof(Piping) || piping is PipingLong)
            ? new Grade((int)piping.PipeType, (int)piping.PipeContentType,
                $"{piping.PipeType} {piping.PipeContentType}".ToLowerInvariant())
            : null;

    internal override IReferencable? NetworkOf(SmallGrid piece) => piece is Pipe pipe ? pipe.PipeNetwork : null;

    internal override PipeContent? ContentOf(SmallGrid piece) =>
        piece is Pipe pipe
            ? new PipeContent((int)pipe.PipeContentType, pipe.PipeContentType == Pipe.ContentType.All)
            : null;

    internal override bool IsMountedOn(Device device) => device is DevicePipeMounted;

    // DevicePipeMounted.IsValidPipe: the pipe in its cell, not burst, the same axis, matching content.
    internal override bool MountedNow(Device device, SmallGrid piece) =>
        device is DevicePipeMounted mounted && mounted.SmallCell != null && mounted.SmallCell.Pipe == piece &&
        mounted.IsValidPipe();

    internal override void Join(SmallGrid replacement, IReferencable network)
    {
        if (replacement is Pipe pipe && network is PipeNetwork pipes && pipe.PipeNetwork != pipes)
        {
            pipes.Add(pipe);
        }
    }

    /// <summary>
    /// Merges one pipe network into another as the game does when a placed pipe joins both (Pipe.OnRegistered:
    /// StructureNetwork.Merge): AtmosphericsNetwork.Merge queues the old network's gas to be added to the new one's and
    /// moves its members over (JobGas.Settle applies the gas).
    /// </summary>
    internal static void Merge(IReferencable into, IReferencable from)
    {
        if (into is PipeNetwork target && from is PipeNetwork source && target != source)
        {
            target.Merge(source);
        }
    }

    // AtmosphericsNetwork.Remove takes the pipe's volume off the network's Atmosphere and leaves the gas where it is.
    // Its DeviceRegister entries move to the replacement as for cables.
    internal override void Leave(SmallGrid old, List<SmallGrid> replacements, IReferencable network)
    {
        if (!(old is Pipe pipe) || !(network is PipeNetwork pipes))
        {
            return;
        }

        pipes.Remove(pipe);
        List<Device> registered = new List<Device>(pipes.DeviceList);
        foreach (Device device in registered)
        {
            HashSet<INetworkedStructure> through = pipes.GetDeviceRegistration(device);
            if (through == null || !through.Contains(pipe))
            {
                continue;
            }

            foreach (SmallGrid next in ThroughWhich(device, replacements))
            {
                if (next is Pipe nextPipe)
                {
                    pipes.AddDevice(nextPipe, device);
                }
            }

            pipes.RemoveDevice(pipe, device);
        }
    }

    internal override double RatingOf(Structure structure) =>
        structure is Pipe pipe ? pipe.MaxPressure.ToDouble() : 0.0;

    internal override bool Holds(SmallCell cell, SmallGrid piece) => cell.Pipe == piece;

    internal override List<long> DeviceNetworks(Device device) => Ids(device.ConnectedPipeNetworks);

    internal override NetworkRecord Record(IReferencable network, List<PlannedSwap> swaps) =>
        new PipeNetworkRecord((PipeNetwork)network, swaps);

    internal override object Mapping(Api.Views.UpgradeMappingCount count, Structure source, Structure target) =>
        new Api.Views.PipeMappingView(count, NumbersOf(source), NumbersOf(target));

    // The game's own removal of a network's last pipe (Pipe.OnDestroy) divides its contents among the networks its
    // neighbours rebuild, which are none: the gas or liquid is deleted. A removal that would do that is held back.
    internal override Dictionary<long, string> RemovalHolds(List<SmallGrid> removed)
    {
        Dictionary<long, string> holds = new Dictionary<long, string>();
        Dictionary<long, List<SmallGrid>> byNetwork = new Dictionary<long, List<SmallGrid>>();
        Dictionary<long, PipeNetwork> networks = new Dictionary<long, PipeNetwork>();
        foreach (SmallGrid piece in removed)
        {
            if (!(piece is Pipe pipe) || pipe.PipeNetwork == null)
            {
                continue;
            }

            long id = pipe.PipeNetwork.ReferenceId;
            if (!byNetwork.TryGetValue(id, out List<SmallGrid> list))
            {
                list = new List<SmallGrid>();
                byNetwork[id] = list;
                networks[id] = pipe.PipeNetwork;
            }

            list.Add(piece);
        }

        foreach (KeyValuePair<long, List<SmallGrid>> entry in byNetwork)
        {
            PipeNetwork network = networks[entry.Key];
            double moles = network.Atmosphere != null ? GasSnapshot.Of(network.Atmosphere).TotalMol() : 0.0;
            if (entry.Value.Count < NonNull(network.StructureList).Count || moles <= 1e-6)
            {
                continue;
            }

            foreach (SmallGrid piece in entry.Value)
            {
                holds[piece.ReferenceId] = $"{moles:0.###} mol in pipe network {entry.Key} would be deleted with its " +
                                           "last pipe (the game's own removal divides it among no network)";
            }
        }

        return holds;
    }

    internal override string CleanNote =>
        "A pipe network's gas or liquid stays in the network's own Atmosphere the whole time (every replacement " +
        "joins before the old piece leaves): moles and energy are unchanged, only the volume changes by the " +
        "difference between the old and new pieces, and the report shows the pressure that gives. The run is refused " +
        "if that pressure would exceed the weakest pipe's rating, and the contents are checked again after it.";

    internal override string DeviceNote =>
        "Pumps, vents, tanks and other devices connect to normal and insulated pipe alike: PipeType is read only " +
        "when placing and merging pipes, for the debug drawing and by Pipe.MaxPressure, which gives both the same " +
        "rating. Insulated pieces exchange heat with their surroundings by their own ThermodynamicsScale " +
        "(heat_exchange_factor).";

    private Api.Views.PipePrefabNumbers NumbersOf(Structure structure) =>
        new Api.Views.PipePrefabNumbers(RatingOf(structure), VolumeOf(structure), HeatExchangeOf(structure));

    /// <summary>The piece's volume in litres (Pipe.Volume).</summary>
    internal static double VolumeOf(Structure structure) => structure is Pipe pipe ? pipe.Volume.ToDouble() : 0.0;

    /// <summary>Thing.ThermodynamicsScale: Pipe.ConvectionFactor and RadiationFactor scale with it.</summary>
    internal static double HeatExchangeOf(Structure structure) => structure.ThermodynamicsScale;
}
