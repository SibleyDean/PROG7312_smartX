using System.ComponentModel.DataAnnotations;

namespace SmartX.Gateway.Shared.Models;

public class SensorProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required(ErrorMessage = "Device MAC address is required.")]
    [RegularExpression("^([0-9A-Fa-f]{2}[:-]){5}([0-9A-Fa-f]{2})$", ErrorMessage = "Invalid MAC address format.")]
    public string MacAddress { get; set; } = string.Empty;

    [Required(ErrorMessage = "Deployment location is required.")]
    public string Location { get; set; } = string.Empty;

    [Required(ErrorMessage = "Sensor category is required.")]
    public SensorCategory Category { get; set; }

    public SensorSource Source { get; set; } = SensorSource.Manual;

    public List<MediaAttachment> Attachments { get; set; } = new();
    public List<TelemetryReading<double>> HistoricalTelemetry { get; set; } = new();
}

public enum SensorCategory
{
    Environmental,
    PowerConsumption,
    Actuator,
    Unknown
}

// Seeded sensors are demo data, Manual ones were registered through the API, Mesh ones belong to the fixed mesh network.
public enum SensorSource
{
    Seeded,
    Manual,
    Mesh
}

public class MediaAttachment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public byte[] FileData { get; set; } = Array.Empty<byte>();
}