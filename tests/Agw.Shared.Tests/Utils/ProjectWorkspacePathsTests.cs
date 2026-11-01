using Agw.Shared.Runtime;
using Agw.Shared.Utils;

namespace Agw.Shared.Tests.Utils;

public sealed class ProjectWorkspacePathsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        AppContext.BaseDirectory,
        ".scratch",
        "project-workspace-paths",
        Guid.CreateVersion7().ToString("N")
    );

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("additional")]
    public void EnsureCreated_MissingDirectory_CreatesDirectoryAndParents(string kind)
    {
        // Arrange
        var path = Path.Combine(_root, kind, "parent", "target");

        // Act
        var created = ProjectWorkspacePaths.EnsureCreated(path + Path.DirectorySeparatorChar);

        // Assert
        Assert.Equal(path, created);
        Assert.True(Directory.Exists(path));
    }

    [Theory]
    [InlineData("primary")]
    [InlineData("additional")]
    public void EnsureCreated_ExistingDirectory_PreservesFiles(string kind)
    {
        // Arrange
        var path = Path.Combine(_root, kind);
        Directory.CreateDirectory(Path.Combine(path, "nested"));
        File.WriteAllText(Path.Combine(path, "nested", "keep.txt"), "kept");

        // Act
        ProjectWorkspacePaths.EnsureCreated(path);
        ProjectWorkspacePaths.EnsureCreated(path);

        // Assert
        Assert.Equal("kept", File.ReadAllText(Path.Combine(path, "nested", "keep.txt")));
        Assert.Single(Directory.EnumerateFileSystemEntries(path));
    }

    [Fact]
    public void EnsureCreated_FileOccupiesPath_FailsWithoutChangingFile()
    {
        // Arrange
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "occupied");
        File.WriteAllText(path, "file content");

        // Act
        Assert.ThrowsAny<IOException>(() => ProjectWorkspacePaths.EnsureCreated(path));

        // Assert
        Assert.True(File.Exists(path));
        Assert.Equal("file content", File.ReadAllText(path));
    }

    [Fact]
    public void EnsureAvailable_CustomWorkspaceMissing_CreatesPrimaryDirectory()
    {
        // Arrange
        var workspace = Path.Combine(_root, "custom", "workspace");
        var snapshot = ProjectWorkspacePaths.CreateSnapshot(Guid.CreateVersion7(), workspace);

        // Act
        ProjectWorkspacePaths.EnsureAvailable(snapshot);

        // Assert
        Assert.True(Directory.Exists(workspace));
    }

    [Fact]
    public void EnsureAvailable_AdditionalDirectoriesMissing_CreatesAllDirectories()
    {
        // Arrange
        var workspace = Path.Combine(_root, "workspace");
        var first = Path.Combine(_root, "additional", "first");
        var second = Path.Combine(_root, "additional", "deep", "second");
        var snapshot = ProjectWorkspacePaths.CreateSnapshot(
            Guid.CreateVersion7(),
            workspace,
            [
                new ProjectWorkspaceDirectory(Guid.CreateVersion7(), first),
                new ProjectWorkspaceDirectory(Guid.CreateVersion7(), second),
            ]
        );

        // Act
        ProjectWorkspacePaths.EnsureAvailable(snapshot);

        // Assert
        Assert.True(Directory.Exists(workspace));
        Assert.True(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
    }

    [Fact]
    public void EnsureAvailable_AdditionalDirectoryOccupiedByFile_FailsWithoutChangingFile()
    {
        // Arrange
        var workspace = Path.Combine(_root, "workspace");
        var occupied = Path.Combine(_root, "occupied");
        Directory.CreateDirectory(_root);
        File.WriteAllText(occupied, "file content");
        var snapshot = ProjectWorkspacePaths.CreateSnapshot(
            Guid.CreateVersion7(),
            workspace,
            [new ProjectWorkspaceDirectory(Guid.CreateVersion7(), occupied)]
        );

        // Act
        Assert.ThrowsAny<IOException>(() => ProjectWorkspacePaths.EnsureAvailable(snapshot));

        // Assert
        Assert.True(File.Exists(occupied));
        Assert.Equal("file content", File.ReadAllText(occupied));
    }
}
