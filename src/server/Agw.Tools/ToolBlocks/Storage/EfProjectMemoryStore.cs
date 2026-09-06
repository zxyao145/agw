using System.IO.Enumeration;
using System.Text.RegularExpressions;
using Agw.Auth.Contracts;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Exceptions;
using Agw.Tools.Application.Persistence;
using Microsoft.Agents.AI;
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
        await using var lifecycleLease = await _applicationLock
            .AcquireAsync(ProjectLifecycleLock.GetResourceName(_projectId), cancellationToken)
            .ConfigureAwait(false);
        await using var mutationLease = await AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IProjectMemoryPersistence>();
        await persistence
            .WriteAsync(
                _projectId,
                ResolveOwnerUserId(),
                normalizedPath,
                content,
                _timeProvider.GetUtcNow(),
                cancellationToken
            )
            .ConfigureAwait(false);
    }

    public override async Task<string?> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path);
        await using var mutationLease = await AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IProjectMemoryPersistence>();
        return await persistence
            .ReadAsync(_projectId, ResolveOwnerUserId(), normalizedPath, cancellationToken)
            .ConfigureAwait(false);
    }

    public override async Task<bool> DeleteAsync(string path, CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path);
        await using var lifecycleLease = await _applicationLock
            .AcquireAsync(ProjectLifecycleLock.GetResourceName(_projectId), cancellationToken)
            .ConfigureAwait(false);
        await using var mutationLease = await AcquireMutationLockAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IProjectMemoryPersistence>();
        return await persistence
            .DeleteAsync(_projectId, ResolveOwnerUserId(), normalizedPath, cancellationToken)
            .ConfigureAwait(false);
    }

    public override async Task<IReadOnlyList<FileStoreEntry>> ListChildrenAsync(
        string directory,
        CancellationToken cancellationToken = default
    )
    {
        var prefix = DirectoryPrefix(directory);
        await using var scope = _serviceScopeFactory.CreateAsyncScope();
        var persistence = scope.ServiceProvider.GetRequiredService<IProjectMemoryPersistence>();
        var paths = await persistence
            .ListPathsAsync(_projectId, ResolveOwnerUserId(), prefix, cancellationToken)
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
        var persistence = scope.ServiceProvider.GetRequiredService<IProjectMemoryPersistence>();
        return await persistence
            .FileExistsAsync(_projectId, ResolveOwnerUserId(), normalizedPath, cancellationToken)
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
        var persistence = scope.ServiceProvider.GetRequiredService<IProjectMemoryPersistence>();
        var entries = persistence.ListEntriesAsync(_projectId, ResolveOwnerUserId(), prefix, cancellationToken);

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

    private static string ResolveOwnerUserId()
    {
        if (!UserInfoUtil.IsContextActive)
        {
            throw new AgwException(ErrorCodes.AuthenticationRequired);
        }

        return UserInfoUtil.RequiredUserId;
    }

    private Task<IApplicationLockLease> AcquireMutationLockAsync(CancellationToken cancellationToken) =>
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
