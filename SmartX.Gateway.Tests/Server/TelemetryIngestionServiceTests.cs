using System.Text.RegularExpressions;
using SmartX.Gateway.Services;
using SmartX.Gateway.Shared.DataStructures;
using SmartX.Gateway.Shared.Models;
using SmartX.Gateway.Shared.Services;

namespace SmartX.Gateway.Tests.Server;

public class TelemetryIngestionServiceTests
{
    private const string MeshEnvironmental = "00:1B:44:11:3A:01";
    private const string MeshActuator = "00:1B:44:11:3A:03";

    private static SensorProfile Sensor(TelemetryIngestionService service, string mac) =>
        service.GetSensors().Single(s => s.MacAddress == mac);

    private static void Add(TelemetryIngestionService service, string mac, double value, DateTime? at = null) =>
        service.AddTelemetryBatch(Sensor(service, mac).Id, [Build.Reading(Guid.Empty, value, at ?? DateTime.UtcNow)]);

    [Fact]
    public void Starts_with_the_six_mesh_sensors()
    {
        var sensors = Build.Service().GetSensors();

        Assert.Equal(6, sensors.Count);
        Assert.All(sensors, s => Assert.Equal(SensorSource.Mesh, s.Source));
        Assert.All(sensors, s => Assert.Matches(@"^[A-Za-z ]+ - Line \d$", s.Location));
    }

    [Theory]
    [InlineData(97.0, 1)]
    [InlineData(2.0, 1)]
    [InlineData(92.0, 2)]
    [InlineData(7.0, 2)]
    public void Bad_readings_take_the_alert_path(double value, int priority)
    {
        var service = Build.Service();
        var sensor = Sensor(service, MeshEnvironmental);

        Add(service, MeshEnvironmental, value);

        Assert.Equal(priority, TelemetryIngestionService.GetAlertPriority(value));
        Assert.True(service.GetActiveAlerts().Contains(sensor.Id));
        Assert.Equal(1, service.GetAlertQueue().Count);
        Assert.Equal(0, service.GetIngestQueue().Count);
        var detail = Assert.Single(service.GetAlertDetails());
        Assert.Equal((sensor.Id, value, priority), (detail.SensorId, detail.Value, detail.Priority));
    }

    [Fact]
    public void Normal_readings_take_the_standard_queue()
    {
        var service = Build.Service();
        Add(service, MeshEnvironmental, 50);

        Assert.Null(TelemetryIngestionService.GetAlertPriority(50));
        Assert.Equal(1, service.GetIngestQueue().Count);
        Assert.Equal(0, service.GetAlertQueue().Count);
        Assert.Empty(service.GetAlertDetails());
    }

    [Fact]
    public void Alert_detail_keeps_the_latest_bad_reading()
    {
        var service = Build.Service();
        var now = DateTime.UtcNow;
        Add(service, MeshEnvironmental, 92, now);
        Add(service, MeshEnvironmental, 98, now.AddSeconds(-30));

        Assert.Equal(92, Assert.Single(service.GetAlertDetails()).Value);
    }

    [Fact]
    public void History_keeps_the_newest_200_readings()
    {
        var service = Build.Service();
        var id = Sensor(service, MeshEnvironmental).Id;
        var start = DateTime.UtcNow.AddHours(-1);
        service.AddTelemetryBatch(id, Enumerable.Range(0, 210).Select(i => Build.Reading(id, 40 + i % 10, start.AddSeconds(i))));

        var history = Sensor(service, MeshEnvironmental).HistoricalTelemetry;
        Assert.Equal(200, history.Count);
        Assert.Equal(start.AddSeconds(10), history.Min(r => r.Timestamp));
        Assert.Equal(start.AddSeconds(209), history.Max(r => r.Timestamp));
    }

