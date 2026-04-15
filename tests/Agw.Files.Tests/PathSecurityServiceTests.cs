using Agw.Files.Application.Files;

namespace Agw.Files.Tests;

public class PathSecurityServiceTests
{
    [Fact]
    public void TryResolvePath_WhenPathIsRoot_AllowsRoot()
    {
        using var scope = TempPathScope.Create();
        var service = new PathSecurityService(scope.RootPath);

        var allowed = service.TryResolvePath(scope.RootPath, out var resolvedPath);

        Assert.True(allowed);
        Assert.Equal(Path.GetFullPath(scope.RootPath), resolvedPath);
    }

    [Fact]
    public void TryResolvePath_WhenPathIsChild_AllowsChild()
    {
        using var scope = TempPathScope.Create();
        var childPath = Path.Combine(scope.RootPath, "src", "file.txt");
        var service = new PathSecurityService(scope.RootPath);

        var allowed = service.TryResolvePath(childPath, out var resolvedPath);

        Assert.True(allowed);
        Assert.Equal(Path.GetFullPath(childPath), resolvedPath);
    }

    [Fact]
    public void TryResolvePath_WhenPathIsRelativeChild_AllowsChildUnderRoot()
    {
        using var scope = TempPathScope.Create();
        var service = new PathSecurityService(scope.RootPath);

        var allowed = service.TryResolvePath(Path.Combine("src", "file.txt"), out var resolvedPath);

        Assert.True(allowed);
        Assert.Equal(Path.GetFullPath(Path.Combine(scope.RootPath, "src", "file.txt")), resolvedPath);
    }

    [Fact]
    public void TryResolvePath_WhenPathIsAbsoluteSibling_RejectsPath()
    {
        using var scope = TempPathScope.Create();
        var service = new PathSecurityService(scope.RootPath);
        var sibling = Path.Combine(scope.ParentPath, $"{Path.GetFileName(scope.RootPath)}-outside", "file.txt");

        var allowed = service.TryResolvePath(sibling, out var resolvedPath);

        Assert.False(allowed);
        Assert.Equal(string.Empty, resolvedPath);
    }

    [Fact]
    public void TryResolvePath_WhenPathTraversesToSibling_RejectsPath()
    {
        using var scope = TempPathScope.Create();
        var service = new PathSecurityService(scope.RootPath);
        var relativeTraversal = Path.Combine("..", $"{Path.GetFileName(scope.RootPath)}-outside", "file.txt");

        var allowed = service.TryResolvePath(relativeTraversal, out var resolvedPath);

        Assert.False(allowed);
        Assert.Equal(string.Empty, resolvedPath);
    }

    [Fact]
    public void TryResolvePath_WhenSiblingSharesRootPrefix_RejectsPath()
    {
        using var scope = TempPathScope.Create();
        var service = new PathSecurityService(scope.RootPath);
        var prefixedSibling = scope.RootPath + "-prefixed";

        var allowed = service.TryResolvePath(prefixedSibling, out var resolvedPath);

        Assert.False(allowed);
        Assert.Equal(string.Empty, resolvedPath);
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
