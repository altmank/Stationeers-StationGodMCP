#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>What the caller allows an edit to do to networks: merges and device bridges by id, and splits.</summary>
internal sealed class EditAllowance
{
    internal EditAllowance(HashSet<long> bridge, bool split)
    {
        Bridge = bridge;
        Split = split;
    }

    internal static EditAllowance Nothing => new EditAllowance(new HashSet<long>(), false);

    /// <summary>Network and device ids the caller names as meant to be joined (allow_bridge).</summary>
    internal HashSet<long> Bridge { get; }

    /// <summary>allow_split: networks may fall apart and ports lose their connection.</summary>
    internal bool Split { get; }
}

/// <summary>A network's power numbers before an edit (CableNetwork.PotentialLoad, RequiredLoad, weakest ratings).</summary>
internal sealed class NetworkPower
{
    internal NetworkPower(double potentialW, double requiredW, double? lowestCableW, double? lowestFuseW)
    {
        PotentialW = potentialW;
        RequiredW = requiredW;
        LowestCableW = lowestCableW;
        LowestFuseW = lowestFuseW;
    }

    internal double PotentialW { get; }

    internal double RequiredW { get; }

    internal double? LowestCableW { get; }

    internal double? LowestFuseW { get; }
}

/// <summary>What one device port supplies to and draws from its network (PowerTick's per-device numbers).</summary>
internal sealed class PortPower
{
    internal PortPower(double potentialW, double requiredW)
    {
        PotentialW = potentialW;
        RequiredW = requiredW;
    }

    internal double PotentialW { get; }

    internal double RequiredW { get; }
}

/// <summary>A network after an edit as power sees it: the supply and demand it pools and its weakest cable.</summary>
internal sealed class PowerAfter
{
    internal PowerAfter(double potentialW, double requiredW, double? lowestCableW, double? lowestFuseW)
    {
        PotentialW = potentialW;
        RequiredW = requiredW;
        LowestCableW = lowestCableW;
        LowestFuseW = lowestFuseW;
    }

    internal double PotentialW { get; }

    internal double RequiredW { get; }

    /// <summary>
    /// What would flow: min(potential, required), the power the devices draw (PowerTick). A split network's numbers
    /// are counted whole in each part, so this is an upper bound there.
    /// </summary>
    internal double FlowW => System.Math.Min(PotentialW, RequiredW);

    internal double? LowestCableW { get; }

    internal double? LowestFuseW { get; }

    /// <summary>The flow is above the weakest cable: PowerTick breaks a fuse or burns a cable each power tick.</summary>
    internal bool Overloads => LowestCableW.HasValue && FlowW > LowestCableW.Value;

    /// <summary>
    /// The pooled numbers of the networks before it holds, with the weakest of their cables and of the new pieces,
    /// plus what its ports bring from outside those networks: a port whose network the edit removes whole brings that
    /// network's numbers (a reroute replacing every piece keeps its devices' load), and a port on no network now, or
    /// on one that stays apart, brings its device's own estimate (joining). Without them a network of new pieces only
    /// would forecast 0 W however much its devices move. dormant: what each port adds on top once its device, off now,
    /// is switched on (PortLoads.Dormant), counted for every port of the network after, those of the networks pooled
    /// too, whose numbers leave an off device out; null for the network as it is.
    /// </summary>
    internal static PowerAfter Of(ForecastNetwork network, IReadOnlyDictionary<long, NetworkPower> before,
        IReadOnlyDictionary<long, double> newRatings, ICollection<long>? gone = null,
        Func<ForecastPort, PortPower?>? joining = null, Func<ForecastPort, PortPower?>? dormant = null)
    {
        double potential = 0.0;
        double required = 0.0;
        double? cable = null;
        double? fuse = null;
        foreach (long id in network.NetworksBefore)
        {
            if (!before.TryGetValue(id, out NetworkPower power))
            {
                continue;
            }

            potential += power.PotentialW;
            required += power.RequiredW;
            cable = Lowest(cable, power.LowestCableW);
            fuse = Lowest(fuse, power.LowestFuseW);
        }

        HashSet<long> pooledGone = new HashSet<long>();
        foreach (ForecastPort port in network.Ports)
        {
            PortPower? extra = dormant?.Invoke(port);
            if (extra != null)
            {
                potential += extra.PotentialW;
                required += extra.RequiredW;
            }

            long? was = port.NetworkBefore;
            if (was.HasValue && network.NetworksBefore.Contains(was.Value))
            {
                continue;
            }

            if (was.HasValue && gone != null && gone.Contains(was.Value))
            {
                if (pooledGone.Add(was.Value) && before.TryGetValue(was.Value, out NetworkPower old))
                {
                    potential += old.PotentialW;
                    required += old.RequiredW;
                }

                continue;
            }

            PortPower? own = joining?.Invoke(port);
            if (own != null)
            {
                potential += own.PotentialW;
                required += own.RequiredW;
            }
        }

        foreach (long piece in network.NewPieces)
        {
            if (newRatings.TryGetValue(piece, out double rating))
            {
                cable = Lowest(cable, rating);
            }
        }

        return new PowerAfter(potential, required, cable, fuse);
    }

