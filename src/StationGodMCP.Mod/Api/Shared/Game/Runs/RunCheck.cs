#nullable enable

using System.Collections.Generic;
using Assets.Scripts;
using Assets.Scripts.GridSystem;
using Assets.Scripts.Objects;
using Assets.Scripts.Objects.Pipes;
using StationGodMCP.Api.Shared.Game.Upgrades;
using StationGodMCP.Api.Views;
using StationGodMCP.Pure;

namespace StationGodMCP.Api.Shared.Game.Runs;

/// <summary>
/// The check after a run, once Unity has destroyed the removed and replaced pieces. Links: the game's links around
/// every built piece, each counted under its forecast id, must be the predicted ones. Networks: the built pieces and a
/// staying piece of each network before must share one network now exactly where the forecast puts them in one, and
/// every device port must be on the network the forecast joins it to (or on none).
/// </summary>
internal static class RunCheck
{
    internal static RunVerificationView Verify(RunPlan plan, RunOutcome outcome)
    {
        List<RunIssueView> problems = new List<RunIssueView>();
        RunForecast forecast = plan.Forecast!;
        UpgradeFamily family = plan.Request.Kind.Family;
        if (outcome.Log.StoppedAt != null)
        {
            problems.Add(new RunIssueView("stopped", outcome.Log.StoppedAt.Message, null, null));
        }

        CheckLinks(problems, forecast, outcome);
        Dictionary<int, HashSet<long>> networksByPart = new Dictionary<int, HashSet<long>>();
        HashSet<long> now = new HashSet<long>();
        foreach (KeyValuePair<long, SmallGrid> built in outcome.Built)
        {
            Record(networksByPart, now, Part(forecast, built.Key), family.NetworkOf(built.Value));
        }

        foreach (KeyValuePair<long, SmallGrid> representative in forecast.Representatives)
        {
            SmallGrid piece = representative.Value;
            if (piece != null && !piece.IsBeingDestroyed)
            {
                Record(networksByPart, null, Part(forecast, piece.ReferenceId), family.NetworkOf(piece));
            }
        }

        Dictionary<long, int> partOfNetwork = new Dictionary<long, int>();
        foreach (KeyValuePair<int, HashSet<long>> part in networksByPart)
        {
            if (part.Value.Count > 1)
            {
                problems.Add(new RunIssueView("network_not_joined",
                    $"Forecast network {part.Key} is split over networks {string.Join(", ", part.Value)} now.", null,
                    null));
            }

            foreach (long network in part.Value)
            {
                if (partOfNetwork.TryGetValue(network, out int other) && other != part.Key)
                {
                    problems.Add(new RunIssueView("networks_joined",
                        $"Network {network} now holds forecast networks {other} and {part.Key}, which were to stay " +
                        "apart.", new ThingId(network), null));
                }

                partOfNetwork[network] = part.Key;
            }
        }

        CheckPorts(problems, plan, forecast, networksByPart);
        List<ThingId> ids = new List<ThingId>(now.Count);
        foreach (long id in now)
        {
            ids.Add(new ThingId(id));
        }

        return new RunVerificationView(problems, ids);
    }

    private static int? Part(RunForecast forecast, long id) =>
        forecast.NodeOf.TryGetValue(id, out long node) && forecast.ComponentOf.TryGetValue(node, out int part)
            ? part
            : (int?)null;

    private static void Record(Dictionary<int, HashSet<long>> byPart, HashSet<long>? now, int? part,
        IReferencable? network)
    {
        if (network == null)
        {
            return;
        }

        now?.Add(network.ReferenceId);
        if (!part.HasValue)
        {
            return;
        }

        if (!byPart.TryGetValue(part.Value, out HashSet<long> set))
        {
            set = new HashSet<long>();
            byPart[part.Value] = set;
        }

        set.Add(network.ReferenceId);
    }

