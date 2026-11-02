using System.Text.RegularExpressions;
using SmartX.Gateway.Shared.Models;
using SmartX.Gateway.Shared.Services;

namespace SmartX.Gateway.Client;

// Normal/Warning/Critical/NoData describe a sensor's latest reading; Online describes a working router or gateway.
public enum NodeState
{
    Normal,
    Warning,
    Critical,
    NoData,
    Online,
    Offline,
    NoRoute
}

public sealed record TopologyNodeView(
    MeshTopologyNode Node,
    NodeState State,
    AlertDetail? Alert,
    int IssuesDownstream,
    bool IsProblem,
    bool IsMatch,
    bool IsDimmed,
    bool IsInvalid)
{
    public string Mac => Node.MacAddress;
    public bool IsSensor => Node.Role == MeshNodeRole.Sensor && !Node.IsGateway;
    public bool IsRouter => Node.Role == MeshNodeRole.Router;
    public bool IsGateway => Node.IsGateway || Node.Role == MeshNodeRole.Gateway;
    public string Name => TopologyView.DisplayName(Node);
    public string ShortLabel => TopologyView.ShortLabel(Node);
    public string StateWord => TopologyView.StateWord(State);
    public string Glyph => TopologyView.Glyph(State);

    // The full description used for tooltips, focus text and screen readers.
    public string Description
    {
        get
        {
            var parts = new List<string> { Name, Mac, StateWord };
            if (IsSensor && Node.LatestValue is double value && Node.Category is SensorCategory category)
            {
                parts.Add($"latest {SensorReadingInfo.Format(value, category)}");
            }

            if (Alert is not null)
            {
                parts.Add($"open {(Alert.Priority == 1 ? "critical" : "warning")} alert");
            }

            if (Node.IsRerouted)
            {
                parts.Add("rerouted");
            }

            if (IssuesDownstream > 0)
            {
                parts.Add(TopologyView.IssuesText(IssuesDownstream));
            }

            if (IsInvalid)
            {
                parts.Add("failed validation");
            }

            return string.Join(", ", parts);
        }
    }
}

public static partial class TopologyView
{
    public static NodeState StateOf(MeshTopologyNode node)
    {
        if (node.IsOffline)
        {
            return NodeState.Offline;
        }

        if (!node.HasRoute)
        {
            return NodeState.NoRoute;
        }

        if (node.IsGateway || node.Role != MeshNodeRole.Sensor)
        {
            return NodeState.Online;
        }

        return node.LatestValue is double value
            ? SensorReadingInfo.GetStatus(value) switch
            {
                ReadingStatus.Critical => NodeState.Critical,
                ReadingStatus.Warning => NodeState.Warning,
                _ => NodeState.Normal
            }
            : NodeState.NoData;
    }

    public static bool IsProblemState(NodeState state) => state is NodeState.Warning or NodeState.Critical or NodeState.Offline or NodeState.NoRoute;

    public static string StateWord(NodeState state) => state switch
    {
        NodeState.NoData => "No data",
        NodeState.NoRoute => "No route",
        _ => state.ToString()
    };

    // A shape inside the node, so status never relies on colour alone. Offline is drawn as a cross instead.
    public static string Glyph(NodeState state) => state switch
    {
        NodeState.Normal => "✓",
        NodeState.Warning => "!",
        NodeState.Critical => "‼",
        NodeState.NoData => "…",
        NodeState.NoRoute => "?",
        _ => string.Empty
    };

    public static string IssuesText(int count) => count == 1 ? "1 issue downstream" : $"{count} issues downstream";

    public static string DisplayName(MeshTopologyNode node) => node switch
    {
        { IsGateway: true } or { Role: MeshNodeRole.Gateway } => "Gateway",
        { Role: MeshNodeRole.Router } => $"{node.Area} router",
        _ => string.IsNullOrWhiteSpace(node.Location) ? node.MacAddress : node.Location!
    };

    // Map labels stay short: the area name sits beside the router, so a sensor only needs its line ("L2").
    public static string ShortLabel(MeshTopologyNode node)
    {
        if (node.IsGateway || node.Role == MeshNodeRole.Gateway)
        {
            return "Gateway";
        }

        if (node.Role == MeshNodeRole.Router)
        {
            return node.Area;
        }

        if (!string.IsNullOrWhiteSpace(node.Location))
        {
            var line = LinePattern().Match(node.Location);
            if (line.Success)
            {
                return $"L{line.Groups[1].Value}";
            }

            return node.Location.Length > 8 ? node.Location[..7] + "…" : node.Location;
        }

        return node.MacAddress.Length > 5 ? node.MacAddress[^5..] : node.MacAddress;
    }

    public static List<TopologyNodeView> Build(
        MeshTopology topology,
        IReadOnlyList<AlertDetail> alerts,
        string? search = null,
        bool problemsOnly = false,
        IReadOnlySet<string>? invalidMacs = null)
    {
        var alertsBySensor = alerts.GroupBy(a => a.SensorId).ToDictionary(g => g.Key, g => g.OrderBy(a => a.Priority).First());
        var searching = SensorSearch.Normalise(search).Length > 0;

        var basics = topology.Nodes.Select(node =>
        {
            var state = StateOf(node);
            var alert = node.SensorId is Guid id && alertsBySensor.TryGetValue(id, out var found) ? found : null;
            var isProblem = IsProblemState(state) || alert is not null;
            var isMatch = node.Role == MeshNodeRole.Sensor && !node.IsGateway && SensorSearch.Matches(ToProfile(node), search);
            return (Node: node, State: state, Alert: alert, IsProblem: isProblem, IsMatch: isMatch);
        }).ToList();

        var childrenByRouter = basics
            .Where(b => b.Node.Role == MeshNodeRole.Sensor && b.Node.ParentMac is not null)
            .GroupBy(b => b.Node.ParentMac!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        return basics.Select(b =>
        {
            var node = b.Node;
            var isGateway = node.IsGateway || node.Role == MeshNodeRole.Gateway;
            var children = childrenByRouter.TryGetValue(node.MacAddress, out var list) ? list : [];

            var issues = isGateway
                ? basics.Count(o => o.IsProblem && !ReferenceEquals(o.Node, node))
                : node.Role == MeshNodeRole.Router ? children.Count(c => c.IsProblem) : 0;

            // A router is relevant to a search when its area matches or when any of its sensors does.
            var isMatch = node.Role == MeshNodeRole.Router
                ? SensorSearch.Normalise(node.Area).Contains(SensorSearch.Normalise(search), StringComparison.Ordinal) || children.Any(c => c.IsMatch)
                : isGateway ? !searching : b.IsMatch;

            var hiddenBySearch = searching && !isGateway && !isMatch;
            var hiddenByProblems = problemsOnly && !b.IsProblem && issues == 0;

            return new TopologyNodeView(
                node,
                b.State,
                b.Alert,
                issues,
                b.IsProblem,
                searching && isMatch && !isGateway,
                hiddenBySearch || hiddenByProblems,
                invalidMacs?.Contains(node.MacAddress) ?? false);
        }).ToList();
    }

    private static SensorProfile ToProfile(MeshTopologyNode node) => new()
    {
        MacAddress = node.MacAddress,
        Location = node.Location ?? string.Empty,
        Category = node.Category ?? SensorCategory.Unknown
    };

    [GeneratedRegex(@"Line\s*(\d+)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LinePattern();
}
