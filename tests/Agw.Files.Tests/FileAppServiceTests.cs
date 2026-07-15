using Agw.Files.Application.Files;
using Agw.Files.Services;

using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Files.Tests;

public class FileAppServiceTests
{
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
            TestContext.Current.CancellationToken);
        var deletedFile = Path.Combine(scope.Path, "deleted.txt");
        var git = new FakeGitCommandService
        {
            ChangedFiles = new GitChangedFiles(
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [nestedFile] = "modified",
                    [changedFile] = "added",
                    [deletedFile] = "deleted"
                },
                new HashSet<string>([deletedFile], StringComparer.OrdinalIgnoreCase))
        };
        var service = CreateService(git);

        var result = await service.ListAsync(
            scope.Path,
            diff: true,
            recursive: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Collection(
            result.Value!.Items,
            item => AssertListEntry(item, "changed-dir", "directory", null),
            item => AssertListEntry(item, "changed.txt", "file", "added"),
            item => AssertListEntry(item, "deleted.txt", "file", "deleted"));
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
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [nestedFile] = "modified",
                    [deletedFile] = "deleted"
                },
                new HashSet<string>([deletedFile], StringComparer.OrdinalIgnoreCase))
        };
        var service = CreateService(git);

        var result = await service.ListAsync(
            scope.Path,
            diff: true,
            recursive: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Collection(
            result.Value!.Items,
            item => Assert.Equal(deletedFile, item.Path),
            item => Assert.Equal(nestedFile, item.Path));
    }

    [Fact]
    public async Task ReadAsync_FileExists_ReturnsContent()
    {
        using var scope = TempDirectoryScope.Create();
        var filePath = Path.Combine(scope.Path, "read.txt");
        await File.WriteAllTextAsync(filePath, "content", TestContext.Current.CancellationToken);
        var service = CreateService();

        var result = await service.ReadAsync(filePath, TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Equal("content", result.Value);
    }

    [Fact]
    public async Task ReadAsync_FileDoesNotExist_ReturnsNotFound()
    {
        var service = CreateService();

        var result = await service.ReadAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.NotFound, result.Status);
        Assert.Equal("File not found", result.Message);
    }

    [Fact]
    public async Task DiffAsync_ChangedFile_ReturnsDiff()
    {
        using var scope = TempDirectoryScope.Create();
        var filePath = Path.Combine(scope.Path, "changed.txt");
        await File.WriteAllTextAsync(filePath, "content", TestContext.Current.CancellationToken);
        var git = new FakeGitCommandService
        {
            DiffResult = new GitDiffResult(true, "diff content", false, null, null)
        };
        var service = CreateService(git);

        var result = await service.DiffAsync(filePath, TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.False(result.Value!.Unchanged);
        Assert.Equal("diff content", result.Value.Diff);
    }

    [Fact]
    public async Task DiffAsync_UnchangedFile_ReturnsOriginalContent()
    {
        using var scope = TempDirectoryScope.Create();
        var filePath = Path.Combine(scope.Path, "unchanged.txt");
        await File.WriteAllTextAsync(filePath, "content", TestContext.Current.CancellationToken);
        var git = new FakeGitCommandService
        {
            DiffResult = new GitDiffResult(true, "", true, "original", null)
        };
        var service = CreateService(git);

        var result = await service.DiffAsync(filePath, TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.True(result.Value!.Unchanged);
        Assert.Equal("original", result.Value.OriginalContent);
    }

    [Fact]
    public async Task DiffAsync_GitFails_ReturnsInvalidRequest()
    {
        using var scope = TempDirectoryScope.Create();
        var filePath = Path.Combine(scope.Path, "changed.txt");
        await File.WriteAllTextAsync(filePath, "content", TestContext.Current.CancellationToken);
        var git = new FakeGitCommandService
        {
            DiffResult = new GitDiffResult(false, "", false, null, "git failed")
        };
        var service = CreateService(git);

        var result = await service.DiffAsync(filePath, TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.InvalidRequest, result.Status);
        Assert.Equal("Git diff failed", result.Message);
        Assert.Equal("git failed", result.Details);
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
                TestContext.Current.CancellationToken);
        }
        else
        {
            await File.WriteAllTextAsync(targetPath, "content", TestContext.Current.CancellationToken);
        }
        var service = CreateService();

        var result = await service.DeleteAsync(targetPath, TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.True(result.Value!.Success);
        Assert.False(File.Exists(targetPath));
        Assert.False(Directory.Exists(targetPath));
    }

    [Fact]
    public async Task DeleteAsync_PathDoesNotExist_ReturnsNotFound()
    {
        var service = CreateService();

        var result = await service.DeleteAsync(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.NotFound, result.Status);
        Assert.Equal("File or directory not found", result.Message);
    }

    [Fact]
    public async Task ResetAsync_Succeeds_ReturnsSuccessfulMutation()
    {
        using var scope = TempDirectoryScope.Create();
        var filePath = Path.Combine(scope.Path, "changed.txt");
        await File.WriteAllTextAsync(filePath, "content", TestContext.Current.CancellationToken);
        var git = new FakeGitCommandService
        {
            ResetResult = new GitResetResult(true, "File reset successfully", null, false)
        };
        var service = CreateService(git);

        var result = await service.ResetAsync(filePath, TestContext.Current.CancellationToken);

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
            ResetResult = new GitResetResult(false, "File has no modifications to reset", null, true)
        };
        var service = CreateService(git);

        var result = await service.ResetAsync(filePath, TestContext.Current.CancellationToken);

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
            ResetResult = new GitResetResult(false, "Git reset failed", "git failed", false)
        };
        var service = CreateService(git);

        var result = await service.ResetAsync(filePath, TestContext.Current.CancellationToken);

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
            ResetResult = new GitResetResult(false, "No changes to reset", null, false)
        };
        var service = CreateService(git);

        var result = await service.ResetAsync(filePath, TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.False(result.Value!.Success);
        Assert.Equal("No changes to reset", result.Value.Message);
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
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(scope.Path, "tmpclaude-target"),
            "content",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(scope.Path, ".hidden", "target.txt"),
            "content",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(scope.Path, "node_modules", "target.txt"),
            "content",
            TestContext.Current.CancellationToken);
        var service = CreateService();

        var result = await service.SearchAsync(
            scope.Path,
            "target",
            limit: 2,
            recursive: true,
            TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        Assert.Collection(
            result.Value!.Results,
            item => AssertSearchEntry(item, "target-dir/", "directory"),
            item => AssertSearchEntry(item, "target-dir/target-a.txt", "file"));
    }

    [Fact]
    public async Task SearchAsync_NonRecursive_ReturnsOnlyDirectMatches()
    {
        using var scope = TempDirectoryScope.Create();
        Directory.CreateDirectory(Path.Combine(scope.Path, "direct-target"));
        Directory.CreateDirectory(Path.Combine(scope.Path, "parent", "nested-target"));
        var service = CreateService();

        var result = await service.SearchAsync(
            scope.Path,
            "target",
            limit: 10,
            recursive: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(FileOperationStatus.Success, result.Status);
        var entry = Assert.Single(result.Value!.Results);
        AssertSearchEntry(entry, "direct-target/", "directory");
    }

    private static FileAppService CreateService(FakeGitCommandService? git = null)
    {
        return new FileAppService(
            git ?? new FakeGitCommandService(),
            NullLogger<FileAppService>.Instance);
    }

    private static void AssertListEntry(
        FileListEntry entry,
        string name,
        string type,
        string? gitStatus)
    {
        Assert.Equal(name, entry.Name);
        Assert.Equal(type, entry.Type);
        Assert.Equal(gitStatus, entry.GitStatus);
    }

    private static void AssertSearchEntry(FileSearchEntry entry, string relativePath, string type)
    {
        Assert.Equal(relativePath, entry.RelativePath);
        Assert.Equal(type, entry.Type);
    }

    private sealed class FakeGitCommandService : IGitCommandService
    {
        public GitChangedFiles? ChangedFiles { get; set; }

        public GitDiffResult DiffResult { get; set; } =
            new(true, "diff", false, null, null);

        public GitResetResult ResetResult { get; set; } =
            new(true, "File reset successfully", null, false);

        public Task<GitChangedFiles?> GetChangedFilesAsync(
            string directory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ChangedFiles);
        }

        public Task<GitDiffResult> GetDiffAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(DiffResult);
        }

        public Task<GitResetResult> ResetFileAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(ResetResult);
        }

        public Task<GitCloneResult> CloneRepositoryAsync(
            string gitAddress,
            string workingDirectory,
            CancellationToken cancellationToken = default)
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
            return new TempDirectoryScope(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "agw-file-app-service-tests",
                Guid.NewGuid().ToString("N")));
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
