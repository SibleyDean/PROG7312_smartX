namespace SmartX.Gateway.Shared.DataStructures;

public record MeshNode(string MacAddress, bool IsGateway = false);

public record MeshEdge(string SourceMac, string DestinationMac, double Weight);

public record MeshGraphSnapshot(List<MeshNode> Nodes, List<MeshEdge> Edges, List<string> OfflineNodes);

public class MeshNetworkGraph
{
    private readonly Dictionary<string, MeshNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<MeshEdge>> _adjacencyList = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _offlineNodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    public void AddNode(string macAddress, bool isGateway = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(macAddress);

        lock (_lock)
        {
            if (_nodes.ContainsKey(macAddress))
            {
                throw new InvalidOperationException($"Node '{macAddress}' already exists in the mesh.");
            }

            _nodes.Add(macAddress, new MeshNode(macAddress, isGateway));
            _adjacencyList.Add(macAddress, new List<MeshEdge>());
        }
    }

    public void AddEdge(string sourceMac, string destMac, double weight)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(weight);

        lock (_lock)
        {
            var source = GetExistingNode(sourceMac);
            var destination = GetExistingNode(destMac);

            if (source == destination)
            {
                throw new InvalidOperationException("A node cannot link to itself.");
            }

            _adjacencyList[source.MacAddress].RemoveAll(e => IsSameMac(e.DestinationMac, destination.MacAddress));
            _adjacencyList[destination.MacAddress].RemoveAll(e => IsSameMac(e.DestinationMac, source.MacAddress));

            _adjacencyList[source.MacAddress].Add(new MeshEdge(source.MacAddress, destination.MacAddress, weight));
            _adjacencyList[destination.MacAddress].Add(new MeshEdge(destination.MacAddress, source.MacAddress, weight));
        }
    }

    public void SetNodeFailure(string macAddress, bool isOffline)
    {
        lock (_lock)
        {
            var node = GetExistingNode(macAddress);

            if (isOffline)
            {
                _offlineNodes.Add(node.MacAddress);
            }
            else
            {
                _offlineNodes.Remove(node.MacAddress);
            }
        }
    }

    public bool ContainsNode(string macAddress)
    {
        lock (_lock)
        {
            return _nodes.ContainsKey(macAddress);
        }
    }

    public bool IsOffline(string macAddress)
    {
        lock (_lock)
        {
            return _offlineNodes.Contains(macAddress);
        }
    }

    // Returns how many nodes were brought back online.
    public int RestoreAll()
    {
        lock (_lock)
        {
            var restored = _offlineNodes.Count;
            _offlineNodes.Clear();
            return restored;
        }
    }

    // Sum of link weights along a path; throws if two consecutive hops are not linked.
    public double GetPathCost(IReadOnlyList<string> path)
    {
        ArgumentNullException.ThrowIfNull(path);

        lock (_lock)
        {
            double cost = 0;
            for (int i = 0; i < path.Count - 1; i++)
            {
                var edge = _adjacencyList.TryGetValue(path[i], out var edges)
                    ? edges.FirstOrDefault(e => IsSameMac(e.DestinationMac, path[i + 1]))
                    : null;

                if (edge is null)
                {
                    throw new InvalidOperationException($"'{path[i]}' and '{path[i + 1]}' are not linked.");
                }

                cost += edge.Weight;
            }

            return cost;
        }
    }

    // ignoreOffline finds the route the mesh would use with every node online, which is how a reroute is recognised.
    public List<string> FindShortestPath(string startMac, string endMac, bool ignoreOffline = false)
    {
        lock (_lock)
        {
            if (!_nodes.TryGetValue(startMac, out var start) || !_nodes.TryGetValue(endMac, out var end))
            {
                return new List<string>();
            }

            var offline = ignoreOffline ? new HashSet<string>() : _offlineNodes;

            if (offline.Contains(start.MacAddress) || offline.Contains(end.MacAddress))
            {
                return new List<string>();
            }

            var distances = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase) { [start.MacAddress] = 0 };
            var previous = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var frontier = new PriorityQueue<string, double>();
            frontier.Enqueue(start.MacAddress, 0);

            // The queue can hold stale entries for a node; the visited check discards all but the cheapest.
            while (frontier.TryDequeue(out var current, out var distance))
            {
                if (!visited.Add(current))
                {
                    continue;
                }

                if (IsSameMac(current, end.MacAddress))
                {
                    break;
                }

                foreach (var edge in _adjacencyList[current])
                {
                    if (offline.Contains(edge.DestinationMac) || visited.Contains(edge.DestinationMac))
                    {
                        continue;
                    }

                    var candidate = distance + edge.Weight;
                    if (!distances.TryGetValue(edge.DestinationMac, out var known) || candidate < known)
                    {
                        distances[edge.DestinationMac] = candidate;
                        previous[edge.DestinationMac] = current;
                        frontier.Enqueue(edge.DestinationMac, candidate);
                    }
                }
            }

            if (!visited.Contains(end.MacAddress))
            {
                return new List<string>();
            }

            var path = new List<string> { end.MacAddress };
            var step = end.MacAddress;
            while (previous.TryGetValue(step, out var prior))
            {
                path.Add(prior);
                step = prior;
            }

            path.Reverse();
            return path;
        }
    }

    public MeshGraphSnapshot GetSnapshot()
    {
        lock (_lock)
        {
            var edges = _adjacencyList.Values
                .SelectMany(list => list)
                .Where(e => string.Compare(e.SourceMac, e.DestinationMac, StringComparison.OrdinalIgnoreCase) < 0)
                .ToList();

            return new MeshGraphSnapshot(_nodes.Values.ToList(), edges, _offlineNodes.ToList());
        }
    }

    private MeshNode GetExistingNode(string macAddress)
    {
        if (!_nodes.TryGetValue(macAddress, out var node))
        {
            throw new KeyNotFoundException($"Node '{macAddress}' not found in the mesh.");
        }

        return node;
    }

    private static bool IsSameMac(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}
