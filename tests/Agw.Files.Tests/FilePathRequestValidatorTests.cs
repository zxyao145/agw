using Agw.Files.Application.Files;

namespace Agw.Files.Tests;

public class FilePathRequestValidatorTests
{
    [Fact]
    public void ValidateRequiredPath_WhenPathIsMissing_ReturnsRequiredError()
    {
        var validator = new FilePathRequestValidator(
            Directory.GetCurrentDirectory(),
            Array.Empty<string>());

        var result = validator.ValidateRequiredPath(" ");

        Assert.False(result.IsValid);
        Assert.Equal("Path parameter is required", result.ErrorMessage);
        Assert.Equal(string.Empty, result.ResolvedPath);
    }

    [Fact]
    public void ValidateRequiredPath_WhenPathIsRejected_ReturnsInvalidPathError()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var validator = new FilePathRequestValidator(rootPath, Array.Empty<string>());

        var result = validator.ValidateRequiredPath(Path.Combine(rootPath, "..", "outside.txt"));

        Assert.False(result.IsValid);
        Assert.Equal("Invalid path", result.ErrorMessage);
        Assert.Equal(string.Empty, result.ResolvedPath);
    }

    [Fact]
    public void ValidateRequiredPath_WhenPathIsAccepted_ReturnsResolvedPath()
    {
        var rootPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var validator = new FilePathRequestValidator(rootPath, Array.Empty<string>());

        var result = validator.ValidateRequiredPath("inside.txt");

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorMessage);
        Assert.Equal(Path.GetFullPath(Path.Combine(rootPath, "inside.txt")), result.ResolvedPath);
    }
}
