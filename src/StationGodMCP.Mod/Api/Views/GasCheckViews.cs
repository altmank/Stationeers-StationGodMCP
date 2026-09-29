#nullable enable

using System.Collections.Generic;
using System.Globalization;
using StationGodMCP.Api.Shared;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Views;

/// <summary>
/// A job's check of the pipe networks' contents (gas_check): every family of networks it changed, read before the job
/// and again after, once every gas change the job queued had been applied. Ok when each family holds what it held
/// (or lost it to the removal of its last pipe, which the planner holds back unless allowed; or lost what the job's
/// plan deleted on purpose, planned_loss_mol) and no network without pipes is left holding gas. Gas a family lost in
/// the game's merge is put back into its networks (recovered) and the empty networks left holding it are cleared; a
/// refill that would take a network over its weakest pipe is not made (withheld). What could not be put back leaves
/// the check failed, the job gas_lost, and further pipe jobs refused.
/// </summary>
internal sealed class GasCheckView
{
    private GasCheckView(bool isChecked, bool ok, string summary, List<GasFamilyView> families,
        List<GasGhostView> ghosts, List<GasRefillView> recovered, List<ThingId> ghostsCleared,
        List<GasGhostView> oldGhosts, List<GasOrphanView> orphans, List<GasOrphanView> oldOrphans,
        List<GasWithheldView> withheld)
    {
        Checked = isChecked;
        Ok = ok;
        Summary = summary;
        Families = families;
        Ghosts = ghosts;
        Recovered = recovered;
        GhostsCleared = ghostsCleared;
        OldGhosts = oldGhosts;
        Orphans = orphans;
        OldOrphans = oldOrphans;
        Withheld = withheld;
    }

    /// <summary>False when the check could not be made (a save held the tick); nothing is known then.</summary>
    public bool Checked { get; }

    public bool Ok { get; }

    public string Summary { get; }

    public List<GasFamilyView> Families { get; }

    /// <summary>Networks without pipes the job left holding gas: where the missing gas sits.</summary>
    public List<GasGhostView> Ghosts { get; }

    /// <summary>Gas put back into a family's networks after the game's merge lost it.</summary>
    public List<GasRefillView> Recovered { get; }

    /// <summary>
    /// Gas a family lacks that was not put back, because it would take one of its networks over the rating of its
    /// weakest pipe (1.4.4+): the family stays short and the check fails.
    /// </summary>
    public List<GasWithheldView> Withheld { get; }

    /// <summary>Networks without pipes the job left, emptied and dropped once their family was whole again.</summary>
    public List<ThingId> GhostsCleared { get; }

    /// <summary>Networks without pipes that held the same gas before the job: not the job's doing, left as they are.</summary>
    public List<GasGhostView> OldGhosts { get; }

    /// <summary>
    /// Networks the game no longer lists that pipes still name, left by the job (1.4.4+): nothing simulates them and
    /// their gas is counted where it sits, so a copy the game's merge already gave the survivor shows as gas that
    /// appeared. Any fails the check.
    /// </summary>
    public List<GasOrphanView> Orphans { get; }

    /// <summary>Such networks that were there before the job: not its doing, left as they are.</summary>
    public List<GasOrphanView> OldOrphans { get; }

    internal static GasCheckView Of(GasAudit audit, List<GasRefill> recovered, List<long> ghostsCleared,
        GasOrphans? orphans = null, List<GasRefillWithheld>? withheld = null)
    {
        orphans ??= GasOrphans.None;
        withheld ??= new List<GasRefillWithheld>();
        List<GasFamilyView> families = new List<GasFamilyView>(audit.Families.Count);
        foreach (GasFamily family in audit.Families)
        {
            families.Add(GasFamilyView.Of(family));
        }

        List<GasGhostView> ghosts = new List<GasGhostView>(audit.Ghosts.Count);
        foreach (GasNetworkGhost ghost in audit.Ghosts)
        {
            ghosts.Add(GasGhostView.Of(ghost.Network, ghost.Family));
        }

        List<GasGhostView> oldGhosts = new List<GasGhostView>(audit.OldGhosts.Count);
        foreach (NetworkGas ghost in audit.OldGhosts)
        {
            oldGhosts.Add(GasGhostView.Of(ghost, null));
        }

        List<GasRefillView> refills = new List<GasRefillView>(recovered.Count);
        foreach (GasRefill refill in recovered)
        {
            refills.Add(new GasRefillView(new ThingId(refill.Into), refill.Gas.TotalMol, refill.Gas.TotalEnergyJ));
        }

        List<ThingId> cleared = ghostsCleared.ConvertAll(static id => new ThingId(id));
        return new GasCheckView(true, audit.Ok && orphans.Ok, Summarise(audit, recovered, orphans, withheld), families,
            ghosts, refills, cleared, oldGhosts, orphans.Left.ConvertAll(GasOrphanView.Of),
            orphans.Old.ConvertAll(GasOrphanView.Of), withheld.ConvertAll(GasWithheldView.Of));
    }

