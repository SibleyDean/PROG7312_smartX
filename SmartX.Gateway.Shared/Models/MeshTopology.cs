using SmartX.Gateway.Shared.DataStructures;

namespace SmartX.Gateway.Shared.Models;

public enum MeshNodeRole
{
    Gateway,
    Router,
    Sensor
}

// The positional fields are the original contract; everything below them was added later and is optional for older clients.
public record MeshTopologyNode(string MacAddress, bool IsGateway, bool IsOffline, double X, double Y)
{
    public MeshNodeRole Role { get; init; } = MeshNodeRole.Sensor;
    public string Area { get; init; } = string.Empty;
    public string? ParentMac { get; init; }
    public bool HasRoute { get; init; } = true;
    public bool IsRerouted { get; init; }
    public Guid? SensorId { get; init; }
    public string? Location { get; init; }
    public SensorCategory? Category { get; init; }
    public SensorSource? Source { get; init; }
    public double? LatestValue { get; init; }
    public DateTime? LatestTimestamp { get; init; }
}

public record MeshTopology(List<MeshTopologyNode> Nodes, List<MeshEdge> Edges);

// Path is the live route; BaselinePath is the route the node uses when nothing is offline.
public record MeshRoute(List<string> Path, List<string> BaselinePath, double Cost);

public enum ConnectionProblemKind
{
    Offline,
    NoRoute
}

public record ConnectionProblem(string MacAddress, ConnectionProblemKind Kind, string Message);
