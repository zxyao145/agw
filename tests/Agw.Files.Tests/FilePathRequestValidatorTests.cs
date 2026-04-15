using Agw.Files.Application.Files;

namespace Agw.Files.Tests;

public class FilePathRequestValidatorTests
{
    [Fact]
    public void ValidateRequiredPath_WhenPathIsMissing_ReturnsRequiredError()
    {
        var validator = new FilePathRequestValidator(new AcceptingPathSecurityService());

        var result = validator.ValidateRequiredPath(" ");

        Assert.False(result.IsValid);
        Assert.Equal("Path parameter is required", result.ErrorMessage);
        Assert.Equal(string.Empty, result.ResolvedPath);
    }

    [Fact]
    public void ValidateRequiredPath_WhenPathIsRejected_ReturnsInvalidPathError()
    {
        var validator = new FilePathRequestValidator(new RejectingPathSecurityService());

        var result = validator.ValidateRequiredPath(Path.Combine("..", "outside.txt"));

        Assert.False(result.IsValid);
        Assert.Equal("Invalid path", result.ErrorMessage);
        Assert.Equal(string.Empty, result.ResolvedPath);
    }

    [Fact]
    public void ValidateRequiredPath_WhenPathIsAccepted_ReturnsResolvedPath()
    {
        var validator = new FilePathRequestValidator(new AcceptingPathSecurityService());

        var result = validator.ValidateRequiredPath("inside.txt");

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(Path.GetFullPath("inside.txt"), result.ResolvedPath);
    }

    private sealed class AcceptingPathSecurityService : IPathSecurityService
    {
        public string RootPath => Directory.GetCurrentDirectory();

        public bool TryResolvePath(string path, out string resolvedPath)
        {
            resolvedPath = Path.GetFullPath(path);
            return true;
        }
    }

    private sealed class RejectingPathSecurityService : IPathSecurityService
    {
        public string RootPath => Directory.GetCurrentDirectory();

        public bool TryResolvePath(string path, out string resolvedPath)
        {
            resolvedPath = string.Empty;
            return false;
        }
    }
}
