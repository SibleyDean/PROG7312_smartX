using SmartX.Gateway.Shared.Models;

namespace SmartX.Gateway.Shared.Services;

// Turns the live mesh into the Gateway > Router > Sensor tree that DeploymentValidator checks.
public static class MeshDeploymentTree
{
    public const string SiteZone = "Site";

    public static DeploymentNode Build(MeshTopology topology)
    {
        ArgumentNullException.ThrowIfNull(topology);

        var gateway = topology.Nodes.FirstOrDefault(n => n.IsGateway || n.Role == MeshNodeRole.Gateway);
        var root = new DeploymentNode { NodeName = gateway?.MacAddress ?? "Gateway", ZoneId = SiteZone };
        var routers = new Dictionary<string, DeploymentNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var router in topology.Nodes.Where(n => n.Role == MeshNodeRole.Router))
        {
            var node = new DeploymentNode { NodeName = router.MacAddress, ZoneId = router.Area };
            routers[router.MacAddress] = node;
            root.Children.Add(node);
        }

        foreach (var sensor in topology.Nodes.Where(n => n.Role == MeshNodeRole.Sensor && !n.IsGateway))
        {
            var node = new DeploymentNode { NodeName = sensor.MacAddress, ZoneId = FactoryAreas.AreaOf(sensor.Location) };
            var parent = sensor.ParentMac is not null && routers.TryGetValue(sensor.ParentMac, out var router) ? router : root;
            parent.Children.Add(node);
        }

        return root;
    }
}
