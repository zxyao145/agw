using DSystem.Domain.Attributes;
using System.ComponentModel;

namespace DSystem.Domain.Tools;

/// <summary>
/// Provides basic utility tools for agents.
/// </summary>
public static class BasicTools
{
    [Tool(Category = "DateTime")]
    [Description("Returns the current UTC date and time")]
    public static string GetCurrentDateTime()
    {
        return DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss UTC");
    }

    [Tool(Category = "DateTime")]
    [Description("Returns the current date in yyyy-MM-dd format")]
    public static string GetCurrentDate()
    {
        return DateTime.UtcNow.ToString("yyyy-MM-dd");
    }

    [Tool(Category = "Math")]
    [Description("Adds two numbers and returns the result")]
    public static double Add(
        [Description("The first number")] double a,
        [Description("The second number")] double b)
    {
        return a + b;
    }

    [Tool(Category = "Math")]
    [Description("Multiplies two numbers and returns the result")]
    public static double Multiply(
        [Description("The first number")] double a,
        [Description("The second number")] double b)
    {
        return a * b;
    }

    [Tool(Category = "Utility")]
    [Description("Generates a random integer between min and max (inclusive)")]
    public static int GetRandomNumber(
        [Description("Minimum value (inclusive)")] int min,
        [Description("Maximum value (inclusive)")] int max)
    {
        return Random.Shared.Next(min, max + 1);
    }

    [Tool(Category = "Text")]
    [Description("Converts the input text to uppercase")]
    public static string ToUpperCase(
        [Description("The text to convert")] string text)
    {
        return text.ToUpper();
    }

    [Tool(Category = "Text")]
    [Description("Converts the input text to lowercase")]
    public static string ToLowerCase(
        [Description("The text to convert")] string text)
    {
        return text.ToLower();
    }

    [Tool(Category = "Text")]
    [Description("Returns the number of characters in the given text")]
    public static int CountCharacters(
        [Description("The text to count")] string text)
    {
        return text.Length;
    }
}
