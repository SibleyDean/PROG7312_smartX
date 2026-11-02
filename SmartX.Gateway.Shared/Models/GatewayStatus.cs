namespace SmartX.Gateway.Shared.Models;

// The reading that most recently raised (or re-raised) a sensor's open alert. Priority 1 is critical, 2 is warning.
public record AlertDetail(Guid SensorId, double Value, int Priority, DateTime Timestamp);

public record GatewaySummary(
    int SensorCount,
    int OpenAlerts,
    int ReadingsLastMinute,
    int TotalReadings,
    int AlertsAcknowledged,
    bool IsGenerationPaused)
{
    // Seeded and manually registered sensors; mesh sensors always exist, so they do not count as "added".
    public int DemoSensorCount { get; init; }
    public int ConnectionProblems { get; init; }
    public List<string> ConnectionIssues { get; init; } = new();
}

public record SimulationStatus(bool IsPaused);
