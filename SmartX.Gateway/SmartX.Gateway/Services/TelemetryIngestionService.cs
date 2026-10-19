using SmartX.Gateway.Shared.DataStructures;
using SmartX.Gateway.Shared.Models;
using SmartX.Gateway.Shared.Services;

namespace SmartX.Gateway.Services
{
    public class TelemetryIngestionService
    {
        private readonly DeviceRegistry _registry = new();
        private readonly TelemetryMessageQueue<TelemetryReading<double>> _ingestQueue = new();
        private readonly CriticalAlertPriorityQueue<TelemetryReading<double>> _alertQueue = new();
        private readonly CommandHistoryStack<ManualCommand> _commandHistory = new();
        private readonly ActiveAlertSet _activeAlerts = new();
        private readonly SortedTelemetryLog _sortedLog = new();
        private readonly PredictiveActionEngine _predictiveEngine;

        public TelemetryIngestionService(PredictiveActionEngine predictiveEngine)
        {
            _predictiveEngine = predictiveEngine;
        }

        // The background processor appends readings while the API serialises sensors, so callers get copies.
        public IReadOnlyList<SensorProfile> GetSensors() => _registry.GetAll().Select(Snapshot).ToList();

        public bool TryGetSensor(Guid sensorId, out SensorProfile? profile) => _registry.TryGetById(sensorId, out profile);

        public ActiveAlertSet GetActiveAlerts() => _activeAlerts;

        public SortedTelemetryLog GetTelemetryLog() => _sortedLog;

        public CommandHistoryStack<ManualCommand> GetCommandHistory() => _commandHistory;

        public CriticalAlertPriorityQueue<TelemetryReading<double>> GetAlertQueue() => _alertQueue;

        public TelemetryMessageQueue<TelemetryReading<double>> GetIngestQueue() => _ingestQueue;

        public void RegisterSensor(SensorProfile profile)
        {
            _registry.Register(profile);
        }

        public void AddTelemetryBatch(Guid sensorId, IEnumerable<TelemetryReading<double>> readings)
        {
            if (!_registry.TryGetById(sensorId, out var sensor) || sensor is null)
            {
                throw new KeyNotFoundException($"Sensor with ID '{sensorId}' not found.");
            }

            var batch = readings.ToList();

            foreach (var reading in batch)
            {
                reading.SensorId = sensorId;

                lock (sensor.HistoricalTelemetry)
                {
                    sensor.HistoricalTelemetry.Add(reading);
                }

                Route(sensor, reading);
            }
        }

        public List<SensorProfile> SeedMockData(int numberOfSensors)
        {
            var random = new Random();
            var seededSensors = new List<SensorProfile>();

            for (int i = 0; i < numberOfSensors; i++)
            {
                var sensor = new SensorProfile
                {
                    MacAddress = $"00:1B:44:{random.Next(10, 99):D2}:{random.Next(10, 99):D2}:{random.Next(10, 99):D2}",
                    Location = $"Zone-{random.Next(1, 10)}/Node-{random.Next(1, 50)}",
                    Category = (SensorCategory)random.Next(0, 4),
                    HistoricalTelemetry = new List<TelemetryReading<double>>()
                };

                var numberOfBatches = random.Next(1, 6);
                for (int b = 0; b < numberOfBatches; b++)
                {
                    var batch = new TelemetryReading<double>[random.Next(5, 20)];
                    for (int j = 0; j < batch.Length; j++)
                    {
                        batch[j] = new TelemetryReading<double>
                        {
                            SensorId = sensor.Id,
                            Value = random.NextDouble() * 100,
                            Timestamp = DateTime.UtcNow.AddMinutes(-random.Next(1, 60))
                        };
                    }
                    sensor.HistoricalTelemetry.AddRange(batch);
                }

                // Once registered, the live generator can append to this list, so route from a copy.
                var seededReadings = sensor.HistoricalTelemetry.ToList();
                _registry.Register(sensor);

                foreach (var reading in seededReadings)
                {
                    Route(sensor, reading);
                }

                seededSensors.Add(Snapshot(sensor));
            }

            return seededSensors;
        }

        // Priority 1 is the most severe; null means the reading is within normal range.
        private static int? GetAlertPriority(double value)
        {
            if (value > 95 || value < 5)
            {
                return 1;
            }

            if (value > 90 || value < 10)
            {
                return 2;
            }

            return null;
        }

        // Critical readings bypass the standard FIFO queue so the processor handles them first.
        private void Route(SensorProfile sensor, TelemetryReading<double> reading)
        {
            _sortedLog.Add(reading);

            if (GetAlertPriority(reading.Value) is int priority)
            {
                _alertQueue.Enqueue(reading, priority);
                _activeAlerts.Add(sensor.Id);
                _predictiveEngine.RecordCriticalEvent(sensor.Id, sensor.MacAddress, reading.Value);
            }
            else
            {
                _ingestQueue.Enqueue(reading);
            }
        }

        private static SensorProfile Snapshot(SensorProfile sensor)
        {
            lock (sensor.HistoricalTelemetry)
            {
                return new SensorProfile
                {
                    Id = sensor.Id,
                    MacAddress = sensor.MacAddress,
                    Location = sensor.Location,
                    Category = sensor.Category,
                    Attachments = new List<MediaAttachment>(sensor.Attachments),
                    HistoricalTelemetry = new List<TelemetryReading<double>>(sensor.HistoricalTelemetry)
                };
            }
        }
    }
}
