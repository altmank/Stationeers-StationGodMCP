#nullable enable

using System.Collections.Generic;

namespace StationGodMCP.Pure;

/// <summary>
/// What one build item line may use in the holder, as the game reports it before anything is made: the matching stacks
/// with room (StackSlots, Rooms: parallel) and the empty slots that take the item, each slot named by a number that is
/// the same for the same slot in every line; the amount asked and the item's full stack.
/// </summary>
internal sealed record LineSurvey(IReadOnlyList<int> StackSlots, IReadOnlyList<double> Rooms,
    IReadOnlyList<int> EmptySlots, double Quantity, double Full);

/// <summary>A list of lines planned against one holder: every line's stacks, or the first line that does not fit.</summary>
internal abstract record ItemListPlan
{
    private ItemListPlan()
    {
    }

    /// <summary>One plan per line, in order; its steps index that line's own StackSlots and EmptySlots.</summary>
    internal sealed record Fits(IReadOnlyList<StackPlan> Lines) : ItemListPlan;

    /// <summary>Line Line needs Unplaced more than the slots left by the lines before it.</summary>
    internal sealed record NoRoom(int Line, double Unplaced) : ItemListPlan;
}

/// <summary>
/// Plans a build item list as one: lines in order, each taking what the lines before it left (an empty slot a line
/// filled is no longer empty, a stack it topped up has that much less room), so the whole list fits or none of it is
/// made. Nothing here touches the game.
/// </summary>
internal static class ItemListPlanner
{
    internal static ItemListPlan Plan(IReadOnlyList<LineSurvey> lines)
    {
        Dictionary<int, double> roomLeft = new Dictionary<int, double>();
        HashSet<int> filled = new HashSet<int>();
        List<StackPlan> plans = new List<StackPlan>(lines.Count);
        for (int line = 0; line < lines.Count; line++)
        {
            LineSurvey survey = lines[line];
            List<int> stacks = new List<int>();
            List<double> rooms = new List<double>();
            for (int stack = 0; stack < survey.StackSlots.Count; stack++)
            {
                int slot = survey.StackSlots[stack];
                double room = roomLeft.TryGetValue(slot, out double left) ? left : survey.Rooms[stack];
                if (room > StackPlacement.Trace)
                {
                    stacks.Add(stack);
                    rooms.Add(room);
                }
            }

            List<int> empties = new List<int>();
            for (int empty = 0; empty < survey.EmptySlots.Count; empty++)
            {
                if (!filled.Contains(survey.EmptySlots[empty]))
                {
                    empties.Add(empty);
                }
            }

            StackPlan plan = StackPlacement.Plan(survey.Quantity, survey.Full, rooms, empties.Count, false);
            if (!plan.Fits)
            {
                return new ItemListPlan.NoRoom(line, plan.Unplaced);
            }

            List<PlacementStep> steps = new List<PlacementStep>(plan.Steps.Count);
            foreach (PlacementStep step in plan.Steps)
            {
                switch (step)
                {
                    case PlacementStep.TopUp topUp:
                        int stackIndex = stacks[topUp.Stack];
                        roomLeft[survey.StackSlots[stackIndex]] = rooms[topUp.Stack] - step.Quantity;
                        steps.Add(new PlacementStep.TopUp(stackIndex, step.Quantity));
                        break;
                    case PlacementStep.NewStack newStack:
                        int emptyIndex = empties[newStack.Slot];
                        filled.Add(survey.EmptySlots[emptyIndex]);
                        steps.Add(new PlacementStep.NewStack(emptyIndex, step.Quantity));
                        break;
                    default:
                        steps.Add(step);
                        break;
                }
            }

            plans.Add(new StackPlan(steps, 0.0));
        }

        return new ItemListPlan.Fits(plans);
    }
}
