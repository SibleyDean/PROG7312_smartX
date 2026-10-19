using SmartX.Gateway.Shared.Models;

namespace SmartX.Gateway.Shared.DataStructures;

public class DeviceRegistry
{
    private readonly Dictionary<string, SensorProfile> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Guid, string> _idToMacMap = new();
    private readonly object _lock = new();

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _devices.Count;
            }
        }
    }

    public bool IsEmpty => Count == 0;

    public void Register(SensorProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var key = NormaliseMac(profile.MacAddress);

        lock (_lock)
        {
            if (_idToMacMap.ContainsKey(profile.Id))
            {
                throw new InvalidOperationException($"Sensor with ID {profile.Id} is already registered.");
            }

            if (!_devices.TryAdd(key, profile))
            {
                throw new InvalidOperationException($"Sensor with MAC {profile.MacAddress} is already registered.");
            }

            _idToMacMap.Add(profile.Id, key);
        }
    }

    public bool TryGet(string macAddress, out SensorProfile? profile)
    {
        var key = NormaliseMac(macAddress);

        lock (_lock)
        {
            return _devices.TryGetValue(key, out profile);
        }
    }

    public bool TryGetById(Guid id, out SensorProfile? profile)
    {
        lock (_lock)
        {
            if (_idToMacMap.TryGetValue(id, out var key))
            {
                return _devices.TryGetValue(key, out profile);
            }

            profile = null;
            return false;
        }
    }

    public bool Remove(string macAddress)
    {
        var key = NormaliseMac(macAddress);

        lock (_lock)
        {
            if (!_devices.Remove(key, out var profile))
            {
                return false;
            }

            _idToMacMap.Remove(profile.Id);
            return true;
        }
    }

    public IReadOnlyList<SensorProfile> GetAll()
    {
        lock (_lock)
        {
            return new List<SensorProfile>(_devices.Values);
        }
    }

    private static string NormaliseMac(string macAddress) => macAddress.Replace('-', ':').ToUpperInvariant();
}
