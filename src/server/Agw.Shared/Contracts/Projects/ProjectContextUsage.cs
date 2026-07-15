namespace Agw.Shared.Contracts.Projects;

public sealed record ProjectContextUsage
{
    public long InputTokenCount { get; init; }

    public long OutputTokenCount { get; init; }

    public long TotalTokenCount { get; init; }

    public long CachedInputTokenCount { get; init; }

    public long ReasoningTokenCount { get; init; }
}
