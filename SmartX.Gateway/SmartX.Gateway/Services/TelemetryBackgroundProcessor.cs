using SmartX.Gateway.Shared.Models;

namespace SmartX.Gateway.Services
{
    public class TelemetryBackgroundProcessor : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
        private static readonly TimeSpan GenerationInterval = TimeSpan.FromSeconds(3);

        private readonly TelemetryIngestionService _ingestionService;
        private readonly ILogger<TelemetryBackgroundProcessor> _logger;

        public TelemetryBackgroundProcessor(TelemetryIngestionService ingestionService, ILogger<TelemetryBackgroundProcessor> logger)
        {
            _ingestionService = ingestionService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var lastGeneratedAt = DateTime.UtcNow;

            while (!stoppingToken.IsCancellationRequested)
            {
                if (DateTime.UtcNow - lastGeneratedAt >= GenerationInterval)
                {
                    GenerateSyntheticReading();
                    lastGeneratedAt = DateTime.UtcNow;
                }

                // Critical alerts are always drained before standard telemetry.
                if (_ingestionService.GetAlertQueue().TryDequeue(out var alert) && alert is not null)
                {
                    _logger.LogWarning("Critical reading processed: sensor {SensorId} value {Value:F2} at {Timestamp:HH:mm:ss}",
                        alert.SensorId, alert.Value, alert.Timestamp);
                }
                else if (_ingestionService.GetIngestQueue().TryDequeue(out var reading) && reading is not null)
                {
                    _logger.LogDebug("Telemetry processed: sensor {SensorId} value {Value:F2} at {Timestamp:HH:mm:ss}",
                        reading.SensorId, reading.Value, reading.Timestamp);
                }

                try
                {
                    await Task.Delay(PollInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }

        private void GenerateSyntheticReading()
        {
            var sensors = _ingestionService.GetSensors();
            if (sensors.Count == 0)
            {
                return;
            }

            var sensor = sensors[Random.Shared.Next(sensors.Count)];
            var reading = new TelemetryReading<double>
            {
                SensorId = sensor.Id,
                Value = Random.Shared.NextDouble() * 100,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                _ingestionService.AddTelemetryBatch(sensor.Id, [reading]);
            }
            catch (KeyNotFoundException)
            {
                // The sensor was removed between picking it and ingesting; the next tick picks another.
            }
        }
    }
}
