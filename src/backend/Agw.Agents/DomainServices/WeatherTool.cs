using Microsoft.Extensions.AI;
using System.ComponentModel;

namespace Agw.Domain.Tools;

/// <summary>
/// Weather-related tools for agents.
/// Demonstrates both static method tools and class-based tools.
/// </summary>
[AiToolContainer(DefaultCategory = "Weather")]
public static class WeatherTools
{
    [AiTool(Category = "Weather")]
    [Description("Get the current weather for a given location.")]
    public static string GetWeather(
        [Description("The location to get the weather for (city name or coordinates).")] string location)
    {
        // In a real implementation, this would call a weather API
        return $"The weather in {location} is cloudy with a high of 15°C.";
    }

    [AiTool(Category = "Weather")]
    [Description("Get the weather forecast for the next few days at a given location.")]
    public static string GetForecast(
        [Description("The location to get the forecast for.")] string location,
        [Description("Number of days to forecast (1-7).")] int days = 3)
    {
        // Clamp days to valid range
        days = Math.Clamp(days, 1, 7);

        // In a real implementation, this would call a weather API
        var forecasts = new List<string>();
        var baseTemp = 15;
        var conditions = new[] { "Sunny", "Cloudy", "Rainy", "Partly cloudy", "Clear" };
        var random = new Random();

        for (int i = 0; i < days; i++)
        {
            var date = DateTime.UtcNow.AddDays(i).ToString("yyyy-MM-dd");
            var condition = conditions[random.Next(conditions.Length)];
            var temp = baseTemp + random.Next(-5, 6);
            forecasts.Add($"{date}: {condition}, High of {temp}°C");
        }

        return $"Weather forecast for {location}:\n" + string.Join("\n", forecasts);
    }

    [AiTool(Category = "Weather")]
    [Description("Convert temperature between Celsius and Fahrenheit.")]
    public static string ConvertTemperature(
        [Description("The temperature value to convert.")] double temperature,
        [Description("The unit to convert from ('C' for Celsius, 'F' for Fahrenheit).")] string fromUnit)
    {
        fromUnit = fromUnit.ToUpperInvariant();

        if (fromUnit == "C" || fromUnit == "CELSIUS")
        {
            var fahrenheit = (temperature * 9 / 5) + 32;
            return $"{temperature}°C = {fahrenheit:F1}°F";
        }
        else if (fromUnit == "F" || fromUnit == "FAHRENHEIT")
        {
            var celsius = (temperature - 32) * 5 / 9;
            return $"{temperature}°F = {celsius:F1}°C";
        }
        else
        {
            return $"Unknown unit '{fromUnit}'. Use 'C' for Celsius or 'F' for Fahrenheit.";
        }
    }
}

/// <summary>
/// Example of a class-based weather tool that could use dependency injection.
/// Demonstrates the IAiTool interface pattern.
/// </summary>
public class AdvancedWeatherTool : SimpleAiTool
{
    public override string Name => "GetAdvancedWeather";

    public override string Description => "Get advanced weather information including humidity, wind, and UV index.";

    public override string Category => "Weather";

    protected override object? ExecuteCore(AIFunctionArguments arguments)
    {
        var location = arguments.TryGetValue("location", out var loc) ? loc?.ToString() : "Unknown";
        var includeAlerts = arguments.TryGetValue("includeAlerts", out var alerts) && alerts is true;

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
            result += "\n- Alerts: None active";
        }

        return result;
    }

    public override AIFunction ToAIFunction()
    {
        return AIFunctionFactory.Create(
            ([Description("The location to get weather for")] string location,
             [Description("Whether to include weather alerts")] bool includeAlerts = false) =>
            {
                var args = new AIFunctionArguments
                {
                    ["location"] = location,
                    ["includeAlerts"] = includeAlerts
                };
                return ExecuteCore(args)?.ToString() ?? string.Empty;
            },
            new AIFunctionFactoryOptions
            {
                Name = Name,
                Description = Description
            });
    }
}
