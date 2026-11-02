namespace SmartX.Gateway.Shared.Services;

// The one list of factory areas, shared by seeding, the mesh routers and the console.
public static class FactoryAreas
{
    public static IReadOnlyList<string> All { get; } =
        ["Assembly", "Packaging", "Boiler Room", "Cold Storage", "Paint Shop", "Loading Bay", "Press Shop"];

    // Locations read "<Area> - Line <N>"; anything before the first " - " is the area.
    public static string AreaOf(string? location)
    {
        if (string.IsNullOrWhiteSpace(location))
        {
            return string.Empty;
        }

        var separator = location.IndexOf(" - ", StringComparison.Ordinal);
        return (separator >= 0 ? location[..separator] : location).Trim();
    }

    public static int IndexOf(string? area)
    {
        for (int i = 0; i < All.Count; i++)
        {
            if (string.Equals(All[i], area, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    public static bool IsKnown(string? area) => IndexOf(area) >= 0;

    // The canonical spelling of a known area, so "boiler room" and "Boiler Room" share one router.
    public static string? Canonical(string? area) => IndexOf(area) is var index and >= 0 ? All[index] : null;
}
