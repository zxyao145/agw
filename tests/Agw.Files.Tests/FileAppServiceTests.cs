using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Files.Application.Files;
using Agw.Files.Infrastructure.Git;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Files.Tests;

public class FileAppServiceTests
{
    private static readonly Guid ProjectId = Guid.Parse("4d9adf7c-7e79-4b0f-af37-301aac328c2b");

    [Fact]
    public async Task ListAsync_DiffDirect_ReturnsChangedEntriesAndDeletedFilesWithDirectoriesFirst()
    {
        using var scope = TempDirectoryScope.Create();
        var changedDirectory = Directory.CreateDirectory(Path.Combine(scope.Path, "changed-dir"));
        var nestedFile = Path.Combine(changedDirectory.FullName, "nested.txt");
        await File.WriteAllTextAsync(nestedFile, "nested", TestContext.Current.CancellationToken);
        var changedFile = Path.Combine(scope.Path, "changed.txt");
        await File.WriteAllTextAsync(changedFile, "changed", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(scope.Path, "unchanged.txt"),
            "unchanged",
            TestContext.Current.CancellationToken
        );
        var deletedFile = Path.Combine(scope.Path, "deleted.txt");
        var git = new FakeGitCommandService
        {
            ChangedFiles = new GitChangedFiles(
                new Dictionary<string, GitFileStatus>(StringComparer.OrdinalIgnoreCase)
                {
                    [nestedFile] = new GitFileStatus(null, "modified"),
                    [changedFile] = new GitFileStatus("added", null),
                    [deletedFile] = new GitFileStatus(null, "deleted"),
                },
                new HashSet<string>([deletedFile], StringComparer.OrdinalIgnoreCase)
            ),
        };
        var service = CreateService(scope.Path, git);

        var result = await service.ListAsync(
            ProjectId,
            "",
            diff: true,
            recursive: false,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Collection(
            result.Value!.Items,
            item => AssertListEntry(item, "changed-dir", "directory", "modified", null, "modified"),
            item => AssertListEntry(item, "changed.txt", "file", "added", "added", null),
            item => AssertListEntry(item, "deleted.txt", "file", "deleted", null, "deleted")
        );
    }

    [Fact]
    public async Task ListAsync_DiffRecursive_ReturnsAllChangedFilesSortedByPath()
    {
        using var scope = TempDirectoryScope.Create();
        var nestedDirectory = Directory.CreateDirectory(Path.Combine(scope.Path, "nested"));
        var nestedFile = Path.Combine(nestedDirectory.FullName, "b.txt");
        await File.WriteAllTextAsync(nestedFile, "nested", TestContext.Current.CancellationToken);
        var deletedFile = Path.Combine(scope.Path, "a.txt");
        var git = new FakeGitCommandService
        {
            ChangedFiles = new GitChangedFiles(
                new Dictionary<string, GitFileStatus>(StringComparer.OrdinalIgnoreCase)
                {
                    [nestedFile] = new GitFileStatus(null, "modified"),
                    [deletedFile] = new GitFileStatus("deleted", null),
                },
                new HashSet<string>([deletedFile], StringComparer.OrdinalIgnoreCase)
            ),
        };
        var service = CreateService(scope.Path, git);

        var result = await service.ListAsync(
            ProjectId,
            "",
            diff: true,
            recursive: true,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Collection(
            result.Value!.Items,
            item => Assert.Equal("a.txt", item.Path),
            item => Assert.Equal("nested/b.txt", item.Path)
        );
    }

    [Fact]
    public async Task ReadAsync_FileExists_ReturnsContent()
    {
        using var scope = TempDirectoryScope.Create();
        var filePath = Path.Combine(scope.Path, "read.txt");
        await File.WriteAllTextAsync(filePath, "content", TestContext.Current.CancellationToken);
        var service = CreateService(scope.Path);

        var result = await service.ReadAsync(ProjectId, "read.txt", TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Equal("content", result.Value);
    }

    [Fact]
    public async Task ReadAsync_FileDoesNotExist_ReturnsNotFound()
    {
        using var scope = TempDirectoryScope.Create();
        var service = CreateService(scope.Path);

        var result = await service.ReadAsync(ProjectId, "missing.txt", TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.NotFound, result.Status);
        Assert.Equal("File not found", result.Message);
    }

    [Fact]
    public async Task ReadAsync_EmptyPath_ReturnsInvalidRequest()
    {
        using var scope = TempDirectoryScope.Create();
        var service = CreateService(scope.Path);

        var result = await service.ReadAsync(ProjectId, "", TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.InvalidRequest, result.Status);
        Assert.Equal("Path parameter is required", result.Message);
    }

    [Fact]
    public async Task ReadAsync_NonLocalFileSystem_ReturnsContentThroughAbstraction()
    {
        using var scope = TempDirectoryScope.Create();
        await File.WriteAllTextAsync(
            Path.Combine(scope.Path, "read.txt"),
            "content",
            TestContext.Current.CancellationToken
        );
        var service = CreateService(scope.Path, fileSystem: new NonLocalFileSystem(TestFileSystems.Local(scope.Path)));

        var result = await service.ReadAsync(ProjectId, "read.txt", TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Equal("content", result.Value);
    }

    [Fact]
    public async Task DiffAsync_StagedScope_ReturnsHeadAndIndexText()
    {
        using var repository = PartiallyStagedRepository();

        var result = await DiffAsync(repository, "mixed.txt", "staged");

        AssertDiff(result, unchanged: false, original: "base\n", modified: "base\nstaged\n");
        Assert.Contains("+staged", result.Diff, StringComparison.Ordinal);
        Assert.DoesNotContain("+unstaged", result.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiffAsync_UnstagedScope_ReturnsIndexAndWorkingTreeText()
    {
        using var repository = PartiallyStagedRepository();

        var result = await DiffAsync(repository, "mixed.txt", "unstaged");

        AssertDiff(result, unchanged: false, original: "base\nstaged\n", modified: "base\nstaged\nunstaged\n");
        Assert.Contains("+unstaged", result.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiffAsync_AllScope_ReturnsHeadAndWorkingTreeText()
    {
        using var repository = PartiallyStagedRepository();

        var result = await DiffAsync(repository, "mixed.txt", null);

        AssertDiff(result, unchanged: false, original: "base\n", modified: "base\nstaged\nunstaged\n");
    }

    [Fact]
    public async Task DiffAsync_UntrackedFile_ReturnsNullOriginal()
    {
        using var repository = TempGitRepository.Create();
        repository.Write("tracked.txt", "base\n");
        repository.CommitAll();
        repository.Write("new.txt", "new\n");

        var result = await DiffAsync(repository, "new.txt", "unstaged");

        AssertDiff(result, unchanged: false, original: null, modified: "new\n");
        Assert.Contains("+new", result.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiffAsync_DeletedInWorkingTree_ReturnsNullModified()
    {
        using var repository = TempGitRepository.Create();
        repository.Write("gone.txt", "gone\n");
        repository.CommitAll();
        repository.Delete("gone.txt");

        var result = await DiffAsync(repository, "gone.txt", "unstaged");

        AssertDiff(result, unchanged: false, original: "gone\n", modified: null);
        Assert.Contains("-gone", result.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiffAsync_StagedDeletedFile_ReturnsHeadTextAndNullModified()
    {
        using var repository = TempGitRepository.Create();
        repository.Write("deleted.txt", "deleted\n");
        repository.CommitAll();
        repository.Git("rm", "--quiet", "deleted.txt");

        var result = await DiffAsync(repository, "deleted.txt", "staged");

        AssertDiff(result, unchanged: false, original: "deleted\n", modified: null);
    }

    [Fact]
    public async Task DiffAsync_BinaryFile_ReturnsBinaryWithoutContent()
    {
        using var repository = TempGitRepository.Create();
        repository.WriteBytes("image.bin", [0x89, 0x50, 0x4e, 0x47, 0x00, 0x01]);
        repository.CommitAll();
        repository.WriteBytes("image.bin", [0x89, 0x50, 0x4e, 0x47, 0x00, 0x02]);

        var result = await DiffAsync(repository, "image.bin", "unstaged");

        Assert.False(result.Unchanged);
        Assert.True(result.IsBinary);
        Assert.Null(result.OriginalContent);
        Assert.Null(result.ModifiedContent);
        Assert.Contains("Binary files", result.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiffAsync_UnchangedFile_ReturnsUnchangedWithContent()
    {
        using var repository = TempGitRepository.Create();
        repository.Write("same.txt", "same\n");
        repository.CommitAll();

        var result = await DiffAsync(repository, "same.txt", "unstaged");

        AssertDiff(result, unchanged: true, original: "same\n", modified: "same\n");
        Assert.Equal(string.Empty, result.Diff);
    }

    [Fact]
    public async Task DiffAsync_BrokenGitDirectory_ReturnsInvalidRequest()
    {
        using var repository = TempGitRepository.Create();
        repository.Write("file.txt", "content");
        // .git 改为指向不存在目录的文件。.git becomes a file pointing at a missing directory.
        Directory.Delete(repository.GetPath(".git"), recursive: true);
        repository.Write(".git", "gitdir: missing-git-directory\n");
        var service = CreateService(repository.Path, new GitCommandService(NullLogger<GitCommandService>.Instance));

        var result = await service.DiffAsync(ProjectId, "file.txt", null, TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.InvalidRequest, result.Status);
        Assert.Equal("Git diff failed", result.Message);
        Assert.False(string.IsNullOrWhiteSpace(result.Details));
    }

    [Fact]
    public async Task DiffAsync_InvalidScope_ReturnsInvalidRequest()
    {
        using var scope = TempDirectoryScope.Create();
        var service = CreateService(scope.Path);

        var result = await service.DiffAsync(
            ProjectId,
            "changed.txt",
            "invalid",
            TestContext.Current.CancellationToken
        );

        Assert.Equal(FileOperationStatus.InvalidRequest, result.Status);
        Assert.Equal("Scope must be 'staged' or 'unstaged'", result.Message);
    }

    [Fact]
    public async Task DiffAsync_NonLocalFileSystem_ReturnsInvalidRequest()
    {
        using var scope = TempDirectoryScope.Create();
        var service = CreateService(scope.Path, fileSystem: new NonLocalFileSystem(TestFileSystems.Local(scope.Path)));

        var result = await service.DiffAsync(ProjectId, "file.txt", null, TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.InvalidRequest, result.Status);
        Assert.Equal("Git operations are only supported for local project file systems", result.Message);
    }

    [Fact]
    public async Task DiffAsync_EmptyPath_ReturnsInvalidRequest()
    {
        using var scope = TempDirectoryScope.Create();
        var service = CreateService(scope.Path);

        var result = await service.DiffAsync(ProjectId, "", null, TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.InvalidRequest, result.Status);
        Assert.Equal("Path parameter is required", result.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAsync_PathExists_RemovesPath(bool isDirectory)
    {
        using var scope = TempDirectoryScope.Create();
        var targetPath = Path.Combine(scope.Path, isDirectory ? "directory" : "file.txt");
        if (isDirectory)
        {
            Directory.CreateDirectory(targetPath);
            await File.WriteAllTextAsync(
                Path.Combine(targetPath, "nested.txt"),
                "content",
                TestContext.Current.CancellationToken
            );
        }
        else
        {
            await File.WriteAllTextAsync(targetPath, "content", TestContext.Current.CancellationToken);
        }
        var service = CreateService(scope.Path);

        var result = await service.DeleteAsync(
            ProjectId,
            isDirectory ? "directory" : "file.txt",
            recursive: true,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.True(result.Value!.Success);
        Assert.False(File.Exists(targetPath));
        Assert.False(Directory.Exists(targetPath));
    }

    [Fact]
    public async Task DeleteAsync_PathDoesNotExist_ReturnsNotFound()
    {
        using var scope = TempDirectoryScope.Create();
        var service = CreateService(scope.Path);

        var result = await service.DeleteAsync(
            ProjectId,
            "missing.txt",
            recursive: true,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(FileOperationStatus.NotFound, result.Status);
        Assert.Equal("File or directory not found", result.Message);
    }

    [Fact]
    public async Task DeleteAsync_EmptyPath_ReturnsInvalidRequest()
    {
        using var scope = TempDirectoryScope.Create();
        var service = CreateService(scope.Path);

        var result = await service.DeleteAsync(ProjectId, "", recursive: true, TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.InvalidRequest, result.Status);
        Assert.Equal("Path parameter is required", result.Message);
        Assert.True(Directory.Exists(scope.Path));
    }

    [Fact]
    public async Task ResetAsync_Succeeds_ReturnsSuccessfulMutation()
    {
        using var scope = TempDirectoryScope.Create();
        var filePath = Path.Combine(scope.Path, "changed.txt");
        await File.WriteAllTextAsync(filePath, "content", TestContext.Current.CancellationToken);
        var git = new FakeGitCommandService
        {
            ResetResult = new GitResetResult(true, "File reset successfully", null, false),
        };
        var service = CreateService(scope.Path, git);

        var result = await service.ResetAsync(ProjectId, "changed.txt", TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.True(result.Value!.Success);
        Assert.Equal("File reset successfully", result.Value.Message);
    }

    [Fact]
    public async Task ResetAsync_ClientError_ReturnsInvalidRequest()
    {
        using var scope = TempDirectoryScope.Create();
        var filePath = Path.Combine(scope.Path, "changed.txt");
        await File.WriteAllTextAsync(filePath, "content", TestContext.Current.CancellationToken);
        var git = new FakeGitCommandService
        {
            ResetResult = new GitResetResult(false, "File has no modifications to reset", null, true),
        };
        var service = CreateService(scope.Path, git);

        var result = await service.ResetAsync(ProjectId, "changed.txt", TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.InvalidRequest, result.Status);
        Assert.Equal("File has no modifications to reset", result.Message);
    }

    [Fact]
    public async Task ResetAsync_ServerError_ReturnsFailure()
    {
        using var scope = TempDirectoryScope.Create();
        var filePath = Path.Combine(scope.Path, "changed.txt");
        await File.WriteAllTextAsync(filePath, "content", TestContext.Current.CancellationToken);
        var git = new FakeGitCommandService
        {
            ResetResult = new GitResetResult(false, "Git reset failed", "git failed", false),
        };
        var service = CreateService(scope.Path, git);

        var result = await service.ResetAsync(ProjectId, "changed.txt", TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.Failure, result.Status);
        Assert.Equal("Git reset failed", result.Message);
        Assert.Equal("git failed", result.Details);
    }

    [Fact]
    public async Task ResetAsync_UnsuccessfulWithoutError_ReturnsUnsuccessfulMutation()
    {
        using var scope = TempDirectoryScope.Create();
        var filePath = Path.Combine(scope.Path, "unchanged.txt");
        await File.WriteAllTextAsync(filePath, "content", TestContext.Current.CancellationToken);
        var git = new FakeGitCommandService
        {
            ResetResult = new GitResetResult(false, "No changes to reset", null, false),
        };
        var service = CreateService(scope.Path, git);

        var result = await service.ResetAsync(ProjectId, "unchanged.txt", TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.False(result.Value!.Success);
        Assert.Equal("No changes to reset", result.Value.Message);
    }

    [Fact]
    public async Task ResetAsync_EmptyPath_ReturnsInvalidRequest()
    {
        using var scope = TempDirectoryScope.Create();
        var service = CreateService(scope.Path);

        var result = await service.ResetAsync(ProjectId, "", TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.InvalidRequest, result.Status);
        Assert.Equal("Path parameter is required", result.Message);
    }

    [Fact]
    public async Task StageAsync_DeletedPath_ForwardsToGitWithoutRequiringPhysicalEntry()
    {
        using var scope = TempDirectoryScope.Create();
        var git = new FakeGitCommandService
        {
            IndexResult = new GitIndexResult(true, "Changes staged successfully", null, false),
        };
        var service = CreateService(scope.Path, git);

        var result = await service.StageAsync(ProjectId, "deleted.txt", TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.True(result.Value!.Success);
        Assert.True(git.LastStaged);
        Assert.Equal(Path.Combine(scope.Path, "deleted.txt"), git.LastIndexPath);
    }

    [Fact]
    public async Task UnstageAsync_Directory_ForwardsDirectoryPathToGit()
    {
        using var scope = TempDirectoryScope.Create();
        var git = new FakeGitCommandService
        {
            IndexResult = new GitIndexResult(true, "Changes unstaged successfully", null, false),
        };
        var service = CreateService(scope.Path, git);

        var result = await service.UnstageAsync(ProjectId, "src/features", TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.True(result.Value!.Success);
        Assert.False(git.LastStaged);
        Assert.Equal(Path.Combine(scope.Path, "src", "features"), git.LastIndexPath);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    [InlineData("src/..")]
    public async Task StageAsync_WorkspaceRoot_ReturnsInvalidRequest(string path)
    {
        using var scope = TempDirectoryScope.Create();
        var git = new FakeGitCommandService();
        var service = CreateService(scope.Path, git);

        var result = await service.StageAsync(ProjectId, path, TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.InvalidRequest, result.Status);
        Assert.Equal("Workspace root cannot be staged or unstaged", result.Message);
        Assert.Null(git.LastIndexPath);
    }

    [Fact]
    public async Task SearchAsync_Recursive_AppliesIgnoreRulesAndLimit()
    {
        using var scope = TempDirectoryScope.Create();
        Directory.CreateDirectory(Path.Combine(scope.Path, ".hidden"));
        Directory.CreateDirectory(Path.Combine(scope.Path, "node_modules"));
        var matchingDirectory = Directory.CreateDirectory(Path.Combine(scope.Path, "target-dir"));
        await File.WriteAllTextAsync(
            Path.Combine(matchingDirectory.FullName, "target-a.txt"),
            "content",
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            Path.Combine(scope.Path, "tmpclaude-target"),
            "content",
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            Path.Combine(scope.Path, ".hidden", "target.txt"),
            "content",
            TestContext.Current.CancellationToken
        );
        await File.WriteAllTextAsync(
            Path.Combine(scope.Path, "node_modules", "target.txt"),
            "content",
            TestContext.Current.CancellationToken
        );
        var service = CreateService(scope.Path);

        var result = await service.SearchAsync(
            ProjectId,
            "",
            "target",
            limit: 2,
            recursive: true,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Collection(
            result.Value!.Results,
            item => AssertSearchEntry(item, "target-dir/", "directory"),
            item => AssertSearchEntry(item, "target-dir/target-a.txt", "file")
        );
    }

    [Theory]
    [InlineData(".github/")]
    [InlineData("./.github/")]
    public async Task SearchAsync_Recursive_WhenHiddenPathIsExplicit_ReturnsMatchingDescendants(string keyword)
    {
        // Arrange
        using var scope = TempDirectoryScope.Create();
        var workflowsDirectory = Directory.CreateDirectory(Path.Combine(scope.Path, ".github", "workflows"));
        await File.WriteAllTextAsync(
            Path.Combine(workflowsDirectory.FullName, "build.yml"),
            "content",
            TestContext.Current.CancellationToken
        );
        var service = CreateService(scope.Path);

        // Act
        var result = await service.SearchAsync(
            ProjectId,
            "",
            keyword,
            limit: 10,
            recursive: true,
            cancellationToken: TestContext.Current.CancellationToken
        );

        // Assert
        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Collection(
            result.Value!.Results,
            item => AssertSearchEntry(item, ".github/", "directory"),
            item => AssertSearchEntry(item, ".github/workflows/", "directory"),
            item => AssertSearchEntry(item, ".github/workflows/build.yml", "file")
        );
    }

    [Fact]
    public async Task SearchAsync_NonRecursive_ReturnsOnlyDirectMatches()
    {
        using var scope = TempDirectoryScope.Create();
        Directory.CreateDirectory(Path.Combine(scope.Path, "direct-target"));
        Directory.CreateDirectory(Path.Combine(scope.Path, "parent", "nested-target"));
        var service = CreateService(scope.Path);

        var result = await service.SearchAsync(
            ProjectId,
            "",
            "target",
            limit: 10,
            recursive: false,
            cancellationToken: TestContext.Current.CancellationToken
        );

        Assert.Equal(FileOperationStatus.Success, result.Status);
        var entry = Assert.Single(result.Value!.Results);
        AssertSearchEntry(entry, "direct-target/", "directory");
    }

    /// <summary>
    /// mixed.txt：HEAD 为 "base"，暂存区多一行 "staged"，工作区再多一行 "unstaged"。mixed.txt is "base" in HEAD, adds
    /// "staged" in the index and "unstaged" in the working tree.
    /// </summary>
    private static TempGitRepository PartiallyStagedRepository()
    {
        var repository = TempGitRepository.Create();
        repository.Write("mixed.txt", "base\n");
        repository.CommitAll();
        repository.Write("mixed.txt", "base\nstaged\n");
        repository.Git("add", "mixed.txt");
        repository.Write("mixed.txt", "base\nstaged\nunstaged\n");
        return repository;
    }

    private static async Task<FileDiffOutput> DiffAsync(TempGitRepository repository, string path, string? scope)
    {
        var service = CreateService(repository.Path, new GitCommandService(NullLogger<GitCommandService>.Instance));
        var result = await service.DiffAsync(ProjectId, path, scope, TestContext.Current.CancellationToken);
        Assert.Equal(FileOperationStatus.Success, result.Status);
        return result.Value!;
    }

    private static void AssertDiff(FileDiffOutput diff, bool unchanged, string? original, string? modified)
    {
        Assert.Equal(unchanged, diff.Unchanged);
        Assert.False(diff.IsBinary);
        Assert.Equal(original, diff.OriginalContent);
        Assert.Equal(modified, diff.ModifiedContent);
    }

    private static FileAppService CreateService(
        string rootPath,
        IGitCommandService? git = null,
        IAgwFileSystem? fileSystem = null
    )
    {
        return new FileAppService(
            new FakeFileSystemResolver(fileSystem ?? TestFileSystems.Local(rootPath)),
            git ?? new FakeGitCommandService(),
            NullLogger<FileAppService>.Instance,
            TestProjectWorkspaceAccess.Disabled
        );
    }

    private sealed class NonLocalFileSystem : IAgwFileSystem
    {
        private readonly IAgwFileSystem _inner;

        public NonLocalFileSystem(IAgwFileSystem inner)
        {
            _inner = inner;
        }

        public Task<bool> ExistsFileAsync(string path, CancellationToken ct) => _inner.ExistsFileAsync(path, ct);

        public Task<bool> ExistsDirectoryAsync(string path, CancellationToken ct) =>
            _inner.ExistsDirectoryAsync(path, ct);

        public Task<Agw.Files.Abstracts.Dtos.FileEntry?> StatAsync(string path, CancellationToken ct) =>
            _inner.StatAsync(path, ct);

        public Task<string> ReadAllTextAsync(string path, CancellationToken ct) => _inner.ReadAllTextAsync(path, ct);

        public Task<string[]> ReadAllLinesAsync(string path, CancellationToken ct) =>
            _inner.ReadAllLinesAsync(path, ct);

        public Task WriteAllTextAsync(string path, string content, CancellationToken ct) =>
            _inner.WriteAllTextAsync(path, content, ct);

        public Task CreateDirectoryAsync(string path, CancellationToken ct) => _inner.CreateDirectoryAsync(path, ct);

        public Task DeleteAsync(string path, bool recursive, CancellationToken ct) =>
            _inner.DeleteAsync(path, recursive, ct);

        public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => _inner.OpenReadAsync(path, ct);

        public Task WriteAsync(
            string path,
            Stream content,
            bool overwrite,
            bool createParents,
            string? expectedSha256,
            CancellationToken ct
        ) => _inner.WriteAsync(path, content, overwrite, createParents, expectedSha256, ct);

        public Task MoveAsync(string sourcePath, string destinationPath, bool overwrite, CancellationToken ct) =>
            _inner.MoveAsync(sourcePath, destinationPath, overwrite, ct);

        public IAsyncEnumerable<Agw.Files.Abstracts.Dtos.FileEntry> EnumerateAsync(
            string path,
            string searchPattern,
            bool recursive,
            CancellationToken ct
        ) => _inner.EnumerateAsync(path, searchPattern, recursive, ct);

        public IAsyncEnumerable<Agw.Files.Abstracts.Dtos.AgwFileSearchResult> SearchAsync(
            string rootPath,
            Agw.Files.Abstracts.Dtos.SearchOptions options,
            CancellationToken ct
        ) => _inner.SearchAsync(rootPath, options, ct);

        public Task<bool> CreateTextFileAsync(string path, string content, CancellationToken ct) =>
            _inner.CreateTextFileAsync(path, content, ct);

        public Task<IReadOnlyList<string>> ReadLinesAsync(
            string path,
            int startLine,
            int? endLine,
            CancellationToken ct
        ) => _inner.ReadLinesAsync(path, startLine, endLine, ct);

        public Task<int> ReplaceTextAsync(
            string path,
            string oldString,
            string newString,
            bool replaceAll,
            CancellationToken ct
        ) => _inner.ReplaceTextAsync(path, oldString, newString, replaceAll, ct);

        public Task ReplaceLinesAsync(
            string path,
            IReadOnlyList<Agw.Files.Abstracts.Dtos.AgwFileLineEdit> edits,
            CancellationToken ct
        ) => _inner.ReplaceLinesAsync(path, edits, ct);

        public IAgwFileSystem GetSubFileSystem(string path) => new NonLocalFileSystem(_inner.GetSubFileSystem(path));
    }

    private sealed class FakeFileSystemResolver : IAgwFileSystemResolver
    {
        private readonly IAgwFileSystem _fileSystem;

        public FakeFileSystemResolver(IAgwFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }

        public Task<IAgwFileSystem?> ResolveAsync(Guid projectId, CancellationToken ct)
        {
            return Task.FromResult<IAgwFileSystem?>(_fileSystem);
        }
    }

    private static void AssertListEntry(
        FileListEntry entry,
        string name,
        string type,
        string? gitStatus,
        string? gitStagedStatus,
        string? gitUnstagedStatus
    )
    {
        Assert.Equal(name, entry.Name);
        Assert.Equal(type, entry.Type);
        Assert.Equal(gitStatus, entry.GitStatus);
        Assert.Equal(gitStagedStatus, entry.GitStagedStatus);
        Assert.Equal(gitUnstagedStatus, entry.GitUnstagedStatus);
    }

    private static void AssertSearchEntry(FileSearchEntry entry, string relativePath, string type)
    {
        Assert.Equal(relativePath, entry.RelativePath);
        Assert.Equal(type, entry.Type);
    }

    private sealed class FakeGitCommandService : IGitCommandService
    {
        public GitChangedFiles? ChangedFiles { get; set; }

        public GitResetResult ResetResult { get; set; } = new(true, "File reset successfully", null, false);

        public GitIndexResult IndexResult { get; set; } = new(true, "Changes staged successfully", null, false);

        public string? LastIndexPath { get; private set; }

        public bool LastStaged { get; private set; }

        public Task<GitChangedFiles?> GetChangedFilesAsync(
            string directory,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(ChangedFiles);
        }

        public Task<GitDiffResult> GetDiffAsync(
            string filePath,
            CancellationToken cancellationToken = default,
            GitDiffScope scope = GitDiffScope.All
        )
        {
            return Task.FromResult(new GitDiffResult(false, string.Empty, "Not implemented by test fake."));
        }

        public Task<GitBlobResult> ReadBlobAsync(
            string filePath,
            GitBlobSource source,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(new GitBlobResult(false, null, "Not implemented by test fake."));
        }

        public Task<GitResetResult> ResetFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ResetResult);
        }

        public Task<GitIndexResult> SetStagedAsync(
            string path,
            bool staged,
            CancellationToken cancellationToken = default
        )
        {
            LastIndexPath = path;
            LastStaged = staged;
            return Task.FromResult(IndexResult);
        }

        public Task<GitCloneResult> CloneRepositoryAsync(
            string gitAddress,
            string workingDirectory,
            CancellationToken cancellationToken = default
        )
        {
            return Task.FromResult(new GitCloneResult(false, "Not implemented by test fake.", null, null));
        }
    }

    private sealed class TempDirectoryScope : IDisposable
    {
        private TempDirectoryScope(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public static TempDirectoryScope Create()
        {
            return new TempDirectoryScope(TestDirectoryHelper.Create("file-app-service-tests"));
        }

        public void Dispose()
        {
            TestDirectoryHelper.Delete(Path);
        }
    }
}
