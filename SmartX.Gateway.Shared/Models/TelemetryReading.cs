namespace SmartX.Gateway.Shared.Models;

public class TelemetryReading<T> where T : struct
{
    public Guid SensorId { get; set; }
    public T Value { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public static TelemetryReading<T> operator +(TelemetryReading<T> left, TelemetryReading<T> right)
    {
        if (left is null || right is null)
            throw new ArgumentNullException("Cannot aggregate null telemetry readings.");

        return new TelemetryReading<T>
        {
            SensorId = left.SensorId,
            Value = (dynamic)left.Value + right.Value,
            Timestamp = DateTime.UtcNow
        };
    }
}