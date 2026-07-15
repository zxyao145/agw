using Agw.Files.Api;
using Agw.Files.Api.Dtos;
using Agw.Files.Application.Files;
using Agw.Files.Services;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Files.Tests;

public class FilesControllerSearchTests
{
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

            var result = await controller.SearchAsync(rootPath, keyword, recursive: true);

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

            var result = await controller.SearchAsync(rootPath, "target", recursive: false);

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
            new FakeGitCommandService(),
            NullLogger<FileAppService>.Instance);
        return new FilesController(
            fileAppService,
            new FilePathRequestValidator(rootPath, Array.Empty<string>()));
    }

    private static FileSearchResponse GetResponse(IActionResult result)
    {
        var okResult = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<FileSearchResponse>(okResult.Value);
    }

    private static void AssertResult(FileSearchResult result, string relativePath, string type)
    {
        Assert.Equal(relativePath, result.RelativePath);
        Assert.Equal(type, result.Type);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "agw-files-search-tests", Guid.NewGuid().ToString("N"));
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
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitDiffResult(false, "", false, null, null));
        }

        public Task<GitResetResult> ResetFileAsync(
            string filePath,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitResetResult(false, "Not implemented by test fake.", null, false));
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
