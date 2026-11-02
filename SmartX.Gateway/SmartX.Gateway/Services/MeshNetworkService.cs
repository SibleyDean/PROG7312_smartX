using SmartX.Gateway.Shared.DataStructures;
using SmartX.Gateway.Shared.Models;
using SmartX.Gateway.Shared.Services;

namespace SmartX.Gateway.Services
{
    // Owns the mesh: the gateway, one router per factory area, and every sensor attached to its area's router.
    public class MeshNetworkService
    {
        public const string GatewayMac = "00:00:00:00:00:00";

        public record MeshSensorDefinition(string MacAddress, string Location, SensorCategory Category);

        // The fixed mesh sensors that exist from start-up and survive "Clear all sensors".
        public static IReadOnlyList<MeshSensorDefinition> BaseSensors { get; } =
        [
            new("00:1B:44:11:3A:01", "Assembly - Line 1", SensorCategory.Environmental),
            new("00:1B:44:11:3A:02", "Assembly - Line 2", SensorCategory.PowerConsumption),
            new("00:1B:44:11:3A:03", "Packaging - Line 1", SensorCategory.Actuator),
            new("00:1B:44:11:3A:04", "Boiler Room - Line 1", SensorCategory.Environmental),
            new("00:1B:44:11:3A:05", "Boiler Room - Line 3", SensorCategory.Actuator),
            new("00:1B:44:11:3A:06", "Cold Storage - Line 2", SensorCategory.Environmental)
        ];

        // Link costs grow with distance: a sensor's own router is cheap, its backup router dearer, the gateway dearest.
        internal const double SensorLinkCost = 2;
        internal const double BackupLinkCost = 12;
        private const double RouterRingRadius = 5;
        private const double LeafBaseRadius = 1.6;
        private const double LeafRingStep = 0.85;
        private const int LeafRingBaseCapacity = 5;
        private const double LeafSpacingArc = 120 * Math.PI / 180;

        private readonly object _lock = new();
        private MeshNetworkGraph _graph = new();
        private readonly Dictionary<string, NodeInfo> _nodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<string>> _leavesByRouter = new(StringComparer.OrdinalIgnoreCase);

        private sealed record NodeInfo(MeshNodeRole Role, string Area, string? Location, string? ParentMac);

        public MeshNetworkService()
        {
            Reset();
        }

        public static string RouterMacFor(string area) => $"00:1B:44:00:00:{FactoryAreas.IndexOf(area) + 1:X2}";

        // Rebuilds the base mesh: removes attached demo sensors and dynamically added routers, and brings every node back online.
        public void Reset()
        {
            lock (_lock)
            {
                _graph = new MeshNetworkGraph();
                _nodes.Clear();
                _leavesByRouter.Clear();
                _graph.AddNode(GatewayMac, isGateway: true);

                // Routers first: a sensor's backup link goes to the nearest router, so they must all exist before any sensor joins.
                foreach (var area in BaseSensors.Select(s => FactoryAreas.AreaOf(s.Location)).Distinct())
                {
                    EnsureRouter(area);
                }

                foreach (var sensor in BaseSensors)
                {
                    AttachSensorLocked(sensor.MacAddress, sensor.Location);
                }
            }
        }

        public bool ContainsNode(string macAddress)
        {
            lock (_lock)
            {
                return _graph.ContainsNode(macAddress);
            }
        }

        public bool IsRouter(string macAddress)
        {
            lock (_lock)
            {
                return _nodes.TryGetValue(macAddress, out var info) && info.Role == MeshNodeRole.Router;
            }
        }

        // Attaches a sensor to its area's router (adding the router if needed) and returns that router's MAC.
        public string AttachSensor(string macAddress, string location)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(macAddress);

            lock (_lock)
            {
                return AttachSensorLocked(macAddress, location);
            }
        }

        public string? GetRouterOf(string macAddress)
        {
            lock (_lock)
            {
                return _nodes.TryGetValue(macAddress, out var info) ? info.ParentMac : null;
            }
        }

        public List<string> FindRouteToGateway(string macAddress)
        {
            lock (_lock)
            {
                return _graph.FindShortestPath(macAddress, GatewayMac);
            }
        }

        public MeshRoute GetRoute(string macAddress)
        {
            lock (_lock)
            {
                var path = _graph.FindShortestPath(macAddress, GatewayMac);
                var baseline = _graph.FindShortestPath(macAddress, GatewayMac, ignoreOffline: true);
                var cost = path.Count > 1 ? _graph.GetPathCost(path) : 0;
                return new MeshRoute(path, baseline, cost);
            }
        }

        public void SetNodeFailure(string macAddress, bool isOffline)
        {
            lock (_lock)
            {
                _graph.SetNodeFailure(macAddress, isOffline);
            }
        }

