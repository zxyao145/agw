using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Shared.Exceptions;
using Agw.Shared.Utils;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Logging;

namespace Agw.Files.Application.Files;

public sealed class FileAppService
{
    /// <summary>
    /// 单次读取或写入的文件内容上限（100 MiB）。The limit of file content in one read or write (100 MiB).
    /// </summary>
    public const long MaxTransferBytes = 100L * 1024 * 1024;

    private const int Sha256HexLength = 64;

    /// <summary>判断二进制时检查的字符数，与 git 相同。The characters checked for binary content, as in git.</summary>
    private const int BinaryProbeLength = 8000;

    private const string GitRequiresLocalFileSystem =
        "Git operations are only supported for local project file systems";

    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    private static readonly HashSet<string> IgnoreDirectories = new() { "node_modules", "obj", "bin" };

    private static readonly HashSet<string> IgnoreFiles = new() { "tmpclaude*" };

    private readonly IAgwFileSystemResolver _fileSystemResolver;
    private readonly IGitCommandService _gitCommandService;
    private readonly ILogger<FileAppService> _logger;

    public FileAppService(
        IAgwFileSystemResolver fileSystemResolver,
        IGitCommandService gitCommandService,
        ILogger<FileAppService> logger
    )
    {
        _fileSystemResolver = fileSystemResolver;
        _gitCommandService = gitCommandService;
        _logger = logger;
    }

    public async Task<FileOperationResult<FileListOutput>> ListAsync(
        Guid projectId,
        string? path,
        bool diff,
        bool recursive,
        CancellationToken cancellationToken = default,
        Guid? directoryId = null
    )
    {
        if (projectId == Guid.Empty)
        {
            return FileOperationResult<FileListOutput>.Invalid("Project ID is required");
        }

        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken, directoryId);
        if (fileSystem == null)
        {
            return FileOperationResult<FileListOutput>.Missing(
                directoryId.HasValue ? "Project directory not found" : "Project not found"
            );
        }

        path ??= string.Empty;
        if (!await fileSystem.ExistsDirectoryAsync(path, cancellationToken))
        {
            return FileOperationResult<FileListOutput>.Missing("Directory not found");
        }

        if (diff && fileSystem is not ILocalFileSystem)
        {
            return FileOperationResult<FileListOutput>.Invalid(GitRequiresLocalFileSystem);
        }

        if (recursive && diff)
        {
            return await GetAllChangedFilesAsync((ILocalFileSystem)fileSystem, path, cancellationToken);
        }

        GitChangedFiles? changedFiles = null;
        ILocalFileSystem? local = null;
        if (diff)
        {
            local = (ILocalFileSystem)fileSystem;
            var physicalPath = local.ResolvePhysicalPath(path);
            changedFiles = await _gitCommandService.GetChangedFilesAsync(physicalPath, cancellationToken);
            if (changedFiles == null || changedFiles.FileStatuses.Count == 0)
            {
                return FileOperationResult<FileListOutput>.Succeeded(new FileListOutput([]));
            }
        }

