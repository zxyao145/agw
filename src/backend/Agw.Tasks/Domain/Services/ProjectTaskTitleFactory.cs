namespace Agw.Tasks.Domain.Services;

public static class ProjectTaskTitleFactory
{
    public static string Create(string? text, string fallback = "New Chat")
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return fallback;
        }

        return trimmed[..Math.Min(trimmed.Length, 80)];
    }
}