    [Fact]
    public void GetSensors_can_limit_each_history_to_the_newest_readings()
    {
        var service = Build.Service();
        var id = Sensor(service, MeshEnvironmental).Id;
        var start = DateTime.UtcNow.AddMinutes(-5);
        service.AddTelemetryBatch(id, Enumerable.Range(0, 30).Select(i => Build.Reading(id, 50, start.AddSeconds(i))));

        var limited = service.GetSensors(5).Single(s => s.Id == id).HistoricalTelemetry;
        Assert.Equal(5, limited.Count);
        Assert.Equal(start.AddSeconds(25), limited.Min(r => r.Timestamp));
        Assert.Equal(30, service.GetSensors().Single(s => s.Id == id).HistoricalTelemetry.Count);
        Assert.Empty(service.GetSensors(0).Single(s => s.Id == id).HistoricalTelemetry);
    }

    [Fact]
    public void Seeding_builds_realistic_demo_sensors_attached_to_their_area_router()
    {
        var service = Build.Service(out var mesh, out _);
        var before = DateTime.UtcNow;
        var seeded = service.SeedMockData(20, new Random(42));
        var areas = string.Join("|", FactoryAreas.All.Select(Regex.Escape));

        Assert.Equal(20, seeded.Count);
        Assert.Equal(26, service.GetSensors().Count);
        Assert.Equal(26, service.GetSensors().Select(s => s.MacAddress).Distinct().Count());

        foreach (var sensor in seeded)
        {
            Assert.Equal(SensorSource.Seeded, sensor.Source);
            Assert.Matches("^00:1B:44:[0-9A-F]{2}:[0-9A-F]{2}:[0-9A-F]{2}$", sensor.MacAddress);
            Assert.Matches($"^({areas}) - Line [1-4]$", sensor.Location);
            Assert.Equal(MeshNetworkService.RouterMacFor(FactoryAreas.AreaOf(sensor.Location)), mesh.GetRouterOf(sensor.MacAddress));

            var readings = sensor.HistoricalTelemetry;
            Assert.InRange(readings.Count, 8, 20);
            Assert.All(readings, r => Assert.Equal(sensor.Id, r.SensorId));
            for (int i = 1; i < readings.Count; i++)
            {
                Assert.InRange((readings[i].Timestamp - readings[i - 1].Timestamp).TotalSeconds, 20, 90);
            }

            var lastAge = (before - readings[^1].Timestamp).TotalSeconds;
            Assert.InRange(lastAge, 4, 31);
        }
    }

    [Fact]
    public void Seeding_never_reuses_a_mesh_or_router_mac()
    {
        // The first MAC picked is a mesh sensor's, the second a router's; both must be skipped.
        var service = Build.Service(out var mesh, out _);
        var random = new ScriptedRandom(0x11, 0x3A, 0x01, 0x00, 0x00, 0x01, 0xAB, 0xCD, 0xEF);

        var sensor = Assert.Single(service.SeedMockData(1, random));

        Assert.Equal("00:1B:44:AB:CD:EF", sensor.MacAddress);
        Assert.True(mesh.ContainsNode(sensor.MacAddress));
    }

    [Fact]
    public void Generation_stops_when_paused()
    {
        var service = Build.Service();
        service.SetGenerationPaused(true);

        Assert.False(service.GenerateNextReading(new Random(1)));
        Assert.All(service.GetSensors(), s => Assert.Empty(s.HistoricalTelemetry));

        service.SetGenerationPaused(false);
        Assert.True(service.GenerateNextReading(new Random(1)));
    }

    [Fact]
    public void Generation_has_nothing_to_do_when_no_sensor_can_reach_the_gateway()
    {
        var service = Build.Service(out var mesh, out _);
        mesh.SetNodeFailure(MeshNetworkService.GatewayMac, isOffline: true);

        Assert.False(service.GenerateNextReading(new Random(1)));
    }

