using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SmartX.Gateway.Services;
using SmartX.Gateway.Shared.DataStructures;
using SmartX.Gateway.Shared.Models;
using SmartX.Gateway.Shared.Services;

namespace SmartX.Gateway.Tests.Api;

// Each test gets its own in-memory gateway, so the singletons never leak state between tests.
public sealed class GatewayApiTests : IDisposable
{
    private const string MeshEnvironmental = "00:1B:44:11:3A:01";
    private const string MeshPower = "00:1B:44:11:3A:02";
    private const string MeshActuator = "00:1B:44:11:3A:03";

    private readonly GatewayFactory _factory = new();
    private readonly HttpClient _client;

    public GatewayApiTests()
    {
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    private async Task<List<SensorProfile>> SensorsAsync(string query = "") =>
        (await _client.GetFromJsonAsync<List<SensorProfile>>($"/api/sensors{query}"))!;

    private async Task<Guid> IdOfAsync(string mac) => (await SensorsAsync()).Single(s => s.MacAddress == mac).Id;

    private Task<HttpResponseMessage> IssueAsync(string mac, string command) =>
        _client.PostAsJsonAsync("/api/commands/issue", new ManualCommand(Guid.Empty, mac, command, null, DateTime.UtcNow));

    private async Task PostReadingAsync(string mac, double value) =>
        (await _client.PostAsJsonAsync($"/api/sensors/{await IdOfAsync(mac)}/telemetry",
            new[] { new TelemetryReading<double> { Value = value, Timestamp = DateTime.UtcNow } })).EnsureSuccessStatusCode();

    [Fact]
    public async Task Api_responses_are_never_cached()
    {
        var response = await _client.GetAsync("/api/summary");

        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Theory]
    [InlineData(0, HttpStatusCode.BadRequest)]
    [InlineData(-3, HttpStatusCode.BadRequest)]
    [InlineData(51, HttpStatusCode.BadRequest)]
    [InlineData(1, HttpStatusCode.OK)]
    [InlineData(50, HttpStatusCode.OK)]
    public async Task Seed_count_must_be_between_1_and_50(int count, HttpStatusCode expected)
    {
        var response = await _client.PostAsync($"/api/sensors/seed/{count}", null);

        Assert.Equal(expected, response.StatusCode);
        Assert.Equal(6 + (expected == HttpStatusCode.OK ? count : 0), (await SensorsAsync()).Count);
    }

    [Fact]
    public async Task Clear_keeps_and_resets_only_the_mesh_sensors()
    {
        await _client.PostAsync("/api/sensors/seed/5", null);
        await PostReadingAsync(MeshEnvironmental, 99);

        (await _client.PostAsync("/api/sensors/clear", null)).EnsureSuccessStatusCode();

        var sensors = await SensorsAsync();
        Assert.Equal(6, sensors.Count);
        Assert.All(sensors, s => Assert.Equal(SensorSource.Mesh, s.Source));
        Assert.All(sensors, s => Assert.Empty(s.HistoricalTelemetry));
        Assert.Empty((await _client.GetFromJsonAsync<List<Guid>>("/api/alerts/active"))!);
    }

    [Fact]
    public async Task Pause_and_resume_live_generation()
    {
        Assert.False((await _client.GetFromJsonAsync<SimulationStatus>("/api/simulation"))!.IsPaused);

        Assert.True((await (await _client.PostAsync("/api/simulation/pause", null)).Content.ReadFromJsonAsync<SimulationStatus>())!.IsPaused);
        Assert.True((await _client.GetFromJsonAsync<GatewaySummary>("/api/summary"))!.IsGenerationPaused);
        Assert.False(_factory.Ingestion.GenerateNextReading(new Random(1)));

        Assert.False((await (await _client.PostAsync("/api/simulation/resume", null)).Content.ReadFromJsonAsync<SimulationStatus>())!.IsPaused);
        Assert.True(_factory.Ingestion.GenerateNextReading(new Random(1)));
    }

    [Fact]
    public async Task Summary_reports_demo_sensors_alerts_and_connection_problems()
    {
        await _client.PostAsync("/api/sensors/seed/3", null);
        await _client.PostAsync("/api/alerts/acknowledge-all", null);
        await PostReadingAsync(MeshEnvironmental, 2);
        await _client.PostAsync($"/api/topology/fail/{MeshNetworkService.RouterMacFor("Cold Storage")}", null);

        var summary = (await _client.GetFromJsonAsync<GatewaySummary>("/api/summary"))!;

        Assert.Equal(9, summary.SensorCount);
        Assert.Equal(3, summary.DemoSensorCount);
        Assert.Equal(1, summary.OpenAlerts);
        Assert.True(summary.ReadingsLastMinute >= 1);
        Assert.Equal(1, summary.ConnectionProblems);
        Assert.Equal(["Cold Storage router is offline (simulated)"], summary.ConnectionIssues);
    }

    [Fact]
    public async Task Alert_details_carry_the_triggering_reading()
    {
        await PostReadingAsync(MeshEnvironmental, 97.5);
        await PostReadingAsync(MeshActuator, 8);

        var details = (await _client.GetFromJsonAsync<List<AlertDetail>>("/api/alerts/details"))!;

        Assert.Equal(2, details.Count);
        var environmentalId = await IdOfAsync(MeshEnvironmental);
        var critical = details.Single(d => d.SensorId == environmentalId);
        Assert.Equal((97.5, 1), (critical.Value, critical.Priority));
        Assert.Equal(2, details.Single(d => d.Value == 8).Priority);
    }

    [Fact]
    public async Task Acknowledge_and_acknowledge_all()
    {
        await PostReadingAsync(MeshEnvironmental, 99);
        await PostReadingAsync(MeshActuator, 1);
        await PostReadingAsync(MeshPower, 93);
        var id = await IdOfAsync(MeshEnvironmental);

        var first = await (await _client.PostAsync($"/api/alerts/{id}/resolve", null)).Content.ReadFromJsonAsync<JsonElement>();
        var again = await (await _client.PostAsync($"/api/alerts/{id}/resolve", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(first.GetProperty("resolved").GetBoolean());
        Assert.False(again.GetProperty("resolved").GetBoolean());

        var all = await (await _client.PostAsync("/api/alerts/acknowledge-all", null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, all.GetProperty("acknowledged").GetInt32());
        Assert.Empty((await _client.GetFromJsonAsync<List<AlertDetail>>("/api/alerts/details"))!);
        Assert.Equal(3, (await _client.GetFromJsonAsync<GatewaySummary>("/api/summary"))!.AlertsAcknowledged);
    }

    [Fact]
    public async Task Commands_issue_undo_and_history()
    {
        Assert.Equal(HttpStatusCode.OK, (await IssueAsync(MeshActuator, "OpenValve")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await IssueAsync(MeshEnvironmental, "RestartSensor")).StatusCode);

        var history = (await _client.GetFromJsonAsync<List<ManualCommand>>("/api/commands/history"))!;
        Assert.Equal(["RestartSensor", "OpenValve"], history.Select(c => c.CommandType));
        Assert.All(history, c => Assert.NotEqual(Guid.Empty, c.Id));

        var undone = await (await _client.PostAsync("/api/commands/undo", null)).Content.ReadFromJsonAsync<ManualCommand>();
        Assert.Equal("RestartSensor", undone!.CommandType);
        Assert.Single((await _client.GetFromJsonAsync<List<ManualCommand>>("/api/commands/history"))!);

        await _client.PostAsync("/api/commands/undo", null);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/commands/undo", null)).StatusCode);
    }

    [Fact]
    public async Task Valve_commands_and_their_undo_change_the_reading()
    {
        await PostReadingAsync(MeshActuator, 40);

        await IssueAsync(MeshActuator, "OpenValve");
        Assert.Equal(55, Latest(await SensorsAsync(), MeshActuator), 6);

        await _client.PostAsync("/api/commands/undo", null);
        Assert.Equal(40, Latest(await SensorsAsync(), MeshActuator), 6);
    }

    [Theory]
    [InlineData(MeshEnvironmental, "OpenValve", "Environmental")]
    [InlineData(MeshEnvironmental, "ToggleRelay", "Environmental")]
    [InlineData(MeshPower, "CloseValve", "PowerConsumption")]
    [InlineData(MeshActuator, "ToggleRelay", "Actuator")]
    public async Task Commands_that_do_not_fit_the_device_are_rejected(string mac, string command, string category)
    {
        var response = await IssueAsync(mac, command);
        var message = await response.Content.ReadFromJsonAsync<string>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(CommandCatalog.Label(command), message);
        Assert.Contains(category, message);
        Assert.Contains("Commands that fit", message);
        Assert.Empty((await _client.GetFromJsonAsync<List<ManualCommand>>("/api/commands/history"))!);
    }

    [Fact]
    public async Task Unknown_commands_are_rejected()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await IssueAsync(MeshActuator, "Explode")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await IssueAsync("", "RestartSensor")).StatusCode);
    }

    [Fact]
    public async Task Registration_is_manual_joins_the_mesh_and_rejects_duplicates()
    {
        var profile = new SensorProfile { MacAddress = "AA:BB:CC:DD:EE:01", Location = "Press Shop - Line 1", Category = SensorCategory.Environmental, Source = SensorSource.Mesh };

        var created = await (await _client.PostAsJsonAsync("/api/sensors/register", profile)).Content.ReadFromJsonAsync<SensorProfile>();
        Assert.Equal(SensorSource.Manual, created!.Source);

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/sensors/register",
            new SensorProfile { MacAddress = profile.MacAddress, Location = profile.Location })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/sensors/register",
            new SensorProfile { MacAddress = "aa-bb-cc-dd-ee-01", Location = "Assembly - Line 1" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsJsonAsync("/api/sensors/register",
            new SensorProfile { MacAddress = MeshNetworkService.RouterMacFor("Assembly"), Location = "Assembly - Line 1" })).StatusCode);