        public int RestoreAll()
        {
            lock (_lock)
            {
                return _graph.RestoreAll();
            }
        }

        // A node reports only while it is online and still has a path to the gateway. Nodes outside the mesh always report.
        public bool IsReporting(string macAddress)
        {
            lock (_lock)
            {
                if (!_graph.ContainsNode(macAddress))
                {
                    return true;
                }

                return !_graph.IsOffline(macAddress) && _graph.FindShortestPath(macAddress, GatewayMac).Count > 0;
            }
        }

        public HashSet<string> GetSilentNodes()
        {
            lock (_lock)
            {
                return _graph.GetSnapshot().Nodes
                    .Where(n => _graph.IsOffline(n.MacAddress) || _graph.FindShortestPath(n.MacAddress, GatewayMac).Count == 0)
                    .Select(n => n.MacAddress)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }

        public List<ConnectionProblem> GetConnectionProblems()
        {
            lock (_lock)
            {
                var problems = new List<ConnectionProblem>();

                foreach (var node in _graph.GetSnapshot().Nodes)
                {
                    var name = DisplayName(node.MacAddress);

                    if (_graph.IsOffline(node.MacAddress))
                    {
                        problems.Add(new ConnectionProblem(node.MacAddress, ConnectionProblemKind.Offline, $"{name} is offline (simulated)"));
                    }
                    else if (!node.IsGateway && _graph.FindShortestPath(node.MacAddress, GatewayMac).Count == 0)
                    {
                        problems.Add(new ConnectionProblem(node.MacAddress, ConnectionProblemKind.NoRoute, $"{name} has no route to the gateway"));
                    }
                }

                return problems;
            }
        }

        public MeshTopology GetTopology()
        {
            lock (_lock)
            {
                var snapshot = _graph.GetSnapshot();
                var offline = new HashSet<string>(snapshot.OfflineNodes, StringComparer.OrdinalIgnoreCase);
                var positions = ComputeLayout();

                var nodes = snapshot.Nodes
                    .Select(n =>
                    {
                        _nodes.TryGetValue(n.MacAddress, out var info);
                        var path = n.IsGateway ? new List<string> { n.MacAddress } : _graph.FindShortestPath(n.MacAddress, GatewayMac);
                        var baseline = n.IsGateway ? path : _graph.FindShortestPath(n.MacAddress, GatewayMac, ignoreOffline: true);
                        var (x, y) = positions.TryGetValue(n.MacAddress, out var point) ? point : (0, 0);

                        return new MeshTopologyNode(n.MacAddress, n.IsGateway, offline.Contains(n.MacAddress), Math.Round(x, 2), Math.Round(y, 2))
                        {
                            Role = n.IsGateway ? MeshNodeRole.Gateway : info?.Role ?? MeshNodeRole.Sensor,
                            Area = info?.Area ?? string.Empty,
                            Location = info?.Location,
                            ParentMac = info?.ParentMac,
                            HasRoute = path.Count > 0 && !offline.Contains(GatewayMac),
                            IsRerouted = path.Count > 0 && !path.SequenceEqual(baseline, StringComparer.OrdinalIgnoreCase)
                        };
                    })
                    .ToList();

                return new MeshTopology(nodes, snapshot.Edges);
            }
        }

        private string AttachSensorLocked(string macAddress, string location)
        {
            if (_nodes.TryGetValue(macAddress, out var existing) && existing.ParentMac is not null)
            {
                return existing.ParentMac;
            }

            var area = FactoryAreas.Canonical(FactoryAreas.AreaOf(location));
            var routerMac = area is not null ? EnsureRouter(area) : ClosestFitRouter();

            _graph.AddNode(macAddress);
            _nodes[macAddress] = new NodeInfo(MeshNodeRole.Sensor, _nodes[routerMac].Area, location, routerMac);
            _leavesByRouter[routerMac].Add(macAddress);
            _graph.AddEdge(macAddress, routerMac, SensorLinkCost);

            // A second, dearer link to the next router round the ring lets the mesh reroute when the sensor's own router fails.
            if (NeighbourRouter(routerMac) is string backup)
            {
                _graph.AddEdge(macAddress, backup, BackupLinkCost);
            }

            return routerMac;
        }

        // Locations outside the known areas join the existing router with the fewest sensors.
        private string ClosestFitRouter()
        {
            var routers = _nodes.Where(n => n.Value.Role == MeshNodeRole.Router).Select(n => n.Key).ToList();
            if (routers.Count == 0)
            {
                return EnsureRouter(FactoryAreas.All[0]);
            }

            return routers
                .OrderBy(mac => _leavesByRouter[mac].Count)
                .ThenBy(mac => FactoryAreas.IndexOf(_nodes[mac].Area))
                .First();
        }

        private string EnsureRouter(string area)
        {
            var mac = RouterMacFor(area);
            if (_nodes.ContainsKey(mac))
            {
                return mac;
            }

            _graph.AddNode(mac);
            _nodes[mac] = new NodeInfo(MeshNodeRole.Router, area, area, GatewayMac);
            _leavesByRouter[mac] = new List<string>();

            var (x, y) = RouterPosition(area);
            _graph.AddEdge(mac, GatewayMac, Math.Round(x * x + y * y, 2));

            // Routers link to their nearest neighbours round the ring so traffic can hop across areas.
            foreach (var neighbour in RingNeighbours(area))
            {
                var (nx, ny) = RouterPosition(_nodes[neighbour].Area);
                _graph.AddEdge(mac, neighbour, Math.Round(Math.Pow(nx - x, 2) + Math.Pow(ny - y, 2), 2));
            }

            return mac;
        }

        private IEnumerable<string> RingNeighbours(string area)
        {
            var routers = _nodes.Where(n => n.Value.Role == MeshNodeRole.Router && !string.Equals(n.Value.Area, area, StringComparison.OrdinalIgnoreCase))
                .Select(n => (Mac: n.Key, Index: FactoryAreas.IndexOf(n.Value.Area)))
                .ToList();
            if (routers.Count == 0)
            {
                yield break;
            }

            var index = FactoryAreas.IndexOf(area);
            var count = FactoryAreas.All.Count;
            var next = routers.MinBy(r => (r.Index - index + count) % count);
            var previous = routers.MinBy(r => (index - r.Index + count) % count);

            yield return next.Mac;
            if (previous.Mac != next.Mac)
            {
                yield return previous.Mac;
            }
        }

        // The backup is the closest router round the ring in either direction (ties go clockwise).
        private string? NeighbourRouter(string routerMac)
        {
            var index = FactoryAreas.IndexOf(_nodes[routerMac].Area);
            var count = FactoryAreas.All.Count;

            return _nodes
                .Where(n => n.Value.Role == MeshNodeRole.Router && !string.Equals(n.Key, routerMac, StringComparison.OrdinalIgnoreCase))
                .Select(n => (Mac: n.Key, Index: FactoryAreas.IndexOf(n.Value.Area)))
                .OrderBy(r => Math.Min((r.Index - index + count) % count, (index - r.Index + count) % count))
                .ThenBy(r => (r.Index - index + count) % count)
                .Select(r => r.Mac)
                .FirstOrDefault();
        }

        private string DisplayName(string macAddress)
        {
            if (string.Equals(macAddress, GatewayMac, StringComparison.OrdinalIgnoreCase))
            {
                return "The gateway";
            }

            if (!_nodes.TryGetValue(macAddress, out var info))
            {
                return macAddress;
            }

            return info.Role == MeshNodeRole.Router ? $"{info.Area} router" : info.Location ?? macAddress;
        }

        // Routers sit on a ring, one fixed angle per area starting at the top; sensors fan outwards from their router.
        private static (double X, double Y) RouterPosition(string area)
        {
            var angle = Math.PI / 2 - 2 * Math.PI * FactoryAreas.IndexOf(area) / FactoryAreas.All.Count;
            return (RouterRingRadius * Math.Cos(angle), RouterRingRadius * Math.Sin(angle));
        }

        private Dictionary<string, (double X, double Y)> ComputeLayout()
        {
            var positions = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase) { [GatewayMac] = (0, 0) };

            foreach (var (routerMac, leaves) in _leavesByRouter)
            {
                var area = _nodes[routerMac].Area;
                var (rx, ry) = RouterPosition(area);
                var outward = Math.Atan2(ry, rx);
                positions[routerMac] = (rx, ry);

                // Rings fill in turn, each holding two more sensors than the one inside it, so 60 sensors stay legible.
                var placed = 0;
                for (int ring = 0; placed < leaves.Count; ring++)
                {
                    var capacity = LeafRingBaseCapacity + 2 * ring;
                    var inRing = Math.Min(capacity, leaves.Count - placed);
                    var radius = LeafBaseRadius + LeafRingStep * ring;
                    var step = LeafSpacingArc / (capacity - 1);

                    for (int i = 0; i < inRing; i++)
                    {
                        var angle = outward + (i - (inRing - 1) / 2.0) * step;
                        positions[leaves[placed + i]] = (rx + radius * Math.Cos(angle), ry + radius * Math.Sin(angle));
                    }

                    placed += inRing;
                }
            }

            return positions;
        }
    }
}
