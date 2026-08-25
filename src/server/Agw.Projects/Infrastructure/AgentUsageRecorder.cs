using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Projects.Infrastructure;

public sealed class AgentUsageRecorder : IAgentUsageRecorder
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;

    public AgentUsageRecorder(IServiceScopeFactory serviceScopeFactory, TimeProvider timeProvider)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
    }

    public async Task AddAsync(
        Guid projectId,
        string contextId,
        string agentName,
        ProjectContextUsage usage,
        CancellationToken cancellationToken = default
    )
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();

        await dbContext
            .Set<AgentUsage>()
            .AddAsync(
                new AgentUsage
                {
                    Id = Guid.CreateVersion7(),
                    ProjectId = projectId,
                    ContextId = ContextIdUtil.NormalizeContextId(contextId),
                    AgentName = agentName,
                    RecordedAt = _timeProvider.GetUtcNow(),
                    InputTokenCount = usage.InputTokenCount,
                    OutputTokenCount = usage.OutputTokenCount,
                    TotalTokenCount = usage.TotalTokenCount,
                    CachedInputTokenCount = usage.CachedInputTokenCount,
                    ReasoningTokenCount = usage.ReasoningTokenCount,
                },
                cancellationToken
            );
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
