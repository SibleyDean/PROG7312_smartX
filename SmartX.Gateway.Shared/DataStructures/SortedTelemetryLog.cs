using SmartX.Gateway.Shared.Models;

namespace SmartX.Gateway.Shared.DataStructures;

public class SortedTelemetryLog
{
    private readonly SortedDictionary<DateTime, List<TelemetryReading<double>>> _log = new();
    private readonly object _lock = new();
    private readonly int _maxReadings;
    private int _readingCount;

    public SortedTelemetryLog(int maxReadings = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxReadings);
        _maxReadings = maxReadings;
    }

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
            TrimOldest();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _log.Clear();
            _readingCount = 0;
        }
    }

    // Called under the lock; drops whole buckets from the oldest second until the log fits, splitting the last one if needed.
    private void TrimOldest()
    {
        while (_readingCount > _maxReadings)
        {
            var (oldestKey, oldestBucket) = _log.First();
            var excess = _readingCount - _maxReadings;

            if (oldestBucket.Count <= excess)
            {
                _log.Remove(oldestKey);
                _readingCount -= oldestBucket.Count;
            }
            else
            {
                oldestBucket.RemoveRange(0, excess);
                _readingCount -= excess;
            }
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