    [Fact]
    public void Generation_skips_offline_and_unreachable_sensors()
    {
        var service = Build.Service(out var mesh, out _);
        var target = MeshActuator;

        // Every other mesh sensor is offline, except one that is cut off by failing its router and its backup router.
        var others = MeshNetworkService.BaseSensors.Select(s => s.MacAddress).Where(m => m != target).ToList();
        var cutOff = others[0];
        var router = mesh.GetRouterOf(cutOff)!;
        mesh.SetNodeFailure(router, isOffline: true);
        var backup = mesh.GetRoute(cutOff).Path[1];
        mesh.SetNodeFailure(backup, isOffline: true);
        foreach (var mac in others.Skip(1))
        {
            mesh.SetNodeFailure(mac, isOffline: true);
        }

        Assume(mesh.IsReporting(target), "the target must still have a route for this test to mean anything");
        Assert.False(mesh.IsReporting(cutOff));

        for (int i = 0; i < 25; i++)
        {
            Assert.True(service.GenerateNextReading(new Random(i)));
        }

        Assert.Equal(25, Sensor(service, target).HistoricalTelemetry.Count);
        Assert.All(others, mac => Assert.Empty(Sensor(service, mac).HistoricalTelemetry));
    }

    [Fact]
    public void Restart_brings_the_reading_back_to_nominal()
    {
        var service = Build.Service();
        Add(service, MeshEnvironmental, 97);

        var reading = service.ApplyCommandEffect(MeshEnvironmental, CommandCatalog.RestartSensor);

        Assert.NotNull(reading);
        Assert.InRange(reading!.Value, 44, 46);
        Assert.Equal(reading.Value, Sensor(service, MeshEnvironmental).HistoricalTelemetry.MaxBy(r => r.Timestamp)!.Value);
    }

    [Theory]
    [InlineData(50, "OpenValve", false, 65)]
    [InlineData(50, "CloseValve", false, 35)]
    [InlineData(95, "OpenValve", false, 100)]
    [InlineData(5, "CloseValve", false, 0)]
    [InlineData(65, "OpenValve", true, 50)]
    [InlineData(35, "CloseValve", true, 50)]
    public void Valve_commands_move_an_actuator_by_fifteen_points(double start, string command, bool reverse, double expected)
    {
        var service = Build.Service();
        Add(service, MeshActuator, start, DateTime.UtcNow.AddSeconds(-1));

        var reading = service.ApplyCommandEffect(MeshActuator, command, reverse);

        Assert.Equal(expected, reading!.Value, 6);
    }

    [Fact]
    public void Commands_without_a_simulated_effect_return_null()
    {
        var service = Build.Service();

        Assert.Null(service.ApplyCommandEffect(MeshEnvironmental, CommandCatalog.OpenValve));
        Assert.Null(service.ApplyCommandEffect(MeshEnvironmental, CommandCatalog.RestartSensor, reverse: true));
        Assert.Null(service.ApplyCommandEffect("00:1B:44:11:3A:02", CommandCatalog.ToggleRelay));
        Assert.Null(service.ApplyCommandEffect("FF:FF:FF:FF:FF:FF", CommandCatalog.RestartSensor));
    }

