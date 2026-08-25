using Agw.Projects.Contracts.Metrics;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Repositories;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application.Facades;

public sealed class ProjectMetricsFacade : IProjectMetricsFacade
{
    private readonly IRepository<Project> _projectRepository;
    private readonly IRepository<ProjectConversation> _conversationRepository;
    private readonly IRepository<ProjectConversationChatHistory> _historyRepository;
    private readonly IRepository<AgentUsage> _usageRepository;

    public ProjectMetricsFacade(
        IRepository<Project> projectRepository,
        IRepository<ProjectConversation> conversationRepository,
        IRepository<ProjectConversationChatHistory> historyRepository,
        IRepository<AgentUsage> usageRepository
    )
    {
        _projectRepository = projectRepository;
        _conversationRepository = conversationRepository;
        _historyRepository = historyRepository;
        _usageRepository = usageRepository;
    }

    public async Task<ProjectMetrics> GetAsync(CancellationToken cancellationToken = default)
    {
        var projectCount = await _projectRepository.Queryable.CountAsync(cancellationToken).ConfigureAwait(false);
        var conversationCount = await _conversationRepository
            .Queryable.CountAsync(cancellationToken)
            .ConfigureAwait(false);
        var historyCount = await _historyRepository.Queryable.CountAsync(cancellationToken).ConfigureAwait(false);
        var inputTokens = await _usageRepository
            .Queryable.SumAsync(usage => usage.InputTokenCount, cancellationToken)
            .ConfigureAwait(false);
        var outputTokens = await _usageRepository
            .Queryable.SumAsync(usage => usage.OutputTokenCount, cancellationToken)
            .ConfigureAwait(false);
        var totalTokens = await _usageRepository
            .Queryable.SumAsync(usage => usage.TotalTokenCount, cancellationToken)
            .ConfigureAwait(false);

        return new ProjectMetrics(
            projectCount,
            conversationCount,
            historyCount,
            inputTokens,
            outputTokens,
            totalTokens
        );
    }
}
