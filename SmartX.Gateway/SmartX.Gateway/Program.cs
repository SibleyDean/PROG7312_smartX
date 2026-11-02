using System.Globalization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SmartX.Gateway.Client; 
using SmartX.Gateway.Components;
using SmartX.Gateway.Services;
using SmartX.Gateway.Shared.DataStructures;
using SmartX.Gateway.Shared.Models;
using SmartX.Gateway.Shared.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddSignalR();

builder.Services.AddSingleton<PredictiveActionEngine>();
builder.Services.AddSingleton<TelemetryIngestionService>();
builder.Services.AddSingleton<MeshNetworkService>();
builder.Services.AddHostedService<TelemetryBackgroundProcessor>();
builder.Services.AddSingleton<IDeploymentValidator, DeploymentValidator>();
builder.Services.AddScoped(sp =>
{
    var navigationManager = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(navigationManager.BaseUri) };
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Building the ingestion service registers the mesh sensors, which must happen before the generator starts picking devices.
app.Services.GetRequiredService<TelemetryIngestionService>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

// API data is live, so neither the browser nor a proxy may serve a stale copy.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
    }

    await next();
});

app.UseRouting();

app.UseAntiforgery();

app.MapRazorComponents<App>()
   .AddInteractiveWebAssemblyRenderMode()
   .AddAdditionalAssemblies(typeof(SmartX.Gateway.Client._Imports).Assembly);

// ?readings=N returns only each sensor's newest N readings; without it every stored reading is returned, as before.
app.MapGet("/api/sensors", Results<Ok<IReadOnlyList<SensorProfile>>, BadRequest<string>> (TelemetryIngestionService service, int? readings) =>
{
    if (readings is < 0)
    {
        return TypedResults.BadRequest("readings must be zero or more.");
    }

    return TypedResults.Ok(service.GetSensors(readings));
})
.WithName("GetAllSensors");

app.MapPost("/api/sensors/register", Results<Ok<SensorProfile>, BadRequest<string>> (SensorProfile profile, TelemetryIngestionService service) =>
{
    if (string.IsNullOrWhiteSpace(profile.MacAddress) || string.IsNullOrWhiteSpace(profile.Location))
    {
        return TypedResults.BadRequest("MAC Address and Location are required.");
    }

    // Only the gateway itself creates demo or mesh sensors; anything registered through the API is manual.
    profile.Source = SensorSource.Manual;

    try
    {
        service.RegisterSensor(profile);
        return TypedResults.Ok(profile);
    }
    catch (InvalidOperationException ex)
    {
        return TypedResults.BadRequest(ex.Message);
    }
})
.WithName("RegisterSensor");

app.MapPost("/api/sensors/{id:guid}/telemetry", Results<Ok, NotFound<string>, BadRequest<string>> (Guid id, List<TelemetryReading<double>> readings, TelemetryIngestionService service) =>
{
    if (readings == null || readings.Count == 0)
    {
        return TypedResults.BadRequest("Telemetry batch cannot be empty.");
    }

    try
    {
        service.AddTelemetryBatch(id, readings);
        return TypedResults.Ok();
    }
    catch (KeyNotFoundException ex)
    {
        return TypedResults.NotFound(ex.Message);
    }
})
.WithName("AddTelemetryBatch");

app.MapPost("/api/sensors/seed/{count:int}", Results<Ok<List<SensorProfile>>, BadRequest<string>> (int count, TelemetryIngestionService service) =>
{
    if (count < 1 || count > TelemetryIngestionService.MaxSeedCount)
    {
        return TypedResults.BadRequest($"count must be between 1 and {TelemetryIngestionService.MaxSeedCount}.");
    }

    var seeded = service.SeedMockData(count);
    return TypedResults.Ok(seeded);
})
.WithName("SeedMockData");

app.MapPost("/api/sensors/clear", (TelemetryIngestionService service) =>
{
    service.ClearAll();
    return TypedResults.Ok();
})
.WithName("ClearAllSensors");

app.MapGet("/api/summary", (TelemetryIngestionService service) => TypedResults.Ok(service.GetSummary()))
.WithName("GetGatewaySummary");

app.MapGet("/api/simulation", (TelemetryIngestionService service) => TypedResults.Ok(new SimulationStatus(service.IsGenerationPaused)))
.WithName("GetSimulationStatus");

