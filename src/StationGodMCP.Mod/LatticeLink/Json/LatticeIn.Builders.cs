#nullable enable

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Linq;
using StationGodMCP.Pure.Lint;
using L = Lattice.Api;

namespace StationGodMCP.LatticeLink.Json;

/// <summary>
/// Contract 0.22's builders: a plan's colour and builders, and what a builder says of itself (area, word, supply, its
/// answer to a plan). A method here names members older Lattices lack, so each is called only after Needs.
/// </summary>
internal sealed partial class LatticeIn
{
    /// <summary>Whether the running contract has the members of this minor; a problem at path when not.</summary>
    internal bool Needs(int minor, string path)
    {
        if (_system.Version.Major == L.ApiVersion.ContractMajor && Minor >= minor)
        {
            return true;
        }

        Problem(path, $"needs Lattice 0.{minor} or later (this one is {_system.Version.Major}.{Minor})");
        return false;
    }

    /// <summary>A builder by id ("owner/name", or as Lattice lists it in builders or a plan's build_by).</summary>
    internal L.ExecutorId? Builder(JToken? token, string path)
    {
        if (Text(token, path) is not { } text)
        {
            return null;
        }

        if (Minor >= LatticeOut.BuildersMinor && Listed(text) is { } listed)
        {
            return listed;
        }

        int slash = text.IndexOf('/');
        if (slash <= 0)
        {
            Problem(path, $"'{text}' is no builder Lattice lists; a mod's builder is written owner/name");
            return null;
        }

        return Made(L.ModId.Create(text.Substring(0, slash)), path) is { } owner
            ? Made(L.ExecutorId.Create(owner, text.Substring(slash + 1)), path)
            : null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private L.ExecutorId? Listed(string text)
    {
        foreach (L.Builder builder in _system.Executors.All)
        {
            if (builder.Info.Id.Value == text)
            {
                return builder.Info.Id;
            }
        }

        foreach (L.Plan plan in _system.Plans.All)
        {
            foreach (L.ExecutorId id in plan.BuildBy)
            {
                if (id.Value == text)
                {
                    return id;
                }
            }
        }

        return null;
    }

    /// <summary>An item by prefab name (its game hash) or hash.</summary>
    internal L.ItemKey? Item(JToken? token, string path)
    {
        if (token is JValue { Type: JTokenType.Integer })
        {
            return Whole(token, path) is { } hash ? new L.ItemKey(hash) : (L.ItemKey?)null;
        }

        return Text(token, path) is { } name ? new L.ItemKey(LintChipPrograms.HashOf(name)) : (L.ItemKey?)null;
    }

    /// <summary>[{item, count}, ...].</summary>
    internal L.ValueList<L.ItemQuantity>? Quantities(JToken? token, string path)
    {
        if (List(token, path) is not { } listed)
        {
            return null;
        }

        List<L.ItemQuantity> items = new List<L.ItemQuantity>();
        for (int index = 0; index < listed.Count; index++)
        {
            string at = $"{path}[{index}]";
            if (Object(listed[index], at) is not { } entry)
            {
                continue;
            }

            L.ItemKey? item = Item(entry["item"], at + ".item");
            int? count = Whole(entry["count"], at + ".count");
            if (item is { } i && count is { } c && Made(L.ItemQuantity.Create(i, c), at) is { } quantity)
            {
                items.Add(quantity);
            }
        }

        return Failed ? null : L.ValueList.From(items);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private L.PlanDraft? Coloured(L.PlanDraft draft, JToken? colour, string path) =>
        Tint(colour, path) is { } tint ? draft with { Colour = tint } : null;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private L.PlanEdit? BuildersEdit(string kind, JToken? payload, string at)
    {
        if (kind == "recolour")
        {
            return Tint(payload, at) is { } tint ? new L.PlanEdit.Recolour(tint) : null;
        }

        if (Builder(payload, at) is not { } builder)
        {
            return null;
        }

        return kind == "assign" ? new L.PlanEdit.Assign(builder) : new L.PlanEdit.Unassign(builder);
    }

    /// <summary>Where a builder works: null (nowhere in particular), or {"sphere": {at, build_reach, source_range}}.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal L.Option<L.BuilderArea>? Area(JToken? token, string path)
    {
        if (token == null || token.Type == JTokenType.Null)
        {
            return L.Option<L.BuilderArea>.None;
        }

        if (Case(token, path, "sphere") is not { } area || Object(area.Payload, path + ".sphere") is not { } sphere)
        {
            return null;
        }

        string at = path + ".sphere";
        L.WorldPoint? centre = Point(sphere["at"], at + ".at");
        L.Metres? reach = Metres(sphere["build_reach"], at + ".build_reach");
        L.Option<L.Metres> range = L.Option<L.Metres>.None;
        if (Given(sphere, "source_range") && Metres(sphere["source_range"], at + ".source_range") is { } metres)
        {
            range = L.Option.Some(metres);
        }

        return centre is { } c && reach is { } r && !Failed
            ? L.Option.Some<L.BuilderArea>(new L.BuilderArea.Sphere(c, r, range))
            : null;
    }

    /// <summary>{reaches: [{item, count}], toward: [{plan, items: [{item, count}]}]}.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal L.SupplyReport? Supply(JToken? token, string path)
    {
        if (Object(token, path) is not { } supply)
        {
            return null;
        }

        L.ValueList<L.ItemQuantity>? reaches = Given(supply, "reaches")
            ? Quantities(supply["reaches"], path + ".reaches")
            : L.ValueList<L.ItemQuantity>.Empty;
        L.ValueMap<L.PlanId, L.ValueList<L.ItemQuantity>> toward = L.ValueMap<L.PlanId, L.ValueList<L.ItemQuantity>>.Empty;
        if (Given(supply, "toward") && List(supply["toward"], path + ".toward") is { } plans)
        {
            for (int index = 0; index < plans.Count; index++)
            {
                string at = $"{path}.toward[{index}]";
                if (Object(plans[index], at) is { } entry && Plan(entry["plan"], at + ".plan") is { } plan &&
                    Quantities(entry["items"], at + ".items") is { } items)
                {
                    toward = toward.With(plan, items);
                }
            }
        }

        return reaches != null && !Failed ? new L.SupplyReport(reaches, toward) : null;
    }
}
