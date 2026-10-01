#nullable enable

using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure;

/// <summary>
/// The loss a failed gas check recorded, which holds every later pipe job: the job, the pipe networks it left wrong
/// (the failed families' networks, plus any orphan or ghost it left), how much is missing, and the check's summary.
/// </summary>
internal sealed class GasLoss
{
    internal GasLoss(string jobId, List<long> networks, double missingMol, string summary)
        : this(jobId, networks, missingMol, summary, false)
    {
    }

    private GasLoss(string jobId, List<long> networks, double missingMol, string summary, bool contentsUnchecked)
    {
        JobId = jobId;
        Networks = networks;
        MissingMol = missingMol;
        Summary = summary;
        ContentsUnchecked = contentsUnchecked;
    }

    /// <summary>
    /// A job whose atmosphere work overran its limit and finished after the job stopped: no check ran, so nothing is
    /// known missing (MissingMol 0) and no network is named; the hold is set because the contents are unknown.
    /// </summary>
    internal static GasLoss Unchecked(string jobId, string summary) =>
        new GasLoss(jobId, new List<long>(), 0.0, summary, true);

    internal string JobId { get; }

    internal List<long> Networks { get; }

    /// <summary>What the failed families lack; negative when gas appeared, zero when only orphans failed the check.</summary>
    internal double MissingMol { get; }

    internal string Summary { get; }

    /// <summary>Set by Unchecked: the job's pipe contents were never checked.</summary>
    internal bool ContentsUnchecked { get; }

    /// <summary>"job run-4 lost 12.5 mol from pipe network(s) 101, 102".</summary>
    internal string Describe()
    {
        if (ContentsUnchecked)
        {
            return $"job {JobId} left its pipe networks' contents unchecked";
        }

        string networks = Networks.Count == 0
            ? "its pipe networks"
            : "pipe network(s) " +
              string.Join(", ", Networks.ConvertAll(static id => id.ToString(CultureInfo.InvariantCulture)));
        string amount = MissingMol >= 0.0
            ? $"lost {Mol(MissingMol)} mol from {networks}"
            : $"left {Mol(-MissingMol)} mol too much in {networks}";
        return $"job {JobId} {amount}";
    }

    internal static string Mol(double moles) => moles.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>Where a verdict is reported: a dry run, a run that started, one queued behind another, one not started.</summary>
internal enum GasHoldStage
{
    DryRun,
    Started,
    Queued,
    NotStarted
}

/// <summary>
/// What the gas hold means for one run (GasHoldRule.Judge): no hold, a hold that does not apply (the run touches no
/// pipe), a hold that refuses it (not acknowledged, or acknowledged with another job's id), or a hold the run lifts
/// because acknowledge_gas_lost names the job that set it.
/// </summary>
internal abstract class GasHoldVerdict
{
    internal const string AcknowledgeArgument = "acknowledge_gas_lost";

    private GasHoldVerdict(GasLoss? hold, string? given)
    {
        Hold = hold;
        Given = given;
    }

    /// <summary>The loss holding pipe jobs now, or null.</summary>
    internal GasLoss? Hold { get; }

    /// <summary>The acknowledge_gas_lost the run gave, or null.</summary>
    internal string? Given { get; }

    /// <summary>The verdict's wire name: none, not_applicable, held, mismatch or acknowledged.</summary>
    internal abstract string Status { get; }

    /// <summary>Whether the hold stops (or, acknowledged, would have stopped) this run.</summary>
    internal bool Applies => this is Refusing || this is Lifting;

    /// <summary>Whether a reply should say anything: the hold applies to the run, or an acknowledgement was given.</summary>
    internal bool Reportable => Applies || Given != null;

    internal abstract string Note(GasHoldStage stage);

    /// <summary>No hold is in place.</summary>
    internal sealed class Free : GasHoldVerdict
    {
        internal Free(string? given)
            : base(null, given)
        {
        }

        internal override string Status => "none";

        internal override string Note(GasHoldStage stage) => Given == null
            ? "No gas hold is in place."
            : $"{AcknowledgeArgument} \"{Given}\": no gas hold is in place, so there is nothing to lift.";
    }

    /// <summary>A hold is in place, but the run touches no pipe network: it runs, and the hold stays.</summary>
    internal sealed class Unaffected : GasHoldVerdict
    {
        internal Unaffected(GasLoss hold, string? given)
            : base(hold, given)
        {
        }

        internal override string Status => "not_applicable";

        internal override string Note(GasHoldStage stage) =>
            $"Pipe jobs are held ({Hold!.Describe()}), but this run touches no pipe network, so the hold does not " +
            "apply to it and stays" + (Given != null ? $"; {AcknowledgeArgument} was not used." : ".");
    }