        var items = new List<FileListEntry>();
        await foreach (var entry in fileSystem.EnumerateAsync(path, "*", recursive: false, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            GitFileStatus? gitStatus = null;
            if (changedFiles != null && local != null)
            {
                var physicalEntryPath = local.ResolvePhysicalPath(entry.Path);
                if (entry.IsDirectory)
                {
                    var descendantStatuses = changedFiles
                        .FileStatuses.Where(change => IsPathUnderDirectory(change.Key, physicalEntryPath))
                        .Select(change => change.Value)
                        .ToList();
                    if (descendantStatuses.Count == 0)
                    {
                        continue;
                    }

                    gitStatus = CombineGitStatuses(descendantStatuses);
                }
                else if (!changedFiles.FileStatuses.TryGetValue(physicalEntryPath, out gitStatus))
                {
                    continue;
                }
            }

            items.Add(ToListEntry(entry, gitStatus));
        }

        if (changedFiles != null && local != null)
        {
            var physicalDirectory = local.ResolvePhysicalPath(path);
            foreach (var deletedFile in changedFiles.DeletedFiles)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(Path.GetDirectoryName(deletedFile), physicalDirectory, PathComparison))
                {
                    continue;
                }

                var relativePath = local.GetRelativePath(deletedFile);
                var normalizedPath = NormalizePath(relativePath);
                if (items.Any(item => string.Equals(item.Path, normalizedPath, PathComparison)))
                {
                    continue;
                }

                var gitStatus = changedFiles.FileStatuses[deletedFile];
                items.Add(
                    new FileListEntry(
                        GetFileName(relativePath),
                        normalizedPath,
                        "file",
                        null,
                        null,
                        gitStatus.AggregateStatus,
                        gitStatus.StagedStatus,
                        gitStatus.UnstagedStatus
                    )
                );
            }
        }

        var sortedItems = items
            .OrderBy(item => item.Type == "file")
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return FileOperationResult<FileListOutput>.Succeeded(new FileListOutput(sortedItems));
    }

    public async Task<FileOperationResult<string>> ReadAsync(
        Guid projectId,
        string? path,
        CancellationToken cancellationToken = default,
        Guid? directoryId = null
    )
    {
        if (projectId == Guid.Empty)
        {
            return FileOperationResult<string>.Invalid("Project ID is required");
        }

        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken, directoryId);
        if (fileSystem == null)
        {
            return FileOperationResult<string>.Missing(
                directoryId.HasValue ? "Project directory not found" : "Project not found"
            );
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return FileOperationResult<string>.Invalid("Path parameter is required");
        }

        if (!await fileSystem.ExistsFileAsync(path, cancellationToken))
        {
            return FileOperationResult<string>.Missing("File not found");
        }

        var content = await fileSystem.ReadAllTextAsync(path, cancellationToken);
        return FileOperationResult<string>.Succeeded(content);
    }

    public async Task<FileOperationResult<FileDiffOutput>> DiffAsync(
        Guid projectId,
        string? path,
        string? scope,
        CancellationToken cancellationToken = default,
        Guid? directoryId = null
    )
    {
        if (projectId == Guid.Empty)
        {
            return FileOperationResult<FileDiffOutput>.Invalid("Project ID is required");
        }

        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken, directoryId);
        if (fileSystem == null)
        {
            return FileOperationResult<FileDiffOutput>.Missing(
                directoryId.HasValue ? "Project directory not found" : "Project not found"
            );
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return FileOperationResult<FileDiffOutput>.Invalid("Path parameter is required");
        }

        if (fileSystem is not ILocalFileSystem localFileSystem)
        {
            return FileOperationResult<FileDiffOutput>.Invalid(GitRequiresLocalFileSystem);
        }

        if (!TryParseDiffScope(scope, out var diffScope))
        {
            return FileOperationResult<FileDiffOutput>.Invalid("Scope must be 'staged' or 'unstaged'");
        }

        var physicalPath = localFileSystem.ResolvePhysicalPath(path);
        if (!await fileSystem.ExistsFileAsync(path, cancellationToken))
        {
            var changedFiles = await _gitCommandService.GetChangedFilesAsync(physicalPath, cancellationToken);
            if (
                changedFiles == null
                || !changedFiles.FileStatuses.TryGetValue(physicalPath, out var gitStatus)
                || gitStatus.GetStatus(diffScope) == null
            )
            {
                return FileOperationResult<FileDiffOutput>.Missing("File not found");
            }
        }

        var diff = await _gitCommandService.GetDiffAsync(physicalPath, cancellationToken, diffScope);
        if (!diff.Success)
        {
            return DiffFailed(diff.Error);
        }

        // 原始侧：unstaged 为暂存区，其他范围为 HEAD。The original side: the index for unstaged, HEAD otherwise.
        var original = await _gitCommandService.ReadBlobAsync(
            physicalPath,
            diffScope == GitDiffScope.Unstaged ? GitBlobSource.Index : GitBlobSource.Head,
            cancellationToken
        );
        if (!original.Success)
        {
            return DiffFailed(original.Error);
        }

        var modified = await ReadModifiedSideAsync(fileSystem, path, physicalPath, diffScope, cancellationToken);
        if (!modified.Success)
        {
            return DiffFailed(modified.Error);
        }

        var isBinary = IsBinaryText(original.Content) || IsBinaryText(modified.Content);
        return FileOperationResult<FileDiffOutput>.Succeeded(
            new FileDiffOutput
            {
                Diff = diff.Diff,
                Unchanged = string.IsNullOrWhiteSpace(diff.Diff),
                IsBinary = isBinary,
                OriginalContent = isBinary ? null : original.Content,
                ModifiedContent = isBinary ? null : modified.Content,
            }
        );
    }

    /// <summary>
    /// 修改侧：staged 为暂存区，其他范围为工作区（与 /api/files/read 相同的路径限制）。
    /// The modified side: the index for staged, the working tree otherwise (with the same path confinement as
    /// /api/files/read).
    /// </summary>
    private async Task<GitBlobResult> ReadModifiedSideAsync(
        IAgwFileSystem fileSystem,
        string path,
        string physicalPath,
        GitDiffScope diffScope,
        CancellationToken cancellationToken
    )
    {
        if (diffScope == GitDiffScope.Staged)
        {
            return await _gitCommandService.ReadBlobAsync(physicalPath, GitBlobSource.Index, cancellationToken);
        }

        var content = await fileSystem.ExistsFileAsync(path, cancellationToken)
            ? await fileSystem.ReadAllTextAsync(path, cancellationToken)
            : null;
        return new GitBlobResult(true, content, null);
    }

    /// <summary>
    /// 前 <see cref="BinaryProbeLength"/> 个字符中有 NUL 时按二进制处理，与 git 的判断相同。
    /// Text with a NUL in its first <see cref="BinaryProbeLength"/> characters is binary, as git decides.
    /// </summary>
    private static bool IsBinaryText(string? content) =>
        content != null && content.AsSpan(0, Math.Min(content.Length, BinaryProbeLength)).Contains('\0');

    private FileOperationResult<FileDiffOutput> DiffFailed(string? error)
    {
        _logger.LogWarning("Git diff failed: {Error}", error);
        return FileOperationResult<FileDiffOutput>.Invalid("Git diff failed", error);
    }

    public async Task<FileOperationResult<FileMutationOutput>> DeleteAsync(
        Guid projectId,
        string? path,
        bool recursive,
        CancellationToken cancellationToken = default,
        Guid? directoryId = null
    )
    {
        if (projectId == Guid.Empty)
        {
            return FileOperationResult<FileMutationOutput>.Invalid("Project ID is required");
        }

        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken, directoryId);
        if (fileSystem == null)
        {
            return FileOperationResult<FileMutationOutput>.Missing(
                directoryId.HasValue ? "Project directory not found" : "Project not found"
            );
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return FileOperationResult<FileMutationOutput>.Invalid("Path parameter is required");
        }

        if (TargetsWorkspaceRoot(path))
        {
            return FileOperationResult<FileMutationOutput>.Invalid("Project directory root cannot be deleted");
        }

        var isFile = await fileSystem.ExistsFileAsync(path, cancellationToken);
        var isDirectory = !isFile && await fileSystem.ExistsDirectoryAsync(path, cancellationToken);
        if (!isFile && !isDirectory)
        {
            return FileOperationResult<FileMutationOutput>.Missing("File or directory not found");
        }

        await fileSystem.DeleteAsync(path, recursive, cancellationToken);
        _logger.LogInformation(
            "Deleted {EntryType} in project {ProjectId}: {Path}",
            isDirectory ? "directory" : "file",
            projectId,
            path
        );

        return FileOperationResult<FileMutationOutput>.Succeeded(
            new FileMutationOutput(true, isDirectory ? "Directory deleted successfully" : "File deleted successfully")
        );
    }

    public async Task<FileOperationResult<FileListEntry>> StatAsync(
        Guid projectId,
        string? path,
        CancellationToken cancellationToken = default,
        Guid? directoryId = null
    )
    {
        if (projectId == Guid.Empty)
        {
            return FileOperationResult<FileListEntry>.Invalid("Project ID is required");
        }

        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken, directoryId);
        if (fileSystem == null)
        {
            return FileOperationResult<FileListEntry>.Missing(
                directoryId.HasValue ? "Project directory not found" : "Project not found"
            );
        }

        var entry = await fileSystem.StatAsync(path ?? string.Empty, cancellationToken);
        return entry == null
            ? FileOperationResult<FileListEntry>.Missing("File or directory not found")
            : FileOperationResult<FileListEntry>.Succeeded(ToListEntry(entry, null));
    }

    /// <summary>
    /// 打开文件用于下载，返回的流由调用方释放；文件超过 <see cref="MaxTransferBytes"/> 时抛出 <c>FileTooLarge</c>。
    /// Opens a file for download; the caller disposes the returned stream. Throws <c>FileTooLarge</c> when the file
    /// exceeds <see cref="MaxTransferBytes"/>.
    /// </summary>
    public async Task<FileOperationResult<Stream>> OpenReadAsync(
        Guid projectId,
        string? path,
        CancellationToken cancellationToken = default,
        Guid? directoryId = null
    )
    {
        if (projectId == Guid.Empty)
        {
            return FileOperationResult<Stream>.Invalid("Project ID is required");
        }

        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken, directoryId);
        if (fileSystem == null)
        {
            return FileOperationResult<Stream>.Missing(
                directoryId.HasValue ? "Project directory not found" : "Project not found"
            );
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return FileOperationResult<Stream>.Invalid("Path parameter is required");
        }

        return await OpenReadCoreAsync(fileSystem, path, cancellationToken);
    }

    /// <summary>
    /// Opens an image under the selected project directory; the caller disposes the returned stream.
    /// 打开选定项目目录中的图像；返回的流由调用方释放。
    /// </summary>
    public async Task<FileOperationResult<(Stream Stream, string ContentType)>> OpenImageAsync(
        Guid projectId,
        string? path,
        CancellationToken cancellationToken = default,
        Guid? directoryId = null
    )
    {
        if (projectId == Guid.Empty)
        {
            return FileOperationResult<(Stream Stream, string ContentType)>.Invalid("Project ID is required");
        }

        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken, directoryId);
        if (fileSystem == null)
        {
            return FileOperationResult<(Stream Stream, string ContentType)>.Missing(
                directoryId.HasValue ? "Project directory not found" : "Project not found"
            );
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return FileOperationResult<(Stream Stream, string ContentType)>.Invalid("Path parameter is required");
        }

        if (
            !ContentTypeProvider.TryGetContentType(path, out var contentType)
            || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        )
        {
            return FileOperationResult<(Stream Stream, string ContentType)>.Invalid("Path must refer to an image file");
        }

        if (fileSystem is not ILocalFileSystem localFileSystem)
        {
            return FileOperationResult<(Stream Stream, string ContentType)>.Invalid(
                "Image files require a local project file system"
            );
        }

        var relativePath = ResolveImagePath(localFileSystem, path);
        var root = localFileSystem.ResolvePhysicalPath(string.Empty);
        var confinedFileSystem = localFileSystem.Confine([root]);
        var result = await OpenReadCoreAsync(confinedFileSystem, relativePath, cancellationToken);
        return result.Status == FileOperationStatus.Success
            ? FileOperationResult<(Stream Stream, string ContentType)>.Succeeded((result.Value!, contentType))
            : new FileOperationResult<(Stream Stream, string ContentType)>(
                result.Status,
                default,
                result.Message,
                result.Details
            );
    }

    private static async Task<FileOperationResult<Stream>> OpenReadCoreAsync(
        IAgwFileSystem fileSystem,
        string path,
        CancellationToken cancellationToken
    )
    {
        if (!await fileSystem.ExistsFileAsync(path, cancellationToken))
        {
            return FileOperationResult<Stream>.Missing("File not found");
        }

        // 长度取自已打开的流，与随后发送的内容属于同一个文件。
        // The length comes from the opened stream, the same file whose content is sent afterwards.
        var stream = await fileSystem.OpenReadAsync(path, cancellationToken);
        if (stream.Length > MaxTransferBytes)
        {
            await stream.DisposeAsync();
            throw new AgwException(ErrorCodes.FileTooLarge, $"File '{path}' exceeds the 100 MiB transfer limit.");
        }

        return FileOperationResult<Stream>.Succeeded(stream);
    }

    private static string ResolveImagePath(ILocalFileSystem fileSystem, string path)
    {
        var expandedPath = PathUtil.ExpandTilde(path);
        if (!Path.IsPathRooted(expandedPath))
        {
            return path;
        }

        var fullPath = Path.GetFullPath(expandedPath);
        var rootPath = Path.GetFullPath(fileSystem.ResolvePhysicalPath(string.Empty));
        var relativePath = Path.GetRelativePath(rootPath, fullPath);
        if (
            Path.IsPathRooted(relativePath)
            || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
            || relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison)
        )
        {
            throw new AgwException(ErrorCodes.FilePathOutsideRoot, $"Path '{path}' is outside the project directory.");
        }

        return fileSystem.GetRelativePath(fullPath);
    }

    public async Task<FileOperationResult<FileListEntry>> WriteAsync(
        Guid projectId,
        string? path,
        Stream content,
        bool overwrite,
        bool createParents,
        string? expectedSha256,
        CancellationToken cancellationToken = default,
        Guid? directoryId = null
    )
    {
        if (projectId == Guid.Empty)
        {
            return FileOperationResult<FileListEntry>.Invalid("Project ID is required");
        }

        expectedSha256 = NormalizeExpectedSha256(expectedSha256, overwrite);
        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken, directoryId);
        if (fileSystem == null)
        {
            return FileOperationResult<FileListEntry>.Missing(
                directoryId.HasValue ? "Project directory not found" : "Project not found"
            );
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return FileOperationResult<FileListEntry>.Invalid("Path parameter is required");
        }

        if (TargetsWorkspaceRoot(path))
        {
            return FileOperationResult<FileListEntry>.Invalid("Project directory root cannot be written as a file");
        }

        if (content.Length > MaxTransferBytes)
        {
            throw new AgwException(
                ErrorCodes.FileTooLarge,
                $"Content for '{path}' exceeds the 100 MiB transfer limit."
            );
        }

        if (!createParents && !await fileSystem.ExistsDirectoryAsync(GetParentPath(path), cancellationToken))
        {
            return FileOperationResult<FileListEntry>.Missing("Parent directory not found");
        }

        await fileSystem.WriteAsync(path, content, overwrite, createParents, expectedSha256, cancellationToken);
        _logger.LogInformation("Wrote file in project {ProjectId}: {Path}", projectId, path);
        return await StatEntryAsync(fileSystem, path, cancellationToken);
    }

    /// <summary>
    /// 校验写入前提：必须是 64 位十六进制并与 overwrite 一起使用，返回小写形式。
    /// Validates the write precondition: it must be 64 hex digits used together with overwrite; returns it lowercased.
    /// </summary>
    private static string? NormalizeExpectedSha256(string? expectedSha256, bool overwrite)
    {
        if (expectedSha256 == null)
        {
            return null;
        }

        if (expectedSha256.Length != Sha256HexLength || !expectedSha256.All(char.IsAsciiHexDigit))
        {
            throw new AgwException(ErrorCodes.InvalidParam, "expectedSha256 must be 64 hexadecimal characters.");
        }

        if (!overwrite)
        {
            throw new AgwException(ErrorCodes.InvalidParam, "expectedSha256 requires overwrite=true.");
        }

        return expectedSha256.ToLowerInvariant();
    }

    public async Task<FileOperationResult<FileListEntry>> CreateDirectoryAsync(
        Guid projectId,
        string? path,
        bool recursive,
        CancellationToken cancellationToken = default,
        Guid? directoryId = null
    )
    {
        if (projectId == Guid.Empty)
        {
            return FileOperationResult<FileListEntry>.Invalid("Project ID is required");
        }

        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken, directoryId);
        if (fileSystem == null)
        {
            return FileOperationResult<FileListEntry>.Missing(
                directoryId.HasValue ? "Project directory not found" : "Project not found"
            );
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return FileOperationResult<FileListEntry>.Invalid("Path parameter is required");
        }

        // 与 mkdir 相同：已有同名文件总是冲突；已有目录只在非递归时冲突。
        // Like mkdir: an existing file of the same name always conflicts, an existing directory only when not recursive.
        if (await fileSystem.ExistsFileAsync(path, cancellationToken))
        {
            throw new AgwException(ErrorCodes.FileAlreadyExists, $"A file named '{path}' already exists.");
        }

        if (await fileSystem.ExistsDirectoryAsync(path, cancellationToken))
        {
            return recursive
                ? await StatEntryAsync(fileSystem, path, cancellationToken)
                : throw new AgwException(ErrorCodes.FileAlreadyExists, $"Directory '{path}' already exists.");
        }

        if (!recursive && !await fileSystem.ExistsDirectoryAsync(GetParentPath(path), cancellationToken))
        {
            return FileOperationResult<FileListEntry>.Missing("Parent directory not found");
        }

        await fileSystem.CreateDirectoryAsync(path, cancellationToken);
        _logger.LogInformation("Created directory in project {ProjectId}: {Path}", projectId, path);
        return await StatEntryAsync(fileSystem, path, cancellationToken);
    }

    public async Task<FileOperationResult<FileListEntry>> MoveAsync(
        Guid projectId,
        string? sourcePath,
        string? destinationPath,
        bool overwrite,
        CancellationToken cancellationToken = default,
        Guid? directoryId = null
    )
    {
        if (projectId == Guid.Empty)
        {
            return FileOperationResult<FileListEntry>.Invalid("Project ID is required");
        }

        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken, directoryId);
        if (fileSystem == null)
        {
            return FileOperationResult<FileListEntry>.Missing(
                directoryId.HasValue ? "Project directory not found" : "Project not found"
            );
        }

        var invalid = ValidateMovePaths(sourcePath, destinationPath);
        if (invalid != null)
        {
            return FileOperationResult<FileListEntry>.Invalid(invalid);
        }

        if (
            !await fileSystem.ExistsFileAsync(sourcePath!, cancellationToken)
            && !await fileSystem.ExistsDirectoryAsync(sourcePath!, cancellationToken)
        )
        {
            return FileOperationResult<FileListEntry>.Missing("File or directory not found");
        }

        if (!await fileSystem.ExistsDirectoryAsync(GetParentPath(destinationPath!), cancellationToken))
        {
            return FileOperationResult<FileListEntry>.Missing("Destination directory not found");
        }

        await fileSystem.MoveAsync(sourcePath!, destinationPath!, overwrite, cancellationToken);
        _logger.LogInformation(
            "Moved {SourcePath} to {DestinationPath} in project {ProjectId}",
            sourcePath,
            destinationPath,
            projectId
        );
        return await StatEntryAsync(fileSystem, destinationPath!, cancellationToken);
    }

    public async Task<FileOperationResult<FileMutationOutput>> ResetAsync(
        Guid projectId,
        string? path,
        CancellationToken cancellationToken = default,
        Guid? directoryId = null
    )
    {
        if (projectId == Guid.Empty)
        {
            return FileOperationResult<FileMutationOutput>.Invalid("Project ID is required");
        }

        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken, directoryId);
        if (fileSystem == null)
        {
            return FileOperationResult<FileMutationOutput>.Missing(
                directoryId.HasValue ? "Project directory not found" : "Project not found"
            );
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return FileOperationResult<FileMutationOutput>.Invalid("Path parameter is required");
        }

        if (fileSystem is not ILocalFileSystem localFileSystem)
        {
            return FileOperationResult<FileMutationOutput>.Invalid(GitRequiresLocalFileSystem);
        }

        if (!await fileSystem.ExistsFileAsync(path, cancellationToken))
        {
            return FileOperationResult<FileMutationOutput>.Missing("File not found");
        }

        var physicalPath = localFileSystem.ResolvePhysicalPath(path);
        var result = await _gitCommandService.ResetFileAsync(physicalPath, cancellationToken);
        if (!result.Success && result.IsClientError)
        {
            return FileOperationResult<FileMutationOutput>.Invalid(result.Message);
        }

        if (!result.Success && !string.IsNullOrEmpty(result.Error))
        {
            _logger.LogError("Git reset failed: {Error}", result.Error);
            return FileOperationResult<FileMutationOutput>.Failed("Git reset failed", result.Error);
        }

        if (!result.Success)
        {
            return FileOperationResult<FileMutationOutput>.Succeeded(new FileMutationOutput(false, result.Message));
        }

        _logger.LogInformation("Reset file to HEAD in project {ProjectId}: {Path}", projectId, path);
        return FileOperationResult<FileMutationOutput>.Succeeded(new FileMutationOutput(true, result.Message));
    }

    public Task<FileOperationResult<FileMutationOutput>> StageAsync(
        Guid projectId,
        string? path,
        CancellationToken cancellationToken = default,
        Guid? directoryId = null
    )
    {
        return SetStagedAsync(projectId, path, staged: true, cancellationToken, directoryId);
    }

    public Task<FileOperationResult<FileMutationOutput>> UnstageAsync(
        Guid projectId,
        string? path,
        CancellationToken cancellationToken = default,
        Guid? directoryId = null
    )
    {
        return SetStagedAsync(projectId, path, staged: false, cancellationToken, directoryId);
    }

    public async Task<FileOperationResult<FileSearchOutput>> SearchAsync(
        Guid projectId,
        string? path,
        string? keyword,
        int limit,
        bool recursive,
        CancellationToken cancellationToken = default,
        Guid? directoryId = null
    )
    {
        if (projectId == Guid.Empty)
        {
            return FileOperationResult<FileSearchOutput>.Invalid("Project ID is required");
        }

        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken, directoryId);
        if (fileSystem == null)
        {
            return FileOperationResult<FileSearchOutput>.Missing(
                directoryId.HasValue ? "Project directory not found" : "Project not found"
            );
        }

        path ??= string.Empty;
        if (!await fileSystem.ExistsDirectoryAsync(path, cancellationToken))
        {
            return FileOperationResult<FileSearchOutput>.Missing("Directory not found");
        }

        keyword = NormalizeSearchKeyword(keyword ?? string.Empty);
        if (limit <= 0 || IsIgnoredDirectoryPath(path))
        {
            return FileOperationResult<FileSearchOutput>.Succeeded(new FileSearchOutput([]));
        }

        var results = new List<FileSearchEntry>();
        var keywordSegments = keyword.Split('/', StringSplitOptions.RemoveEmptyEntries);
        await SearchDirectoryAsync(
            fileSystem,
            path,
            path,
            keyword,
            keywordSegments,
            limit,
            recursive,
            results,
            cancellationToken
        );

        var sortedResults = results
            .OrderBy(result => result.Type == "file")
            .ThenBy(result => result.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(result =>
                fileSystem is ILocalFileSystem local
                    ? result with
                    {
                        FullPath = NormalizePath(local.ResolvePhysicalPath(result.FullPath)),
                    }
                    : result
            )
            .ToList();

        return FileOperationResult<FileSearchOutput>.Succeeded(new FileSearchOutput(sortedResults));
    }

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private async Task<IAgwFileSystem?> ResolveFileSystemAsync(
        Guid projectId,
        CancellationToken cancellationToken,
        Guid? directoryId
    )
    {
        return projectId == Guid.Empty
            ? null
            : await _fileSystemResolver.ResolveAsync(projectId, directoryId, cancellationToken);
    }

    private async Task<FileOperationResult<FileMutationOutput>> SetStagedAsync(
        Guid projectId,
        string? path,
        bool staged,
        CancellationToken cancellationToken,
        Guid? directoryId
    )
    {
        if (projectId == Guid.Empty)
        {
            return FileOperationResult<FileMutationOutput>.Invalid("Project ID is required");
        }

        var fileSystem = await ResolveFileSystemAsync(projectId, cancellationToken, directoryId);
        if (fileSystem == null)
        {
            return FileOperationResult<FileMutationOutput>.Missing(
                directoryId.HasValue ? "Project directory not found" : "Project not found"
            );
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return FileOperationResult<FileMutationOutput>.Invalid("Path parameter is required");
        }

        if (fileSystem is not ILocalFileSystem localFileSystem)
        {
            return FileOperationResult<FileMutationOutput>.Invalid(GitRequiresLocalFileSystem);
        }

        if (TargetsWorkspaceRoot(path))
        {
            return FileOperationResult<FileMutationOutput>.Invalid("Workspace root cannot be staged or unstaged");
        }

        var physicalPath = localFileSystem.ResolvePhysicalPath(path);
        var result = await _gitCommandService.SetStagedAsync(physicalPath, staged, cancellationToken);
        if (!result.Success && result.IsClientError)
        {
            return FileOperationResult<FileMutationOutput>.Invalid(result.Message, result.Error);
        }

        if (!result.Success)
        {
            _logger.LogError("Git {Operation} failed: {Error}", staged ? "stage" : "unstage", result.Error);
            return FileOperationResult<FileMutationOutput>.Failed(result.Message, result.Error);
        }

        _logger.LogInformation(
            "{Operation} changes in project {ProjectId}: {Path}",
            staged ? "Staged" : "Unstaged",
            projectId,
            path
        );
        return FileOperationResult<FileMutationOutput>.Succeeded(new FileMutationOutput(true, result.Message));
    }

    private async Task<FileOperationResult<FileListOutput>> GetAllChangedFilesAsync(
        ILocalFileSystem fileSystem,
        string directoryPath,
        CancellationToken cancellationToken
    )
    {
        var physicalDirectory = fileSystem.ResolvePhysicalPath(directoryPath);
        var changedFiles = await _gitCommandService.GetChangedFilesAsync(physicalDirectory, cancellationToken);
        if (changedFiles == null || changedFiles.FileStatuses.Count == 0)
        {
            return FileOperationResult<FileListOutput>.Succeeded(new FileListOutput([]));
        }

        var items = new List<FileListEntry>();
        foreach (var (physicalPath, status) in changedFiles.FileStatuses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (
                !IsPathUnderDirectory(physicalPath, physicalDirectory)
                || string.Equals(physicalPath, physicalDirectory, PathComparison)
            )
            {
                continue;
            }

            var relativePath = fileSystem.GetRelativePath(physicalPath);
            var entry = await fileSystem.StatAsync(relativePath, cancellationToken);
            items.Add(
                entry == null
                    ? new FileListEntry(
                        GetFileName(relativePath),
                        NormalizePath(relativePath),
                        "file",
                        null,
                        null,
                        status.AggregateStatus,
                        status.StagedStatus,
                        status.UnstagedStatus
                    )
                    : ToListEntry(entry, status)
            );
        }

        var sortedItems = items.OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList();
        return FileOperationResult<FileListOutput>.Succeeded(new FileListOutput(sortedItems));
    }

    private static async Task<FileOperationResult<FileListEntry>> StatEntryAsync(
        IAgwFileSystem fileSystem,
        string path,
        CancellationToken cancellationToken
    )
    {
        var entry = await fileSystem.StatAsync(path, cancellationToken);
        return entry == null
            ? FileOperationResult<FileListEntry>.Missing("File or directory not found")
            : FileOperationResult<FileListEntry>.Succeeded(ToListEntry(entry, null));
    }

    /// <summary>
    /// 检查移动的源与目标：都必须给出且都不是根目录，两者不能相同，目录不能移动到自身之内。越过根目录的路径交给文件系统拒绝。
    /// Checks the source and destination of a move: both are required and neither may be the root, they must differ,
    /// and a directory cannot move into itself. Paths climbing above the root are left to the file system to reject.
    /// </summary>
    private static string? ValidateMovePaths(string? sourcePath, string? destinationPath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(destinationPath))
        {
            return "Source path and destination path are required";
        }

        if (TargetsWorkspaceRoot(sourcePath) || TargetsWorkspaceRoot(destinationPath))
        {
            return "Project directory root cannot be moved or replaced";
        }

        var source = NormalizeRelativePath(sourcePath);
        var destination = NormalizeRelativePath(destinationPath);
        if (source == null || destination == null)
        {
            return null;
        }

        if (string.Equals(source, destination, PathComparison))
        {
            return "Source and destination must be different";
        }

        return destination.StartsWith(source + "/", PathComparison) ? "A directory cannot be moved into itself" : null;
    }

    /// <summary>
    /// 在文本层面折叠 "." 与 ".."，返回以 "/" 分隔的相对路径；越过根目录时返回 null。
    /// Collapses "." and ".." textually and returns the "/"-separated relative path, or null when it climbs above the root.
    /// </summary>
    private static string? NormalizeRelativePath(string path)
    {
        var segments = new List<string>();
        foreach (var segment in NormalizePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    return null;
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    private static string GetParentPath(string path)
    {
        var normalized = NormalizePath(path).TrimEnd('/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? string.Empty : normalized[..separator];
    }

    private static FileListEntry ToListEntry(FileEntry entry, GitFileStatus? gitStatus)
    {
        var path = NormalizePath(entry.Path);
        return new FileListEntry(
            GetFileName(path),
            path,
            entry.IsDirectory ? "directory" : "file",
            entry.IsDirectory ? null : entry.Size,
            entry.LastModifiedUtc,
            gitStatus?.AggregateStatus,
            gitStatus?.StagedStatus,
            gitStatus?.UnstagedStatus
        );
    }

    private static GitFileStatus CombineGitStatuses(IEnumerable<GitFileStatus> statuses)
    {
        var values = statuses.ToList();
        return new GitFileStatus(
            GetAggregatedStatus(values.Select(status => status.StagedStatus)),
            GetAggregatedStatus(values.Select(status => status.UnstagedStatus))
        );
    }

    private static string? GetAggregatedStatus(IEnumerable<string?> statuses)
    {
        var values = statuses.Where(status => status != null).ToHashSet();
        if (values.Contains("modified"))
            return "modified";
        if (values.Contains("added"))
            return "added";
        if (values.Contains("untracked"))
            return "untracked";
        if (values.Contains("deleted"))
            return "deleted";
        return null;
    }

    private static bool TryParseDiffScope(string? value, out GitDiffScope scope)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            scope = GitDiffScope.All;
            return true;
        }

        if (value.Equals("staged", StringComparison.OrdinalIgnoreCase))
        {
            scope = GitDiffScope.Staged;
            return true;
        }

        if (value.Equals("unstaged", StringComparison.OrdinalIgnoreCase))
        {
            scope = GitDiffScope.Unstaged;
            return true;
        }

        scope = GitDiffScope.All;
        return false;
    }

    private static bool IsPathUnderDirectory(string candidatePath, string directoryPath)
    {
        if (string.Equals(candidatePath, directoryPath, PathComparison))
        {
            return true;
        }

        var relativePath = Path.GetRelativePath(directoryPath, candidatePath);
        return !Path.IsPathRooted(relativePath)
            && !string.Equals(relativePath, "..", PathComparison)
            && !relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", PathComparison)
            && !relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", PathComparison);
    }

    private static string GetPathRelativeTo(string entryPath, string rootPath)
    {
        var normalizedEntry = NormalizePath(entryPath).Trim('/');
        var normalizedRoot = NormalizePath(rootPath).Trim('/');
        if (string.IsNullOrEmpty(normalizedRoot))
        {
            return normalizedEntry;
        }

        if (string.Equals(normalizedEntry, normalizedRoot, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        var prefix = $"{normalizedRoot}/";
        return normalizedEntry.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? normalizedEntry[prefix.Length..]
            : normalizedEntry;
    }

    private static async Task SearchDirectoryAsync(
        IAgwFileSystem fileSystem,
        string rootPath,
        string currentPath,
        string keyword,
        string[] keywordSegments,
        int limit,
        bool recursive,
        List<FileSearchEntry> results,
        CancellationToken cancellationToken
    )
    {
        if (results.Count >= limit)
        {
            return;
        }

        var entries = new List<FileEntry>();
        await foreach (var entry in fileSystem.EnumerateAsync(currentPath, "*", recursive: false, cancellationToken))
        {
            entries.Add(entry);
        }

        var directories = new List<FileEntry>();
        foreach (var entry in entries.Where(entry => entry.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = GetPathRelativeTo(entry.Path, rootPath);
            if (ShouldIgnoreSearchEntry(relativePath, isDirectory: true, recursive: recursive, keywordSegments))
            {
                continue;
            }

            directories.Add(entry);
            AddSearchResult(entry, relativePath, keyword, limit, results);
            if (results.Count >= limit)
            {
                return;
            }
        }

        foreach (var entry in entries.Where(entry => !entry.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = GetPathRelativeTo(entry.Path, rootPath);
            if (ShouldIgnoreSearchEntry(relativePath, isDirectory: false, recursive: recursive, keywordSegments))
            {
                continue;
            }

            AddSearchResult(entry, relativePath, keyword, limit, results);
            if (results.Count >= limit)
            {
                return;
            }
        }

        if (!recursive)
        {
            return;
        }

        foreach (var directory in directories)
        {
            await SearchDirectoryAsync(
                fileSystem,
                rootPath,
                directory.Path,
                keyword,
                keywordSegments,
                limit,
                recursive: true,
                results,
                cancellationToken
            );
            if (results.Count >= limit)
            {
                return;
            }
        }
    }

    private static void AddSearchResult(
        FileEntry entry,
        string relativePath,
        string keyword,
        int limit,
        List<FileSearchEntry> results
    )
    {
        if (results.Count >= limit)
        {
            return;
        }

        var resultPath = entry.IsDirectory ? $"{relativePath.TrimEnd('/')}/" : relativePath;
        if (!resultPath.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        results.Add(
            new FileSearchEntry(NormalizePath(entry.Path), resultPath, entry.IsDirectory ? "directory" : "file")
        );
    }

    private static bool IsIgnoredDirectoryPath(string path)
    {
        return NormalizePath(path)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.StartsWith('.') || IgnoreDirectories.Contains(segment));
    }

    private static bool ShouldIgnoreSearchEntry(
        string relativePath,
        bool isDirectory,
        bool recursive,
        string[] keywordSegments
    )
    {
        var segments = NormalizePath(relativePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        var directorySegmentCount = isDirectory ? segments.Length : Math.Max(0, segments.Length - 1);
        for (var index = 0; index < directorySegmentCount; index++)
        {
            var segment = segments[index];
            if (IgnoreDirectories.Contains(segment))
            {
                return true;
            }

            if (segment.StartsWith('.') && !IsExplicitlyRequestedHiddenDirectory(segments, index, keywordSegments))
            {
                return true;
            }
        }

        return recursive && !isDirectory && segments.Length > 0 && ShouldIgnoreFile(segments[^1]);
    }

    private static bool IsExplicitlyRequestedHiddenDirectory(string[] pathSegments, int index, string[] keywordSegments)
    {
        if (keywordSegments.Length <= index)
        {
            return false;
        }

        for (var parentIndex = 0; parentIndex < index; parentIndex++)
        {
            if (
                !string.Equals(
                    pathSegments[parentIndex],
                    keywordSegments[parentIndex],
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                return false;
            }
        }

        var requestedSegment = keywordSegments[index];
        return requestedSegment != ".."
            && requestedSegment.StartsWith('.')
            && pathSegments[index].StartsWith(requestedSegment, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldIgnoreFile(string fileName)
    {
        foreach (var pattern in IgnoreFiles)
        {
            if (pattern.StartsWith('*') && fileName.EndsWith(pattern[1..]))
            {
                return true;
            }

            if (pattern.EndsWith('*') && fileName.StartsWith(pattern[..^1]))
            {
                return true;
            }

            if (pattern == fileName)
            {
                return true;
            }
        }

        return false;
    }

    private static string GetFileName(string path)
    {
        var normalized = NormalizePath(path).TrimEnd('/');
        var separatorIndex = normalized.LastIndexOf('/');
        return separatorIndex >= 0 ? normalized[(separatorIndex + 1)..] : normalized;
    }

    private static bool TargetsWorkspaceRoot(string path)
    {
        var depth = 0;
        foreach (var segment in NormalizePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (depth == 0)
                {
                    return false;
                }

                depth -= 1;
                continue;
            }

            depth += 1;
        }

        return depth == 0;
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/');
    }

    private static string NormalizeSearchKeyword(string keyword)
    {
        var normalized = NormalizePath(keyword);
        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized;
    }
}
