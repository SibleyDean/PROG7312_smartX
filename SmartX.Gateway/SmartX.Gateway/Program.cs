using Microsoft.AspNetCore.Http.HttpResults;
using SmartX.Gateway.Client; 
using SmartX.Gateway.Components;
using SmartX.Gateway.Services;
using SmartX.Gateway.Shared.Models;
using SmartX.Gateway.Shared.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

builder.Services.AddSignalR(); // For real-time dashboard updates

// Register our custom services as singletons for this demo.
builder.Services.AddSingleton<TelemetryIngestionService>();
builder.Services.AddSingleton<IDeploymentValidator, DeploymentValidator>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Configure the HTTP request pipeline.
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

// --- Minimal API Endpoints ---

// GET: api/sensors
app.MapGet("/api/sensors", (TelemetryIngestionService service) =>
{
    var sensors = service.GetSensors();
    return Results.Ok(sensors);
})
.WithName("GetAllSensors");

// POST: api/sensors/register
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

// POST: api/sensors/{id}/telemetry
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

// POST: api/sensors/seed
app.MapPost("/api/sensors/seed/{count:int}", (int count, TelemetryIngestionService service) =>
{
    var seeded = service.SeedMockData(count);
    return Results.Ok(seeded);
})
.WithName("SeedMockData");

// POST: api/deployment/validate
app.MapPost("/api/deployment/validate", (DeploymentNode node, IDeploymentValidator validator) =>
{
    bool isValid = validator.ValidateDeploymentTree(node);
    return Results.Ok(new { IsValid = isValid });
})
.WithName("ValidateDeploymentTree");

app.Run();