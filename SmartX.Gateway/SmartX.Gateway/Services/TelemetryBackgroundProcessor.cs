namespace SmartX.Gateway.Services
{
    public class TelemetryBackgroundProcessor : BackgroundService
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
        private const int MinGenerationDelaySeconds = 4;
        private const int MaxGenerationDelaySeconds = 12;

        private readonly TelemetryIngestionService _ingestionService;
        private readonly ILogger<TelemetryBackgroundProcessor> _logger;

        public TelemetryBackgroundProcessor(TelemetryIngestionService ingestionService, ILogger<TelemetryBackgroundProcessor> logger)
        {
            _ingestionService = ingestionService;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var nextGenerationAt = DateTime.UtcNow + NextGenerationDelay(Random.Shared);

            while (!stoppingToken.IsCancellationRequested)
            {
                // Real sensors report irregularly, so each reading waits a fresh random delay rather than a fixed tick.
                if (DateTime.UtcNow >= nextGenerationAt)
                {
                    _ingestionService.GenerateNextReading(Random.Shared);
                    nextGenerationAt = DateTime.UtcNow + NextGenerationDelay(Random.Shared);
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

        internal const int MinDelaySeconds = MinGenerationDelaySeconds;
        internal const int MaxDelaySeconds = MaxGenerationDelaySeconds;

        internal static TimeSpan NextGenerationDelay(Random random) =>
            TimeSpan.FromSeconds(MinGenerationDelaySeconds + random.NextDouble() * (MaxGenerationDelaySeconds - MinGenerationDelaySeconds));
    }
}
