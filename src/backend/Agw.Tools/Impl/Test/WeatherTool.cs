using System.Globalization;

namespace Agw.Tools.Impl.Weather;

/// <summary>
/// Weather-related tools exposed as attributed static methods.
/// </summary>
[AiToolContainer(DefaultCategory = "Test")]
public static class WeatherTools
{
    [AiTool("get_weather", Category = "Test")]
    [Description("Get the current weather for a given location.")]
    public static string GetWeather(
        [Description("The location to get the weather for (city name or coordinates).")] string location)
    {
        return $"The weather in {location} is cloudy with a high of 15°C.";
    }

    [AiTool("get_forecast", Category = "Test")]
    [Description("Get the weather forecast for the next few days at a given location.")]
    public static string GetForecast(
        [Description("The location to get the forecast for.")] string location,
        [Description("Number of days to forecast (1-7).")] int days = 3)
    {
        days = Math.Clamp(days, 1, 7);

        var forecasts = new List<string>();
        var conditions = new[] { "Sunny", "Cloudy", "Rainy", "Partly cloudy", "Clear" };

        for (var i = 0; i < days; i++)
        {
            var date = DateTime.UtcNow.AddDays(i).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var condition = conditions[Random.Shared.Next(conditions.Length)];
            var temp = 15 + Random.Shared.Next(-5, 6);
            forecasts.Add($"{date}: {condition}, High of {temp.ToString(CultureInfo.InvariantCulture)}°C");
        }

        return $"Weather forecast for {location}:{Environment.NewLine}{string.Join(Environment.NewLine, forecasts)}";
    }

    [AiTool("convert_temperature", Category = "Test")]
    [Description("Convert temperature between Celsius and Fahrenheit.")]
    public static string ConvertTemperature(
        [Description("The temperature value to convert.")] double temperature,
        [Description("The unit to convert from ('C' for Celsius, 'F' for Fahrenheit).")] string fromUnit)
    {
        fromUnit = fromUnit.ToUpperInvariant();

        if (fromUnit is "C" or "CELSIUS")
        {
            var fahrenheit = (temperature * 9 / 5) + 32;
            return $"{temperature.ToString(CultureInfo.InvariantCulture)}°C = {fahrenheit.ToString("F1", CultureInfo.InvariantCulture)}°F";
        }

        if (fromUnit is "F" or "FAHRENHEIT")
        {
            var celsius = (temperature - 32) * 5 / 9;
            return $"{temperature.ToString(CultureInfo.InvariantCulture)}°F = {celsius.ToString("F1", CultureInfo.InvariantCulture)}°C";
        }

        return $"Unknown unit '{fromUnit}'. Use 'C' for Celsius or 'F' for Fahrenheit.";
    }
}