    /// <summary>A verdict that refuses the run: its error code and message.</summary>
    internal abstract class Refusing : GasHoldVerdict
    {
        private protected Refusing(GasLoss hold, string? given)
            : base(hold, given)
        {
        }

        internal abstract string Code { get; }

        internal abstract string Message { get; }

        internal override string Note(GasHoldStage stage) => stage == GasHoldStage.DryRun
            ? $"A real run would be refused ({Code}): {Message}"
            : Message;
    }

    /// <summary>A hold is in place and the run does not acknowledge it.</summary>
    internal sealed class Held : Refusing
    {
        internal Held(GasLoss hold)
            : base(hold, null)
        {
        }

        internal override string Status => "held";

        internal override string Code => "gas_check_failed";

        internal override string Message =>
            $"Job {Hold!.JobId}'s gas check failed: {Hold.Summary} Poll that job with job_id for its gas_check. " +
            $"Pipe jobs stay refused until the world is loaded again, or until a run passes {AcknowledgeArgument}: " +
            $"\"{Hold.JobId}\" to accept the loss; ask the user before acknowledging it. Nothing was changed.";
    }

    /// <summary>A hold is in place and the run acknowledges another job's loss.</summary>
    internal sealed class Mismatch : Refusing
    {
        internal Mismatch(GasLoss hold, string given)
            : base(hold, given)
        {
        }

        internal override string Status => "mismatch";

        internal override string Code => "gas_hold_mismatch";

        internal override string Message =>
            $"{AcknowledgeArgument} names \"{Given}\", but pipe jobs are held by {Hold!.Describe()} " +
            $"({Hold.Summary}). Only {AcknowledgeArgument}: \"{Hold.JobId}\" lifts that hold, and only after the " +
            "user has agreed to accept that loss. Nothing was changed.";
    }

    /// <summary>A hold is in place and the run names the job that set it: the hold is lifted and the run goes on.</summary>
    internal sealed class Lifting : GasHoldVerdict
    {
        internal Lifting(GasLoss hold)
            : base(hold, hold.JobId)
        {
        }

        internal override string Status => "acknowledged";

        internal override string Note(GasHoldStage stage) => stage switch
        {
            GasHoldStage.DryRun =>
                $"Pipe jobs are held: {Hold!.Describe()}. A real run with {AcknowledgeArgument} \"{Hold.JobId}\" " +
                "would accept that loss, lift the hold and go on; a dry run lifts nothing.",
            GasHoldStage.Started =>
                $"GAS LOSS ACKNOWLEDGED: {Hold!.Describe()} ({Hold.Summary}). The hold on pipe jobs is lifted; a later " +
                "gas check that fails holds them again.",
            GasHoldStage.Queued =>
                $"Queued with {AcknowledgeArgument} \"{Hold!.JobId}\" ({Hold.Describe()}): the hold is lifted when " +
                "this run starts, if that job's loss still holds pipe jobs then; until then it stays.",
            _ =>
                $"Nothing was started, so the hold ({Hold!.Describe()}) was not lifted."
        };
    }
}

/// <summary>
/// The manual lift of the gas hold (LU 2026-09-29): after a pipe job ends gas_lost, every later pipe-touching job is
/// refused until the world is left, unless a run passes acknowledge_gas_lost with the id of the job that set the hold.
/// A run that touches no pipe is never held and lifts nothing; another job's id is refused, naming the hold's own.
/// </summary>
internal static class GasHoldRule
{
    /// <summary>
    /// Why an acknowledge_gas_lost as given is refused: an empty or blank id names no job and must not read as left
    /// out. Null when it is left out or names something.
    /// </summary>
    internal static string? BlankRefusal(string? acknowledge) =>
        acknowledge != null && string.IsNullOrWhiteSpace(acknowledge)
            ? $"{GasHoldVerdict.AcknowledgeArgument} is empty; pass the id of the job whose gas check failed " +
              "(run-N, upgrade-N, place-N, remove-N), or leave it out."
            : null;

    internal static GasHoldVerdict Judge(GasLoss? hold, bool touchesPipes, string? acknowledge)
    {
        string? given = string.IsNullOrWhiteSpace(acknowledge) ? null : acknowledge!.Trim();
        if (hold == null)
        {
            return new GasHoldVerdict.Free(given);
        }

        if (!touchesPipes)
        {
            return new GasHoldVerdict.Unaffected(hold, given);
        }

        if (given == null)
        {
            return new GasHoldVerdict.Held(hold);
        }

        return string.Equals(given, hold.JobId, System.StringComparison.OrdinalIgnoreCase)
            ? new GasHoldVerdict.Lifting(hold)
            : new GasHoldVerdict.Mismatch(hold, given);
    }
}
