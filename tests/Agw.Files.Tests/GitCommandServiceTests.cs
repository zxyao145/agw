using System.Diagnostics;
using Agw.Files.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Files.Tests;

public class GitCommandServiceTests
{
    [Fact]
    public async Task GetChangedFilesAsync_MixedWorkingTree_ReturnsStagedAndUnstagedStatuses()
    {
        using var repository = TempGitRepository.Create();
        repository.Write("staged.txt", "base\n");
        repository.Write("unstaged.txt", "base\n");
        repository.Write("mixed.txt", "base\n");
        repository.CommitAll();

        repository.Write("staged.txt", "staged\n");
        repository.Git("add", "staged.txt");
        repository.Write("unstaged.txt", "unstaged\n");
        repository.Write("mixed.txt", "staged\n");
        repository.Git("add", "mixed.txt");
        repository.Write("mixed.txt", "unstaged\n");
        repository.Write("untracked.txt", "new\n");

        var service = new GitCommandService(NullLogger<GitCommandService>.Instance);
        var result = await service.GetChangedFilesAsync(repository.Path, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        AssertStatus(result, repository, "staged.txt", "modified", null);
        AssertStatus(result, repository, "unstaged.txt", null, "modified");
        AssertStatus(result, repository, "mixed.txt", "modified", "modified");
        AssertStatus(result, repository, "untracked.txt", null, "untracked");
    }

    [Fact]
    public async Task GetDiffAsync_PartiallyStagedFile_ReturnsRequestedScope()
    {
        using var repository = TempGitRepository.Create();
        repository.Write("mixed.txt", "base\n");
        repository.CommitAll();
        repository.Write("mixed.txt", "base\nstaged\n");
        repository.Git("add", "mixed.txt");
        repository.Write("mixed.txt", "base\nstaged\nunstaged\n");

        var service = new GitCommandService(NullLogger<GitCommandService>.Instance);
        var filePath = repository.GetPath("mixed.txt");
        var staged = await service.GetDiffAsync(filePath, TestContext.Current.CancellationToken, GitDiffScope.Staged);
        var unstaged = await service.GetDiffAsync(
            filePath,
            TestContext.Current.CancellationToken,
            GitDiffScope.Unstaged
        );
        var all = await service.GetDiffAsync(filePath, TestContext.Current.CancellationToken);

        Assert.True(staged.Success);
        Assert.Contains("+staged", staged.Diff, StringComparison.Ordinal);
        Assert.DoesNotContain("+unstaged", staged.Diff, StringComparison.Ordinal);
        Assert.True(unstaged.Success);
        Assert.Contains("+unstaged", unstaged.Diff, StringComparison.Ordinal);
        Assert.DoesNotContain("+staged", unstaged.Diff, StringComparison.Ordinal);
        Assert.True(all.Success);
        Assert.Contains("+staged", all.Diff, StringComparison.Ordinal);
        Assert.Contains("+unstaged", all.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetDiffAsync_UntrackedFile_ReturnsDiffAgainstEmptyFile()
    {
        using var repository = TempGitRepository.Create();
        repository.Write("tracked.txt", "base\n");
        repository.CommitAll();
        repository.Write("untracked.txt", "new content\n");

        var service = new GitCommandService(NullLogger<GitCommandService>.Instance);
        var result = await service.GetDiffAsync(
            repository.GetPath("untracked.txt"),
            TestContext.Current.CancellationToken,
            GitDiffScope.Unstaged
        );

        Assert.True(result.Success);
        Assert.Contains("+new content", result.Diff, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetStagedAsync_File_MovesChangesBetweenIndexAndWorkingTree()
    {
        using var repository = TempGitRepository.Create();
        repository.Write("changed.txt", "base\n");
        repository.CommitAll();
        repository.Write("changed.txt", "changed\n");
        var service = new GitCommandService(NullLogger<GitCommandService>.Instance);
        var filePath = repository.GetPath("changed.txt");

        var stageResult = await service.SetStagedAsync(filePath, staged: true, TestContext.Current.CancellationToken);
        var stagedFiles = await service.GetChangedFilesAsync(repository.Path, TestContext.Current.CancellationToken);

        Assert.True(stageResult.Success, $"{stageResult.Message}: {stageResult.Error}");
        Assert.NotNull(stagedFiles);
        AssertStatus(stagedFiles, repository, "changed.txt", "modified", null);

        var unstageResult = await service.SetStagedAsync(
            filePath,
            staged: false,
            TestContext.Current.CancellationToken
        );
        var unstagedFiles = await service.GetChangedFilesAsync(repository.Path, TestContext.Current.CancellationToken);

        Assert.True(unstageResult.Success);
        Assert.NotNull(unstagedFiles);
        AssertStatus(unstagedFiles, repository, "changed.txt", null, "modified");
    }

    [Fact]
    public async Task SetStagedAsync_Directory_MovesAllDescendantChanges()
    {
        using var repository = TempGitRepository.Create();
        Directory.CreateDirectory(repository.GetPath("src"));
        repository.Write("src/first.txt", "base\n");
        repository.Write("src/second.txt", "base\n");
        repository.CommitAll();
        repository.Write("src/first.txt", "first change\n");
        repository.Write("src/second.txt", "second change\n");
        var service = new GitCommandService(NullLogger<GitCommandService>.Instance);

        var result = await service.SetStagedAsync(
            repository.GetPath("src"),
            staged: true,
            TestContext.Current.CancellationToken
        );
        var changedFiles = await service.GetChangedFilesAsync(repository.Path, TestContext.Current.CancellationToken);

        Assert.True(result.Success, $"{result.Message}: {result.Error}");
        Assert.NotNull(changedFiles);
        AssertStatus(changedFiles, repository, "src/first.txt", "modified", null);
        AssertStatus(changedFiles, repository, "src/second.txt", "modified", null);

        var unstageResult = await service.SetStagedAsync(
            repository.GetPath("src"),
            staged: false,
            TestContext.Current.CancellationToken
        );
        var unstagedFiles = await service.GetChangedFilesAsync(repository.Path, TestContext.Current.CancellationToken);

        Assert.True(unstageResult.Success, $"{unstageResult.Message}: {unstageResult.Error}");
        Assert.NotNull(unstagedFiles);
        AssertStatus(unstagedFiles, repository, "src/first.txt", null, "modified");
        AssertStatus(unstagedFiles, repository, "src/second.txt", null, "modified");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SetStagedAsync_MissingPath_ReturnsClientError(bool staged)
    {
        using var repository = TempGitRepository.Create();
        repository.Write("tracked.txt", "content\n");
        repository.CommitAll();
        var service = new GitCommandService(NullLogger<GitCommandService>.Instance);

        var result = await service.SetStagedAsync(
            repository.GetPath("missing.txt"),
            staged,
            TestContext.Current.CancellationToken
        );

        Assert.False(result.Success);
        Assert.True(result.IsClientError);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task SetStagedAsync_IndexLockFailure_RemainsServerError()
    {
        using var repository = TempGitRepository.Create();
        repository.Write("changed.txt", "base\n");
        repository.CommitAll();
        repository.Write("changed.txt", "changed\n");
        await File.WriteAllTextAsync(
            repository.GetPath(".git/index.lock"),
            "locked",
            TestContext.Current.CancellationToken
        );
        var service = new GitCommandService(NullLogger<GitCommandService>.Instance);

        var result = await service.SetStagedAsync(
            repository.GetPath("changed.txt"),
            staged: true,
            TestContext.Current.CancellationToken
        );

        Assert.False(result.Success);
        Assert.False(result.IsClientError);
        Assert.Contains("index.lock", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertStatus(
        GitChangedFiles changedFiles,
        TempGitRepository repository,
        string relativePath,
        string? stagedStatus,
        string? unstagedStatus
    )
    {
        var status = changedFiles.FileStatuses[repository.GetPath(relativePath)];
        Assert.Equal(stagedStatus, status.StagedStatus);
        Assert.Equal(unstagedStatus, status.UnstagedStatus);
    }

    private sealed class TempGitRepository : IDisposable
    {
        private TempGitRepository(string path)
        {
            Path = path;
        }

        public string Path { get; }

        public static TempGitRepository Create()
        {
            var path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "agw-git-command-tests",
                Guid.CreateVersion7().ToString("N")
            );
            Directory.CreateDirectory(path);
            var repository = new TempGitRepository(path);
            repository.Git("init");
            repository.Git("config", "user.email", "tests@agw.local");
            repository.Git("config", "user.name", "Agw Tests");
            return repository;
        }

        public string GetPath(string relativePath) => System.IO.Path.Combine(Path, relativePath);

        public void Write(string relativePath, string content)
        {
            File.WriteAllText(GetPath(relativePath), content);
        }

        public void CommitAll()
        {
            Git("add", ".");
            Git("commit", "-m", "test fixture");
        }

        public void Git(params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = Path,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start git");
            var standardOutput = process.StandardOutput.ReadToEnd();
            var standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(
                process.ExitCode == 0,
                $"git {string.Join(' ', arguments)} failed: {standardError}{standardOutput}"
            );
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
