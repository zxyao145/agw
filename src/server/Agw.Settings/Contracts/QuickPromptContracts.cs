namespace Agw.Settings.Contracts;

public static class QuickPromptSettings
{
    public const string Key = "quick-prompts";
}

public sealed record QuickPromptItem(string Id, string Label, string Text, string? Description = null);

public sealed record QuickPromptResponse(string Id, string Label, string Text, string? Description, string Kind);

public sealed record QuickPromptList(IReadOnlyList<QuickPromptResponse> Items);

public sealed record QuickPromptManageResponse(
    QuickPromptList System,
    long? SystemVersion,
    QuickPromptList User,
    long? UserVersion,
    bool CanManageSystem
);

public sealed record QuickPromptUpdateRequest(long? Version, IReadOnlyList<QuickPromptItem> Items);

internal sealed class QuickPromptDocument
{
    public IReadOnlyList<QuickPromptItem> Items { get; init; } = [];
}
