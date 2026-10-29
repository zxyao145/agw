using System.Runtime.CompilerServices;
using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Shared.Exceptions;

namespace Agw.Files.Infrastructure.Storage;

internal sealed class AuthorizedLocalFileSystem : ILocalFileSystem
{
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private readonly LocalFileSystem _localFileSystem;
    private readonly IAgwFileSystem _fileSystem;
    private readonly IProjectWorkspaceAccess _workspaceAccess;
    private readonly Guid _projectId;

    public AuthorizedLocalFileSystem(
        LocalFileSystem localFileSystem,
        IAgwFileSystem fileSystem,
        IProjectWorkspaceAccess workspaceAccess,
        Guid projectId
    )
    {
        _localFileSystem = localFileSystem;
        _fileSystem = fileSystem;
        _workspaceAccess = workspaceAccess;
        _projectId = projectId;
    }

    public string ResolvePhysicalPath(string path) => _localFileSystem.ResolvePhysicalPath(path);

    public string GetRelativePath(string fullPath) => _localFileSystem.GetRelativePath(fullPath);

    public async Task<bool> IsSymbolicLinkAsync(string path, CancellationToken ct)
    {
        await ValidateEntryPathAsync(path, ct).ConfigureAwait(false);
        if (_fileSystem is not ILocalFileSystem localFileSystem)
        {
            throw new AgwException(
                ErrorCodes.FileOperationFailed,
                "Symbolic link checks require an unconfined local file system."
            );
        }

        return await localFileSystem.IsSymbolicLinkAsync(path, ct).ConfigureAwait(false);
    }

    public async Task<bool?> GetSymbolicLinkDirectoryAsync(string path, CancellationToken ct)
    {
        await ValidateEntryPathAsync(path, ct).ConfigureAwait(false);
        if (_fileSystem is not ILocalFileSystem localFileSystem)
        {
            throw new AgwException(
                ErrorCodes.FileOperationFailed,
                "Symbolic link metadata requires an unconfined local file system."
            );
        }

        var lexicalPath = _localFileSystem.ResolvePhysicalPath(path);
        var physicalPath = PhysicalPaths.ResolveContent(lexicalPath);
        if (!await _workspaceAccess.IsAccessibleAsync([lexicalPath, physicalPath], ct).ConfigureAwait(false))
        {
            return null;
        }

        return await localFileSystem.GetSymbolicLinkDirectoryAsync(path, ct).ConfigureAwait(false);
    }

    public IAgwFileSystem Confine(IReadOnlyList<string> allowedRoots) =>
        new AuthorizedLocalFileSystem(
            _localFileSystem,
            _localFileSystem.Confine(allowedRoots),
            _workspaceAccess,
            _projectId
        );

    public async Task<bool> ExistsFileAsync(string path, CancellationToken ct)
    {
        await ValidateContentPathAsync(path, ct).ConfigureAwait(false);
        return await _fileSystem.ExistsFileAsync(path, ct).ConfigureAwait(false);
    }

    public async Task<bool> ExistsDirectoryAsync(string path, CancellationToken ct)
    {
        await ValidateContentPathAsync(path, ct).ConfigureAwait(false);
        return await _fileSystem.ExistsDirectoryAsync(path, ct).ConfigureAwait(false);
    }

    public async Task<FileEntry?> StatAsync(string path, CancellationToken ct)
    {
        await ValidateContentPathAsync(path, ct).ConfigureAwait(false);
        return await _fileSystem.StatAsync(path, ct).ConfigureAwait(false);
    }

    public async Task<string> ReadAllTextAsync(string path, CancellationToken ct)
    {
        await ValidateContentPathAsync(path, ct).ConfigureAwait(false);
        return await _fileSystem.ReadAllTextAsync(path, ct).ConfigureAwait(false);
    }

    public async Task<string[]> ReadAllLinesAsync(string path, CancellationToken ct)
    {
        await ValidateContentPathAsync(path, ct).ConfigureAwait(false);
        return await _fileSystem.ReadAllLinesAsync(path, ct).ConfigureAwait(false);
    }

