namespace Agw.Projects.Contracts.Metrics;

public sealed record ProjectMetrics(
    int ProjectCount,
    int ConversationCount,
    int TaskRecordCount,
    long InputTokens,
    long OutputTokens,
    long TotalTokens
);

public interface IProjectMetricsFacade
{
    Task<ProjectMetrics> GetAsync(CancellationToken cancellationToken = default);
}
