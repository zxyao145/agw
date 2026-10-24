using Agw.Auth.Contracts;
using Agw.Projects.Application.Persistence;
using Agw.Projects.Contracts.Execution;
using Agw.Projects.Domain.Behaviors;
using Microsoft.EntityFrameworkCore;

namespace Agw.Projects.Application.Facades;

public sealed class ProjectConversationAccessFacade : IProjectConversationAccessFacade
{
    private readonly IProjectsDbContext _dbContext;

    public ProjectConversationAccessFacade(IProjectsDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task EnsureWritableAsync(
        Guid conversationId,
        string? requesterExtensionId,
        CancellationToken cancellationToken = default
    )
    {
        if (requesterExtensionId == null)
        {
            return;
        }

        var owner = UserInfoUtil.RequiredUserId;
        var conversation = await _dbContext
            .ProjectConversations.AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.Id == conversationId && item.CreateBy == owner && item.Project!.CreateBy == owner,
                cancellationToken
            );
        // 没有保存的对话（草稿）由解析任务时插入并记录创建方；其他用户的对话同样按草稿处理，之后的受理会拒绝它。
        // An unsaved (draft) conversation is inserted with its creator when the task resolves; another user's conversation is treated the same way and the acceptance that follows refuses it.
        if (conversation == null)
        {
            return;
        }

        var behavior = new ProjectConversationBehavior(conversation);
        if (behavior.IsCreatedBy(requesterExtensionId))
        {
            return;
        }

        var hasAcceptedTurns = await _dbContext.ProjectConversationTurns.AnyAsync(
            turn => turn.ProjectConversationId == conversationId,
            cancellationToken
        );
        behavior.EnsureWritableBy(requesterExtensionId, hasAcceptedTurns);
    }
}
