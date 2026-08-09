using Agw.Files.Abstracts;
using Agw.Files.Api;
using Agw.Files.Application.Files;
using Agw.Files.Application.Storage.Local;
using Agw.Files.Services;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Files.Tests;

public class FilesControllerProjectFileSystemTests
{
    [Fact]
    public async Task ReadAsync_WhenProjectIdIsMissing_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = await controller.ReadAsync(Guid.Empty, "file.txt");

        AssertApiResult(result, "Project ID is required");
    }

    [Fact]
    public async Task SearchAsync_WhenProjectIdIsMissing_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = await controller.SearchAsync(Guid.Empty, "", "query");

        AssertApiResult(result, "Project ID is required");
    }

    [Fact]
    public async Task ReadAsync_WhenPathIsMissing_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = await controller.ReadAsync(Guid.CreateVersion7(), "");

        AssertApiResult(result, "Path parameter is required");
    }

    [Fact]
    public async Task ReadAsync_WhenFileDoesNotExist_ReturnsApiResult()
    {
        var controller = CreateController();

        var result = await controller.ReadAsync(Guid.CreateVersion7(), "missing-file.txt");

        AssertApiResult(result, "File not found");
    }

    [Fact]
    public async Task DiffAsync_WhenScopeIsInvalid_ReturnsBadRequestEnvelope()
    {
        var controller = CreateController();

        var result = await controller.DiffAsync(
            Guid.CreateVersion7(),
            "file.txt",
            "invalid");

        AssertApiResult(result, "Scope must be 'staged' or 'unstaged'");
    }

    private static void AssertApiResult(IActionResult result, string expectedTitle)
    {
        Assert.StartsWith("Bens.Results.ApiResult", result.GetType().FullName);
        var title = result.GetType().GetProperty("Title")?.GetValue(result) as string;
        Assert.Contains(expectedTitle, title, StringComparison.Ordinal);
    }

    private static FilesController CreateController()
    {
        var fileAppService = new FileAppService(
            new FakeFileSystemResolver(new LocalFileSystem(Path.GetTempPath())),
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

    private sealed class FakeGitCommandService : IGitCommandService
    {
        public Task<GitChangedFiles?> GetChangedFilesAsync(string directory, CancellationToken cancellationToken = default)
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

        public Task<GitResetResult> ResetFileAsync(string filePath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitResetResult(false, "Not implemented by test fake.", null, false));
        }

        public Task<GitCloneResult> CloneRepositoryAsync(string gitAddress, string workingDirectory, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GitCloneResult(false, "Not implemented by test fake.", null, null));
        }
    }
}
