using System.Net;
using System.Text;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SmartX.Gateway.Services;
using SmartX.Gateway.Shared.Models;

namespace SmartX.Gateway.Tests;

// Answers HttpClient calls from components. The longest matching "METHOD path-prefix" route wins.
public sealed class FakeHttpHandler : HttpMessageHandler
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly List<(HttpMethod Method, string Prefix, Func<HttpRequestMessage, Task<HttpResponseMessage>> Responder)> _routes = new();

    public List<(HttpMethod Method, string Path, string? Body)> Requests { get; } = new();

    // When true every request fails as if the gateway were unreachable.
    public bool Offline { get; set; }

    public FakeHttpHandler Get(string prefix, Func<object?> body) => Map(HttpMethod.Get, prefix, _ => Task.FromResult(JsonResponse(body())));

    public FakeHttpHandler Post(string prefix, Func<object?> body) => Map(HttpMethod.Post, prefix, _ => Task.FromResult(JsonResponse(body())));

    public FakeHttpHandler Map(HttpMethod method, string prefix, Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        _routes.RemoveAll(r => r.Method == method && r.Prefix == prefix);
        _routes.Add((method, prefix, responder));
        return this;
    }

    public static HttpResponseMessage JsonResponse(object? body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json")
    };

    public int Count(HttpMethod method, string prefix) => Requests.Count(r => r.Method == method && r.Path.StartsWith(prefix, StringComparison.Ordinal));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery.TrimStart('/');
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add((request.Method, Uri.UnescapeDataString(path), body));

        if (Offline)
        {
            throw new HttpRequestException("Gateway unreachable (test).");
        }

        var route = _routes
            .Where(r => r.Method == request.Method && Uri.UnescapeDataString(path).StartsWith(r.Prefix, StringComparison.Ordinal))
            .OrderByDescending(r => r.Prefix.Length)
            .FirstOrDefault();

        return route.Responder is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : await route.Responder(request);
    }
}

public abstract class ComponentTestBase : TestContext
{
    protected ComponentTestBase()
    {
        Http = new FakeHttpHandler();
        Services.AddSingleton(new HttpClient(Http) { BaseAddress = new Uri("http://localhost/") });
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    protected FakeHttpHandler Http { get; }
}

// Hosts the real gateway in memory without the background generator, so API results only change when a test changes them.
public sealed class GatewayFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            foreach (var descriptor in services.Where(d => d.ImplementationType == typeof(TelemetryBackgroundProcessor)).ToList())
            {
                services.Remove(descriptor);
            }
        });
    }

    public TelemetryIngestionService Ingestion => Services.GetRequiredService<TelemetryIngestionService>();

    public MeshNetworkService Mesh => Services.GetRequiredService<MeshNetworkService>();
}

// NextDouble always returns the same value and Next returns its minimum, which turns every random choice into a fixed one.
public sealed class FixedRandom(double value) : Random
{
    public override double NextDouble() => value;

    protected override double Sample() => value;

    public override int Next() => 0;

    public override int Next(int maxValue) => 0;

    public override int Next(int minValue, int maxValue) => minValue;
}

// A seeded Random whose Next(256) calls (the MAC byte picks) can be scripted.
public sealed class ScriptedRandom(params int[] macBytes) : Random(42)
{
    private readonly Queue<int> _macBytes = new(macBytes);

    public override int Next(int maxValue) => maxValue == 256 && _macBytes.Count > 0 ? _macBytes.Dequeue() : base.Next(maxValue);
}

public static class Build
{
    public static TelemetryReading<double> Reading(Guid sensorId, double value, DateTime timestamp) =>
        new() { SensorId = sensorId, Value = value, Timestamp = timestamp };

    public static SensorProfile Sensor(
        string mac,
        string location,
        SensorCategory category,
        SensorSource source = SensorSource.Seeded,
        params (double Value, int SecondsAgo)[] readings)
    {
        var sensor = new SensorProfile { MacAddress = mac, Location = location, Category = category, Source = source };
        sensor.HistoricalTelemetry = readings
            .Select(r => Reading(sensor.Id, r.Value, DateTime.UtcNow.AddSeconds(-r.SecondsAgo)))
            .OrderBy(r => r.Timestamp)
            .ToList();
        return sensor;
    }

    public static TelemetryIngestionService Service(out MeshNetworkService mesh, out SmartX.Gateway.Shared.Services.PredictiveActionEngine engine)
    {
        mesh = new MeshNetworkService();
        engine = new SmartX.Gateway.Shared.Services.PredictiveActionEngine();
        return new TelemetryIngestionService(engine, mesh);
    }

    public static TelemetryIngestionService Service() => Service(out _, out _);
}
