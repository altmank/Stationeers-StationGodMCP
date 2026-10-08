#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.Networking;
using StationGodMCP.Api.Shared;
using StationGodMCP.Api.Shared.Game;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api;

/// <summary>
/// vault_transfer: move stored amounts from one Ingot Vault's store straight into another's, as the vaults' own
/// bookkeeping: an ingot line's reagent grams off one ReagentMixture onto the other, an ore or ice line's count off one
/// _storedOresAndIces onto the other. No item is made, no slot, chute, export or locker is involved, so nothing sits
/// in an atmosphere on the way (ices cannot melt). Writes; host only; needs IngotVault.
///
/// The vault's own sides (CODE, IngotVaultFix StructureIngotVault): TryVendSelected takes a line off the store and an
/// ore line flags MarkVaultStateChanged (NetworkUpdateFlags 256); CollectResource/ResourceImport.Store adds an ingot's
/// reagents to the ReagentMixture (no flag) or an ore's count to the dictionary (flag 256), then UpdateDisplay, which
/// also resizes the logic slots. VaultStock.Take and Add do the same on each side, and both vaults are refreshed
/// through PreviousContentsShow (UpdateDisplay). Nothing in the game reads flag 256 for a DeviceImportExport (only
/// Structure's 64 is built into its updates), and a joined client receives the store only in SerializeOnJoin
/// (Thing writes the ReagentMixture, the vault its ore dictionary): a transfer reaches clients exactly as the vault's
/// own imports and vends do. An ice's gas is not kept by the store (the import counts the stack and destroys it), so a
/// transfer moves counts only and loses nothing more.
/// </summary>
internal static class VaultTransferApi
{
    internal static VaultTransferView Handle(Args args)
    {
        if (NetworkManager.IsClient || !GameManager.RunSimulation)
        {
            throw ApiErrors.Refused("not_host", "This game is a multiplayer client; only the host moves items.");
        }

        IngotVaults.RequireLoaded();
        VaultTransferRequest request = VaultTransferRequest.Of(args, WriteMode.IsDryRun(args));
        VaultTarget from = VaultTarget.Resolve(request.From);
        VaultTarget to = VaultTarget.Resolve(request.To);
        if (from.Vault == to.Vault)
        {
            throw ApiErrors.Refused("same_vault",
                $"{request.From} and {request.To} both reach {Names.Of(from.Vault)} ({from.Vault.ReferenceId}); " +
                "nothing to move.");
        }

        from.RequirePowered();
        to.RequirePowered();
        VaultStore source = from.Store();
        VaultStore target = to.Store();
        List<VaultStock> stocks = source.Lines();
        List<TransferOutcome> outcomes = VaultTransferRule.Plan(LinesOf(stocks, source, target), request.Asks);
        BatchBuilder batch = new BatchBuilder(outcomes.Count);
        int partial = 0;
        bool moved = false;
        foreach (TransferOutcome outcome in outcomes)
        {
            switch (outcome)
            {
                case TransferOutcome.Move move:
                    VaultStock stock = stocks[move.Line];
                    batch.Succeeded(request.DryRun
                        ? View(move, stock, move.SourceAfter, move.TargetBefore, move.TargetAfter)
                        : Moved(move, stock, source, target));
                    moved = true;
                    partial += move.Partial ? 1 : 0;
                    break;
                case TransferOutcome.Refusal refusal:
                    batch.Failed(new NotTransferredView(refusal.Index, refusal.Asked,
                        new ErrorView(refusal.Code, refusal.Message)));
                    break;
            }
        }

        if (!request.DryRun && moved)
        {
            source.Refresh();
            target.Refresh();
        }

        return new VaultTransferView(request.DryRun, from.View(), to.View(), batch.Build(), partial);
    }

    private static List<TransferLine> LinesOf(List<VaultStock> stocks, VaultStore source, VaultStore target)
    {
        List<TransferLine> lines = new List<TransferLine>(stocks.Count);
        foreach (VaultStock stock in stocks)
        {
            VaultItemView view = stock.View();
            string? reagentDisplay = stock is VaultStock.IngotStock ingot ? ingot.Reagent.DisplayName : null;
            lines.Add(new TransferLine(view.PrefabName, stock.PrefabHash, view.Reagent, reagentDisplay,
                stock.CountsWhole, stock.AmountIn(source), stock.AmountIn(target)));
        }

        return lines;
    }

    // A real run: take off the source as a vend does, add to the target as an import does, read both back.
    private static TransferredView Moved(TransferOutcome.Move move, VaultStock stock, VaultStore source,
        VaultStore target)
    {
        double targetBefore = stock.AmountIn(target);
        stock.Take(source, move.Quantity);
        try
        {
            stock.Add(target, move.Quantity);
        }
        catch (System.Exception)
        {
            stock.Add(source, move.Quantity);
            throw;
        }

        return View(move, stock, stock.AmountIn(source), targetBefore, stock.AmountIn(target));
    }

    private static TransferredView View(TransferOutcome.Move move, VaultStock stock, double sourceAfter,
        double targetBefore, double targetAfter) =>
        new TransferredView(move.Index, stock.View(), move.Requested, move.Quantity, move.Partial,
            new TransferSideView(move.SourceBefore, sourceAfter), new TransferSideView(targetBefore, targetAfter));
}
