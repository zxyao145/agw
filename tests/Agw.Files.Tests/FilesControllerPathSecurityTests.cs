using Agw.Files.Api;
using Agw.Files.Application.Files;
using Agw.Files.Services;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agw.Files.Tests;

public class FilesControllerPathSecurityTests
{
    [Fact]
    public async Task ReadAsync_WhenPathIsRejected_ReturnsBadRequest()
    {
        var controller = CreateController(new RejectingFilePathRequestValidator());

        var result = await controller.ReadAsync(Path.Combine("..", "outside.txt"));

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Invalid path", badRequest.Value?.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAsync_WhenPathIsRejected_ReturnsBadRequest()
    {
        var controller = CreateController(new RejectingFilePathRequestValidator());

        var result = await controller.SearchAsync(Path.Combine("..", "outside"), "query");

        var badRequest = Assert.IsType<BadRequestObjectResult>(result);
        Assert.Contains("Invalid path", badRequest.Value?.ToString(), StringComparison.Ordinal);
    }

    private static FilesController CreateController(IFilePathRequestValidator pathValidator)
    {
        var fileAppService = new FileAppService(
            new FakeGitCommandService(),
            NullLogger<FileAppService>.Instance);
        return new FilesController(
            fileAppService,
            pathValidator);
    }

    private sealed class RejectingFilePathRequestValidator : IFilePathRequestValidator
    {
        public FilePathRequestValidationResult ValidateRequiredPath(string? path)
        {
            return FilePathRequestValidationResult.Error("Invalid path");
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
