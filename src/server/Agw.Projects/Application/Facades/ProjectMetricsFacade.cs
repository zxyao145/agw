using Agw.Auth.Contracts;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.Metrics;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application.Facades;

public sealed class ProjectMetricsFacade : IProjectMetricsFacade
{
    private readonly IProjectsDbContext _dbContext;
    private readonly IUserInfoService _userInfoService;

    public ProjectMetricsFacade(IProjectsDbContext dbContext, IUserInfoService userInfoService)
    {
        _dbContext = dbContext;
        _userInfoService = userInfoService;
    }

    public async Task<ProjectMetrics> GetAsync(CancellationToken cancellationToken = default)
    {
        var ownerUserId = ResolveOwnerUserId();
        var projectCount = await _dbContext
            .Projects.CountAsync(project => project.CreateBy == ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        var conversationCount = await _dbContext
            .ProjectConversations.CountAsync(conversation => conversation.CreateBy == ownerUserId, cancellationToken)
            .ConfigureAwait(false);
        var historyCount = await _dbContext
            .ProjectConversationChatHistories.CountAsync(
                history => history.ProjectConversation!.CreateBy == ownerUserId,
                cancellationToken
            )
            .ConfigureAwait(false);
        var inputTokens = await _dbContext
            .AgentUsages.Where(usage => usage.UserId == ownerUserId)
            .SumAsync(usage => usage.InputTokenCount, cancellationToken)
            .ConfigureAwait(false);
        var outputTokens = await _dbContext
            .AgentUsages.Where(usage => usage.UserId == ownerUserId)
            .SumAsync(usage => usage.OutputTokenCount, cancellationToken)
            .ConfigureAwait(false);
        var totalTokens = await _dbContext
            .AgentUsages.Where(usage => usage.UserId == ownerUserId)
            .SumAsync(usage => usage.TotalTokenCount, cancellationToken)
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

    private string ResolveOwnerUserId() => _userInfoService.RequiredUserId;
}
