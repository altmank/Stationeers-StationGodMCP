#nullable enable

using System.Collections.Generic;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;
using Xunit;

namespace StationGodMCP.Tests;

/// <summary>
/// Fixes from round 4 of the 1.4.4 live test of the structure tools (2026-09-29). structures-34: remove_structure with
/// allow_contents on a split removal (F: chain 641-644-[tank 638]-645-646, 690 mol N2) deleted exactly the tank's share
/// its dry run forecast (564.545 mol), and the job's gas check then took that for a merge loss and put it back into
/// the two 10 L pipes left: 345 mol each, 84 MPa against a 60.8 MPa rating, both burst. The check now expects the
/// planned deletion, and never refills a network past its weakest pipe. structures-35/36: the frame a piece stands on
/// is looked for in the cell the game looks in.
/// </summary>
public sealed class StructuresRound4Tests
{
    private const double Rating = 60795.0;

    private static GasMix N2(double mol) => new GasMix(new[] { mol }, new[] { mol * 6000.0 });

    private static NetworkGas Net(long id, double mol, double volumeL, params long[] members) =>
        new NetworkGas(id, N2(mol), volumeL, members, new long[0], null, Rating);

    // Ideal gas at 293 K: kPa from moles and litres.
    private static double IdealKpa(NetworkGas network, GasMix contents) =>
        contents.TotalMol * 8.314462618 * 293.0 / network.VolumeL;

    private static List<NetworkGas> Before() => new List<NetworkGas> { Net(639, 690.0, 140.0, 641, 644, 638, 645, 646) };

    // What the game left: the pipe at each end on its own network, each with its forecast share.
    private static List<NetworkGas> After(double eachMol) =>
        new List<NetworkGas> { Net(931, eachMol, 10.0, 641), Net(933, eachMol, 10.0, 646) };

    private static readonly List<PlannedGasLoss> Planned =
        new List<PlannedGasLoss> { PlannedGasLoss.Of(639, 564.545, 690.0) };

    private static GasCheckView ViewOf(GasAudit audit, GasRefillPlan plan) =>
        GasCheckView.Of(audit, plan.Refills, new List<long>(),
            GasOrphans.Of(new List<NetworkGas>(), new List<NetworkGas>()), plan.Withheld);

    [Fact]
    public void ThePlannedDeletionIsExpectedAndNotPutBack()
    {
        GasAudit audit = GasAudit.Of(Before(), After(62.7275), GasTolerance.Default, Planned);
        GasRefillPlan plan = GasRefills.Plan(audit, GasTolerance.Default, IdealKpa);
        GasCheckView check = ViewOf(audit, plan);

        Assert.True(audit.Ok);
        GasFamily family = Assert.Single(audit.Families);
        Assert.True(family.Conserved);
        Assert.Equal(564.545, family.PlannedLoss.TotalMol, 6);
        Assert.Equal(0.0, family.MissingMol, 6);
        Assert.Empty(plan.Refills);
        Assert.Empty(plan.Withheld);
        Assert.Equal(564.545, check.Families[0].PlannedLossMol, 6);
        Assert.Equal(0.0, check.Families[0].MissingMol, 6);
        Assert.Equal("applied", GasCheckView.JobStatus("applied", check));
        Assert.Contains("564.545 mol went with the parts of split networks", check.Summary);
    }

    [Fact]
    public void ATrueLossBeyondThePlanIsStillPutBack()
    {
        // 10 mol more than the plan deleted went missing: that, and only that, is put back.
        GasAudit audit = GasAudit.Of(Before(), After(57.7275), GasTolerance.Default, Planned);
        GasRefillPlan plan = GasRefills.Plan(audit, GasTolerance.Default, IdealKpa);

        Assert.False(audit.Ok);
        Assert.Equal(10.0, Assert.Single(audit.Families).MissingMol, 6);
        Assert.Equal(2, plan.Refills.Count);
        Assert.All(plan.Refills, refill => Assert.Equal(5.0, refill.Gas.TotalMol, 6));
        Assert.Empty(plan.Withheld);
    }

    [Fact]
    public void ARefillNeverTakesANetworkOverItsWeakestPipe()
    {
        // The live failure without the plan: 564.545 mol short, put back by volume it would be 345 mol in each 10 L
        // pipe, about 84 MPa against 60.8 MPa. Nothing is put back; the family stays short and the job is gas_lost.
        GasAudit audit = GasAudit.Of(Before(), After(62.7275), GasTolerance.Default);
        GasRefillPlan plan = GasRefills.Plan(audit, GasTolerance.Default, IdealKpa);
        GasCheckView check = ViewOf(audit, plan);

        Assert.False(audit.Ok);
        Assert.Empty(plan.Refills);
        GasRefillWithheld held = Assert.Single(plan.Withheld);
        Assert.Equal(564.545, held.Lacking.TotalMol, 6);
        Assert.Equal(Rating, held.RatingKpa);
        Assert.True(held.PressureAfterKpa > Rating);
        Assert.Equal(GasCheckView.GasLostStatus, GasCheckView.JobStatus("applied", check));
        GasWithheldView view = Assert.Single(check.Withheld);
        Assert.Equal(564.545, view.Mol, 6);
        Assert.Contains("was not put back", check.Summary);
    }

