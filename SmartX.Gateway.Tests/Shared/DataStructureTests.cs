using SmartX.Gateway.Shared.DataStructures;
using SmartX.Gateway.Shared.Models;

namespace SmartX.Gateway.Tests.Shared;

public class SortedTelemetryLogTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Returns_readings_in_time_order()
    {
        var log = new SortedTelemetryLog();
        var id = Guid.NewGuid();
        log.Add(Build.Reading(id, 3, T0.AddSeconds(30)));
        log.Add(Build.Reading(id, 1, T0));
        log.Add(Build.Reading(id, 2, T0.AddSeconds(10)));

        Assert.Equal([1d, 2d, 3d], log.GetAllOrdered().Select(r => r.Value));
        Assert.Equal(3, log.Count);
        Assert.Equal([2d], log.GetRange(T0.AddSeconds(5), T0.AddSeconds(15)).Select(r => r.Value));
    }

    [Fact]
    public void The_limit_drops_the_oldest_readings()
    {
        var log = new SortedTelemetryLog(5000);
        var id = Guid.NewGuid();
        for (int i = 0; i < 5003; i++)
        {
            log.Add(Build.Reading(id, i, T0.AddSeconds(i)));
        }

        Assert.Equal(5000, log.Count);
        Assert.Equal(3d, log.GetAllOrdered()[0].Value);
        Assert.Equal(5002d, log.GetAllOrdered()[^1].Value);
    }

    [Fact]
    public void Trimming_can_split_a_same_second_bucket()
    {
        var log = new SortedTelemetryLog(3);
        var id = Guid.NewGuid();
        log.Add(Build.Reading(id, 1, T0));
        log.Add(Build.Reading(id, 2, T0.AddMilliseconds(500)));
        log.Add(Build.Reading(id, 3, T0.AddSeconds(5)));
        log.Add(Build.Reading(id, 4, T0.AddSeconds(5).AddMilliseconds(100)));

        Assert.Equal(3, log.Count);
        Assert.Equal(2, log.BucketCount);
        Assert.Equal([2d, 3d, 4d], log.GetAllOrdered().Select(r => r.Value));
    }

    [Fact]
    public void Clear_empties_the_log()
    {
        var log = new SortedTelemetryLog();
        log.Add(Build.Reading(Guid.NewGuid(), 1, T0));
        log.Clear();

        Assert.Equal(0, log.Count);
        Assert.True(log.IsEmpty);
        Assert.Empty(log.GetAllOrdered());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_invalid_limit_throws(int max)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SortedTelemetryLog(max));
    }
}

public class TelemetryAvlTreeTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Range_queries_return_matching_readings_in_order()
    {
        var tree = new TelemetryAvlTree();
        var id = Guid.NewGuid();
        foreach (var second in new[] { 50, 10, 40, 20, 30, 0, 60 })
        {
            tree.Insert(Build.Reading(id, second, T0.AddSeconds(second)));
        }

        Assert.Equal([20d, 30d, 40d], tree.GetRange(T0.AddSeconds(15), T0.AddSeconds(40)).Select(r => r.Value));
        Assert.Equal(7, tree.Count);
        Assert.Empty(tree.GetRange(T0.AddSeconds(61), T0.AddSeconds(99)));
    }

    [Fact]
    public void Duplicate_timestamps_are_all_returned()
    {
        var tree = new TelemetryAvlTree();
        for (int i = 0; i < 20; i++)
        {
            tree.Insert(Build.Reading(Guid.NewGuid(), i, T0));
        }

        Assert.Equal(20, tree.GetRange(T0, T0).Count);
    }

    [Fact]
    public void The_limit_keeps_only_the_newest_readings()
    {
        var tree = new TelemetryAvlTree(100);
        var random = new Random(42);
        var seconds = Enumerable.Range(0, 1000).OrderBy(_ => random.Next()).ToList();
        foreach (var second in seconds)
        {
            tree.Insert(Build.Reading(Guid.NewGuid(), second, T0.AddSeconds(second)));
        }

        Assert.Equal(100, tree.Count);
        var all = tree.GetRange(DateTime.MinValue, DateTime.MaxValue);
        Assert.Equal(100, all.Count);
        Assert.Equal(all.OrderBy(r => r.Timestamp).Select(r => r.Value), all.Select(r => r.Value));

        // Only the current oldest is ever dropped, so whatever the insertion order the survivors are exactly the newest 100.
        Assert.Equal(Enumerable.Range(900, 100).Select(i => (double)i), all.Select(r => r.Value));
    }

    [Fact]
    public void The_limit_drops_the_oldest_when_inserted_in_order()
    {
        var tree = new TelemetryAvlTree(5000);
        for (int i = 0; i < 5010; i++)
        {
            tree.Insert(Build.Reading(Guid.NewGuid(), i, T0.AddSeconds(i)));
        }

        var all = tree.GetRange(DateTime.MinValue, DateTime.MaxValue);
        Assert.Equal(5000, tree.Count);
        Assert.Equal(10d, all[0].Value);
        Assert.Equal(5009d, all[^1].Value);
    }

    [Fact]
    public void Clear_empties_the_tree()
    {
        var tree = new TelemetryAvlTree();
        tree.Insert(Build.Reading(Guid.NewGuid(), 1, T0));
        tree.Clear();

        Assert.Equal(0, tree.Count);
        Assert.Empty(tree.GetRange(DateTime.MinValue, DateTime.MaxValue));
    }

    [Fact]
    public void An_invalid_limit_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TelemetryAvlTree(0));
    }
}