    private static void CheckLinks(List<RunIssueView> problems, RunForecast forecast, RunOutcome outcome)
    {
        Dictionary<long, long> groupOf = new Dictionary<long, long>();
        List<PieceModel> models = new List<PieceModel>();
        HashSet<long> focus = new HashSet<long>();
        foreach (KeyValuePair<long, SmallGrid> built in outcome.Built)
        {
            if (built.Value == null || built.Value.IsBeingDestroyed)
            {
                problems.Add(new RunIssueView("piece_gone", $"The piece built for {built.Key} no longer exists.", null,
                    null));
                continue;
            }

            groupOf[built.Value.ReferenceId] = built.Key;
            focus.Add(built.Value.ReferenceId);
            models.Add(PieceShapes.Live(built.Value));
        }

        List<long> unreadable = new List<long>();
        HashSet<Link> game = Connectivity.Grouped(
            LinkSurvey.GameLinks(LinkSurvey.Neighbourhood(models), focus, unreadable), groupOf);
        HashSet<Link> expected = new HashSet<Link>();
        foreach (Link link in forecast.After)
        {
            if (outcome.Built.ContainsKey(link.From) || outcome.Built.ContainsKey(link.To))
            {
                expected.Add(link);
            }
        }

        LinkDiff diff = Connectivity.Compare(expected, game);
        foreach (long id in unreadable)
        {
            problems.Add(new RunIssueView("links_unreadable", $"The connections of {id} could not be read.",
                new ThingId(id), null));
        }

        foreach (Link link in diff.Added)
        {
            problems.Add(new RunIssueView("link_added",
                $"{link.From} connects to {link.To}, which was not predicted.", null, null));
        }

        foreach (Link link in diff.Lost)
        {
            problems.Add(new RunIssueView("link_missing",
                $"{link.From} was to connect to {link.To} and does not.", null, null));
        }
    }

    private static void CheckPorts(List<RunIssueView> problems, RunPlan plan, RunForecast forecast,
        Dictionary<int, HashSet<long>> byPart)
    {
        RunKind kind = plan.Request.Kind;
        GridController world = GridController.World;
        foreach (KeyValuePair<Device, ForecastPort> entry in forecast.Ports)
        {
            Device device = entry.Key;
            ForecastPort port = entry.Value;
            if (device == null || device.IsBeingDestroyed || device.OpenEnds == null ||
                port.Index >= device.OpenEnds.Count)
            {
                continue;
            }

            Connection end = device.OpenEnds[port.Index];
            SmallCell? cell = world.GetSmallCell(end.GetLocalGrid());
            SmallGrid? piece = cell != null ? kind.SlotOf(cell) : null;
            IReferencable? network = piece != null && !piece.IsBeingDestroyed && piece.IsConnected(end)
                ? kind.Family.NetworkOf(piece)
                : null;
            int? part = port.AttachedAfter.HasValue && forecast.ComponentOf.TryGetValue(port.AttachedAfter.Value,
                out int found)
                ? found
                : (int?)null;
            if (!part.HasValue)
            {
                if (network != null)
                {
                    problems.Add(new RunIssueView("port_joined",
                        $"Port {port.Index} of {device.PrefabName} {device.ReferenceId} is on network " +
                        $"{network.ReferenceId}; nothing was to join it.", new ThingId(device.ReferenceId), null));
                }

                continue;
            }

            if (network == null)
            {
                problems.Add(new RunIssueView("port_not_joined",
                    $"Port {port.Index} of {device.PrefabName} {device.ReferenceId} is joined to nothing.",
                    new ThingId(device.ReferenceId), null));
            }
            else if (byPart.TryGetValue(part.Value, out HashSet<long> expected) &&
                     !expected.Contains(network.ReferenceId))
            {
                problems.Add(new RunIssueView("port_network_differs",
                    $"Port {port.Index} of {device.PrefabName} {device.ReferenceId} is on network " +
                    $"{network.ReferenceId}, not with forecast network {part.Value}.",
                    new ThingId(device.ReferenceId), null));
            }
        }
    }
}