    [Fact]
    public void ARefillWithinTheRatingIsMade()
    {
        // 30 mol lost from 50: 25 mol in each 10 L pipe is about 6 MPa, well under the rating.
        List<NetworkGas> before = new List<NetworkGas> { Net(700, 50.0, 30.0, 701, 702, 703) };
        List<NetworkGas> after = new List<NetworkGas> { Net(710, 10.0, 10.0, 701), Net(711, 10.0, 10.0, 703) };
        GasAudit audit = GasAudit.Of(before, after, GasTolerance.Default);
        GasRefillPlan plan = GasRefills.Plan(audit, GasTolerance.Default, IdealKpa);

        Assert.Empty(plan.Withheld);
        Assert.Equal(2, plan.Refills.Count);
        Assert.All(plan.Refills, refill => Assert.Equal(15.0, refill.Gas.TotalMol, 6));
    }

    [Fact]
    public void AnUnratedNetworkIsRefilledAsBefore()
    {
        List<NetworkGas> before = new List<NetworkGas> { new NetworkGas(639, N2(690.0), 140.0, new long[] { 641, 646 }, new long[0]) };
        List<NetworkGas> after = new List<NetworkGas> { new NetworkGas(931, N2(10.0), 10.0, new long[] { 641, 646 }, new long[0]) };
        GasAudit audit = GasAudit.Of(before, after, GasTolerance.Default);

        GasRefill refill = Assert.Single(GasRefills.For(audit, GasTolerance.Default));
        Assert.Equal(680.0, refill.Gas.TotalMol, 6);
    }

    [Fact]
    public void ThePlannerModelAndTheCheckAgreeOnASplitThatDeletesATanksShare()
    {
        // Tank 660 between pipes 663 and 664; the pipes beside it go first, so the tank is left alone with its share
        // and deletes it. The parts the model leaves are what the check reads after: nothing to put back.
        List<TakedownMember> members = new List<TakedownMember>
        {
            new TakedownMember(662, 10.0, Rating), new TakedownMember(663, 10.0, Rating),
            new TakedownMember(660, 100.0, Rating), new TakedownMember(664, 10.0, Rating),
            new TakedownMember(665, 10.0, Rating)
        };
        List<Link> links = new List<Link>();
        foreach ((long a, long b) in new[] { (662L, 663L), (663L, 660L), (660L, 664L), (664L, 665L) })
        {
            links.Add(new Link(a, b));
            links.Add(new Link(b, a));
        }

        TakedownOutcome outcome = PipeTakedown.Run(members, links, new long[] { 663, 664, 660 }, false, 690.0);
        Assert.Equal(2, outcome.Parts.Count);
        Assert.True(outcome.LostMol > 400.0);

        List<NetworkGas> after = new List<NetworkGas>();
        long id = 900;
        foreach (TakedownPart part in outcome.Parts)
        {
            after.Add(Net(id++, part.Moles, part.VolumeL, part.Members.ToArray()));
        }

        GasAudit audit = GasAudit.Of(new List<NetworkGas> { Net(661, 690.0, 140.0, 662, 663, 660, 664, 665) },
            after, GasTolerance.Default,
            new List<PlannedGasLoss> { PlannedGasLoss.Of(661, outcome.LostMol, outcome.MolesBefore) });

        Assert.True(audit.Ok);
        Assert.Empty(GasRefills.Plan(audit, GasTolerance.Default, IdealKpa).Refills);
    }

    // structures-35: a landing pad part stands with its origin 2 m above the frame's centre (grid size 2) and the game
    // looks a whole grid size below it (LandingPadModular.CanConstruct); half of it lands on the plane between the two
    // cells, which is the pad's own cell.
    [Fact]
    public void APadPartLooksForItsFrameAWholeGridSizeBelow()
    {
        Vec3 up = new Vec3(0, 1, 0);
        GridCell frame = LargeCells.Containing(new Vec3(-1059, 231, -715));

        Assert.Equal(frame, LargeCells.Below(new Vec3(-1059, 233, -715), up, 2.0));
        Assert.NotEqual(frame, LargeCells.Below(new Vec3(-1059, 233, -715), up, 1.0));
    }

    // A station battery (grid size 2) stands on the frame's top plane and the game looks half its grid size below.
    [Fact]
    public void ABatteryLooksForItsFrameHalfAGridSizeBelow()
    {
        Vec3 up = new Vec3(0, 1, 0);

        Assert.Equal(LargeCells.Containing(new Vec3(-1059, 231, -715)),
            LargeCells.Below(new Vec3(-1059, 232, -715), up, 1.0));
    }
}
