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

app.UseRouting();

app.UseAntiforgery();

app.MapRazorComponents<App>()
   .AddInteractiveWebAssemblyRenderMode()
   .AddAdditionalAssemblies(typeof(SmartX.Gateway.Client._Imports).Assembly);

app.MapGet("/api/sensors", (TelemetryIngestionService service) =>
{
    var sensors = service.GetSensors();
    return Results.Ok(sensors);
})
.WithName("GetAllSensors");

app.MapPost("/api/sensors/register", Results<Ok<SensorProfile>, BadRequest<string>> (SensorProfile profile, TelemetryIngestionService service) =>
{
    if (string.IsNullOrWhiteSpace(profile.MacAddress) || string.IsNullOrWhiteSpace(profile.Location))
    {
        return TypedResults.BadRequest("MAC Address and Location are required.");
    }

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

app.MapPost("/api/sensors/seed/{count:int}", (int count, TelemetryIngestionService service) =>
{
    var seeded = service.SeedMockData(count);
    return Results.Ok(seeded);
})
.WithName("SeedMockData");

app.MapPost("/api/deployment/validate", (DeploymentNode node, IDeploymentValidator validator) =>
{
    bool isValid = validator.ValidateDeploymentTree(node);
    return Results.Ok(new { IsValid = isValid });
})
.WithName("ValidateDeploymentTree");

string[] validCommandTypes = ["OpenValve", "CloseValve", "ToggleRelay", "RestartSensor"];

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

    var issued = command with
    {
        Id = Guid.NewGuid(),
        IssuedAt = DateTime.UtcNow
    };

    service.GetCommandHistory().Push(issued);
    engine.RecordCommand(issued);
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

app.MapPost("/api/alerts/{sensorId:guid}/resolve", (Guid sensorId, TelemetryIngestionService service) =>
{
    var removed = service.GetActiveAlerts().Remove(sensorId);
    return TypedResults.Ok(new { Resolved = removed });
})
.WithName("ResolveAlert");

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

app.Run();