    /// <summary>
    /// A finished job's status with its gas check: gas_lost when the check failed, whatever the rest found;
    /// applied_with_differences for an otherwise clean job whose lost gas had to be put back.
    /// </summary>
    internal static string JobStatus(string status, GasCheckView? check) =>
        check == null || !check.Checked ? status
        : !check.Ok ? GasLostStatus
        : check.Recovered.Count > 0 && status == "applied" ? "applied_with_differences"
        : status;

    internal const string GasLostStatus = "gas_lost";

    /// <summary>
    /// The loss this failed check records for the gas hold (GasHold): the networks of every family that is not whole
    /// (its networks now, or before when none are left), every ghost and orphan it left, and what those families lack.
    /// </summary>
    internal GasLoss Loss(string jobId)
    {
        SortedSet<long> networks = new SortedSet<long>();
        double missing = 0.0;
        foreach (GasFamilyView family in Families)
        {
            if (family.Ok)
            {
                continue;
            }

            missing += family.MissingMol;
            List<ThingId> ids = family.NetworksAfter.Count > 0 ? family.NetworksAfter : family.NetworksBefore;
            ids.ForEach(id => networks.Add(id.Value));
        }

        Ghosts.ForEach(ghost => networks.Add(ghost.NetworkId.Value));
        Orphans.ForEach(orphan => networks.Add(orphan.NetworkId.Value));
        return new GasLoss(jobId, new List<long>(networks), missing, Summary);
    }

    internal static GasCheckView Unchecked(string reason) =>
        new GasCheckView(false, false, reason, new List<GasFamilyView>(), new List<GasGhostView>(),
            new List<GasRefillView>(), new List<ThingId>(), new List<GasGhostView>(), new List<GasOrphanView>(),
            new List<GasOrphanView>(), new List<GasWithheldView>());

    private static string Summarise(GasAudit audit, List<GasRefill> recovered, GasOrphans orphans,
        List<GasRefillWithheld> withheld)
    {
        double kept = 0.0;
        double deleted = 0.0;
        double planned = 0.0;
        double release = 0.0;
        double missing = 0.0;
        foreach (GasFamily family in audit.Families)
        {
            if (family.Emptied)
            {
                deleted += family.GasBefore.TotalMol;
            }
            else
            {
                kept += family.GasAfter.TotalMol;
                planned += family.PlannedLoss.TotalMol;
                release += family.PlannedRelease.TotalMol;
                missing += family.MissingMol;
            }
        }

        double put = 0.0;
        recovered.ForEach(refill => put += refill.Gas.TotalMol);
        double orphaned = 0.0;
        orphans.Left.ForEach(orphan => orphaned += orphan.Gas.TotalMol);
        string orphanText = orphans.Ok
            ? string.Empty
            : $"; {orphans.Left.Count} network(s) the game no longer lists still hold pipes and {Mol(orphaned)} mol " +
              "(orphans: nothing simulates them)";
        string text = audit.Ok && orphans.Ok
            ? $"Contents kept: {audit.Families.Count} network famil{(audit.Families.Count == 1 ? "y" : "ies")} " +
              $"changed, {Mol(kept)} mol in them now."
            : audit.Ok
            ? $"GAS CHECK FAILED{orphanText}." + HoldText
            : (missing >= 0.0
                  ? $"GAS LOST: {Mol(missing)} mol missing from the networks the job changed"
                  : $"GAS CHECK FAILED: {Mol(-missing)} mol more in the networks the job changed than before") +
              (audit.Ghosts.Count > 0 ? $"; {audit.Ghosts.Count} network(s) without pipes hold gas (ghosts)" : "") +
              orphanText +
              "." + HoldText;
        if (put > 0.0)
        {
            text += $" {Mol(put)} mol the game's merge had lost was put back.";
        }

        foreach (GasRefillWithheld held in withheld)
        {
            text += $" {Mol(held.Lacking.TotalMol)} mol was not put back: it would take pipe network {held.Network} " +
                    $"to {Kpa(held.PressureAfterKpa)} kPa, over its weakest pipe (rated {Kpa(held.RatingKpa)} kPa).";
        }

        if (planned > 0.0)
        {
            text += $" {Mol(planned)} mol went with the parts of split networks the job removed, as its plan said " +
                    "(allow_contents).";
        }

        if (release > 0.0)
        {
            text += $" Up to {Mol(release)} mol may leak out once the networks the plan leaves over their weakest " +
                    "pipe burst (allow_burst, will_burst); that release is expected and not put back.";
        }

        if (deleted > 0.0)
        {
            text += $" {Mol(deleted)} mol went with the last pipes of networks the job removed.";
        }

        return text;
    }