    [Fact]
    public void ClearAll_removes_demo_and_manual_sensors_and_resets_the_mesh()
    {
        var service = Build.Service(out var mesh, out var engine);
        var seeded = service.SeedMockData(5, new Random(7));
        service.RegisterSensor(new SensorProfile { MacAddress = "AA:BB:CC:00:00:01", Location = "Paint Shop - Line 1", Source = SensorSource.Manual });
        Add(service, MeshEnvironmental, 99);
        service.GetCommandHistory().Push(new ManualCommand(Guid.NewGuid(), MeshEnvironmental, "RestartSensor", null, DateTime.UtcNow));
        engine.RecordCriticalEvent(Guid.NewGuid(), MeshEnvironmental, 99);
        engine.RecordCriticalEvent(Guid.NewGuid(), MeshEnvironmental, 99);
        engine.RecordCriticalEvent(Guid.NewGuid(), MeshEnvironmental, 99);
        mesh.SetNodeFailure(mesh.GetRouterOf(MeshActuator)!, isOffline: true);
        Assert.NotEmpty(engine.GetSuggestedActions());

        service.ClearAll();

        var sensors = service.GetSensors();
        Assert.Equal(6, sensors.Count);
        Assert.All(sensors, s => Assert.Equal(SensorSource.Mesh, s.Source));
        Assert.All(sensors, s => Assert.Empty(s.HistoricalTelemetry));
        Assert.Empty(service.GetActiveAlerts().GetAll());
        Assert.Empty(service.GetAlertDetails());
        Assert.Equal(0, service.GetTelemetryLog().Count);
        Assert.Equal(0, service.GetAvlLog().Count);
        Assert.True(service.GetCommandHistory().IsEmpty);
        Assert.Empty(engine.GetSuggestedActions());
        Assert.Empty(mesh.GetConnectionProblems());
        Assert.All(seeded, s => Assert.False(mesh.ContainsNode(s.MacAddress)));
        Assert.False(mesh.ContainsNode("AA:BB:CC:00:00:01"));
        Assert.Equal(0, service.GetSummary().AlertsAcknowledged);
    }

    [Fact]
    public void Summary_counts_sensors_alerts_readings_and_connection_problems()
    {
        var service = Build.Service(out var mesh, out _);
        service.SeedMockData(2, new Random(3));
        var id = Sensor(service, MeshEnvironmental).Id;
        service.AcknowledgeAllAlerts();
        service.AddTelemetryBatch(id, [Build.Reading(id, 50, DateTime.UtcNow), Build.Reading(id, 99, DateTime.UtcNow.AddSeconds(-90))]);
        mesh.SetNodeFailure(mesh.GetRouterOf(MeshEnvironmental)!, isOffline: true);

        var summary = service.GetSummary();

        Assert.Equal(8, summary.SensorCount);
        Assert.Equal(2, summary.DemoSensorCount);
        Assert.Equal(1, summary.OpenAlerts);
        Assert.True(summary.ReadingsLastMinute >= 1);
        Assert.Equal(service.GetSensors().Sum(s => s.HistoricalTelemetry.Count), summary.TotalReadings);
        Assert.Equal(1, summary.ConnectionProblems);
        Assert.Equal("Assembly router is offline (simulated)", Assert.Single(summary.ConnectionIssues));
    }

    [Fact]
    public void Acknowledging_counts_as_handled()
    {
        var service = Build.Service();
        Add(service, MeshEnvironmental, 99);
        Add(service, MeshActuator, 1);

        Assert.True(service.AcknowledgeAlert(Sensor(service, MeshEnvironmental).Id));
        Assert.False(service.AcknowledgeAlert(Sensor(service, MeshEnvironmental).Id));
        Assert.Equal(1, service.AcknowledgeAllAlerts());
        Assert.Equal(2, service.GetSummary().AlertsAcknowledged);
        Assert.Equal(0, service.GetSummary().OpenAlerts);
    }

    [Fact]
    public void Manual_registration_joins_the_mesh_and_rejects_mesh_macs()
    {
        var service = Build.Service(out var mesh, out _);
        service.RegisterSensor(new SensorProfile { MacAddress = "AA-BB-CC-00-00-02", Location = "Paint Shop - Line 3" });

        Assert.Equal(MeshNetworkService.RouterMacFor("Paint Shop"), mesh.GetRouterOf("AA:BB:CC:00:00:02"));
        Assert.Throws<InvalidOperationException>(() => service.RegisterSensor(new SensorProfile { MacAddress = MeshNetworkService.RouterMacFor("Assembly"), Location = "Assembly - Line 1" }));
        Assert.Throws<InvalidOperationException>(() => service.RegisterSensor(new SensorProfile { MacAddress = MeshEnvironmental, Location = "Assembly - Line 1" }));
    }

