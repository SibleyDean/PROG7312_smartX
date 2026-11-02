using System.Globalization;

namespace SmartX.Gateway.Client;

// The gateway stamps everything in UTC; the console always shows the operator's local time.
public static class TimeDisplay
{
    public static DateTime ToLocal(DateTime timestamp) => timestamp.Kind switch
    {
        DateTimeKind.Local => timestamp,
        DateTimeKind.Utc => timestamp.ToLocalTime(),
        _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc).ToLocalTime()
    };

    public static string Clock(DateTime timestamp) => ToLocal(timestamp).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Full(DateTime timestamp) => ToLocal(timestamp).ToString("ddd d MMM yyyy, HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Relative(DateTime timestamp)
    {
        var utc = timestamp.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(timestamp, DateTimeKind.Utc) : timestamp.ToUniversalTime();
        var age = DateTime.UtcNow - utc;

        // Small clock differences between gateway and browser can put a fresh reading slightly in the future.
        if (age < TimeSpan.FromSeconds(5))
        {
            return "just now";
        }

        if (age < TimeSpan.FromMinutes(1))
        {
            return $"{(int)age.TotalSeconds}s ago";
        }

        if (age < TimeSpan.FromHours(1))
        {
            return $"{(int)age.TotalMinutes}m ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            return $"{(int)age.TotalHours}h ago";
        }

        return ToLocal(timestamp).ToString("d MMM HH:mm", CultureInfo.InvariantCulture);
    }
}
