using System.Globalization;
using SmartX.Gateway.Shared.Models;

namespace SmartX.Gateway.Shared.Services;

public enum ReadingStatus
{
    Normal,
    Warning,
    Critical
}

// Single source of truth for units, labels and alert thresholds, so the gateway and the console never disagree.
public static class SensorReadingInfo
{
    public const double CriticalLow = 5;
    public const double CriticalHigh = 95;
    public const double WarningLow = 10;
    public const double WarningHigh = 90;

    // The random walk stays inside this band; anything outside it is a fault spike.
    private const double WalkMin = 12;
    private const double WalkMax = 88;
    private const double PullToNominal = 0.15;
    private const double Noise = 3;
    private const double SpikeChance = 0.03;

    public static ReadingStatus GetStatus(double value)
    {
        if (value > CriticalHigh || value < CriticalLow)
        {
            return ReadingStatus.Critical;
        }

        if (value > WarningHigh || value < WarningLow)
        {
            return ReadingStatus.Warning;
        }

        return ReadingStatus.Normal;
    }

    public static string Unit(SensorCategory category) => category switch
    {
        SensorCategory.Environmental => "°C",
        SensorCategory.PowerConsumption => "kW",
        SensorCategory.Actuator => "% open",
        _ => "units"
    };

    public static string Describe(SensorCategory category) => category switch
    {
        SensorCategory.Environmental => "Temperature",
        SensorCategory.PowerConsumption => "Power draw",
        SensorCategory.Actuator => "Valve position",
        _ => "Raw signal"
    };

    public static string Explain(SensorCategory category) => category switch
    {
        SensorCategory.Environmental => "Environmental sensors report the temperature around a machine or room in °C.",
        SensorCategory.PowerConsumption => "Power sensors report how much electricity a machine is drawing in kW.",
        SensorCategory.Actuator => "Actuators are valves you can open or close; they report how far open they are.",
        _ => "Unknown devices send a raw number whose meaning has not been set up yet."
    };

    public static string Explain(ReadingStatus status) => status switch
    {
        ReadingStatus.Critical => $"Critical: outside the safe range (below {CriticalLow:0} or above {CriticalHigh:0}) - act now.",
        ReadingStatus.Warning => $"Warning: close to the limit (below {WarningLow:0} or above {WarningHigh:0}) - keep an eye on it.",
        _ => $"Normal: within the expected range ({WarningLow:0} to {WarningHigh:0}) - nothing to do."
    };

    public static double NominalValue(SensorCategory category) => category switch
    {
        SensorCategory.Environmental => 45,
        SensorCategory.PowerConsumption => 55,
        _ => 50
    };

    public static string Format(double value, SensorCategory category) =>
        $"{value.ToString("0.0", CultureInfo.InvariantCulture)} {Unit(category)}";

    public static double NextValue(double previous, SensorCategory category, Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var nominal = NominalValue(category);

        if (random.NextDouble() < SpikeChance)
        {
            var spike = random.Next(2) == 0
                ? random.NextDouble() * 9
                : 91 + random.NextDouble() * 9;
            return Math.Round(spike, 2);
        }

        // A value outside the walk band can only be a spike (or an override), so the sensor recovers near nominal.
        if (previous < WalkMin || previous > WalkMax || double.IsNaN(previous))
        {
            return Math.Round(nominal + (random.NextDouble() * 2 - 1) * Noise, 2);
        }

        var drift = (nominal - previous) * PullToNominal;
        var noise = (random.NextDouble() * 2 - 1) * Noise;
        return Math.Round(Math.Clamp(previous + drift + noise, WalkMin, WalkMax), 2);
    }
}
