using Agw.Files.Abstracts;
using Agw.Files.Api;
using Agw.Files.Api.Dtos;
using Agw.Files.Application.Files;
using Agw.Files.Application.Storage.Local;
using Agw.Files.Services;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Files.Tests;

public class FilesControllerSearchTests
{
    private static readonly Guid ProjectId = Guid.Parse("bf532c35-3069-4cae-83dc-eb22ebd43c10");

    [Theory]
    [InlineData("a")]
    [InlineData("abc/")]
    public async Task SearchAsync_Recursive_WhenFlattenedPathMatches_ReturnsDirectoryAndDescendants(
        string keyword)
    {
        var rootPath = CreateTempDirectory();
        try
        {
            var matchingDirectory = Path.Combine(rootPath, "demo", "abc");
            Directory.CreateDirectory(matchingDirectory);
            await File.WriteAllTextAsync(
                Path.Combine(matchingDirectory, "d.txt"),
                "content",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(matchingDirectory, "e.log"),
                "content",
                TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(rootPath, "demo", "x.txt"),
                "content",
                TestContext.Current.CancellationToken);
            var controller = CreateController(rootPath);

            var result = await controller.SearchAsync(
                ProjectId,
                "",
                keyword,
                recursive: true);

            var response = GetResponse(result);
            Assert.Collection(
                response.Results,
                item => AssertResult(item, "demo/abc/", "directory"),
                item => AssertResult(item, "demo/abc/d.txt", "file"),
                item => AssertResult(item, "demo/abc/e.log", "file"));
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task SearchAsync_NonRecursive_WhenDirectoryNameMatches_ReturnsOnlyDirectMatches()
    {
        var rootPath = CreateTempDirectory();
        try
        {
            Directory.CreateDirectory(Path.Combine(rootPath, "direct-target"));
            Directory.CreateDirectory(Path.Combine(rootPath, "parent", "nested-target"));
            var controller = CreateController(rootPath);

            var result = await controller.SearchAsync(
                ProjectId,
                "",
                "target",
                recursive: false);

            var response = GetResponse(result);
            var directory = Assert.Single(response.Results);
            AssertResult(directory, "direct-target/", "directory");
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static FilesController CreateController(string rootPath)
    {
        var fileAppService = new FileAppService(
            new FakeFileSystemResolver(new LocalFileSystem(rootPath)),
            new FakeGitCommandService(),
            NullLogger<FileAppService>.Instance);
        return new FilesController(fileAppService);
    }

    private sealed class FakeFileSystemResolver : IAgwFileSystemResolver
    {
        private readonly IAgwFileSystem _fileSystem;

        public FakeFileSystemResolver(IAgwFileSystem fileSystem)
        {
            _fileSystem = fileSystem;
        }

        public Task<IAgwFileSystem> ResolveAsync(Guid projectId, CancellationToken ct)
        {
            return Task.FromResult(_fileSystem);
        }
    }

    private static FileSearchResponse GetResponse(IActionResult result)
    {
        Assert.StartsWith("Bens.Results.ApiResult", result.GetType().FullName);
        var data = result.GetType().GetProperty("Data")?.GetValue(result);
        return Assert.IsType<FileSearchResponse>(data);
    }

    private static void AssertResult(FileSearchResult result, string relativePath, string type)
    {
        Assert.Equal(relativePath, result.RelativePath);
        Assert.Equal(type, result.Type);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "agw-files-search-tests", Guid.CreateVersion7().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FakeGitCommandService : IGitCommandService
    {
        public Task<GitChangedFiles?> GetChangedFilesAsync(
            string directory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult<GitChangedFiles?>(null);
        }

        public Task<GitDiffResult> GetDiffAsync(
            string filePath,
            CancellationToken cancellationToken = default,
            GitDiffScope scope = GitDiffScope.All)
        {
            return Task.FromResult(new GitDiffResult(false, "", false, null, null));
        }

        public Task<GitResetResult> ResetFileAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitResetResult(false, "Not implemented by test fake.", null, false));
        }

        public Task<GitIndexResult> SetStagedAsync(
            string path,
            bool staged,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitIndexResult(false, "Not implemented by test fake.", null, false));
        }

        public Task<GitCloneResult> CloneRepositoryAsync(
            string gitAddress,
            string workingDirectory,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitCloneResult(false, "Not implemented by test fake.", null, null));
        }
    }
}