    private const string HoldText =
        " Further pipe jobs are refused until the world is loaded again, or until a run acknowledges this loss " +
        "(acknowledge_gas_lost with this job's id, only after the user has agreed).";

    private static string Mol(double moles) => moles.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Kpa(double kpa) => kpa.ToString("0.#", CultureInfo.InvariantCulture);
}

/// <summary>One family of networks: which networks it was before and is now, and what they held.</summary>
internal sealed class GasFamilyView
{
    private GasFamilyView(List<ThingId> networksBefore, List<ThingId> networksAfter, double molBefore,
        double molAfter, double energyBeforeJ, double energyAfterJ, double plannedLossMol, double plannedReleaseMol,
        bool emptied, bool ok)
    {
        NetworksBefore = networksBefore;
        NetworksAfter = networksAfter;
        MolBefore = molBefore;
        MolAfter = molAfter;
        EnergyBeforeJ = energyBeforeJ;
        EnergyAfterJ = energyAfterJ;
        PlannedLossMol = plannedLossMol;
        PlannedReleaseMol = plannedReleaseMol;
        MissingMol = emptied ? 0.0 : molBefore - plannedLossMol - molAfter;
        Emptied = emptied;
        Ok = ok;
    }

    public List<ThingId> NetworksBefore { get; }

    public List<ThingId> NetworksAfter { get; }

    public double MolBefore { get; }

    public double MolAfter { get; }

    public double EnergyBeforeJ { get; }

    public double EnergyAfterJ { get; }

    /// <summary>
    /// What the job's plan deleted on purpose (remove_structure with allow_contents: a part of a split network that
    /// lost its last member with its share), expected gone and not put back (1.4.4+).
    /// </summary>
    public double PlannedLossMol { get; }

    /// <summary>
    /// What a burst the plan let happen may let out (remove_structure with allow_burst, will_burst): the burst comes on
    /// a later tick, so the family is ok holding anything from its expected contents down to this much less; never put
    /// back.
    /// </summary>
    public double PlannedReleaseMol { get; }

    /// <summary>Before, less the planned loss, minus after; negative when gas appeared. Zero for an emptied family.</summary>
    public double MissingMol { get; }

    /// <summary>Every pipe of the family was removed: its contents went with the last one, as the game deletes them.</summary>
    public bool Emptied { get; }

    public bool Ok { get; }

    internal static GasFamilyView Of(GasFamily family) =>
        new GasFamilyView(Ids(family.Before), Ids(family.After), family.GasBefore.TotalMol, family.GasAfter.TotalMol,
            family.GasBefore.TotalEnergyJ, family.GasAfter.TotalEnergyJ, family.PlannedLoss.TotalMol,
            family.PlannedRelease.TotalMol, family.Emptied, family.Emptied || family.Conserved);

    internal static List<ThingId> Ids(List<NetworkGas> networks) =>
        networks.ConvertAll(static network => new ThingId(network.Id));
}

/// <summary>
/// A network with no pipes that holds gas (the game keeps it while a gas change for it is queued). Devices can still
/// be registered on it, so their ports read it instead of the pipes they sit on.
/// </summary>
internal sealed class GasGhostView
{
    private GasGhostView(ThingId networkId, double mol, double energyJ, List<ThingId> devices,
        List<ThingId>? familyNetworks)
    {
        NetworkId = networkId;
        Mol = mol;
        EnergyJ = energyJ;
        Devices = devices;
        FamilyNetworks = familyNetworks;
    }

    public ThingId NetworkId { get; }

    public double Mol { get; }

    public double EnergyJ { get; }

    /// <summary>Devices still registered on it.</summary>
    public List<ThingId> Devices { get; }

    /// <summary>The live networks of the family whose pipes it held; null when it held none before the job.</summary>
    public List<ThingId>? FamilyNetworks { get; }

    internal static GasGhostView Of(NetworkGas network, GasFamily? family)
    {
        List<ThingId> devices = new List<ThingId>(network.Devices.Count);
        foreach (long device in network.Devices)
        {
            devices.Add(new ThingId(device));
        }

        return new GasGhostView(new ThingId(network.Id), network.Gas.TotalMol, network.Gas.TotalEnergyJ, devices,
            family != null ? GasFamilyView.Ids(family.After) : null);
    }
}