    private static double? Lowest(double? a, double? b) =>
        !a.HasValue ? b : !b.HasValue ? a : System.Math.Min(a.Value, b.Value);
}

/// <summary>
/// The guards on a forecast: a merge of two networks or a device bridge the caller did not name in allow_bridge is
/// would_bridge; a split, a cut port or a removed whole network with devices without allow_split is would_split.
/// </summary>
internal static class EditGuards
{
    internal const string WouldBridge = "would_bridge";
    internal const string WouldSplit = "would_split";

    /// <summary>
    /// The guards' problems. roots: the devices that feed the networks (SplitAnalysis); each would_split names every
    /// part's devices and those cut off from a root.
    /// </summary>
    internal static List<LayoutIssue> Check(Forecast forecast, EditAllowance allow, NetworkRootSet? roots = null)
    {
        List<LayoutIssue> problems = new List<LayoutIssue>();
        foreach (ForecastNetwork merge in forecast.Merges)
        {
            if (!AllAllowed(merge.NetworksBefore, allow.Bridge))
            {
                problems.Add(new LayoutIssue(WouldBridge,
                    $"The edit would join networks {string.Join(", ", merge.NetworksBefore)} into one; name every " +
                    "one of them in allow_bridge if that is meant."));
            }
        }

        foreach (ForecastBridge bridge in forecast.Bridges)
        {
            if (!allow.Bridge.Contains(bridge.Device))
            {
                List<string> ports = new List<string>(bridge.Ports.Count);
                foreach (ForecastPort port in bridge.Ports)
                {
                    ports.Add(port.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                problems.Add(new LayoutIssue(WouldBridge,
                    $"The edit would put ports {string.Join(", ", ports)} of device {bridge.Device} on one network " +
                    "(both sides of an APC or transformer, a battery's input and output, a pump's two sides, a " +
                    "device's chute output fed back into its own input); name the device in allow_bridge if that " +
                    "is meant.", null, bridge.Device));
            }
        }

        if (allow.Split)
        {
            return problems;
        }

        List<SplitDetail> details = SplitAnalysis.Of(forecast, roots ?? NetworkRootSet.None);
        for (int index = 0; index < forecast.Splits.Count; index++)
        {
            ForecastSplit split = forecast.Splits[index];
            problems.Add(new LayoutIssue(WouldSplit,
                $"Network {split.Network} would fall into {split.Parts.Count} networks." +
                $"{SplitAnalysis.Describe(details[index], roots?.IsNamed ?? false)} Pass allow_split if that is " +
                "meant.", null, split.Network));
        }

        foreach (ForecastPort port in forecast.Cut)
        {
            List<long>? cutOff = SplitAnalysis.CutEntryOf(details, forecast.Splits.Count, port)?.CutOff;
            string cutNote = cutOff != null && cutOff.Count > 0
                ? $" Cut off from the root: {string.Join(", ", cutOff)}."
                : string.Empty;
            problems.Add(new LayoutIssue(WouldSplit,
                $"Port {port.Index} of device {port.DeviceId} would lose its connection to network " +
                $"{port.NetworkBefore}.{cutNote} Pass allow_split if that is meant.", null, port.DeviceId));
        }

        return problems;
    }

    private static bool AllAllowed(List<long> ids, HashSet<long> allowed)
    {
        foreach (long id in ids)
        {
            if (!allowed.Contains(id))
            {
                return false;
            }
        }

        return true;
    }
}