        var topology = (await _client.GetFromJsonAsync<MeshTopology>("/api/topology"))!;
        Assert.Equal(MeshNetworkService.RouterMacFor("Press Shop"), topology.Nodes.Single(n => n.MacAddress == "AA:BB:CC:DD:EE:01").ParentMac);
    }

    [Fact]
    public async Task Readings_query_limits_each_history_to_its_newest_readings()
    {
        await _client.PostAsync("/api/sensors/seed/4", null);
        var full = await SensorsAsync();
        var limited = await SensorsAsync("?readings=3");

        Assert.All(limited, s =>
        {
            var expected = full.Single(f => f.Id == s.Id).HistoricalTelemetry.OrderBy(r => r.Timestamp).TakeLast(3).Select(r => r.Timestamp);
            Assert.Equal(expected, s.HistoricalTelemetry.Select(r => r.Timestamp));
        });
        Assert.Contains(full, s => s.HistoricalTelemetry.Count > 3);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/sensors?readings=-1")).StatusCode);
    }

    [Fact]
    public async Task Topology_includes_the_new_node_fields()
    {
        var seeded = (await (await _client.PostAsync("/api/sensors/seed/2", null)).Content.ReadFromJsonAsync<List<SensorProfile>>())!;

        var topology = (await _client.GetFromJsonAsync<MeshTopology>("/api/topology"))!;

        var gateway = Assert.Single(topology.Nodes, n => n.Role == MeshNodeRole.Gateway);
        Assert.True(gateway.IsGateway);
        Assert.All(topology.Nodes.Where(n => n.Role == MeshNodeRole.Router), r => Assert.True(FactoryAreas.IsKnown(r.Area)));
        foreach (var sensor in seeded)
        {
            var node = topology.Nodes.Single(n => n.MacAddress == sensor.MacAddress);
            Assert.Equal(sensor.Id, node.SensorId);
            Assert.Equal(sensor.Location, node.Location);
            Assert.Equal(sensor.Category, node.Category);
            Assert.Equal(SensorSource.Seeded, node.Source);
            Assert.Equal(MeshNetworkService.RouterMacFor(FactoryAreas.AreaOf(sensor.Location)), node.ParentMac);
            Assert.NotNull(node.LatestValue);
            Assert.True(node.HasRoute);
        }

        Assert.Contains(topology.Edges, e => e.SourceMac == seeded[0].MacAddress || e.DestinationMac == seeded[0].MacAddress);
    }

    [Fact]
    public async Task Failing_and_restoring_nodes_changes_routes_problems_and_generation()
    {
        const string sensor = "00:1B:44:11:3A:04";
        var boiler = MeshNetworkService.RouterMacFor("Boiler Room");
        var cold = MeshNetworkService.RouterMacFor("Cold Storage");

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsync($"/api/topology/fail/{boiler}", null)).StatusCode);
        var rerouted = (await _client.GetFromJsonAsync<MeshRoute>($"/api/topology/route/{sensor}"))!;
        Assert.Equal(cold, rerouted.Path[1]);
        Assert.Equal(boiler, rerouted.BaselinePath[1]);
        Assert.True(rerouted.Cost > 0);

        await _client.PostAsync($"/api/topology/fail/{cold}", null);
        Assert.Empty((await _client.GetFromJsonAsync<MeshRoute>($"/api/topology/route/{sensor}"))!.Path);
        var problems = (await _client.GetFromJsonAsync<List<ConnectionProblem>>("/api/topology/problems"))!;
        Assert.Contains(problems, p => p.MacAddress == sensor && p.Kind == ConnectionProblemKind.NoRoute);
        var node = (await _client.GetFromJsonAsync<MeshTopology>("/api/topology"))!.Nodes.Single(n => n.MacAddress == sensor);
        Assert.False(node.HasRoute);

        for (int i = 0; i < 40; i++)
        {
            _factory.Ingestion.GenerateNextReading(new Random(i));
        }

        Assert.Empty((await SensorsAsync()).Single(s => s.MacAddress == sensor).HistoricalTelemetry);

        await _client.PostAsync($"/api/topology/restore/{boiler}", null);
        Assert.Equal(boiler, (await _client.GetFromJsonAsync<MeshRoute>($"/api/topology/route/{sensor}"))!.Path[1]);

        await _client.PostAsync("/api/topology/restore-all", null);
        Assert.Empty((await _client.GetFromJsonAsync<List<ConnectionProblem>>("/api/topology/problems"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/topology/fail/FF:FF:FF:FF:FF:FF", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/topology/route/FF:FF:FF:FF:FF:FF")).StatusCode);
    }

    [Fact]
    public async Task Validation_reasons_endpoint_is_new_and_the_old_endpoint_is_unchanged()
    {
        var mismatched = new DeploymentNode
        {
            NodeName = "Plant",
            ZoneId = "Site",
            Children = [new DeploymentNode { NodeName = "R1", ZoneId = "Assembly", Children = [new DeploymentNode { NodeName = "S1", ZoneId = "Paint Shop" }] }]
        };

        var old = await (await _client.PostAsJsonAsync("/api/deployment/validate", mismatched)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(old.GetProperty("isValid").GetBoolean());

        var detailed = (await (await _client.PostAsJsonAsync("/api/deployment/validate/details", mismatched)).Content.ReadFromJsonAsync<ValidationResult>())!;
        Assert.False(detailed.IsValid);
        Assert.Equal(3, detailed.NodesChecked);
        var issue = Assert.Single(detailed.Issues);
        Assert.Equal("S1", issue.NodeId);

        var missingZone = new DeploymentNode { NodeName = "Plant", ZoneId = "" };
        var oldMissing = await (await _client.PostAsJsonAsync("/api/deployment/validate", missingZone)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(oldMissing.GetProperty("isValid").GetBoolean());

        var live = MeshDeploymentTree.Build((await _client.GetFromJsonAsync<MeshTopology>("/api/topology"))!);
        Assert.True((await (await _client.PostAsJsonAsync("/api/deployment/validate/details", live)).Content.ReadFromJsonAsync<ValidationResult>())!.IsValid);
    }

    private static double Latest(List<SensorProfile> sensors, string mac) =>
        sensors.Single(s => s.MacAddress == mac).HistoricalTelemetry.MaxBy(r => r.Timestamp)!.Value;
}