/// <summary>A network the game no longer lists that pipes still name (an orphan), with what it holds.</summary>
internal sealed class GasOrphanView
{
    private GasOrphanView(ThingId networkId, int pipes, double mol, double energyJ, double volumeL)
    {
        NetworkId = networkId;
        Pipes = pipes;
        Mol = mol;
        EnergyJ = energyJ;
        VolumeL = volumeL;
    }

    public ThingId NetworkId { get; }

    /// <summary>How many pipes still name it.</summary>
    public int Pipes { get; }

    public double Mol { get; }

    public double EnergyJ { get; }

    public double VolumeL { get; }

    internal static GasOrphanView Of(NetworkGas network) =>
        new GasOrphanView(new ThingId(network.Id), network.Members.Count, network.Gas.TotalMol,
            network.Gas.TotalEnergyJ, network.VolumeL);
}

/// <summary>Gas a family lacks that was not put back: the network it would take over its weakest pipe, and how far.</summary>
internal sealed class GasWithheldView
{
    private GasWithheldView(ThingId networkId, List<ThingId> familyNetworks, double mol, double pressureAfterKpa,
        double ratingKpa)
    {
        NetworkId = networkId;
        FamilyNetworks = familyNetworks;
        Mol = mol;
        PressureAfterKpa = pressureAfterKpa;
        RatingKpa = ratingKpa;
    }

    public ThingId NetworkId { get; }

    /// <summary>The family's live networks, which stay short by Mol.</summary>
    public List<ThingId> FamilyNetworks { get; }

    public double Mol { get; }

    /// <summary>The pressure the refill would have left in NetworkId.</summary>
    public double PressureAfterKpa { get; }

    /// <summary>The lowest MaxPressure of its pipes.</summary>
    public double RatingKpa { get; }

    internal static GasWithheldView Of(GasRefillWithheld held) =>
        new GasWithheldView(new ThingId(held.Network), GasFamilyView.Ids(held.Family.After), held.Lacking.TotalMol,
            held.PressureAfterKpa, held.RatingKpa);
}

/// <summary>Gas put into a network to make its family whole.</summary>
internal sealed class GasRefillView
{
    internal GasRefillView(ThingId into, double mol, double energyJ)
    {
        Into = into;
        Mol = mol;
        EnergyJ = energyJ;
    }

    public ThingId Into { get; }

    public double Mol { get; }

    public double EnergyJ { get; }
}

/// <summary>
/// The gas hold as it bears on one run (a reply's gas_hold): status none, not_applicable, held, mismatch or
/// acknowledged; whether it applies to the run; whether this reply lifted it; the loss holding pipe jobs; and a note
/// that says what a dry run would do, or repeats the loss a real run acknowledged.
/// </summary>
internal sealed class GasHoldView
{
    private GasHoldView(string status, bool applies, bool lifted, string? heldByJobId, GasLossView? loss, string note)
    {
        Status = status;
        Applies = applies;
        Lifted = lifted;
        HeldByJobId = heldByJobId;
        Loss = loss;
        Note = note;
    }

    public string Status { get; }

    /// <summary>The hold stops this run unless it is acknowledged with HeldByJobId.</summary>
    public bool Applies { get; }

    /// <summary>This run acknowledged the loss and lifted the hold.</summary>
    public bool Lifted { get; }

    /// <summary>The job whose failed gas check holds pipe jobs; the id acknowledge_gas_lost must name.</summary>
    public string? HeldByJobId { get; }

    public GasLossView? Loss { get; }

    public string Note { get; }

    internal static GasHoldView Of(GasHoldVerdict verdict, GasHoldStage stage) =>
        new GasHoldView(verdict.Status, verdict.Applies,
            verdict is GasHoldVerdict.Lifting && stage == GasHoldStage.Started, verdict.Hold?.JobId,
            verdict.Hold != null ? GasLossView.Of(verdict.Hold) : null, verdict.Note(stage));
}

/// <summary>The loss holding pipe jobs: the job, its networks, the missing moles and its gas check's summary.</summary>
internal sealed class GasLossView
{
    private GasLossView(string jobId, List<ThingId> networks, double missingMol, string summary)
    {
        JobId = jobId;
        Networks = networks;
        MissingMol = missingMol;
        Summary = summary;
    }

    public string JobId { get; }

    public List<ThingId> Networks { get; }

    public double MissingMol { get; }

    public string Summary { get; }

    internal static GasLossView Of(GasLoss loss) =>
        new GasLossView(loss.JobId, loss.Networks.ConvertAll(static id => new ThingId(id)), loss.MissingMol,
            loss.Summary);
}
