using Agw.Shared.Contracts.Tools.Abstractions;

using Microsoft.Extensions.AI;

namespace Agw.Tools.Impl.Weather;

/// <summary>
/// Example of a class-based weather tool using the new <see cref="IAgwTool"/> pattern.
/// </summary>
public class AdvancedWeatherTool : IAgwTool
{
    public string Name => "get_advanced_weather";

    public string Category => "Test";

    [Description("Get advanced weather information including humidity, wind, UV index, and optional alerts.")]
    public string Execute(
        [Description("The location to get weather for")] string location,
        [Description("Whether to include weather alerts")] bool includeAlerts = false)
    {
        var result = $"""
            Advanced Weather for {location}:
            - Temperature: 18°C (feels like 16°C)
            - Humidity: 65%
            - Wind: 12 km/h from the West
            - UV Index: 3 (Moderate)
            - Visibility: 10 km
            - Pressure: 1013 hPa
            """;

        if (includeAlerts)
        {
            result += $"{Environment.NewLine}- Alerts: None active";
        }

        return result;
    }

    public AITool ToAITool()
    {
        Func<string, bool, string> func = Execute;
        return AIFunctionFactory.Create(func, Name);
    }
}