app.MapPost("/api/simulation/pause", (TelemetryIngestionService service) =>
{
    service.SetGenerationPaused(true);
    return TypedResults.Ok(new SimulationStatus(true));
})
.WithName("PauseSimulation");

app.MapPost("/api/simulation/resume", (TelemetryIngestionService service) =>
{
    service.SetGenerationPaused(false);
    return TypedResults.Ok(new SimulationStatus(false));
})
.WithName("ResumeSimulation");

app.MapPost("/api/deployment/validate", (DeploymentNode node, IDeploymentValidator validator) =>
{
    bool isValid = validator.ValidateDeploymentTree(node);
    return Results.Ok(new { IsValid = isValid });
})
.WithName("ValidateDeploymentTree");

// Same tree format as /api/deployment/validate, but reports every failing node and why.
app.MapPost("/api/deployment/validate/details", (DeploymentNode node, IDeploymentValidator validator) =>
{
    var result = validator.ValidateWithReasons(node);
    return TypedResults.Ok(result);
})
.WithName("ValidateDeploymentTreeWithReasons");

var validCommandTypes = CommandCatalog.All;

app.MapPost("/api/commands/issue", Results<Ok<ManualCommand>, BadRequest<string>> (ManualCommand command, TelemetryIngestionService service, PredictiveActionEngine engine) =>
{
    if (string.IsNullOrWhiteSpace(command.DeviceMac))
    {
        return TypedResults.BadRequest("DeviceMac is required.");
    }

    if (!validCommandTypes.Contains(command.CommandType))
    {
        return TypedResults.BadRequest($"CommandType must be one of: {string.Join(", ", validCommandTypes)}.");
    }

    if (service.TryFindSensorByMac(command.DeviceMac, out var target) && target is not null
        && !CommandCatalog.FitsCategory(command.CommandType, target.Category))
    {
        var fits = string.Join(", ", CommandCatalog.For(target.Category).Select(CommandCatalog.Label));
        return TypedResults.BadRequest(
            $"{CommandCatalog.Label(command.CommandType)} does not work on {target.MacAddress}: it is a {target.Category} device " +
            $"({SensorReadingInfo.Describe(target.Category).ToLowerInvariant()}). Commands that fit: {fits}.");
    }

    var issued = command with
    {
        Id = Guid.NewGuid(),
        IssuedAt = DateTime.UtcNow
    };

    service.GetCommandHistory().Push(issued);
    engine.RecordCommand(issued);
    service.ApplyCommandEffect(issued.DeviceMac, issued.CommandType);
    return TypedResults.Ok(issued);
})
.WithName("IssueCommand");

app.MapPost("/api/commands/undo", Results<Ok<ManualCommand>, NotFound<string>> (TelemetryIngestionService service, PredictiveActionEngine engine) =>
{
    if (!service.GetCommandHistory().TryPop(out var undone))
    {
        return TypedResults.NotFound("No commands to undo.");
    }

    engine.RemoveCommand(undone.Id);

    // Valve moves are reversible; a restart or relay toggle cannot be taken back in the simulation.
    service.ApplyCommandEffect(undone.DeviceMac, undone.CommandType, reverse: true);
    return TypedResults.Ok(undone);
})
.WithName("UndoCommand");

app.MapGet("/api/commands/history", (TelemetryIngestionService service) =>
{
    var snapshot = service.GetCommandHistory().Snapshot();
    return TypedResults.Ok(snapshot);
})
.WithName("GetCommandHistory");

app.MapGet("/api/alerts/active", (TelemetryIngestionService service) =>
{
    var ids = service.GetActiveAlerts().GetAll();
    return TypedResults.Ok(ids);
})
.WithName("GetActiveAlerts");

app.MapGet("/api/alerts/details", (TelemetryIngestionService service) =>
{
    var details = service.GetAlertDetails();
    return TypedResults.Ok(details);
})
.WithName("GetActiveAlertDetails");

// "Resolve" is the original route name; the console calls this action "Acknowledge".
app.MapPost("/api/alerts/{sensorId:guid}/resolve", (Guid sensorId, TelemetryIngestionService service) =>
{
    var removed = service.AcknowledgeAlert(sensorId);
    return TypedResults.Ok(new { Resolved = removed });
})
.WithName("ResolveAlert");

app.MapPost("/api/alerts/acknowledge-all", (TelemetryIngestionService service) =>
{
    var cleared = service.AcknowledgeAllAlerts();
    return TypedResults.Ok(new { Acknowledged = cleared });
})
.WithName("AcknowledgeAllAlerts");

