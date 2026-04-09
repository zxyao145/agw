using System.ComponentModel;
using System.Globalization;

using Agw.Tools.Attributes;

namespace Agw.Tools.Impl.Basic;

/// <summary>
/// Provides basic utility tools for agents.
/// </summary>
[AiToolContainer(DefaultCategory = "Utility")]
public static class BasicTools
{
    [AiTool("get_current_date_time", Category = "DateTime")]
    [Description("Returns the current UTC date and time")]
    public static string GetCurrentDateTime()
    {
        return DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    }

    [AiTool("get_current_date", Category = "DateTime")]
    [Description("Returns the current date in yyyy-MM-dd format")]
    public static string GetCurrentDate()
    {
        return DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    [AiTool("add", Category = "Math")]
    [Description("Adds two numbers and returns the result")]
    public static double Add(
        [Description("The first number")] double a,
        [Description("The second number")] double b)
    {
        return a + b;
    }

    [AiTool("multiply", Category = "Math")]
    [Description("Multiplies two numbers and returns the result")]
    public static double Multiply(
        [Description("The first number")] double a,
        [Description("The second number")] double b)
    {
        return a * b;
    }

    [AiTool("subtract", Category = "Math")]
    [Description("Subtracts the second number from the first and returns the result")]
    public static double Subtract(
        [Description("The first number")] double a,
        [Description("The second number")] double b)
    {
        return a - b;
    }

    [AiTool("divide", Category = "Math")]
    [Description("Divides the first number by the second and returns the result")]
    public static double Divide(
        [Description("The dividend (number to be divided)")] double a,
        [Description("The divisor (number to divide by)")] double b)
    {
        if (b == 0)
        {
            throw new DivideByZeroException("Cannot divide by zero");
        }
        return a / b;
    }

    [AiTool("get_random_number", Category = "Utility")]
    [Description("Generates a random integer between min and max (inclusive)")]
    public static int GetRandomNumber(
        [Description("Minimum value (inclusive)")] int min,
        [Description("Maximum value (inclusive)")] int max)
    {
        return Random.Shared.Next(min, max + 1);
    }

    [AiTool("to_upper_case", Category = "Text")]
    [Description("Converts the input text to uppercase")]
    public static string ToUpperCase(
        [Description("The text to convert")] string text)
    {
        return text.ToUpperInvariant();
    }

    [AiTool("to_lower_case", Category = "Text")]
    [Description("Converts the input text to lowercase")]
    public static string ToLowerCase(
        [Description("The text to convert")] string text)
    {
        return text.ToLowerInvariant();
    }

    [AiTool("count_characters", Category = "Text")]
    [Description("Returns the number of characters in the given text")]
    public static int CountCharacters(
        [Description("The text to count")] string text)
    {
        return text.Length;
    }

    [AiTool("count_words", Category = "Text")]
    [Description("Returns the number of words in the given text")]
    public static int CountWords(
        [Description("The text to count words in")] string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }
        return text.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries).Length;
    }

    [AiTool("reverse_text", Category = "Text")]
    [Description("Reverses the given text")]
    public static string ReverseText(
        [Description("The text to reverse")] string text)
    {
        var chars = text.ToCharArray();
        Array.Reverse(chars);
        return new string(chars);
    }

    [AiTool("trim_text", Category = "Text")]
    [Description("Trims whitespace from both ends of the text")]
    public static string TrimText(
        [Description("The text to trim")] string text)
    {
        return text.Trim();
    }

    [AiTool("generate_guid", Category = "Utility")]
    [Description("Generates a new unique identifier (GUID)")]
    public static string GenerateGuid()
    {
        return Guid.NewGuid().ToString();
    }

    [AiTool("get_unix_timestamp", Category = "DateTime")]
    [Description("Returns the current Unix timestamp (seconds since 1970-01-01)")]
    public static long GetUnixTimestamp()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    [AiTool("unix_timestamp_to_date", Category = "DateTime")]
    [Description("Converts a Unix timestamp to a human-readable date string")]
    public static string UnixTimestampToDate(
        [Description("The Unix timestamp (seconds since 1970-01-01)")] long timestamp)
    {
        var dateTime = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
        return dateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);
    }
}
