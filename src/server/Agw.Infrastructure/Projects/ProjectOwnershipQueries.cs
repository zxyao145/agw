using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Projects;

internal static class ProjectOwnershipQueries
{
    public static IQueryable<ProjectConversation> OwnedProjectConversations(
        this AgwDbContext dbContext,
        Guid projectId,
        string ownerUserId
    ) =>
        dbContext
            .ProjectConversations.AsNoTracking()
            .Where(conversation =>
                conversation.ProjectId == projectId
                && conversation.CreateBy == ownerUserId
                && dbContext.Projects.Any(project =>
                    project.Id == conversation.ProjectId && project.CreateBy == ownerUserId
                )
            );

    public static async Task<bool> LockOwnedProjectAsync(
        this AgwDbContext dbContext,
        Guid projectId,
        string ownerUserId,
        CancellationToken cancellationToken
    ) =>
        await dbContext
            .Projects.Where(project => project.Id == projectId && project.CreateBy == ownerUserId)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(project => project.UpdateTime, project => project.UpdateTime),
                cancellationToken
            )
            .ConfigureAwait(false) > 0;
}
