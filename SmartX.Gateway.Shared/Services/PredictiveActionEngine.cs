using SmartX.Gateway.Shared.DataStructures;

namespace SmartX.Gateway.Shared.Services;

public record ActionInsight(string SensorMac, string SuggestedAction, string Reason, double Confidence);

public class PredictiveActionEngine
{
    private const string InvestigateAction = "Investigate Device";

    private readonly List<(DateTime Timestamp, string Query)> _searchHistory = new();
    private readonly List<(DateTime Timestamp, Guid SensorId, string DeviceMac, double Value)> _criticalEvents = new();
    private readonly List<ManualCommand> _commands = new();
    private readonly object _lock = new();

    public void RecordSearch(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            _searchHistory.RemoveAll(x => x.Timestamp < cutoff);
            _searchHistory.Add((DateTime.UtcNow, query));
        }
    }

    public void RecordCriticalEvent(Guid sensorId, string deviceMac, double value)
    {
        ArgumentNullException.ThrowIfNull(deviceMac);

        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            _criticalEvents.RemoveAll(x => x.Timestamp < cutoff);
            _criticalEvents.Add((DateTime.UtcNow, sensorId, deviceMac, value));
        }
    }

    public void RecordCommand(ManualCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            _commands.RemoveAll(x => x.IssuedAt < cutoff);
            _commands.Add(command);
        }
    }

    public void RemoveCommand(Guid commandId)
    {
        lock (_lock)
        {
            _commands.RemoveAll(c => c.Id == commandId);
        }
    }

    public List<ActionInsight> GetSuggestedActions()
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            _searchHistory.RemoveAll(x => x.Timestamp < cutoff);
            _criticalEvents.RemoveAll(x => x.Timestamp < cutoff);
            _commands.RemoveAll(x => x.IssuedAt < cutoff);

            // A search that follows several critical events must still count once, so occurrences are a set of distinct searches.
            var patterns = new Dictionary<(string DeviceMac, string Query), HashSet<(DateTime Timestamp, string Query)>>();
            foreach (var criticalEvent in _criticalEvents)
            {
                var mac = criticalEvent.DeviceMac;
                var macHex = mac.ToUpperInvariant().Replace(":", "").Replace("-", "");
                var macSuffix = macHex.Length > 6 ? macHex[^6..] : macHex;
                var windowEnd = criticalEvent.Timestamp.AddMinutes(5);

                foreach (var search in _searchHistory)
                {
                    if (search.Timestamp < criticalEvent.Timestamp || search.Timestamp > windowEnd)
                    {
                        continue;
                    }

                    var queryHex = search.Query.ToUpperInvariant().Replace(":", "").Replace("-", "");
                    if (!queryHex.Contains(macHex) && !queryHex.Contains(macSuffix))
                    {
                        continue;
                    }

                    var key = (mac, search.Query);
                    if (!patterns.TryGetValue(key, out var distinctSearches))
                    {
                        distinctSearches = new HashSet<(DateTime Timestamp, string Query)>();
                        patterns[key] = distinctSearches;
                    }

                    distinctSearches.Add((search.Timestamp, search.Query));
                }
            }

            var insights = new Dictionary<(string DeviceMac, string SuggestedAction), ActionInsight>();

            // Devices that keep reporting critical readings are flagged before anyone searches for them.
            var recentCutoff = DateTime.UtcNow.AddMinutes(-5);
            var recentCriticalCounts = _criticalEvents
                .Where(e => e.Timestamp >= recentCutoff)
                .GroupBy(e => CanonicalMac(e.DeviceMac));

            foreach (var group in recentCriticalCounts)
            {
                var count = group.Count();
                if (count < 3)
                {
                    continue;
                }

                insights[(group.Key, InvestigateAction)] = new ActionInsight(
                    group.First().DeviceMac,
                    InvestigateAction,
                    $"{count} critical readings on this device in the last 5 minutes.",
                    0.8);
            }

            foreach (var ((deviceMac, query), distinctSearches) in patterns)
            {
                var count = distinctSearches.Count;
                if (count < 3)
                {
                    continue;
                }

                var canonicalMac = CanonicalMac(deviceMac);
                var latestCommand = _commands
                    .Where(c => CanonicalMac(c.DeviceMac) == canonicalMac)
                    .MaxBy(c => c.IssuedAt);

                var suggestedAction = latestCommand?.CommandType ?? InvestigateAction;
                var confidence = count >= 5 ? 0.9 : count == 4 ? 0.75 : 0.6;
                var insightKey = (canonicalMac, suggestedAction);

                if (insights.TryGetValue(insightKey, out var existing) && existing.Confidence >= confidence)
                {
                    continue;
                }

                insights[insightKey] = new ActionInsight(
                    deviceMac,
                    suggestedAction,
                    $"{count} separate searches for '{query}' followed critical readings on this device in the last 24 hours.",
                    confidence);
            }

            return insights.Values.OrderByDescending(i => i.Confidence).ToList();
        }
    }

    private static string CanonicalMac(string mac) => mac.Replace('-', ':').ToUpperInvariant();

    public void Clear()
    {
        lock (_lock)
        {
            _searchHistory.Clear();
            _criticalEvents.Clear();
            _commands.Clear();
        }
    }
}
