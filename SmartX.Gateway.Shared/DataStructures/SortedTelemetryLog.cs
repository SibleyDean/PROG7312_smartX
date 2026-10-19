using SmartX.Gateway.Shared.Models;

namespace SmartX.Gateway.Shared.DataStructures;

public class SortedTelemetryLog
{
    private readonly SortedDictionary<DateTime, List<TelemetryReading<double>>> _log = new();
    private readonly object _lock = new();
    private int _readingCount;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _readingCount;
            }
        }
    }

    public int BucketCount
    {
        get
        {
            lock (_lock)
            {
                return _log.Count;
            }
        }
    }

    public bool IsEmpty => Count == 0;

    public void Add(TelemetryReading<double> reading)
    {
        ArgumentNullException.ThrowIfNull(reading);
        var key = new DateTime(reading.Timestamp.Ticks - (reading.Timestamp.Ticks % TimeSpan.TicksPerSecond));

        lock (_lock)
        {
            if (_log.TryGetValue(key, out var bucket))
            {
                bucket.Add(reading);
            }
            else
            {
                _log.Add(key, new List<TelemetryReading<double>> { reading });
            }

            _readingCount++;
        }
    }

    public IReadOnlyList<TelemetryReading<double>> GetAllOrdered()
    {
        lock (_lock)
        {
            var readings = new List<TelemetryReading<double>>(_readingCount);
            foreach (var bucket in _log.Values)
            {
                readings.AddRange(bucket);
            }

            return readings;
        }
    }

    public IReadOnlyList<TelemetryReading<double>> GetRange(DateTime from, DateTime to)
    {
        lock (_lock)
        {
            var readings = new List<TelemetryReading<double>>();
            foreach (var (second, bucket) in _log)
            {
                if (second > to)
                {
                    break;
                }

                // A bucket keyed before `from` can still hold readings later in that same second.
                if (second.AddSeconds(1) <= from)
                {
                    continue;
                }

                foreach (var reading in bucket)
                {
                    if (reading.Timestamp >= from && reading.Timestamp <= to)
                    {
                        readings.Add(reading);
                    }
                }
            }

            return readings;
        }
    }
}
