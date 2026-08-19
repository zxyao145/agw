using System.IO.Enumeration;
using System.Text.RegularExpressions;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Exceptions;
using Microsoft.Agents.AI;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Tools.ToolBlocks.Storage;

public sealed class EfProjectMemoryStore : AgentFileStore
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IApplicationLock _applicationLock;
    private readonly Guid _projectId;

    public EfProjectMemoryStore(IServiceScopeFactory serviceScopeFactory, TimeProvider timeProvider, Guid projectId)
        : this(serviceScopeFactory, timeProvider, InMemoryApplicationLock.Shared, projectId) { }

    public EfProjectMemoryStore(
        IServiceScopeFactory serviceScopeFactory,
        TimeProvider timeProvider,
        IApplicationLock applicationLock,
        Guid projectId
    )
    {
        _serviceScopeFactory = serviceScopeFactory;
        _timeProvider = timeProvider;
        _applicationLock = applicationLock;
        _projectId = projectId;
    }

    public override async Task WriteAsync(string path, string content, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path);
        await using var mutationLease = await AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();
        var existingPaths = await Query(dbContext)
            .AsNoTracking()
            .Select(item => item.Path)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        var conflictingPath = existingPaths.FirstOrDefault(existingPath =>
            !string.Equals(existingPath, normalizedPath, StringComparison.Ordinal)
            && (
                existingPath.StartsWith(normalizedPath + "/", StringComparison.Ordinal)
                || normalizedPath.StartsWith(existingPath + "/", StringComparison.Ordinal)
            )
        );
        if (conflictingPath != null)
        {
            throw new AgwException(
                ErrorCodes.InvalidParam,
                $"Project memory path '{normalizedPath}' conflicts with existing path '{conflictingPath}'."
            );
        }

        var entry = await Query(dbContext)
            .SingleOrDefaultAsync(item => item.Path == normalizedPath, cancellationToken)
            .ConfigureAwait(false);
        if (entry == null)
        {
            entry = new ProjectMemoryEntry
            {
                Id = Guid.CreateVersion7(),
                ProjectId = _projectId,
                Path = normalizedPath,
            };
            dbContext.Add(entry);
        }

        entry.Content = content;
        entry.UpdatedAt = _timeProvider.GetUtcNow();
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public override async Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path);
        await using var mutationLease = await AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();
        return await Query(dbContext)
            .AsNoTracking()
            .Where(item => item.Path == normalizedPath)
            .Select(item => item.Content)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public override async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path);
        await using var mutationLease = await AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();
        var entry = await Query(dbContext)
            .SingleOrDefaultAsync(item => item.Path == normalizedPath, cancellationToken)
            .ConfigureAwait(false);
        if (entry == null)
        {
            return false;
        }

        dbContext.Remove(entry);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public override async Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken = default
    )
    {
        var prefix = DirectoryPrefix(directory);
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();
        var paths = await Query(dbContext)
            .AsNoTracking()
            .Where(item => item.Path.StartsWith(prefix))
            .Select(item => item.Path)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return paths
            .Select(path => path[prefix.Length..])
            .Where(static path => path.Length > 0)
            .Select(static path =>
            {
                var separatorIndex = path.IndexOf('/');
                return separatorIndex < 0
                    ? new FileStoreEntry(path, FileStoreEntry.File)
                    : new FileStoreEntry(path[..separatorIndex], FileStoreEntry.Directory);
            })
            .DistinctBy(static entry => (entry.Name, entry.Type))
            .OrderByDescending(static entry => entry.Type == FileStoreEntry.Directory)
            .ThenBy(static entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
    }

    public override async Task<bool> FileExistsAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path);
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();
        return await Query(dbContext)
            .AsNoTracking()
            .AnyAsync(item => item.Path == normalizedPath, cancellationToken)
            .ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<FileSearchResult>> SearchAsync(
        string directory,
        string regexPattern,
        string? globPattern = null,
        bool recursive = false,
        CancellationToken cancellationToken = default
    )
    {
        var prefix = DirectoryPrefix(directory);
        var regex = new Regex(
            regexPattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2)
        );
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<DbContext>();
        var entries = Query(dbContext)
            .AsNoTracking()
            .Where(item => item.Path.StartsWith(prefix))
            .Select(item => new { item.Path, item.Content })
            .AsAsyncEnumerable();

        var results = new List<FileSearchResult>();
        await foreach (var entry in entries.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var relativePath = entry.Path[prefix.Length..];
            if (
                (!recursive && relativePath.Contains('/'))
                || (
                    !string.IsNullOrWhiteSpace(globPattern)
                    && !FileSystemName.MatchesSimpleExpression(
                        globPattern,
                        Path.GetFileName(relativePath),
                        ignoreCase: true
                    )
                )
            )
            {
                continue;
            }

            var matches = entry
                .Content.Split('\n')
                .Select((line, index) => new { Line = line.TrimEnd('\r'), Number = index + 1 })
                .Where(line => regex.IsMatch(line.Line))
                .Select(line => new FileSearchMatch { LineNumber = line.Number, Line = line.Line })
                .ToList();
            if (matches.Count > 0)
            {
                results.Add(
                    new FileSearchResult
                    {
                        FileName = relativePath,
                        Snippet = matches[0].Line,
                        MatchingLines = matches,
                    }
                );
            }
        }

        return results;
    }

    public override Task CreateDirectoryAsync(string path, CancellationToken cancellationToken = default)
    {
        _ = NormalizePath(path, allowEmpty: true);
        return Task.CompletedTask;
    }

    private IQueryable<ProjectMemoryEntry> Query(DbContext dbContext) =>
        dbContext.Set<ProjectMemoryEntry>().Where(item => item.ProjectId == _projectId);

    private Task<IAsyncDisposable> AcquireMutationLockAsync(CancellationToken cancellationToken) =>
        _applicationLock.AcquireAsync($"project-memory-store:{_projectId:D}", cancellationToken);

    private static string DirectoryPrefix(string directory)
    {
        var normalized = NormalizePath(directory, allowEmpty: true);
        return normalized.Length == 0 ? string.Empty : normalized + "/";
    }

    private static string NormalizePath(string path, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        var normalized = path.Replace('\\', '/').Trim('/');
        if (
            (!allowEmpty && normalized.Length == 0)
            || Path.IsPathRooted(path)
            || normalized.Split('/').Any(static part => part is "." or "..")
        )
        {
            throw new AgwException(ErrorCodes.InvalidParam, "Project memory paths must be non-rooted relative paths.");
        }

        return normalized;
    }
}
