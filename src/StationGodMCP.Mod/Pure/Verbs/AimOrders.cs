#nullable enable

using System.Collections.Generic;
using System.Globalization;

namespace StationGodMCP.Pure.Verbs;

/// <summary>
/// plan aim's call, checked without the game: the things to aim (id, one or a list) and the trader contact a dish points
/// at. Whether each thing is a panel or a dish, and so whether contact applies, is the game's to say per id.
/// </summary>
internal sealed class AimOrder
{
    private AimOrder(PerIdList ids, long? contact)
    {
        Ids = ids;
        Contact = contact;
    }

    public PerIdList Ids { get; }

    /// <summary>The trader contact's id; null when the call gives none.</summary>
    public long? Contact { get; }

    /// <summary>The call's order, or null with every problem added.</summary>
    internal static AimOrder? Of(PlanAimOptions options, List<OptionProblem> problems)
    {
        int before = problems.Count;
        long? contact = null;
        if (options.Contact != null)
        {
            contact = long.TryParse(options.Contact.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out long id) && id > 0
                ? id
                : null;
            if (contact == null)
            {
                problems.Add(new OptionProblem("contact", "is a trader contact's id, as trade contacts lists it"));
            }
        }

        if (options.Selector.Ids == null)
        {
            problems.Add(new OptionProblem("id", "is needed: the solar panel or satellite dish to aim"));
        }

        return problems.Count > before ? null : new AimOrder(options.Selector.Ids!, contact);
    }
}
