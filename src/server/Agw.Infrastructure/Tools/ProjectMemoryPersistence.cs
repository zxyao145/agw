using System.Runtime.CompilerServices;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Projects;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Agw.Tools.Application.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Tools;

public sealed class ProjectMemoryPersistence : IProjectMemoryPersistence
{
    private readonly AgwDbContext _dbContext;

    public ProjectMemoryPersistence(AgwDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task WriteAsync(
        Guid projectId,
        string ownerUserId,
        string path,
        string content,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default
    )
    {
        await using var transaction = await _dbContext
            .Database.BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!await _dbContext.LockOwnedProjectAsync(projectId, ownerUserId, cancellationToken).ConfigureAwait(false))
        {
            throw new AgwException(ErrorCodes.ResourceNotFound, "Project was not found.");
        }

        var existingPaths = await _dbContext
            .ProjectMemories.AsNoTracking()
            .Where(entry => entry.ProjectId == projectId)
            .Select(entry => entry.Path)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var conflictingPath = existingPaths.FirstOrDefault(existingPath =>
            !string.Equals(existingPath, path, StringComparison.Ordinal)
            && (
                existingPath.StartsWith(path + "/", StringComparison.Ordinal)
                || path.StartsWith(existingPath + "/", StringComparison.Ordinal)
            )
        );
        if (conflictingPath != null)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Project memory path '{path}' conflicts with existing path '{conflictingPath}'."
            );
        }

        var entry = await _dbContext
            .ProjectMemories.SingleOrDefaultAsync(
                item => item.ProjectId == projectId && item.Path == path,
                cancellationToken
            )
            .ConfigureAwait(false);
        if (entry == null)
        {
            entry = new ProjectMemoryEntry
            {
                Id = Guid.CreateVersion7(),
                ProjectId = projectId,
                Path = path,
            };
            _dbContext.ProjectMemories.Add(entry);
        }

        entry.Content = content;
        entry.UpdatedAt = updatedAt;
        await _dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task<string?> ReadAsync(
        Guid projectId,
        string ownerUserId,
        string path,
        CancellationToken cancellationToken = default
    ) =>
        OwnedProjectMemories(projectId, ownerUserId)
            .AsNoTracking()
            .Where(entry => entry.Path == path)
            .Select(entry => entry.Content)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> DeleteAsync(
        Guid projectId,
        string ownerUserId,
        string path,
        CancellationToken cancellationToken = default
    ) =>
        await OwnedProjectMemories(projectId, ownerUserId)
            .Where(entry => entry.Path == path)
            .ExecuteDeleteAsync(cancellationToken)
            .ConfigureAwait(false) > 0;

    public async Task<IReadOnlyList<string>> ListPathsAsync(
        Guid projectId,
        string ownerUserId,
        string prefix,
        CancellationToken cancellationToken = default
    ) =>
        await OwnedProjectMemories(projectId, ownerUserId)
            .AsNoTracking()
            .Where(entry => entry.Path.StartsWith(prefix))
            .Select(entry => entry.Path)
            .ToArrayAsync(cancellationToken)
            .ConfigureAwait(false);

    public Task<bool> FileExistsAsync(
        Guid projectId,
        string ownerUserId,
        string path,
        CancellationToken cancellationToken = default
    ) => OwnedProjectMemories(projectId, ownerUserId).AnyAsync(entry => entry.Path == path, cancellationToken);

    public async IAsyncEnumerable<ProjectMemoryContentEntry> ListEntriesAsync(
        Guid projectId,
        string ownerUserId,
        string prefix,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        await foreach (
            var entry in OwnedProjectMemories(projectId, ownerUserId)
                .AsNoTracking()
                .Where(entry => entry.Path.StartsWith(prefix))
                .Select(entry => new ProjectMemoryContentEntry(entry.Path, entry.Content))
                .AsAsyncEnumerable()
                .WithCancellation(cancellationToken)
                .ConfigureAwait(false)
        )
        {
            yield return entry;
        }
    }

    private IQueryable<ProjectMemoryEntry> OwnedProjectMemories(Guid projectId, string ownerUserId) =>
        _dbContext.ProjectMemories.Where(entry =>
            entry.ProjectId == projectId
            && _dbContext.Projects.Any(project => project.Id == projectId && project.CreateBy == ownerUserId)
        );
}
