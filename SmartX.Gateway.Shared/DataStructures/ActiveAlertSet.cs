namespace SmartX.Gateway.Shared.DataStructures;

public class ActiveAlertSet
{
    private readonly HashSet<Guid> _activeAlerts = new();
    private readonly object _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _activeAlerts.Count;
            }
        }
    }

    public bool IsEmpty => Count == 0;

    public bool Add(Guid sensorId)
    {
        lock (_lock)
        {
            return _activeAlerts.Add(sensorId);
        }
    }

    public bool Remove(Guid sensorId)
    {
        lock (_lock)
        {
            return _activeAlerts.Remove(sensorId);
        }
    }

    public bool Contains(Guid sensorId)
    {
        lock (_lock)
        {
            return _activeAlerts.Contains(sensorId);
        }
    }

    public IReadOnlyList<Guid> GetAll()
    {
        lock (_lock)
        {
            return new List<Guid>(_activeAlerts);
        }
    }
}
