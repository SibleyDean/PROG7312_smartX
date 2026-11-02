using System.Collections.Concurrent;
using SmartX.Gateway.Shared.DataStructures;
using SmartX.Gateway.Shared.Models;
using SmartX.Gateway.Shared.Services;

namespace SmartX.Gateway.Services
{
    public class TelemetryIngestionService
    {
        public const int MaxReadingsPerSensor = 200;
        public const int MaxLogReadings = 5000;
        public const int MaxSeedCount = 50;

        // How far one Open Valve / Close Valve command moves an actuator, in percentage points.
        internal const double ValveStep = 15;

        private readonly DeviceRegistry _registry = new();
        private readonly TelemetryMessageQueue<TelemetryReading<double>> _ingestQueue = new();
        private readonly CriticalAlertPriorityQueue<TelemetryReading<double>> _alertQueue = new();
        private readonly CommandHistoryStack<ManualCommand> _commandHistory = new();
        private readonly ActiveAlertSet _activeAlerts = new();
        private readonly SortedTelemetryLog _sortedLog = new(MaxLogReadings);
        private readonly TelemetryAvlTree _avlLog = new(MaxLogReadings);
        private readonly ConcurrentDictionary<Guid, AlertDetail> _latestAlertReadings = new();
        private readonly PredictiveActionEngine _predictiveEngine;
        private readonly MeshNetworkService _mesh;
        private int _alertsAcknowledged;
        private volatile bool _isGenerationPaused;

        public TelemetryIngestionService(PredictiveActionEngine predictiveEngine, MeshNetworkService mesh)
        {
            _predictiveEngine = predictiveEngine;
            _mesh = mesh;
            RegisterMeshSensors();
        }

        public bool IsGenerationPaused => _isGenerationPaused;

        public MeshNetworkService Mesh => _mesh;

        // The background processor appends readings while the API serialises sensors, so callers get copies.
        // latestReadings limits each copy to its newest N readings, which keeps polling payloads small.
        public IReadOnlyList<SensorProfile> GetSensors(int? latestReadings = null) =>
            _registry.GetAll().Select(s => Snapshot(s, latestReadings)).ToList();

        public bool TryGetSensor(Guid sensorId, out SensorProfile? profile) => _registry.TryGetById(sensorId, out profile);

        public ActiveAlertSet GetActiveAlerts() => _activeAlerts;

        public SortedTelemetryLog GetTelemetryLog() => _sortedLog;

        public TelemetryAvlTree GetAvlLog() => _avlLog;

        public CommandHistoryStack<ManualCommand> GetCommandHistory() => _commandHistory;

        public CriticalAlertPriorityQueue<TelemetryReading<double>> GetAlertQueue() => _alertQueue;

        public TelemetryMessageQueue<TelemetryReading<double>> GetIngestQueue() => _ingestQueue;

        public void SetGenerationPaused(bool isPaused) => _isGenerationPaused = isPaused;

        // Manually registered sensors join the mesh too, so every sensor appears on the topology map.
        public void RegisterSensor(SensorProfile profile)
        {
            ArgumentNullException.ThrowIfNull(profile);
            profile.MacAddress = profile.MacAddress.Replace('-', ':');

            if (_mesh.ContainsNode(profile.MacAddress))
            {
                throw new InvalidOperationException($"Sensor with MAC {profile.MacAddress} is already registered.");
            }

            _registry.Register(profile);
            _mesh.AttachSensor(profile.MacAddress, profile.Location);
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

                    var excess = sensor.HistoricalTelemetry.Count - MaxReadingsPerSensor;
                    if (excess > 0)
                    {
                        sensor.HistoricalTelemetry.RemoveRange(0, excess);
                    }
                }

