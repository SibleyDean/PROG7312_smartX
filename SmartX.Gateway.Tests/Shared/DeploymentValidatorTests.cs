using SmartX.Gateway.Services;
using SmartX.Gateway.Shared.Models;
using SmartX.Gateway.Shared.Services;

namespace SmartX.Gateway.Tests.Shared;

public class DeploymentValidatorTests
{
    private readonly DeploymentValidator _validator = new();

    private static DeploymentNode Node(string name, string zone, params DeploymentNode[] children) =>
        new() { NodeName = name, ZoneId = zone, Children = children.ToList() };

    [Fact]
    public void Bool_check_is_unchanged()
    {
        Assert.True(_validator.ValidateDeploymentTree(Node("Plant", "Site", Node("R1", "Assembly", Node("S1", "Assembly")))));
        Assert.False(_validator.ValidateDeploymentTree(Node("Plant", "Site", Node("R1", ""))));
        Assert.False(_validator.ValidateDeploymentTree(Node("", "Site")));
        Assert.False(_validator.ValidateDeploymentTree(null!));
        Assert.True(_validator.ValidateDeploymentTree(new DeploymentNode { NodeName = "Plant", ZoneId = "Site", Children = null! }));

        // The bool check never looked at zone consistency or duplicates, and still does not.
        Assert.True(_validator.ValidateDeploymentTree(Node("Plant", "Site", Node("R1", "Assembly", Node("S1", "Packaging")))));
    }

    [Fact]
    public void Reasons_name_every_failing_node()
    {
        var tree = Node("Plant", "Site",
            Node("R1", "Assembly",
                Node("S1", "Packaging"),
                Node("S2", ""),
                Node("", "Assembly")),
            Node("R2", "Packaging",
                Node("S1", "Packaging")));

        var result = _validator.ValidateWithReasons(tree);

        Assert.False(result.IsValid);
        Assert.Equal(7, result.NodesChecked);
        Assert.Contains(result.Issues, i => i.NodeId == "S1" && i.Reason.Contains("does not match its parent's zone 'Assembly'"));
        Assert.Contains(result.Issues, i => i.NodeId == "S2" && i.Reason == "Zone is missing.");
        Assert.Contains(result.Issues, i => i.NodeId == "(unnamed node under R1)" && i.Reason == "Node name is missing.");
        Assert.Contains(result.Issues, i => i.NodeId == "S1" && i.Reason == "Appears more than once in the tree.");
        Assert.Equal(4, result.Issues.Count);
    }

    [Fact]
    public void A_clean_tree_passes_and_routers_may_use_any_zone()
    {
        var result = _validator.ValidateWithReasons(Node("Plant", "Site", Node("R1", "Assembly", Node("S1", "assembly")), Node("R2", "Packaging")));

        Assert.True(result.IsValid);
        Assert.Empty(result.Issues);
        Assert.Equal(4, result.NodesChecked);
    }

    [Fact]
    public void A_missing_tree_fails_with_a_reason()
    {
        var result = _validator.ValidateWithReasons(null);
        Assert.False(result.IsValid);
        Assert.Single(result.Issues);
    }

    [Fact]
    public void The_live_mesh_validates_until_a_sensor_sits_under_another_areas_router()
    {
        var service = Build.Service(out var mesh, out _);
        Assert.True(_validator.ValidateWithReasons(MeshDeploymentTree.Build(service.GetTopology())).IsValid);

        // "Rooftop" is not a factory area, so the sensor joins the emptiest router and its zone no longer matches.
        service.RegisterSensor(new() { MacAddress = "AA:BB:CC:00:00:01", Location = "Rooftop", Source = SensorSource.Manual });
        var result = _validator.ValidateWithReasons(MeshDeploymentTree.Build(service.GetTopology()));

        Assert.False(result.IsValid);
        var issue = Assert.Single(result.Issues);
        Assert.Equal("AA:BB:CC:00:00:01", issue.NodeId);
        Assert.Contains("Rooftop", issue.Reason);
        Assert.NotNull(mesh.GetRouterOf("AA:BB:CC:00:00:01"));
    }
}