    [Fact]
    public void Topology_includes_registry_details_for_every_sensor()
    {
        var service = Build.Service();
        var seeded = service.SeedMockData(3, new Random(5));

        var topology = service.GetTopology();
        var sensorNodes = topology.Nodes.Where(n => n.Role == MeshNodeRole.Sensor).ToList();

        Assert.Equal(9, sensorNodes.Count);
        Assert.All(sensorNodes, n =>
        {
            Assert.NotNull(n.SensorId);
            Assert.NotNull(n.Location);
            Assert.NotNull(n.Category);
            Assert.NotNull(n.Source);
            Assert.NotNull(n.ParentMac);
        });
        var first = sensorNodes.Single(n => n.MacAddress == seeded[0].MacAddress);
        Assert.Equal(seeded[0].Id, first.SensorId);
        Assert.Equal(SensorSource.Seeded, first.Source);
        Assert.Equal(seeded[0].HistoricalTelemetry[^1].Value, first.LatestValue);
    }

    private static void Assume(bool condition, string because) => Assert.True(condition, because);
}

public class MeshNetworkServiceTests
{
    [Fact]
    public void Base_mesh_has_a_gateway_four_routers_and_six_sensors_all_routed()
    {
        var topology = new MeshNetworkService().GetTopology();

        Assert.Single(topology.Nodes, n => n.Role == MeshNodeRole.Gateway);
        Assert.Equal(["Assembly", "Packaging", "Boiler Room", "Cold Storage"],
            topology.Nodes.Where(n => n.Role == MeshNodeRole.Router).Select(n => n.Area).OrderBy(FactoryAreas.IndexOf));
        Assert.Equal(6, topology.Nodes.Count(n => n.Role == MeshNodeRole.Sensor));
        Assert.All(topology.Nodes, n => Assert.True(n.HasRoute));
        Assert.All(topology.Nodes, n => Assert.False(n.IsRerouted));
    }

    [Theory]
    [InlineData("00:1B:44:11:3A:04", "Boiler Room", "Cold Storage")] // Packaging and Cold Storage are equally near; ties go clockwise
    [InlineData("00:1B:44:11:3A:01", "Assembly", "Packaging")]
    [InlineData("00:1B:44:11:3A:06", "Cold Storage", "Boiler Room")]
    public void A_failed_router_reroutes_its_sensors_through_the_nearest_router(string sensor, string area, string backupArea)
    {
        var mesh = new MeshNetworkService();
        var boiler = MeshNetworkService.RouterMacFor(area);

        Assert.Equal([sensor, boiler, MeshNetworkService.GatewayMac], mesh.GetRoute(sensor).Path);

        mesh.SetNodeFailure(boiler, isOffline: true);
        var route = mesh.GetRoute(sensor);

        Assert.Equal([sensor, MeshNetworkService.RouterMacFor(backupArea), MeshNetworkService.GatewayMac], route.Path);
        Assert.Equal([sensor, boiler, MeshNetworkService.GatewayMac], route.BaselinePath);
        Assert.Equal(MeshNetworkService.BackupLinkCost + 25, route.Cost, 6);
        Assert.True(mesh.GetTopology().Nodes.Single(n => n.MacAddress == sensor).IsRerouted);
        Assert.Equal($"{area} router is offline (simulated)", Assert.Single(mesh.GetConnectionProblems()).Message);
    }