public class ClearMethodTests
{
    [Fact]
    public void DeviceRegistry_Clear_removes_every_device()
    {
        var registry = new DeviceRegistry();
        var sensor = new SensorProfile { MacAddress = "00:1B:44:00:00:AA", Location = "Assembly - Line 1" };
        registry.Register(sensor);
        registry.Register(new SensorProfile { MacAddress = "00:1B:44:00:00:AB", Location = "Assembly - Line 1" });

        registry.Clear();

        Assert.Equal(0, registry.Count);
        Assert.False(registry.TryGet("00:1B:44:00:00:AA", out _));
        Assert.False(registry.TryGetById(sensor.Id, out _));

        // The same MAC and ID can be registered again after a clear.
        registry.Register(sensor);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void ActiveAlertSet_Clear_returns_how_many_were_open()
    {
        var alerts = new ActiveAlertSet();
        alerts.Add(Guid.NewGuid());
        alerts.Add(Guid.NewGuid());

        Assert.Equal(2, alerts.Clear());
        Assert.True(alerts.IsEmpty);
        Assert.Equal(0, alerts.Clear());
    }

    [Fact]
    public void CommandHistoryStack_Clear_empties_the_stack()
    {
        var stack = new CommandHistoryStack<ManualCommand>();
        stack.Push(new ManualCommand(Guid.NewGuid(), "00:1B:44:00:00:01", "RestartSensor", null, DateTime.UtcNow));

        stack.Clear();

        Assert.True(stack.IsEmpty);
        Assert.False(stack.TryPop(out _));
        Assert.Null(stack.Peek());
    }
}

public class MeshNetworkGraphTests
{
    // S reaches G through A (cost 1 + 5) or, more expensively, through B (3 + 5).
    private static MeshNetworkGraph Diamond()
    {
        var graph = new MeshNetworkGraph();
        graph.AddNode("G", isGateway: true);
        graph.AddNode("A");
        graph.AddNode("B");
        graph.AddNode("S");
        graph.AddEdge("S", "A", 1);
        graph.AddEdge("A", "G", 5);
        graph.AddEdge("S", "B", 3);
        graph.AddEdge("B", "G", 5);
        return graph;
    }

    [Fact]
    public void Finds_the_cheapest_route_and_its_cost()
    {
        var graph = Diamond();
        var path = graph.FindShortestPath("S", "G");

        Assert.Equal(["S", "A", "G"], path);
        Assert.Equal(6, graph.GetPathCost(path));
    }

    [Fact]
    public void Reroutes_around_an_offline_node_and_remembers_the_baseline()
    {
        var graph = Diamond();
        graph.SetNodeFailure("A", isOffline: true);

        var path = graph.FindShortestPath("S", "G");
        Assert.Equal(["S", "B", "G"], path);
        Assert.Equal(8, graph.GetPathCost(path));
        Assert.Equal(["S", "A", "G"], graph.FindShortestPath("S", "G", ignoreOffline: true));
        Assert.True(graph.IsOffline("A"));
    }

    [Fact]
    public void An_isolated_node_has_no_route_until_restored()
    {
        var graph = Diamond();
        graph.SetNodeFailure("A", isOffline: true);
        graph.SetNodeFailure("B", isOffline: true);

        Assert.Empty(graph.FindShortestPath("S", "G"));

        graph.SetNodeFailure("A", isOffline: false);
        Assert.Equal(["S", "A", "G"], graph.FindShortestPath("S", "G"));

        Assert.Equal(1, graph.RestoreAll());
        Assert.False(graph.IsOffline("B"));
    }

    [Fact]
    public void An_offline_start_node_has_no_route()
    {
        var graph = Diamond();
        graph.SetNodeFailure("S", isOffline: true);
        Assert.Empty(graph.FindShortestPath("S", "G"));
    }

    [Fact]
    public void Path_cost_rejects_hops_that_are_not_linked()
    {
        Assert.Throws<InvalidOperationException>(() => Diamond().GetPathCost(["S", "G"]));
    }
}
