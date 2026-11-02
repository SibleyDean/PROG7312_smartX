using SmartX.Gateway.Shared.Models;

namespace SmartX.Gateway.Shared.Services;

public static class SensorSearch
{
    // Separators and case are ignored, so "1b44a3", "1B-44-A3" and "1B:44:A3" all find the same device.
    public static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        return string.Concat(text.Trim().Where(char.IsLetterOrDigit)).ToLowerInvariant();
    }

    public static bool Matches(SensorProfile sensor, string? query)
    {
        var needle = Normalise(query);
        if (needle.Length == 0)
        {
            return true;
        }

        return Normalise(sensor.MacAddress).Contains(needle, StringComparison.Ordinal)
            || Normalise(sensor.Location).Contains(needle, StringComparison.Ordinal)
            || Normalise(sensor.Category.ToString()).Contains(needle, StringComparison.Ordinal)
            || Normalise(SensorReadingInfo.Describe(sensor.Category)).Contains(needle, StringComparison.Ordinal);
    }
}
