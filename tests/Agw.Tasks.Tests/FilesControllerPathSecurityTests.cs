using Agw.Shared.Services;
using Agw.Tasks.Application.Files;
using Agw.Tasks.Controllers;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Tasks.Tests;

public class FilesControllerPathSecurityTests
{
    [Fact]
    public async Task ReadAsync_WhenPathIsRejected_ReturnsBadRequest()
    {
        var controller = CreateController(new RejectingPathSecurityService());

        var result = await controller.ReadAsync(Path.Combine("..", "outside.txt"));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Invalid path", badRequest.Value?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_WhenPathIsRejected_ReturnsBadRequest()
    {
        var controller = CreateController(new RejectingPathSecurityService());

        var result = await controller.SearchAsync(Path.Combine("..", "outside"), "query");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Invalid path", badRequest.Value?.ToString(), StringComparison.Ordinal);
    }

    private static FilesController CreateController(IPathSecurityService pathSecurityService)
    {
        return new FilesController(
            NullLogger<FilesController>.Instance,
            new FakeGitCommandService(),
            pathSecurityService);
    }

    private sealed class RejectingPathSecurityService : IPathSecurityService
    {
        public string RootPath => Path.GetTempPath();

        public bool TryResolvePath(string path, out string resolvedPath)
        {
            resolvedPath = string.Empty;
            return false;
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
