using SmartX.Gateway.Shared.Models;
using SmartX.Gateway.Shared.Services;

namespace SmartX.Gateway.Tests.Shared;

public class CommandCatalogTests
{
    [Fact]
    public void Commands_fit_their_device_types()
    {
        Assert.Equal(["OpenValve", "CloseValve", "RestartSensor"], CommandCatalog.For(SensorCategory.Actuator));
        Assert.Equal(["ToggleRelay", "RestartSensor"], CommandCatalog.For(SensorCategory.PowerConsumption));
        Assert.Equal(["RestartSensor"], CommandCatalog.For(SensorCategory.Environmental));
        Assert.Equal(["RestartSensor"], CommandCatalog.For(SensorCategory.Unknown));
    }

    [Theory]
    [InlineData("OpenValve", SensorCategory.Actuator, true)]
    [InlineData("OpenValve", SensorCategory.Environmental, false)]
    [InlineData("CloseValve", SensorCategory.PowerConsumption, false)]
    [InlineData("ToggleRelay", SensorCategory.PowerConsumption, true)]
    [InlineData("ToggleRelay", SensorCategory.Actuator, false)]
    [InlineData("RestartSensor", SensorCategory.Unknown, true)]
    [InlineData("Investigate Device", SensorCategory.Actuator, false)]
    public void FitsCategory(string command, SensorCategory category, bool fits)
    {
        Assert.Equal(fits, CommandCatalog.FitsCategory(command, category));
    }

    [Theory]
    [InlineData("OpenValve", "Open Valve")]
    [InlineData("CloseValve", "Close Valve")]
    [InlineData("ToggleRelay", "Toggle Relay")]
    [InlineData("RestartSensor", "Restart Sensor")]
    [InlineData("Investigate Device", "Investigate Device")]
    public void Labels_are_readable(string command, string label)
    {
        Assert.Equal(label, CommandCatalog.Label(command));
    }

    [Fact]
    public void Only_the_four_commands_are_real_commands()
    {
        Assert.Equal(4, CommandCatalog.All.Count);
        Assert.All(CommandCatalog.All, c => Assert.True(CommandCatalog.IsCommand(c)));
        Assert.False(CommandCatalog.IsCommand("Investigate Device"));
        Assert.False(CommandCatalog.IsCommand(null));
        Assert.All(CommandCatalog.All, c => Assert.NotEqual(c, CommandCatalog.Explain(c)));
    }
}

public class SensorSearchTests
{
    private static readonly SensorProfile Valve = new()
    {
        MacAddress = "00:1B:44:A3:5F:10",
        Location = "Boiler Room - Line 2",
        Category = SensorCategory.Actuator
    };

    [Theory]
    [InlineData("1b44a3")]
    [InlineData("1B-44-A3")]
    [InlineData("1B:44:A3")]
    [InlineData("a3 5f 10")]
    [InlineData("boiler")]
    [InlineData("Line 2")]
    [InlineData("line2")]
    [InlineData("Boiler Room - Line 2")]
    [InlineData("Actuator")]
    [InlineData("valve position")]
    [InlineData("   Boiler   ")]
    public void Matches_mac_location_category_and_description_ignoring_separators_and_case(string query)
    {
        Assert.True(SensorSearch.Matches(Valve, query));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("::--")]
    public void Empty_queries_match_everything(string? query)
    {
        Assert.True(SensorSearch.Matches(Valve, query));
    }

    [Theory]
    [InlineData("Packaging")]
    [InlineData("Line 3")]
    [InlineData("1B:44:A4")]
    [InlineData("Temperature")]
    public void Non_matching_queries_do_not_match(string query)
    {
        Assert.False(SensorSearch.Matches(Valve, query));
    }

    [Fact]
    public void Normalise_strips_separators_and_lowercases()
    {
        Assert.Equal("001b44a3", SensorSearch.Normalise(" 00:1B-44 A3 "));
        Assert.Equal(string.Empty, SensorSearch.Normalise(null));
    }
}

public class FactoryAreasTests
{
    [Fact]
    public void Lists_the_seven_areas_once()
    {
        Assert.Equal(["Assembly", "Packaging", "Boiler Room", "Cold Storage", "Paint Shop", "Loading Bay", "Press Shop"], FactoryAreas.All);
    }

    [Theory]
    [InlineData("Boiler Room - Line 2", "Boiler Room")]
    [InlineData("Rooftop", "Rooftop")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void AreaOf_takes_the_part_before_the_line(string? location, string expected)
    {
        Assert.Equal(expected, FactoryAreas.AreaOf(location));
    }

    [Fact]
    public void Canonical_fixes_case_and_rejects_unknown_areas()
    {
        Assert.Equal("Boiler Room", FactoryAreas.Canonical("boiler room"));
        Assert.Null(FactoryAreas.Canonical("Rooftop"));
    }
}
