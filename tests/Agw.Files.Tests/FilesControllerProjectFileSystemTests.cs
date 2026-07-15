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

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Project ID is required", badRequest.Value?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_WhenProjectIdIsMissing_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = await controller.SearchAsync(Guid.Empty, "", "query");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Project ID is required", badRequest.Value?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAsync_WhenPathIsMissing_ReturnsBadRequest()
    {
        var controller = CreateController();

        var result = await controller.ReadAsync(Guid.NewGuid(), "");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Path parameter is required", badRequest.Value?.ToString(), StringComparison.Ordinal);
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

        public Task<GitDiffResult> GetDiffAsync(string filePath, CancellationToken cancellationToken = default)
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