    [Fact]
    public void Losing_both_routers_leaves_a_sensor_with_no_route_until_restored()
    {
        var mesh = new MeshNetworkService();
        const string sensor = "00:1B:44:11:3A:04";
        mesh.SetNodeFailure(MeshNetworkService.RouterMacFor("Boiler Room"), isOffline: true);
        mesh.SetNodeFailure(MeshNetworkService.RouterMacFor("Cold Storage"), isOffline: true);

        Assert.Empty(mesh.GetRoute(sensor).Path);
        Assert.False(mesh.IsReporting(sensor));
        Assert.Contains(mesh.GetConnectionProblems(), p => p.MacAddress == sensor && p.Kind == ConnectionProblemKind.NoRoute
            && p.Message == "Boiler Room - Line 1 has no route to the gateway");
        Assert.False(mesh.GetTopology().Nodes.Single(n => n.MacAddress == sensor).HasRoute);

        Assert.Equal(2, mesh.RestoreAll());
        Assert.True(mesh.IsReporting(sensor));
        Assert.Empty(mesh.GetConnectionProblems());
    }

    [Fact]
    public void Attaching_a_sensor_in_a_new_area_adds_that_router()
    {
        var mesh = new MeshNetworkService();
        var router = mesh.AttachSensor("AA:00:00:00:00:01", "Press Shop - Line 4");

        Assert.Equal(MeshNetworkService.RouterMacFor("Press Shop"), router);
        Assert.True(mesh.IsRouter(router));
        Assert.NotEmpty(mesh.FindRouteToGateway("AA:00:00:00:00:01"));
        Assert.Equal(router, mesh.AttachSensor("AA:00:00:00:00:01", "Press Shop - Line 4"));
    }

    [Fact]
    public void Unknown_areas_join_the_router_with_the_fewest_sensors()
    {
        var mesh = new MeshNetworkService();

        // Packaging and Cold Storage have one base sensor each; Packaging comes first in the area list.
        Assert.Equal(MeshNetworkService.RouterMacFor("Packaging"), mesh.AttachSensor("AA:00:00:00:00:02", "Rooftop"));
    }

    [Fact]
    public void Layout_keeps_sixty_sensors_apart()
    {
        var service = Build.Service();
        service.SeedMockData(50, new Random(11));
        var nodes = service.GetTopology().Nodes;

        Assert.Equal(56, nodes.Count(n => n.Role == MeshNodeRole.Sensor));
        for (int i = 0; i < nodes.Count; i++)
        {
            for (int j = i + 1; j < nodes.Count; j++)
            {
                var distance = Math.Sqrt(Math.Pow(nodes[i].X - nodes[j].X, 2) + Math.Pow(nodes[i].Y - nodes[j].Y, 2));
                Assert.True(distance > 0.3, $"{nodes[i].MacAddress} and {nodes[j].MacAddress} overlap ({distance:0.00} apart)");
            }
        }
    }

    [Fact]
    public void Reset_restores_the_base_mesh()
    {
        var mesh = new MeshNetworkService();
        mesh.AttachSensor("AA:00:00:00:00:03", "Paint Shop - Line 1");
        mesh.SetNodeFailure(MeshNetworkService.RouterMacFor("Assembly"), isOffline: true);

        mesh.Reset();

        Assert.False(mesh.ContainsNode("AA:00:00:00:00:03"));
        Assert.False(mesh.ContainsNode(MeshNetworkService.RouterMacFor("Paint Shop")));
        Assert.Empty(mesh.GetConnectionProblems());
        Assert.Equal(11, mesh.GetTopology().Nodes.Count);
    }
}

public class TelemetryBackgroundProcessorTests
{
    [Fact]
    public void Generation_delay_is_always_between_four_and_twelve_seconds()
    {
        var random = new Random(42);
        var delays = Enumerable.Range(0, 10_000).Select(_ => TelemetryBackgroundProcessor.NextGenerationDelay(random).TotalSeconds).ToList();

        Assert.All(delays, d => Assert.InRange(d, 4, 12));
        Assert.True(delays.Min() < 4.1 && delays.Max() > 11.9, "the whole range should be used");
        Assert.Equal(4, TelemetryBackgroundProcessor.NextGenerationDelay(new FixedRandom(0)).TotalSeconds, 6);
    }
}
