#nullable enable

using System.Collections.Generic;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared;

/// <summary>
/// vault_transfer's arguments: the two vaults (each an Ingot Vault or a Remote Vault linked to one), the items to move
/// (null for everything the source holds), and the write mode.
/// </summary>
internal sealed class VaultTransferRequest
{
    internal const int MaximumItems = 256;

    private VaultTransferRequest(ThingId from, ThingId to, List<TransferAsk>? asks, bool dryRun)
    {
        From = from;
        To = to;
        Asks = asks;
        DryRun = dryRun;
    }

    internal ThingId From { get; }

    internal ThingId To { get; }

    /// <summary>The entries asked for; null moves everything.</summary>
    internal List<TransferAsk>? Asks { get; }

    internal bool DryRun { get; }

    /// <summary>The request; dryRun is WriteMode's answer for the same arguments.</summary>
    internal static VaultTransferRequest Of(Args args, bool dryRun)
    {
        ThingId from = args.ThingId("from_vault_id");
        ThingId to = args.ThingId("to_vault_id");
        if (from.Equals(to))
        {
            throw ApiErrors.Refused("same_vault", "from_vault_id and to_vault_id name the same vault; nothing to move.");
        }

        return new VaultTransferRequest(from, to, args.Has("items") ? AsksOf(args) : null, dryRun);
    }

    private static List<TransferAsk> AsksOf(Args args)
    {
        List<Args?> items = args.Objects("items", MaximumItems);
        if (items.Count == 0)
        {
            throw ApiErrors.InvalidArgument("Argument 'items' must name at least one thing; leave it out to move everything.");
        }

        List<TransferAsk> asks = new List<TransferAsk>(items.Count);
        for (int index = 0; index < items.Count; index++)
        {
            Args item = items[index] ?? throw ApiErrors.InvalidArgument($"items[{index}] must be an object.");
            string? prefabName = item.OptionalString("prefab_name")?.Trim();
            int? prefabHash = item.OptionalInt("prefab_hash", int.MinValue, int.MaxValue);
            string? reagent = item.OptionalString("reagent")?.Trim();
            int named = (string.IsNullOrEmpty(prefabName) ? 0 : 1) + (prefabHash.HasValue ? 1 : 0) +
                        (string.IsNullOrEmpty(reagent) ? 0 : 1);
            if (named != 1)
            {
                throw ApiErrors.InvalidArgument(
                    $"items[{index}]: name what to move with exactly one of prefab_name, prefab_hash or reagent.");
            }

            asks.Add(new TransferAsk(string.IsNullOrEmpty(prefabName) ? null : prefabName, prefabHash,
                string.IsNullOrEmpty(reagent) ? null : reagent, item.OptionalPositiveDouble("quantity")));
        }

        return asks;
    }
}
