using Agw.Files.Application.Storage.Local;
using Agw.Files.Exceptions;

namespace Agw.Files.Tests;

public class LocalFileSystemPathSecurityTests
{
    [Fact]
    public async Task WriteAllTextAsync_RelativePath_WritesUnderRoot()
    {
        using var scope = TempDirectoryScope.Create();
        var fileSystem = new LocalFileSystem(scope.Path);

        await fileSystem.WriteAllTextAsync("src/file.txt", "content", TestContext.Current.CancellationToken);

        Assert.Equal(
            "content",
            await File.ReadAllTextAsync(
                Path.Combine(scope.Path, "src", "file.txt"),
                TestContext.Current.CancellationToken
            )
        );
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("nested/../../outside.txt")]
    public async Task ReadAllTextAsync_TraversalPath_ThrowsPathOutsideRoot(string path)
    {
        using var scope = TempDirectoryScope.Create();
        var fileSystem = new LocalFileSystem(scope.Path);

        var exception = await Assert.ThrowsAsync<AgwFilesException>(() =>
            fileSystem.ReadAllTextAsync(path, TestContext.Current.CancellationToken)
        );

        Assert.Equal(FilesErrorCode.PathOutsideRoot, exception.ErrorCode);
    }

    [Fact]
    public async Task ReadAllTextAsync_AbsolutePath_ThrowsPathOutsideRoot()
    {
        using var scope = TempDirectoryScope.Create();
        var fileSystem = new LocalFileSystem(scope.Path);

        var exception = await Assert.ThrowsAsync<AgwFilesException>(() =>
            fileSystem.ReadAllTextAsync(
                Path.Combine(Path.GetPathRoot(scope.Path)!, "outside.txt"),
                TestContext.Current.CancellationToken
            )
        );

        Assert.Equal(FilesErrorCode.PathOutsideRoot, exception.ErrorCode);
    }

    private sealed class TempDirectoryScope : IDisposable
    {
        private TempDirectoryScope(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public static TempDirectoryScope Create()
        {
            return new TempDirectoryScope(
                System.IO.Path.Combine(
                    System.IO.Path.GetTempPath(),
                    "agw-local-filesystem-tests",
                    Guid.CreateVersion7().ToString("N")
                )
            );
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
