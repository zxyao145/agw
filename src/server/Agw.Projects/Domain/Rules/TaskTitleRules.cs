namespace Agw.Projects.Domain.Rules;

public static class TaskTitleRules
{
    public const string DefaultTitle = "New Chat";

    public static string Create(string? text, string fallback = DefaultTitle)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return fallback;
        }

        return trimmed[..Math.Min(trimmed.Length, 80)];
    }
}