                Route(sensor, reading);
            }
        }

        // Picks one reporting sensor and continues its random walk from its latest reading. Returns false when there is nothing to do.
        // Sensors that are offline, or have no route left to the gateway, are skipped: their readings would never arrive.
        public bool GenerateNextReading(Random random)
        {
            if (_isGenerationPaused)
            {
                return false;
            }

            var silent = _mesh.GetSilentNodes();
            var sensors = _registry.GetAll().Where(s => !silent.Contains(s.MacAddress)).ToList();
            if (sensors.Count == 0)
            {
                return false;
            }

            var sensor = sensors[random.Next(sensors.Count)];
            var previous = GetLatestValue(sensor) ?? SensorReadingInfo.NominalValue(sensor.Category);
            var reading = new TelemetryReading<double>
            {
                SensorId = sensor.Id,
                Value = SensorReadingInfo.NextValue(previous, sensor.Category, random),
                Timestamp = DateTime.UtcNow
            };

            try
            {
                AddTelemetryBatch(sensor.Id, [reading]);
                return true;
            }
            catch (KeyNotFoundException)
            {
                // The sensor was removed between picking it and ingesting; the next tick picks another.
                return false;
            }
        }

        // Commands act on the simulation straight away, so the operator sees the effect in the next poll of the feed.
        public TelemetryReading<double>? ApplyCommandEffect(string deviceMac, string commandType, bool reverse = false)
        {
            if (!_registry.TryGet(deviceMac, out var sensor) || sensor is null)
            {
                return null;
            }

            var nominal = SensorReadingInfo.NominalValue(sensor.Category);
            var latest = GetLatestValue(sensor) ?? nominal;
            var direction = reverse ? -1 : 1;

            double? next = commandType switch
            {
                CommandCatalog.RestartSensor when !reverse => nominal + (Random.Shared.NextDouble() * 2 - 1),
                CommandCatalog.OpenValve when sensor.Category == SensorCategory.Actuator => latest + direction * ValveStep,
                CommandCatalog.CloseValve when sensor.Category == SensorCategory.Actuator => latest - direction * ValveStep,
                _ => null
            };

            if (next is not double value)
            {
                return null;
            }

            var reading = new TelemetryReading<double>
            {
                SensorId = sensor.Id,
                Value = Math.Round(Math.Clamp(value, 0, 100), 2),
                Timestamp = DateTime.UtcNow
            };

            try
            {
                AddTelemetryBatch(sensor.Id, [reading]);
                return reading;
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
        }

        public bool TryFindSensorByMac(string macAddress, out SensorProfile? profile) => _registry.TryGet(macAddress, out profile);

        public List<SensorProfile> SeedMockData(int numberOfSensors, Random? random = null)
        {
            random ??= new Random();
            var seededSensors = new List<SensorProfile>();

            for (int i = 0; i < numberOfSensors; i++)
            {
                var category = (SensorCategory)random.Next(0, 4);
                var sensor = new SensorProfile
                {
                    Location = $"{FactoryAreas.All[random.Next(FactoryAreas.All.Count)]} - Line {random.Next(1, 5)}",
                    Category = category,
                    Source = SensorSource.Seeded
                };
                sensor.HistoricalTelemetry = BuildSeedSeries(sensor.Id, category, random);

                // Once registered, the live generator can append to this list, so route from a copy.
                var seededReadings = sensor.HistoricalTelemetry.ToList();
                RegisterWithFreeMac(sensor, random);
                _mesh.AttachSensor(sensor.MacAddress, sensor.Location);

                foreach (var reading in seededReadings)
                {
                    Route(sensor, reading);
                }

                seededSensors.Add(Snapshot(sensor));
            }

            return seededSensors;
        }

        public bool AcknowledgeAlert(Guid sensorId)
        {
            if (!_activeAlerts.Remove(sensorId))
            {
                return false;
            }

            Interlocked.Increment(ref _alertsAcknowledged);
            return true;
        }

        public int AcknowledgeAllAlerts()
        {
            var cleared = _activeAlerts.Clear();
            Interlocked.Add(ref _alertsAcknowledged, cleared);
            return cleared;
        }

        public List<AlertDetail> GetAlertDetails() => _activeAlerts.GetAll()
            .Select(id => _latestAlertReadings.TryGetValue(id, out var detail) ? detail : null)
            .OfType<AlertDetail>()
            .ToList();

        public GatewaySummary GetSummary()
        {
            var sensors = _registry.GetAll();
            var totalReadings = 0;
            foreach (var sensor in sensors)
            {
                lock (sensor.HistoricalTelemetry)
                {
                    totalReadings += sensor.HistoricalTelemetry.Count;
                }
            }

            var now = DateTime.UtcNow;
            var lastMinute = _sortedLog.GetRange(now.AddMinutes(-1), now).Count;
            var problems = _mesh.GetConnectionProblems();

            return new GatewaySummary(
                sensors.Count,
                _activeAlerts.Count,
                lastMinute,
                totalReadings,
                Volatile.Read(ref _alertsAcknowledged),
                _isGenerationPaused)
            {
                DemoSensorCount = sensors.Count(s => s.Source != SensorSource.Mesh),
                ConnectionProblems = problems.Count,
                ConnectionIssues = problems.Select(p => p.Message).ToList()
            };
        }

        // The mesh's own view plus what the registry knows about each sensor, so the map never needs the full sensor list.
        public MeshTopology GetTopology()
        {
            var topology = _mesh.GetTopology();
            var nodes = topology.Nodes.Select(node =>
            {
                if (node.Role != MeshNodeRole.Sensor || !_registry.TryGet(node.MacAddress, out var sensor) || sensor is null)
                {
                    return node;
                }

                TelemetryReading<double>? latest;
                lock (sensor.HistoricalTelemetry)
                {
                    latest = sensor.HistoricalTelemetry.MaxBy(r => r.Timestamp);
                }

                return node with
                {
                    SensorId = sensor.Id,
                    Location = sensor.Location,
                    Category = sensor.Category,
                    Source = sensor.Source,
                    LatestValue = latest?.Value,
                    LatestTimestamp = latest?.Timestamp
                };
            }).ToList();

            return new MeshTopology(nodes, topology.Edges);
        }

        // Removes demo and manual sensors. Mesh sensors stay registered but lose their readings and alerts,
        // and the mesh itself returns to its base layout with every node online.
        public void ClearAll()
        {
            foreach (var sensor in _registry.GetAll())
            {
                if (sensor.Source == SensorSource.Mesh)
                {
                    lock (sensor.HistoricalTelemetry)
                    {
                        sensor.HistoricalTelemetry.Clear();
                    }
                }
                else
                {
                    _registry.Remove(sensor.MacAddress);
                }
            }

            _activeAlerts.Clear();
            _latestAlertReadings.Clear();
            _sortedLog.Clear();
            _avlLog.Clear();
            _commandHistory.Clear();
            _predictiveEngine.Clear();
            _mesh.Reset();
            Interlocked.Exchange(ref _alertsAcknowledged, 0);

            while (_alertQueue.TryDequeue(out _)) { }
            while (_ingestQueue.TryDequeue(out _)) { }
        }

        private void RegisterMeshSensors()
        {
            foreach (var definition in MeshNetworkService.BaseSensors)
            {
                if (_registry.TryGet(definition.MacAddress, out _))
                {
                    continue;
                }

                _registry.Register(new SensorProfile
                {
                    MacAddress = definition.MacAddress,
                    Location = definition.Location,
                    Category = definition.Category,
                    Source = SensorSource.Mesh
                });
            }
        }

        internal static List<TelemetryReading<double>> BuildSeedSeries(Guid sensorId, SensorCategory category, Random random)
        {
            var count = random.Next(8, 21);
            var timestamps = new DateTime[count];
            var timestamp = DateTime.UtcNow.AddSeconds(-random.Next(5, 31));

            for (int j = count - 1; j >= 0; j--)
            {
                timestamps[j] = timestamp;
                timestamp = timestamp.AddSeconds(-random.Next(20, 91));
            }

            var series = new List<TelemetryReading<double>>(count);
            var value = SensorReadingInfo.NominalValue(category);

            foreach (var time in timestamps)
            {
                value = SensorReadingInfo.NextValue(value, category, random);
                series.Add(new TelemetryReading<double> { SensorId = sensorId, Value = value, Timestamp = time });
            }

            return series;
        }

        // Mesh nodes and earlier seeds share the 00:1B:44 prefix, so a random suffix can already be taken.
        private void RegisterWithFreeMac(SensorProfile sensor, Random random)
        {
            while (true)
            {
                sensor.MacAddress = $"00:1B:44:{random.Next(256):X2}:{random.Next(256):X2}:{random.Next(256):X2}";

                if (_registry.TryGet(sensor.MacAddress, out _) || _mesh.ContainsNode(sensor.MacAddress))
                {
                    continue;
                }

                try
                {
                    _registry.Register(sensor);
                    return;
                }
                catch (InvalidOperationException)
                {
                    // Another request registered the same MAC between the check and the insert; pick again.
                }
            }
        }

        private static double? GetLatestValue(SensorProfile sensor)
        {
            lock (sensor.HistoricalTelemetry)
            {
                return sensor.HistoricalTelemetry.MaxBy(r => r.Timestamp)?.Value;
            }
        }

        internal static int? GetAlertPriority(double value) => SensorReadingInfo.GetStatus(value) switch
        {
            ReadingStatus.Critical => 1,
            ReadingStatus.Warning => 2,
            _ => null
        };

        // Critical readings bypass the standard FIFO queue so the processor handles them first.
        private void Route(SensorProfile sensor, TelemetryReading<double> reading)
        {
            _sortedLog.Add(reading);
            _avlLog.Insert(reading);

            if (GetAlertPriority(reading.Value) is int priority)
            {
                var detail = new AlertDetail(sensor.Id, reading.Value, priority, reading.Timestamp);
                _latestAlertReadings.AddOrUpdate(sensor.Id, detail, (_, existing) => existing.Timestamp > detail.Timestamp ? existing : detail);

                _alertQueue.Enqueue(reading, priority);
                _activeAlerts.Add(sensor.Id);
                _predictiveEngine.RecordCriticalEvent(sensor.Id, sensor.MacAddress, reading.Value);
            }
            else
            {
                _ingestQueue.Enqueue(reading);
            }
        }

        private static SensorProfile Snapshot(SensorProfile sensor) => Snapshot(sensor, null);

        private static SensorProfile Snapshot(SensorProfile sensor, int? latestReadings)
        {
            lock (sensor.HistoricalTelemetry)
            {
                var readings = latestReadings is int limit && limit < sensor.HistoricalTelemetry.Count
                    ? sensor.HistoricalTelemetry.OrderBy(r => r.Timestamp).TakeLast(Math.Max(limit, 0)).ToList()
                    : new List<TelemetryReading<double>>(sensor.HistoricalTelemetry);

                return new SensorProfile
                {
                    Id = sensor.Id,
                    MacAddress = sensor.MacAddress,
                    Location = sensor.Location,
                    Category = sensor.Category,
                    Source = sensor.Source,
                    Attachments = new List<MediaAttachment>(sensor.Attachments),
                    HistoricalTelemetry = readings
                };
            }
        }
    }
}
