using SmartX.Gateway.Shared.Models;

namespace SmartX.Gateway.Services;

public class TelemetryIngestionService
{
    private readonly List<SensorProfile> _sensors = new();
    private readonly object _lock = new object(); // Thread safety

    public IReadOnlyList<SensorProfile> GetSensors() => _sensors.AsReadOnly();

    public void RegisterSensor(SensorProfile profile)
    {
        lock (_lock)
        {
            if (_sensors.Any(s => s.MacAddress.Equals(profile.MacAddress, StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidOperationException($"A sensor with MAC address '{profile.MacAddress}' is already registered.");
            }
            _sensors.Add(profile);
        }
    }

    public void AddTelemetryBatch(Guid sensorId, IEnumerable<TelemetryReading<double>> readings)
    {
        lock (_lock)
        {
            var sensor = _sensors.FirstOrDefault(s => s.Id == sensorId)
                         ?? throw new KeyNotFoundException($"Sensor with ID '{sensorId}' not found.");

            sensor.HistoricalTelemetry.AddRange(readings);
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

            // Generate a jagged array of historical data to simulate batches
            int numberOfBatches = random.Next(1, 6);
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

            RegisterSensor(sensor);
            seededSensors.Add(sensor);
        }

        return seededSensors;
    }
}