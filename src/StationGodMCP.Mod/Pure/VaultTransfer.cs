#nullable enable

using System;
using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// One stored line of the vault a transfer takes from: how it is named (an ingot by its reagent and the ingot a vend
/// makes, an ore or ice by its prefab), whether it counts whole items, how much the source holds and how much the
/// target holds of it.
/// </summary>
internal sealed class TransferLine
{
    internal TransferLine(string? prefabName, int prefabHash, string? reagent, string? reagentDisplay, bool countsWhole,
        double amount, double targetAmount)
    {
        TargetAmount = targetAmount;
        PrefabName = prefabName;
        PrefabHash = prefabHash;
        Reagent = reagent;
        ReagentDisplay = reagentDisplay;
        CountsWhole = countsWhole;
        Amount = amount;
    }

    internal string? PrefabName { get; }

    internal int PrefabHash { get; }

    /// <summary>The stored reagent's type name for an ingot line; null for an ore or ice.</summary>
    internal string? Reagent { get; }

    internal string? ReagentDisplay { get; }

    internal bool CountsWhole { get; }

    internal double Amount { get; }

    /// <summary>What the target vault holds of the same line.</summary>
    internal double TargetAmount { get; }

    /// <summary>The name a refusal lists it by.</summary>
    internal string Shown => PrefabName ?? Reagent ?? PrefabHash.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>One entry of a transfer's items: exactly one of prefab name, prefab hash or reagent, and an amount (null for all).</summary>
internal sealed class TransferAsk
{
    internal TransferAsk(string? prefabName, int? prefabHash, string? reagent, double? quantity)
    {
        PrefabName = prefabName;
        PrefabHash = prefabHash;
        Reagent = reagent;
        Quantity = quantity;
    }

    internal string? PrefabName { get; }

    internal int? PrefabHash { get; }

    internal string? Reagent { get; }

    internal double? Quantity { get; }

    /// <summary>The thing as the request named it.</summary>
    internal string Asked =>
        PrefabName ?? Reagent ?? PrefabHash!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    internal bool Matches(TransferLine line)
    {
        if (PrefabHash.HasValue)
        {
            return line.PrefabHash == PrefabHash.Value;
        }

        if (PrefabName != null)
        {
            return string.Equals(line.PrefabName, PrefabName, StringComparison.OrdinalIgnoreCase);
        }

        return line.Reagent != null &&
               (string.Equals(line.Reagent, Reagent, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(line.ReagentDisplay, Reagent, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>What a transfer does with one entry: a move off one stored line, or a refusal that changes nothing.</summary>
internal abstract class TransferOutcome
{
    private TransferOutcome(int index)
    {
        Index = index;
    }

    /// <summary>The entry's place in items (or in the source's lines, when items was not given).</summary>
    internal int Index { get; }

    internal sealed class Move : TransferOutcome
    {
        internal Move(int index, int line, double? requested, double quantity, double sourceBefore, double targetBefore)
            : base(index)
        {
            TargetBefore = targetBefore;
            Line = line;
            Requested = requested;
            Quantity = quantity;
            SourceBefore = sourceBefore;
        }

        /// <summary>The source line it moves.</summary>
        internal int Line { get; }

        /// <summary>The amount asked for; null for everything.</summary>
        internal double? Requested { get; }

        internal double Quantity { get; }

        /// <summary>What the source line held before this entry (after the entries before it).</summary>
        internal double SourceBefore { get; }

        internal double SourceAfter => SourceBefore - Quantity;

        /// <summary>What the target line held before this entry (after the entries before it).</summary>
        internal double TargetBefore { get; }

        internal double TargetAfter => TargetBefore + Quantity;

        /// <summary>Asked for more than the source still held, so only what it held moves.</summary>
        internal bool Partial => Requested.HasValue && Requested.Value > Quantity + VaultTransferRule.Trace;
    }

    internal sealed class Refusal : TransferOutcome
    {
        internal Refusal(int index, string asked, string code, string message) : base(index)
        {
            Asked = asked;
            Code = code;
            Message = message;
        }

        internal string Asked { get; }

        internal string Code { get; }

        internal string Message { get; }
    }
}

/// <summary>
/// Which stored amounts a vault-to-vault transfer moves. An Ingot Vault keeps no capacity (IngotVault's
/// StructureIngotVault and ResourceImport.Store add to its reagent mixture and ore dictionary without a limit), and
/// the target takes every line the source can hold, so nothing is refused for room: an entry is refused only when the
/// source does not hold it (not_in_vault), holds none of it any more (not_enough_stock, after earlier entries), or
/// names part of an ore or ice (invalid_argument: they count whole items). Asking for more than the source holds moves
/// what it holds, reported partial. Entries naming the same line take from what the earlier ones left.
/// </summary>
internal static class VaultTransferRule
{
    /// <summary>Amounts at or below this are rounding (VaultStore.Trace).</summary>
    internal const double Trace = 1e-6;

    /// <summary>The moves and refusals for asks over the source's lines; asks null moves every line whole.</summary>
    internal static List<TransferOutcome> Plan(List<TransferLine> lines, List<TransferAsk>? asks)
    {
        double[] left = new double[lines.Count];
        double[] gained = new double[lines.Count];
        for (int index = 0; index < lines.Count; index++)
        {
            left[index] = lines[index].Amount;
            gained[index] = lines[index].TargetAmount;
        }

        List<TransferOutcome> outcomes = new List<TransferOutcome>();
        if (asks == null)
        {
            for (int index = 0; index < lines.Count; index++)
            {
                if (left[index] > Trace)
                {
                    outcomes.Add(new TransferOutcome.Move(outcomes.Count, index, null, left[index], left[index],
                        gained[index]));
                }
            }

            return outcomes;
        }

        for (int index = 0; index < asks.Count; index++)
        {
            outcomes.Add(Weigh(index, asks[index], lines, left, gained));
        }

        return outcomes;
    }

    private static TransferOutcome Weigh(int index, TransferAsk ask, List<TransferLine> lines, double[] left,
        double[] gained)
    {
        int line = lines.FindIndex(ask.Matches);
        if (line < 0)
        {
            return new TransferOutcome.Refusal(index, ask.Asked, "not_in_vault",
                $"The source vault holds no {ask.Asked}; it holds {Listed(lines)}.");
        }

        if (lines[line].CountsWhole && ask.Quantity.HasValue &&
            Math.Abs(ask.Quantity.Value - Math.Round(ask.Quantity.Value)) > Trace)
        {
            return new TransferOutcome.Refusal(index, ask.Asked, "invalid_argument",
                $"{lines[line].Shown} counts whole items; quantity {ask.Quantity.Value} is not whole.");
        }

        double held = left[line];
        if (held <= Trace)
        {
            return new TransferOutcome.Refusal(index, ask.Asked, "not_enough_stock",
                $"The source vault has no {lines[line].Shown} left to move: earlier entries took it all.");
        }

        double quantity = ask.Quantity.HasValue && ask.Quantity.Value < held + Trace
            ? Math.Min(ask.Quantity.Value, held)
            : held;
        double targetBefore = gained[line];
        left[line] = held - quantity;
        gained[line] = targetBefore + quantity;
        return new TransferOutcome.Move(index, line, ask.Quantity, quantity, held, targetBefore);
    }

    private static string Listed(List<TransferLine> lines)
    {
        List<string> names = new List<string>(lines.Count);
        foreach (TransferLine line in lines)
        {
            if (line.Amount > Trace)
            {
                names.Add(line.Shown);
            }
        }

        return names.Count > 0 ? string.Join(", ", names) : "nothing";
    }
}
