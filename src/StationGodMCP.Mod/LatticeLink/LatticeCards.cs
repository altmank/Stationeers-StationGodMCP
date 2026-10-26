#nullable enable

using System.Collections.Generic;
using Assets.Scripts.Objects;
using StationGodMCP.LatticeLink.Model;
using StationGodMCP.Pure;
using L = Lattice.Api;

namespace StationGodMCP.LatticeLink;

/// <summary>
/// The ghost tooltip's facts from Lattice, in one place: the board record (intent, state, plan), Lattice's findings
/// (blocked, waiting, warnings), the steps from the world as it is now (the next steps and the materials and tools they
/// use) and the ports where the ghost stands. Asked a few times a second for the one ghost under the crosshair, never
/// per frame. Main thread; reach it only through LatticePresence.
/// </summary>
internal static class LatticeCards
{
    /// <summary>The card of a board ghost Lattice holds; null when Lattice runs no world or does not hold it.</summary>
    internal static GhostCard? Card(long id, string piece, string? label)
    {
        if (LatticeBoard.Current is not { } system ||
            !system.Board.As(LatticeNames.Agent).Find(new L.GhostId(id)).TryGet(out L.Ghost ghost))
        {
            return null;
        }

        GhostCard card = new GhostCard(id, piece, ghost.Label.TryGet(out L.Label own) ? own.Value : label)
        {
            Intent = ghost.Intent switch
            {
                L.Intent.Remove => "Remove",
                L.Intent.Swap => "Swap",
                L.Intent.FinishOnly => "Finish",
                _ => "Build"
            }
        };
        if (system.Plans.Find(ghost.Plan).TryGet(out L.Plan plan))
        {
            card.Plan = plan.Name.Value;
        }

        GhostStanding state = GhostStanding.Planned;
        switch (ghost.State)
        {
            case L.GhostState.Started:
                state = GhostStanding.Started;
                break;
            case L.GhostState.Stale stale:
                state = GhostStanding.Stale;
                card.Reason = LatticeText.Of(stale.Why);
                break;
        }

        Findings(system, ghost.Id, card, state);
        Steps(system, ghost.Id, card);
        if (LatticeBoard.Ports(id) is { } ports)
        {
            card.CountPorts(ports);
        }

        return card;
    }

    // Lattice's findings for the ghost: the worst decides the standing; a fail's reason is the card's, warnings listed.
    private static void Findings(L.IGhostSystem system, L.GhostId id, GhostCard card, GhostStanding state)
    {
        L.Result<L.Answer<L.ValidationReport>, L.Reason> asked =
            system.Validation.Check(new L.Selector.Ids(L.ValueList.Of(id)));
        if (!asked.TryGet(out L.Answer<L.ValidationReport> answer) || answer is not L.Answer<L.ValidationReport>.Ready ready)
        {
            // Refused or still being worked out: the board state alone.
            card.Partial = asked.IsOk;
            card.Standing = state;
            return;
        }

        bool fails = false;
        bool waits = false;
        bool warns = false;
        List<long> waitingFor = new List<long>();
        foreach (L.Finding finding in ready.Value.Findings)
        {
            if (finding.Ghost != id)
            {
                continue;
            }

            switch (finding)
            {
                case L.Finding.WillFail fail:
                    // The first fail is the reason; any more are listed.
                    if (card.Reason == null)
                    {
                        card.Reason = LatticeText.Of(fail.Why);
                    }
                    else
                    {
                        card.Warnings.Add(LatticeText.Of(fail.Why));
                    }

                    fails = true;
                    break;
                case L.Finding.WaitsFor wait:
                    waits = true;
                    foreach (L.GhostId other in wait.Others)
                    {
                        waitingFor.Add(other.Value);
                    }

                    break;
                case L.Finding.Warns warn:
                    warns = true;
                    card.Warnings.Add(LatticeText.Of(warn.Why));
                    break;
            }
        }

        card.Standing = GhostCard.FromFindings(state, fails, waits, warns, true);
        if (waitingFor.Count > 0)
        {
            string ghosts = WaitText(waitingFor);
            if (card.Standing == GhostStanding.Waiting && card.Reason == null)
            {
                card.Reason = ghosts;
            }
            else
            {
                card.Warnings.Add(ghosts);
            }
        }
    }

    private static string WaitText(List<long> ids)
    {
        List<string> shown = new List<string>();
        for (int index = 0; index < ids.Count && index < 4; index++)
        {
            shown.Add(ids[index].ToString());
        }

        string more = ids.Count > 4 ? $" and {ids.Count - 4} more" : string.Empty;
        return (ids.Count == 1 ? "Waits for ghost " : "Waits for ghosts ") + string.Join(", ", shown) + more;
    }

    // The steps from the world as it is now: the first few as lines, and every material and tool they use.
    private static void Steps(L.IGhostSystem system, L.GhostId id, GhostCard card)
    {
        L.Result<L.StepPlan, L.Reason> asked = system.Steps.For(id);
        if (!asked.TryGet(out L.StepPlan plan))
        {
            if (card.Standing != GhostStanding.Blocked && card.Standing != GhostStanding.Stale &&
                asked.TryGetError(out L.Reason why))
            {
                card.Warnings.Add("No build steps: " + LatticeText.Of(why));
            }

            return;
        }

        card.StepsLeft = plan.Steps.Count;
        foreach (L.Step step in plan.Steps)
        {
            if (card.Steps.Count < GhostCard.ShownSteps)
            {
                card.Steps.Add(Line(step));
            }

            switch (step)
            {
                case L.Step.PlaceKit place:
                    card.Need(ItemName(place.Uses.Item.Hash), place.Uses.Count);
                    break;
                case L.Step.Advance advance:
                    Tool(card, advance.Tool);
                    if (advance.Material.TryGet(out L.ItemQuantity material))
                    {
                        card.Need(ItemName(material.Item.Hash), material.Count);
                    }

                    break;
                case L.Step.TakeBack back:
                    Tool(card, back.Tool);
                    break;
                case L.Step.Merge merge:
                    Tool(card, merge.OffhandTool);
                    if (merge.Extra.TryGet(out L.ItemQuantity extra))
                    {
                        card.Need(ItemName(extra.Item.Hash), extra.Count);
                    }

                    break;
            }
        }
    }

    private static void Tool(GhostCard card, L.ItemKey tool)
    {
        string name = ItemName(tool.Hash);
        if (!card.Tools.Contains(name))
        {
            card.Tools.Add(name);
        }
    }

    private static string Line(L.Step step) => step switch
    {
        L.Step.PlaceKit place => "Place " + ItemName(place.Kit.Hash),
        L.Step.Advance advance => advance.Material.TryGet(out L.ItemQuantity material)
            ? $"{ItemName(advance.Tool.Hash)}: {material.Count} x {ItemName(material.Item.Hash)}"
            : $"Use {ItemName(advance.Tool.Hash)}",
        L.Step.TakeBack back => $"Take apart: {ItemName(back.Tool.Hash)}",
        L.Step.Merge merge => $"Merge into {ItemName(merge.Becomes.Hash)}",
        L.Step.Paint => "Paint it",
        L.Step.Rename rename => $"Name it \"{rename.To.Value}\"",
        L.Step.SetValue set => $"Set its value to {set.To.Value}",
        _ => GhostCard.Words(step.GetType().Name)
    };

    // The shown name of an item, kit or piece by prefab hash; the hash where this game has none.
    private static string ItemName(int hash) =>
        Prefab.Find(hash) is Thing prefab && prefab != null && !string.IsNullOrEmpty(prefab.DisplayName)
            ? prefab.DisplayName
            : hash.ToString();
}
