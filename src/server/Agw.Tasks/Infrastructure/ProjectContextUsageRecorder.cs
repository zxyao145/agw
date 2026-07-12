using Agw.Shared.Contracts.Tasks;
using Agw.Shared.Data.Entities.Tasks;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tasks.Infrastructure;

public sealed class ProjectContextUsageRecorder : IProjectContextUsageRecorder
{
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public ProjectContextUsageRecorder(IServiceScopeFactory serviceScopeFactory)
    {
        _serviceScopeFactory = serviceScopeFactory;
    }

    public async Task AddAsync(
        Guid projectId,
        string contextId,
        ProjectContextUsage usage,
        CancellationToken cancellationToken = default)
    {
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();

        await dbContext.Set<ProjectContext>()
            .Where(context => context.ProjectId == projectId && context.ContextId == contextId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(
                        context => context.Usage.InputTokenCount,
                        context => context.Usage.InputTokenCount + usage.InputTokenCount)
                    .SetProperty(
                        context => context.Usage.OutputTokenCount,
                        context => context.Usage.OutputTokenCount + usage.OutputTokenCount)
                    .SetProperty(
                        context => context.Usage.TotalTokenCount,
                        context => context.Usage.TotalTokenCount + usage.TotalTokenCount)
                    .SetProperty(
                        context => context.Usage.CachedInputTokenCount,
                        context => context.Usage.CachedInputTokenCount + usage.CachedInputTokenCount)
                    .SetProperty(
                        context => context.Usage.ReasoningTokenCount,
                        context => context.Usage.ReasoningTokenCount + usage.ReasoningTokenCount),
                cancellationToken);
    }
}
