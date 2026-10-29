using System.Security.Cryptography;
using Agw.Files.Abstracts;
using Agw.Files.Application.Files;
using Agw.Files.Infrastructure.Git;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Files.Tests;

/// <summary>
/// FileAppService 的查询、下载、写入、建目录、移动与非递归删除。
/// Stat, download, write, directory creation, move and non-recursive delete of FileAppService.
/// </summary>
public sealed class FileAppServiceTransferTests : IDisposable
{
    private static readonly Guid ProjectId = Guid.Parse("0192f0c5-3c2a-7cc1-9d4e-6f1a2b3c4d5e");

    private readonly string _root = Directory
        .CreateDirectory(
            Path.Combine(AppContext.BaseDirectory, "file-app-service-transfer", Guid.CreateVersion7().ToString("N"))
        )
        .FullName;

    private readonly FileAppService _service;

    public FileAppServiceTransferTests()
    {
        Directory.CreateDirectory(Workspace);
        _service = new FileAppService(
            new ProjectFileSystemResolver(ProjectId, TestFileSystems.Local(Workspace)),
            new GitCommandService(NullLogger<GitCommandService>.Instance),
            NullLogger<FileAppService>.Instance,
            TestProjectWorkspaceAccess.Disabled
        );
    }

    private string Workspace => Path.Combine(_root, "workspace");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task StatAsync_File_ReturnsEntry()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "docs"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "docs", "a.md"), "hello", Token);

        var result = await _service.StatAsync(ProjectId, "docs/a.md", Token);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Equal("a.md", result.Value!.Name);
        Assert.Equal("docs/a.md", result.Value.Path);
        Assert.Equal("file", result.Value.Type);
        Assert.Equal(5, result.Value.Size);
        Assert.Equal(TimeSpan.Zero, result.Value.ModifiedTime!.Value.Offset);
    }

    [Fact]
    public async Task StatAsync_DirectoryRoot_ReturnsDirectoryEntry()
    {
        var result = await _service.StatAsync(ProjectId, null, Token);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Equal("directory", result.Value!.Type);
        Assert.Equal(string.Empty, result.Value.Path);
        Assert.Null(result.Value.Size);
    }

    [Fact]
    public async Task StatAsync_MissingPath_ReturnsNotFound()
    {
        var result = await _service.StatAsync(ProjectId, "missing.md", Token);

        Assert.Equal(FileOperationStatus.NotFound, result.Status);
    }

    [Fact]
    public async Task StatAsync_UnknownProject_ReturnsNotFound()
    {
        var result = await _service.StatAsync(Guid.CreateVersion7(), "a.md", Token);

        Assert.Equal(FileOperationStatus.NotFound, result.Status);
        Assert.Equal("Project not found", result.Message);
    }

    [Fact]
    public async Task OpenReadAsync_File_ReturnsContentStream()
    {
        byte[] content = [0x00, 0x10, 0xFF];
        await File.WriteAllBytesAsync(Path.Combine(Workspace, "logo.png"), content, Token);

        var result = await _service.OpenReadAsync(ProjectId, "logo.png", Token);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        await using var stream = result.Value!;
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Token);
        Assert.Equal(content, copy.ToArray());
    }

    [Theory]
    [InlineData("missing.png", FileOperationStatus.NotFound)]
    [InlineData("folder", FileOperationStatus.NotFound)]
    [InlineData("", FileOperationStatus.InvalidRequest)]
    public async Task OpenReadAsync_NotAFile_ReturnsFailure(string path, FileOperationStatus expected)
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "folder"));

        var result = await _service.OpenReadAsync(ProjectId, path, Token);

        Assert.Equal(expected, result.Status);
    }

    [Fact]
    public async Task OpenReadAsync_FileLargerThanTransferLimit_ThrowsFileTooLarge()
    {
        CreateSparseFile(Path.Combine(Workspace, "large.bin"), FileAppService.MaxTransferBytes + 1);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _service.OpenReadAsync(ProjectId, "large.bin", Token)
        );

        Assert.Equal(ErrorCodes.FileTooLarge.Code, exception.Code);
        Assert.Equal(ErrorCodes.FileTooLarge.StatusCode, exception.StatusCode);
    }

    [Fact]
    public async Task OpenReadAsync_FileAtTransferLimit_ReturnsStream()
    {
        CreateSparseFile(Path.Combine(Workspace, "limit.bin"), FileAppService.MaxTransferBytes);

        var result = await _service.OpenReadAsync(ProjectId, "limit.bin", Token);

        await using var stream = result.Value!;
        Assert.Equal(FileAppService.MaxTransferBytes, stream.Length);
    }

    [Fact]
    public async Task WriteAsync_NewFileWithCreateParents_WritesContentAndReturnsEntry()
    {
        var result = await _service.WriteAsync(
            ProjectId,
            "docs/b.md",
            new MemoryStream("content"u8.ToArray()),
            overwrite: false,
            createParents: true,
            expectedSha256: null,
            Token
        );

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Equal("docs/b.md", result.Value!.Path);
        Assert.Equal("file", result.Value.Type);
        Assert.Equal(7, result.Value.Size);
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(Workspace, "docs", "b.md"), Token));
    }

    [Fact]
    public async Task WriteAsync_MissingParentWithoutCreateParents_ReturnsNotFound()
    {
        var result = await _service.WriteAsync(
            ProjectId,
            "docs/b.md",
            new MemoryStream("content"u8.ToArray()),
            overwrite: true,
            createParents: false,
            expectedSha256: null,
            Token
        );

        Assert.Equal(FileOperationStatus.NotFound, result.Status);
        Assert.False(Directory.Exists(Path.Combine(Workspace, "docs")));
    }

    [Fact]
    public async Task WriteAsync_ExistingFileWithoutOverwrite_ThrowsFileAlreadyExists()
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "b.md"), "original", Token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _service.WriteAsync(
                ProjectId,
                "b.md",
                new MemoryStream("replaced"u8.ToArray()),
                overwrite: false,
                createParents: false,
                expectedSha256: null,
                Token
            )
        );

        Assert.Equal(ErrorCodes.FileAlreadyExists.Code, exception.Code);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(Workspace, "b.md"), Token));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a0")]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08f")]
    [InlineData("9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a0g")]
    public async Task WriteAsync_InvalidExpectedSha256_ThrowsInvalidParam(string expectedSha256)
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "b.md"), "original", Token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _service.WriteAsync(
                ProjectId,
                "b.md",
                new MemoryStream("replaced"u8.ToArray()),
                overwrite: true,
                createParents: false,
                expectedSha256,
                Token
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(Workspace, "b.md"), Token));
    }

    [Fact]
    public async Task WriteAsync_ExpectedSha256WithoutOverwrite_ThrowsInvalidParam()
    {
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _service.WriteAsync(
                ProjectId,
                "b.md",
                new MemoryStream("content"u8.ToArray()),
                overwrite: false,
                createParents: false,
                Convert.ToHexStringLower(SHA256.HashData("original"u8)),
                Token
            )
        );

        Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        Assert.False(File.Exists(Path.Combine(Workspace, "b.md")));
    }

    [Fact]
    public async Task WriteAsync_UppercaseExpectedSha256_WritesContent()
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "b.md"), "original", Token);

        var result = await _service.WriteAsync(
            ProjectId,
            "b.md",
            new MemoryStream("replaced"u8.ToArray()),
            overwrite: true,
            createParents: false,
            Convert.ToHexString(SHA256.HashData("original"u8)),
            Token
        );

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Equal("replaced", await File.ReadAllTextAsync(Path.Combine(Workspace, "b.md"), Token));
    }

    [Fact]
    public async Task WriteAsync_ContentLargerThanTransferLimit_ThrowsFileTooLargeAndWritesNothing()
    {
        var source = Path.Combine(_root, "large-source.bin");
        CreateSparseFile(source, FileAppService.MaxTransferBytes + 1);
        await using var content = File.OpenRead(source);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _service.WriteAsync(
                ProjectId,
                "large.bin",
                content,
                overwrite: true,
                createParents: true,
                expectedSha256: null,
                Token
            )
        );

        Assert.Equal(ErrorCodes.FileTooLarge.Code, exception.Code);
        Assert.False(File.Exists(Path.Combine(Workspace, "large.bin")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(".")]
    [InlineData("docs/..")]
    public async Task WriteAsync_DirectoryRoot_ReturnsInvalidRequest(string path)
    {
        var result = await _service.WriteAsync(
            ProjectId,
            path,
            new MemoryStream("content"u8.ToArray()),
            overwrite: true,
            createParents: true,
            expectedSha256: null,
            Token
        );

        Assert.Equal(FileOperationStatus.InvalidRequest, result.Status);
    }

    [Fact]
    public async Task CreateDirectoryAsync_RecursiveNestedPath_CreatesAllLevels()
    {
        var result = await _service.CreateDirectoryAsync(ProjectId, "docs/archive/2026", recursive: true, Token);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Equal("docs/archive/2026", result.Value!.Path);
        Assert.Equal("directory", result.Value.Type);
        Assert.True(Directory.Exists(Path.Combine(Workspace, "docs", "archive", "2026")));
    }

    [Fact]
    public async Task CreateDirectoryAsync_NonRecursiveMissingParent_ReturnsNotFoundAndCreatesNothing()
    {
        var result = await _service.CreateDirectoryAsync(ProjectId, "docs/archive", recursive: false, Token);

        Assert.Equal(FileOperationStatus.NotFound, result.Status);
        Assert.False(Directory.Exists(Path.Combine(Workspace, "docs")));
    }

    [Fact]
    public async Task CreateDirectoryAsync_NonRecursiveExistingParent_CreatesDirectory()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "docs"));

        var result = await _service.CreateDirectoryAsync(ProjectId, "docs/archive", recursive: false, Token);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.True(Directory.Exists(Path.Combine(Workspace, "docs", "archive")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CreateDirectoryAsync_FileWithSameName_ThrowsFileAlreadyExists(bool recursive)
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "archive"), "file", Token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _service.CreateDirectoryAsync(ProjectId, "archive", recursive, Token)
        );

        Assert.Equal(ErrorCodes.FileAlreadyExists.Code, exception.Code);
    }

    [Fact]
    public async Task CreateDirectoryAsync_ExistingDirectory_SucceedsOnlyWhenRecursive()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "archive"));

        var recursive = await _service.CreateDirectoryAsync(ProjectId, "archive", recursive: true, Token);
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _service.CreateDirectoryAsync(ProjectId, "archive", recursive: false, Token)
        );

        Assert.Equal(FileOperationStatus.Success, recursive.Status);
        Assert.Equal("directory", recursive.Value!.Type);
        Assert.Equal(ErrorCodes.FileAlreadyExists.Code, exception.Code);
    }

    [Fact]
    public async Task MoveAsync_File_MovesAndReturnsDestinationEntry()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "docs", "archive"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "docs", "a.md"), "a", Token);

        var result = await _service.MoveAsync(ProjectId, "docs/a.md", "docs/archive/a.md", overwrite: false, Token);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Equal("docs/archive/a.md", result.Value!.Path);
        Assert.False(File.Exists(Path.Combine(Workspace, "docs", "a.md")));
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(Workspace, "docs", "archive", "a.md"), Token));
    }

    [Fact]
    public async Task MoveAsync_ExistingDestination_ReplacesOnlyWithOverwrite()
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "a.md"), "source", Token);
        await File.WriteAllTextAsync(Path.Combine(Workspace, "b.md"), "destination", Token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _service.MoveAsync(ProjectId, "a.md", "b.md", overwrite: false, Token)
        );
        var replaced = await _service.MoveAsync(ProjectId, "a.md", "b.md", overwrite: true, Token);

        Assert.Equal(ErrorCodes.FileAlreadyExists.Code, exception.Code);
        Assert.Equal(FileOperationStatus.Success, replaced.Status);
        Assert.Equal("source", await File.ReadAllTextAsync(Path.Combine(Workspace, "b.md"), Token));
    }

    [Theory]
    [InlineData("", "b.md")]
    [InlineData("a.md", "")]
    [InlineData(".", "moved")]
    [InlineData("docs/..", "moved")]
    [InlineData("a.md", ".")]
    [InlineData("a.md", "a.md")]
    [InlineData("a.md", "./docs/../a.md")]
    [InlineData("docs", "docs/inner")]
    public async Task MoveAsync_InvalidSourceOrDestination_ReturnsInvalidRequest(string source, string destination)
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "docs"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "a.md"), "a", Token);

        var result = await _service.MoveAsync(ProjectId, source, destination, overwrite: true, Token);

        Assert.Equal(FileOperationStatus.InvalidRequest, result.Status);
        Assert.True(Directory.Exists(Path.Combine(Workspace, "docs")));
        Assert.True(File.Exists(Path.Combine(Workspace, "a.md")));
    }

    [Theory]
    [InlineData("missing.md", "moved.md")]
    [InlineData("a.md", "missing/a.md")]
    public async Task MoveAsync_MissingSourceOrDestinationParent_ReturnsNotFound(string source, string destination)
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "a.md"), "a", Token);

        var result = await _service.MoveAsync(ProjectId, source, destination, overwrite: false, Token);

        Assert.Equal(FileOperationStatus.NotFound, result.Status);
        Assert.True(File.Exists(Path.Combine(Workspace, "a.md")));
    }

    [Fact]
    public async Task MoveAsync_DestinationOutsideRoot_ThrowsFilePathOutsideRoot()
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "a.md"), "a", Token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _service.MoveAsync(ProjectId, "a.md", "../a.md", overwrite: false, Token)
        );

        Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
        Assert.True(File.Exists(Path.Combine(Workspace, "a.md")));
    }

    [Fact]
    public async Task DeleteAsync_NonRecursiveNonEmptyDirectory_ThrowsDirectoryNotEmptyAndKeepsFiles()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "docs"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "docs", "a.md"), "a", Token);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            _service.DeleteAsync(ProjectId, "docs", recursive: false, Token)
        );

        Assert.Equal(ErrorCodes.DirectoryNotEmpty.Code, exception.Code);
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(Workspace, "docs", "a.md"), Token));
    }

    [Fact]
    public async Task DeleteAsync_NonRecursiveEmptyDirectory_RemovesDirectory()
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "docs"));

        var result = await _service.DeleteAsync(ProjectId, "docs", recursive: false, Token);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.False(Directory.Exists(Path.Combine(Workspace, "docs")));
    }

    /// <summary>
    /// 用 SetLength 建立指定长度的稀疏文件，不必真正写入这么多字节。
    /// Creates a sparse file of the given length with SetLength, without writing that many bytes.
    /// </summary>
    private static void CreateSparseFile(string path, long length)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        stream.SetLength(length);
    }

    /// <summary>
    /// 只为一个 Project 返回固定的文件系统。Returns a fixed file system for a single Project.
    /// </summary>
    private sealed class ProjectFileSystemResolver : IAgwFileSystemResolver
    {
        private readonly Guid _projectId;
        private readonly IAgwFileSystem _fileSystem;

        public ProjectFileSystemResolver(Guid projectId, IAgwFileSystem fileSystem)
        {
            _projectId = projectId;
            _fileSystem = fileSystem;
        }

        public Task<IAgwFileSystem?> ResolveAsync(Guid projectId, CancellationToken ct) =>
            Task.FromResult(projectId == _projectId ? _fileSystem : null);
    }
}