app.MapGet("/api/telemetry/{sensorId:guid}/log", Results<Ok<List<TelemetryReading<double>>>, NotFound<string>> (Guid sensorId, TelemetryIngestionService service) =>
{
    if (!service.TryGetSensor(sensorId, out _))
    {
        return TypedResults.NotFound($"Sensor with ID '{sensorId}' not found.");
    }

    var filtered = service.GetTelemetryLog().GetAllOrdered()
        .Where(r => r.SensorId == sensorId)
        .ToList();

    return TypedResults.Ok(filtered);
})
.WithName("GetSensorTelemetryLog");

app.MapGet("/api/telemetry/{sensorId:guid}/range", Results<Ok<List<TelemetryReading<double>>>, NotFound<string>, BadRequest<string>> (Guid sensorId, string start, string end, TelemetryIngestionService service) =>
{
    if (!service.TryGetSensor(sensorId, out _))
    {
        return TypedResults.NotFound($"Sensor with ID '{sensorId}' not found.");
    }

    DateTime startTime;
    DateTime endTime;

    // Readings are stamped in UTC, so offset-less query values are treated as UTC rather than server-local time.
    try
    {
        startTime = DateTime.Parse(start, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        endTime = DateTime.Parse(end, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }
    catch (FormatException)
    {
        return TypedResults.BadRequest("start and end must be valid date-time values.");
    }

    if (startTime > endTime)
    {
        return TypedResults.BadRequest("start must not be later than end.");
    }

    var filtered = service.GetAvlLog().GetRange(startTime, endTime)
        .Where(r => r.SensorId == sensorId)
        .ToList();

    return TypedResults.Ok(filtered);
})
.WithName("GetSensorTelemetryRange");

app.MapPost("/api/insights/search", Results<Ok, BadRequest<string>> ([FromBody] string query, PredictiveActionEngine engine) =>
{
    if (string.IsNullOrWhiteSpace(query))
    {
        return TypedResults.BadRequest("Search query cannot be empty.");
    }

    engine.RecordSearch(query);
    return TypedResults.Ok();
})
.WithName("RecordSearch");

app.MapGet("/api/insights/suggested", (PredictiveActionEngine engine) =>
{
    var suggestions = engine.GetSuggestedActions();
    return TypedResults.Ok(suggestions);
})
.WithName("GetSuggestedActions");

app.MapGet("/api/topology", (TelemetryIngestionService service) =>
{
    var topology = service.GetTopology();
    return TypedResults.Ok(topology);
})
.WithName("GetTopology");

app.MapGet("/api/topology/problems", (MeshNetworkService mesh) => TypedResults.Ok(mesh.GetConnectionProblems()))
.WithName("GetConnectionProblems");

// Path keeps its original meaning; BaselinePath (the route with every node online) and Cost were added later.
app.MapGet("/api/topology/route/{macAddress}", (string macAddress, MeshNetworkService mesh) =>
{
    if (!mesh.ContainsNode(macAddress))
    {
        return Results.NotFound($"Node '{macAddress}' not found in the mesh.");
    }

    return Results.Ok(mesh.GetRoute(macAddress));
})
.WithName("GetRouteToGateway");

app.MapPost("/api/topology/fail/{macAddress}", Results<Ok, NotFound<string>> (string macAddress, MeshNetworkService mesh) =>
{
    try
    {
        mesh.SetNodeFailure(macAddress, isOffline: true);
        return TypedResults.Ok();
    }
    catch (KeyNotFoundException ex)
    {
        return TypedResults.NotFound(ex.Message);
    }
})
.WithName("FailMeshNode");

app.MapPost("/api/topology/restore/{macAddress}", Results<Ok, NotFound<string>> (string macAddress, MeshNetworkService mesh) =>
{
    try
    {
        mesh.SetNodeFailure(macAddress, isOffline: false);
        return TypedResults.Ok();
    }
    catch (KeyNotFoundException ex)
    {
        return TypedResults.NotFound(ex.Message);
    }
})
.WithName("RestoreMeshNode");

app.MapPost("/api/topology/restore-all", (MeshNetworkService mesh) => TypedResults.Ok(new { Restored = mesh.RestoreAll() }))
.WithName("RestoreAllMeshNodes");

app.Run();

// Lets the test project host the app through WebApplicationFactory<Program>.
public partial class Program { }
