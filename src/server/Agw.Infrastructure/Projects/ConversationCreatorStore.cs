using Agw.Auth.Contracts;
using Agw.Infrastructure.Data;
using Agw.Projects.Application.Persistence;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Projects;

public sealed class ConversationCreatorStore : IConversationCreatorStore
{
    private readonly AgwDbContext _dbContext;

    public ConversationCreatorStore(AgwDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task ClaimOpenConversationAsync(
        Guid conversationId,
        int expectedGeneration,
        Action<ConversationCreatorClaim> operation,
        CancellationToken cancellationToken = default
    )
    {
        var owner = UserInfoUtil.RequiredUserId;
        await using var transaction =
            _dbContext.Database.CurrentTransaction == null
                ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
                : null;
        var projectId = await _dbContext
            .ProjectConversations.AsNoTracking()
            .Where(conversation => conversation.Id == conversationId && conversation.CreateBy == owner)
            .Select(conversation => (Guid?)conversation.ProjectId)
            .SingleOrDefaultAsync(cancellationToken);
        if (!projectId.HasValue || !await _dbContext.LockOwnedProjectAsync(projectId.Value, owner, cancellationToken))
        {
            throw new AgwException(ErrorCodes.ConversationSessionConflict);
        }

        var locked = await _dbContext
            .ProjectConversations.Where(conversation =>
                conversation.Id == conversationId
                && conversation.ProjectId == projectId.Value
                && conversation.CreateBy == owner
                && conversation.Generation == expectedGeneration
            )
            .ExecuteUpdateAsync(
                setters =>
                    setters.SetProperty(
                        conversation => conversation.Generation,
                        conversation => conversation.Generation
                    ),
                cancellationToken
            );
        if (locked != 1)
        {
            throw new AgwException(ErrorCodes.ConversationSessionConflict);
        }

        // 锁内重新读取，与调用方跟踪的实体分开；之后只写入创建方，不保存调用方的其他修改。
        // Re-reads under the lock apart from the entity the caller tracks; only the creator is written afterwards, leaving the caller's other changes unsaved.
        var conversation = await _dbContext
            .ProjectConversations.AsNoTracking()
            .SingleAsync(item => item.Id == conversationId, cancellationToken);
        var hasAcceptedTurns = await _dbContext.ProjectConversationTurns.AnyAsync(
            turn => turn.ProjectConversationId == conversationId,
            cancellationToken
        );
        operation(new ConversationCreatorClaim(conversation, hasAcceptedTurns));
        await _dbContext
            .ProjectConversations.Where(item => item.Id == conversationId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(item => item.ExtensionId, conversation.ExtensionId),
                cancellationToken
            );
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
    }
}
