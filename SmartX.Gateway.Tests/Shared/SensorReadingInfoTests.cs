using SmartX.Gateway.Shared.Models;
using SmartX.Gateway.Shared.Services;

namespace SmartX.Gateway.Tests.Shared;

public class SensorReadingInfoTests
{
    [Theory]
    [InlineData(4.99, ReadingStatus.Critical)]
    [InlineData(5, ReadingStatus.Warning)]
    [InlineData(9.99, ReadingStatus.Warning)]
    [InlineData(10, ReadingStatus.Normal)]
    [InlineData(50, ReadingStatus.Normal)]
    [InlineData(90, ReadingStatus.Normal)]
    [InlineData(90.01, ReadingStatus.Warning)]
    [InlineData(95, ReadingStatus.Warning)]
    [InlineData(95.01, ReadingStatus.Critical)]
    [InlineData(-1, ReadingStatus.Critical)]
    [InlineData(101, ReadingStatus.Critical)]
    public void GetStatus_uses_the_alert_thresholds(double value, ReadingStatus expected)
    {
        Assert.Equal(expected, SensorReadingInfo.GetStatus(value));
    }

    [Theory]
    [InlineData(SensorCategory.Environmental, "°C", "Temperature", 45)]
    [InlineData(SensorCategory.PowerConsumption, "kW", "Power draw", 55)]
    [InlineData(SensorCategory.Actuator, "% open", "Valve position", 50)]
    [InlineData(SensorCategory.Unknown, "units", "Raw signal", 50)]
    public void Each_category_has_a_unit_description_explanation_and_nominal_value(SensorCategory category, string unit, string description, double nominal)
    {
        Assert.Equal(unit, SensorReadingInfo.Unit(category));
        Assert.Equal(description, SensorReadingInfo.Describe(category));
        Assert.Equal(nominal, SensorReadingInfo.NominalValue(category));
        Assert.False(string.IsNullOrWhiteSpace(SensorReadingInfo.Explain(category)));
        Assert.EndsWith(".", SensorReadingInfo.Explain(category));
    }

    [Theory]
    [InlineData(47.34, SensorCategory.Environmental, "47.3 °C")]
    [InlineData(55, SensorCategory.PowerConsumption, "55.0 kW")]
    [InlineData(12.06, SensorCategory.Actuator, "12.1 % open")]
    [InlineData(0, SensorCategory.Unknown, "0.0 units")]
    public void Format_adds_the_unit_with_one_decimal(double value, SensorCategory category, string expected)
    {
        Assert.Equal(expected, SensorReadingInfo.Format(value, category));
    }

    [Fact]
    public void Status_explanations_are_plain_language()
    {
        Assert.StartsWith("Critical: outside the safe range", SensorReadingInfo.Explain(ReadingStatus.Critical));
        Assert.Contains("act now", SensorReadingInfo.Explain(ReadingStatus.Critical));
        Assert.StartsWith("Warning:", SensorReadingInfo.Explain(ReadingStatus.Warning));
        Assert.StartsWith("Normal:", SensorReadingInfo.Explain(ReadingStatus.Normal));
    }

    [Fact]
    public void NextValue_without_noise_pulls_fifteen_percent_toward_nominal()
    {
        // 0.5 means "no spike" (>= 3%) and zero noise, so only the pull towards nominal remains.
        var random = new FixedRandom(0.5);

        Assert.Equal(79, SensorReadingInfo.NextValue(85, SensorCategory.Environmental, random), 6);
        Assert.Equal(19.5, SensorReadingInfo.NextValue(15, SensorCategory.Environmental, random), 6);
        Assert.Equal(45, SensorReadingInfo.NextValue(45, SensorCategory.Environmental, random), 6);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(97)]
    [InlineData(11.9)]
    [InlineData(88.1)]
    public void NextValue_recovers_near_nominal_after_a_spike_or_out_of_band_value(double previous)
    {
        var next = SensorReadingInfo.NextValue(previous, SensorCategory.PowerConsumption, new FixedRandom(0.5));
        Assert.Equal(55, next, 6);
    }

    [Fact]
    public void NextValue_stays_in_band_between_spikes_and_spikes_about_three_percent_of_the_time()
    {
        var random = new Random(42);
        var value = SensorReadingInfo.NominalValue(SensorCategory.Actuator);
        var spikes = 0;
        const int steps = 10_000;

        for (int i = 0; i < steps; i++)
        {
            var previousWasSpike = value < 12 || value > 88;
            value = SensorReadingInfo.NextValue(value, SensorCategory.Actuator, random);

            var isSpike = value is >= 0 and <= 9 or >= 91 and <= 100;
            if (isSpike)
            {
                spikes++;
                continue;
            }

            Assert.InRange(value, 12, 88);
            if (previousWasSpike)
            {
                Assert.InRange(value, 47, 53);
            }
        }

        Assert.InRange(spikes / (double)steps, 0.02, 0.04);
    }

    [Fact]
    public void NextValue_requires_a_random_source()
    {
        Assert.Throws<ArgumentNullException>(() => SensorReadingInfo.NextValue(50, SensorCategory.Actuator, null!));
    }
}