    public async Task WriteAllTextAsync(string path, string content, CancellationToken ct)
    {
        await ValidateContentPathAsync(path, ct).ConfigureAwait(false);
        await _fileSystem.WriteAllTextAsync(path, content, ct).ConfigureAwait(false);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        await ValidateContentPathAsync(path, ct).ConfigureAwait(false);
        await _fileSystem.CreateDirectoryAsync(path, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(string path, bool recursive, CancellationToken ct)
    {
        await ValidateEntryPathAsync(path, ct).ConfigureAwait(false);
        await _fileSystem.DeleteAsync(path, recursive, ct).ConfigureAwait(false);
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        await ValidateContentPathAsync(path, ct).ConfigureAwait(false);
        return await _fileSystem.OpenReadAsync(path, ct).ConfigureAwait(false);
    }

    public async Task WriteAsync(
        string path,
        Stream content,
        bool overwrite,
        bool createParents,
        string? expectedSha256,
        CancellationToken ct
    )
    {
        await ValidateContentPathAsync(path, ct).ConfigureAwait(false);
        await _fileSystem.WriteAsync(path, content, overwrite, createParents, expectedSha256, ct).ConfigureAwait(false);
    }

    public async Task MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken ct)
    {
        await ValidateEntryPathsAsync(sourcePath, destinationPath, ct).ConfigureAwait(false);
        await _fileSystem.MoveAsync(sourcePath, destinationPath, overwrite, ct).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<FileEntry> EnumerateAsync(
        string path,
        string searchPattern,
        bool recursive,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        await ValidateContentPathAsync(path, ct).ConfigureAwait(false);
        await foreach (
            var entry in _fileSystem.EnumerateAsync(path, searchPattern, recursive, ct).ConfigureAwait(false)
        )
        {
            await ValidateContentPathAsync(entry.Path, ct).ConfigureAwait(false);
            yield return entry;
        }
    }

    public async IAsyncEnumerable<AgwFileSearchResult> SearchAsync(
        string rootPath,
        SearchOptions options,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        await ValidateContentPathAsync(rootPath, ct).ConfigureAwait(false);
        await foreach (var result in _fileSystem.SearchAsync(rootPath, options, ct).ConfigureAwait(false))
        {
            yield return result;
        }
    }

    public async Task<bool> CreateTextFileAsync(string path, string content, CancellationToken ct)
    {
        await ValidateContentPathAsync(path, ct).ConfigureAwait(false);
        return await _fileSystem.CreateTextFileAsync(path, content, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<string>> ReadLinesAsync(
        string path,
        int startLine,
        int? endLine,
        CancellationToken ct
    )
    {
        await ValidateContentPathAsync(path, ct).ConfigureAwait(false);
        return await _fileSystem.ReadLinesAsync(path, startLine, endLine, ct).ConfigureAwait(false);
    }

    public async Task<int> ReplaceTextAsync(
        string path,
        string oldString,
        string newString,
        bool replaceAll,
        CancellationToken ct
    )
    {
        await ValidateContentPathAsync(path, ct).ConfigureAwait(false);
        return await _fileSystem.ReplaceTextAsync(path, oldString, newString, replaceAll, ct).ConfigureAwait(false);
    }

    public async Task ReplaceLinesAsync(string path, IReadOnlyList<AgwFileLineEdit> edits, CancellationToken ct)
    {
        await ValidateContentPathAsync(path, ct).ConfigureAwait(false);
        await _fileSystem.ReplaceLinesAsync(path, edits, ct).ConfigureAwait(false);
    }

    public IAgwFileSystem GetSubFileSystem(string path) =>
        new AuthorizedLocalFileSystem(
            (LocalFileSystem)_localFileSystem.GetSubFileSystem(path),
            _fileSystem.GetSubFileSystem(path),
            _workspaceAccess,
            _projectId
        );

    private Task ValidateContentPathAsync(string path, CancellationToken ct)
    {
        var lexicalPath = _localFileSystem.ResolvePhysicalPath(path);
        return ValidatePhysicalPathsAsync([lexicalPath, PhysicalPaths.ResolveContent(lexicalPath)], ct);
    }

    private Task ValidateEntryPathAsync(string path, CancellationToken ct) =>
        ValidatePhysicalPathsAsync(GetEntryValidationPaths(path), ct);

    private async Task ValidateEntryPathsAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        await ValidatePhysicalPathsAsync(
                [.. GetEntryValidationPaths(sourcePath), .. GetEntryValidationPaths(destinationPath)],
                ct
            )
            .ConfigureAwait(false);
    }

    private IReadOnlyList<string> GetEntryValidationPaths(string path)
    {
        var lexicalPath = _localFileSystem.ResolvePhysicalPath(path);
        var physicalEntryPath = PhysicalPaths.ResolveEntry(lexicalPath);
        if (string.Equals(lexicalPath, _localFileSystem.ResolvePhysicalPath(string.Empty), PathComparison))
        {
            return [lexicalPath, physicalEntryPath];
        }

        var lexicalParent = Path.GetDirectoryName(lexicalPath) ?? lexicalPath;
        var physicalParent = Path.GetDirectoryName(physicalEntryPath) ?? physicalEntryPath;
        return [lexicalParent, physicalParent];
    }

    private Task ValidatePhysicalPathsAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        return _workspaceAccess.ValidateAsync([.. paths, $"~/.agw/projects/{_projectId:N}"], cancellationToken: ct);
    }
}
