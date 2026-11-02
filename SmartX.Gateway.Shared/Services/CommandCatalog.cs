using SmartX.Gateway.Shared.Models;

namespace SmartX.Gateway.Shared.Services;

public static class CommandCatalog
{
    public const string OpenValve = "OpenValve";
    public const string CloseValve = "CloseValve";
    public const string ToggleRelay = "ToggleRelay";
    public const string RestartSensor = "RestartSensor";

    public static IReadOnlyList<string> All { get; } = [OpenValve, CloseValve, ToggleRelay, RestartSensor];

    public static bool IsCommand(string? commandType) => commandType is not null && All.Contains(commandType);

    public static string Label(string commandType) => commandType switch
    {
        OpenValve => "Open Valve",
        CloseValve => "Close Valve",
        ToggleRelay => "Toggle Relay",
        RestartSensor => "Restart Sensor",
        _ => commandType
    };

    public static string Explain(string commandType) => commandType switch
    {
        OpenValve => "Opens the valve a further 15 percentage points.",
        CloseValve => "Closes the valve by 15 percentage points.",
        ToggleRelay => "Switches the power relay on or off.",
        RestartSensor => "Restarts the device; its next reading comes back at the normal level.",
        _ => commandType
    };

    // Valves only exist on actuators and relays only on power meters; every device can be restarted.
    public static bool FitsCategory(string commandType, SensorCategory category) => commandType switch
    {
        OpenValve or CloseValve => category == SensorCategory.Actuator,
        ToggleRelay => category == SensorCategory.PowerConsumption,
        RestartSensor => true,
        _ => false
    };

    public static IReadOnlyList<string> For(SensorCategory category) => All.Where(c => FitsCategory(c, category)).ToList();
}
