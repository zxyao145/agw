using Agw.Auth.Contracts;
using Agw.Infrastructure.Projects;
using Agw.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Data;

public partial class AgwDbContext
{
    // The conditional root update holds a database row lock through the child writes.
    // Every reset-sensitive write uses its original generation, never a freshly read replacement.
    public async Task<int> SaveConversationChangesAsync(
        Guid conversationId,
        int expectedGeneration,
        CancellationToken cancellationToken = default
    )
    {
        await using var transaction =
            Database.CurrentTransaction == null ? await Database.BeginTransactionAsync(cancellationToken) : null;
        var owner = UserInfoUtil.RequiredUserId;
        var created = ProjectConversations.Local.FirstOrDefault(conversation =>
            conversation.Id == conversationId
            && conversation.CreateBy == owner
            && conversation.Generation == expectedGeneration
            && Entry(conversation).State == EntityState.Added
        );
        var projectId =
            created?.ProjectId
            ?? await ProjectConversations
                .AsNoTracking()
                .Where(conversation => conversation.Id == conversationId && conversation.CreateBy == owner)
                .Select(conversation => (Guid?)conversation.ProjectId)
                .SingleOrDefaultAsync(cancellationToken);
        if (!projectId.HasValue || !await this.LockOwnedProjectAsync(projectId.Value, owner, cancellationToken))
        {
            throw new AgwException(ErrorCodes.ConversationSessionConflict);
        }
        if (created == null)
        {
            var locked = await ProjectConversations
                .Where(conversation =>
                    conversation.Id == conversationId
                    && conversation.CreateBy == owner
                    && conversation.Project!.CreateBy == owner
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
        }

        var result = await SaveChangesAsync(cancellationToken);
        if (transaction != null)
        {
            await transaction.CommitAsync(cancellationToken);
        }
        return result;
    }
}
