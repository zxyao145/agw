using Agw.Files.Application.Files;

namespace Agw.Files.Tests;

public class FilePathRequestValidatorPathSecurityTests
{
    [Fact]
    public void ValidateRequiredPath_WhenPathIsRoot_AllowsRoot()
    {
        using var scope = TempPathScope.Create();
        var validator = CreateRootOnlyValidator(scope.RootPath);

        var result = validator.ValidateRequiredPath(scope.RootPath);

        Assert.True(result.IsValid);
        Assert.Equal(Path.GetFullPath(scope.RootPath), result.ResolvedPath);
    }

    [Fact]
    public void ValidateRequiredPath_WhenPathIsChild_AllowsChild()
    {
        using var scope = TempPathScope.Create();
        var childPath = Path.Combine(scope.RootPath, "src", "file.txt");
        var validator = CreateRootOnlyValidator(scope.RootPath);

        var result = validator.ValidateRequiredPath(childPath);

        Assert.True(result.IsValid);
        Assert.Equal(Path.GetFullPath(childPath), result.ResolvedPath);
    }

    [Fact]
    public void ValidateRequiredPath_WhenPathIsRelativeChild_AllowsChildUnderRoot()
    {
        using var scope = TempPathScope.Create();
        var validator = CreateRootOnlyValidator(scope.RootPath);

        var result = validator.ValidateRequiredPath(Path.Combine("src", "file.txt"));

        Assert.True(result.IsValid);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(scope.RootPath, "src", "file.txt")),
            result.ResolvedPath);
    }

    [Fact]
    public void ValidateRequiredPath_WhenPathIsUnderUserProfile_AllowsPath()
    {
        using var scope = TempPathScope.Create();
        var validator = new FilePathRequestValidator(scope.RootPath);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrWhiteSpace(userProfile));
        var userPath = Path.Combine(userProfile, "agw-path-security-tests", "file.txt");

        var result = validator.ValidateRequiredPath(userPath);

        Assert.True(result.IsValid);
        Assert.Equal(Path.GetFullPath(userPath), result.ResolvedPath);
    }

    [Fact]
    public void ValidateRequiredPath_WhenTildePathIsUnderUserProfile_AllowsPath()
    {
        using var scope = TempPathScope.Create();
        var validator = new FilePathRequestValidator(scope.RootPath);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.False(string.IsNullOrWhiteSpace(userProfile));
        var tildePath = Path.Combine("~", "agw-path-security-tests", "file.txt");
        var expectedPath = Path.Combine(userProfile, "agw-path-security-tests", "file.txt");

        var result = validator.ValidateRequiredPath(tildePath);

        Assert.True(result.IsValid);
        Assert.Equal(Path.GetFullPath(expectedPath), result.ResolvedPath);
    }

    [Fact]
    public void ValidateRequiredPath_WhenPathIsAbsoluteSibling_RejectsPath()
    {
        using var scope = TempPathScope.Create();
        var validator = CreateRootOnlyValidator(scope.RootPath);
        var sibling = Path.Combine(scope.ParentPath, $"{Path.GetFileName(scope.RootPath)}-outside", "file.txt");

        var result = validator.ValidateRequiredPath(sibling);

        Assert.False(result.IsValid);
        Assert.Equal(string.Empty, result.ResolvedPath);
    }

    [Fact]
    public void ValidateRequiredPath_WhenPathTraversesToSibling_RejectsPath()
    {
        using var scope = TempPathScope.Create();
        var validator = CreateRootOnlyValidator(scope.RootPath);
        var relativeTraversal = Path.Combine("..", $"{Path.GetFileName(scope.RootPath)}-outside", "file.txt");

        var result = validator.ValidateRequiredPath(relativeTraversal);

        Assert.False(result.IsValid);
        Assert.Equal(string.Empty, result.ResolvedPath);
    }

    [Fact]
    public void ValidateRequiredPath_WhenSiblingSharesRootPrefix_RejectsPath()
    {
        using var scope = TempPathScope.Create();
        var validator = CreateRootOnlyValidator(scope.RootPath);
        var prefixedSibling = scope.RootPath + "-prefixed";

        var result = validator.ValidateRequiredPath(prefixedSibling);

        Assert.False(result.IsValid);
        Assert.Equal(string.Empty, result.ResolvedPath);
    }

    [Fact]
    public void ValidateRequiredPath_WhenPathIsUnderAdditionalRoot_AllowsPath()
    {
        using var scope = TempPathScope.Create();
        var additionalRootPath = Path.Combine(scope.ParentPath, $"{Path.GetFileName(scope.RootPath)}-additional");
        var validator = new FilePathRequestValidator(scope.RootPath, additionalRootPath);
        var additionalRootChild = Path.Combine(additionalRootPath, "file.txt");

        var result = validator.ValidateRequiredPath(additionalRootChild);

        Assert.True(result.IsValid);
        Assert.Equal(Path.GetFullPath(additionalRootChild), result.ResolvedPath);
    }

    [Fact]
    public void ValidateRequiredPath_WhenPathSharesAdditionalRootPrefix_RejectsPath()
    {
        using var scope = TempPathScope.Create();
        var additionalRootPath = Path.Combine(scope.ParentPath, $"{Path.GetFileName(scope.RootPath)}-additional");
        var validator = new FilePathRequestValidator(scope.RootPath, additionalRootPath);
        var prefixedSibling = additionalRootPath + "-prefixed";

        var result = validator.ValidateRequiredPath(prefixedSibling);

        Assert.False(result.IsValid);
        Assert.Equal(string.Empty, result.ResolvedPath);
    }

    private static FilePathRequestValidator CreateRootOnlyValidator(string rootPath)
    {
        return new FilePathRequestValidator(rootPath, Array.Empty<string>());
    }

    private sealed class TempPathScope : IDisposable
    {
        private TempPathScope(string rootPath)
        {
            RootPath = rootPath;
            ParentPath = Directory.GetParent(rootPath)?.FullName
                ?? throw new InvalidOperationException("Temporary root must have a parent directory.");
            Directory.CreateDirectory(rootPath);
        }

        public string RootPath { get; }

        public string ParentPath { get; }

        public static TempPathScope Create()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "agw-path-security-tests", Guid.NewGuid().ToString("N"));
            return new TempPathScope(rootPath);
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, recursive: true);
            }
        }
    }
}
