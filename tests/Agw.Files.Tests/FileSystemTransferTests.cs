using Agw.Files.Abstracts;
using Agw.Files.Infrastructure.Storage;
using Agw.Shared.Exceptions;

namespace Agw.Files.Tests;

/// <summary>
/// 词法文件系统（LocalFileSystem）与当前平台受限文件系统的读取流、写入、移动与非递归删除行为。
/// Stream reads, writes, moves and non-recursive deletes of the lexical file system (LocalFileSystem) and the confined
/// file system of the current platform.
/// </summary>
public sealed class FileSystemTransferTests : IDisposable
{
    private static readonly byte[] BinaryContent = [0x00, 0x01, 0x7F, 0x80, 0xFE, 0xFF, 0x0A, 0x0D];

    private readonly string _root = Directory
        .CreateDirectory(
            Path.Combine(AppContext.BaseDirectory, "file-system-transfer", Guid.CreateVersion7().ToString("N"))
        )
        .FullName;

    private string Workspace => Path.Combine(_root, "workspace");
    private string Outside => Path.Combine(_root, "outside");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public FileSystemTransferTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Outside);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenReadAsync_ExistingFile_ReturnsSeekableStreamWithContent(bool confined)
    {
        await File.WriteAllBytesAsync(Path.Combine(Workspace, "data.bin"), BinaryContent, Token);
        var fileSystem = Create(confined);

        await using var stream = await fileSystem.OpenReadAsync("data.bin", Token);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Token);

        Assert.True(stream.CanSeek);
        Assert.Equal(BinaryContent.Length, stream.Length);
        Assert.Equal(BinaryContent, copy.ToArray());
    }

    [Theory]
    [InlineData(false, "missing.bin")]
    [InlineData(false, "directory")]
    [InlineData(true, "missing.bin")]
    [InlineData(true, "directory")]
    public async Task OpenReadAsync_MissingFileOrDirectory_ThrowsFileNotFound(bool confined, string path)
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "directory"));
        var fileSystem = Create(confined);

        var exception = await Assert.ThrowsAsync<AgwException>(() => fileSystem.OpenReadAsync(path, Token));

        Assert.Equal(ErrorCodes.FileNotFound.Code, exception.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpenReadAsync_PathOutsideRoot_ThrowsFilePathOutsideRoot(bool confined)
    {
        await File.WriteAllTextAsync(Path.Combine(Outside, "secret.txt"), "secret", Token);
        var fileSystem = Create(confined);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.OpenReadAsync("../outside/secret.txt", Token)
        );

        Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
    }

    [Fact]
    public async Task OpenReadAsync_ConfinedFileSymlinkOutsideRoot_ThrowsFilePathOutsideRoot()
    {
        await File.WriteAllTextAsync(Path.Combine(Outside, "secret.txt"), "secret", Token);
        File.CreateSymbolicLink(Path.Combine(Workspace, "link.txt"), Path.Combine(Outside, "secret.txt"));
        var fileSystem = Create(confined: true);

        var exception = await Assert.ThrowsAsync<AgwException>(() => fileSystem.OpenReadAsync("link.txt", Token));

        Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
    }

    [Fact]
    public async Task OpenReadAsync_ConfinedSymlinkIntoAdditionalRoot_ReturnsContent()
    {
        var additional = Directory.CreateDirectory(Path.Combine(_root, "additional")).FullName;
        await File.WriteAllBytesAsync(Path.Combine(additional, "shared.bin"), BinaryContent, Token);
        Directory.CreateSymbolicLink(Path.Combine(Workspace, "link"), additional);
        var fileSystem = Create(confined: true, additional);

        await using var stream = await fileSystem.OpenReadAsync("link/shared.bin", Token);
        using var copy = new MemoryStream();
        await stream.CopyToAsync(copy, Token);

        Assert.Equal(BinaryContent, copy.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_NewFileWithCreateParents_CreatesParentsAndWritesContent(bool confined)
    {
        var fileSystem = Create(confined);

        await fileSystem.WriteAsync("nested/deeper/data.bin", new MemoryStream(BinaryContent), false, true, Token);

        Assert.Equal(
            BinaryContent,
            await File.ReadAllBytesAsync(Path.Combine(Workspace, "nested", "deeper", "data.bin"), Token)
        );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_ExistingFileWithOverwrite_ReplacesWholeContent(bool confined)
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "notes.md"), "a much longer original text", Token);
        var fileSystem = Create(confined);

        await fileSystem.WriteAsync("notes.md", new MemoryStream("short"u8.ToArray()), true, false, Token);

        Assert.Equal("short", await File.ReadAllTextAsync(Path.Combine(Workspace, "notes.md"), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_ExistingFileWithoutOverwrite_ThrowsFileAlreadyExistsAndKeepsContent(bool confined)
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "notes.md"), "original", Token);
        var fileSystem = Create(confined);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.WriteAsync("notes.md", new MemoryStream("replaced"u8.ToArray()), false, false, Token)
        );

        Assert.Equal(ErrorCodes.FileAlreadyExists.Code, exception.Code);
        Assert.Equal("original", await File.ReadAllTextAsync(Path.Combine(Workspace, "notes.md"), Token));
    }

    [Theory]
    [InlineData(false, false, 400_0040)]
    [InlineData(false, true, 400_0001)]
    [InlineData(true, false, 400_0040)]
    [InlineData(true, true, 400_0001)]
    public async Task WriteAsync_DirectoryTarget_ThrowsAndKeepsDirectory(bool confined, bool overwrite, int code)
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "folder"));
        var fileSystem = Create(confined);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.WriteAsync("folder", new MemoryStream(BinaryContent), overwrite, false, Token)
        );

        Assert.Equal(code, exception.Code);
        Assert.True(Directory.Exists(Path.Combine(Workspace, "folder")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_MissingParentWithoutCreateParents_ThrowsDirectoryNotFoundAndCreatesNothing(
        bool confined
    )
    {
        var fileSystem = Create(confined);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.WriteAsync("missing/data.bin", new MemoryStream(BinaryContent), true, false, Token)
        );

        Assert.Equal(ErrorCodes.DirectoryNotFound.Code, exception.Code);
        Assert.False(Directory.Exists(Path.Combine(Workspace, "missing")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_PathOutsideRoot_ThrowsFilePathOutsideRoot(bool confined)
    {
        var fileSystem = Create(confined);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.WriteAsync("../outside/escaped.bin", new MemoryStream(BinaryContent), true, true, Token)
        );

        Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
        Assert.False(File.Exists(Path.Combine(Outside, "escaped.bin")));
    }

    [Fact]
    public async Task WriteAsync_ConfinedDirectorySymlinkOutsideRoot_ThrowsAndCreatesNothing()
    {
        Directory.CreateSymbolicLink(Path.Combine(Workspace, "link"), Outside);
        var fileSystem = Create(confined: true);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.WriteAsync("link/nested/escaped.bin", new MemoryStream(BinaryContent), true, true, Token)
        );

        Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Outside));
    }

    [Fact]
    public async Task WriteAsync_ConfinedOverwriteThroughFileSymlinkOutsideRoot_ThrowsAndKeepsOutsideFile()
    {
        await File.WriteAllTextAsync(Path.Combine(Outside, "secret.txt"), "secret", Token);
        File.CreateSymbolicLink(Path.Combine(Workspace, "link.txt"), Path.Combine(Outside, "secret.txt"));
        var fileSystem = Create(confined: true);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.WriteAsync("link.txt", new MemoryStream("payload"u8.ToArray()), true, false, Token)
        );

        Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
        Assert.Equal("secret", await File.ReadAllTextAsync(Path.Combine(Outside, "secret.txt"), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveAsync_FileToExistingDirectory_MovesFile(bool confined)
    {
        await File.WriteAllBytesAsync(Path.Combine(Workspace, "a.bin"), BinaryContent, Token);
        Directory.CreateDirectory(Path.Combine(Workspace, "archive"));
        var fileSystem = Create(confined);

        await fileSystem.MoveAsync("a.bin", "archive/b.bin", false, Token);

        Assert.False(File.Exists(Path.Combine(Workspace, "a.bin")));
        Assert.Equal(BinaryContent, await File.ReadAllBytesAsync(Path.Combine(Workspace, "archive", "b.bin"), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveAsync_Directory_MovesWholeTree(bool confined)
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "tree", "leaf"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "tree", "leaf", "file.txt"), "leaf", Token);
        var fileSystem = Create(confined);

        await fileSystem.MoveAsync("tree", "moved", false, Token);

        Assert.False(Directory.Exists(Path.Combine(Workspace, "tree")));
        Assert.Equal("leaf", await File.ReadAllTextAsync(Path.Combine(Workspace, "moved", "leaf", "file.txt"), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveAsync_ExistingFileWithoutOverwrite_ThrowsFileAlreadyExistsAndKeepsBoth(bool confined)
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "a.txt"), "source", Token);
        await File.WriteAllTextAsync(Path.Combine(Workspace, "b.txt"), "destination", Token);
        var fileSystem = Create(confined);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.MoveAsync("a.txt", "b.txt", false, Token)
        );

        Assert.Equal(ErrorCodes.FileAlreadyExists.Code, exception.Code);
        Assert.Equal("source", await File.ReadAllTextAsync(Path.Combine(Workspace, "a.txt"), Token));
        Assert.Equal("destination", await File.ReadAllTextAsync(Path.Combine(Workspace, "b.txt"), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveAsync_ExistingFileWithOverwrite_ReplacesDestination(bool confined)
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "a.txt"), "source", Token);
        await File.WriteAllTextAsync(Path.Combine(Workspace, "b.txt"), "destination", Token);
        var fileSystem = Create(confined);

        await fileSystem.MoveAsync("a.txt", "b.txt", true, Token);

        Assert.False(File.Exists(Path.Combine(Workspace, "a.txt")));
        Assert.Equal("source", await File.ReadAllTextAsync(Path.Combine(Workspace, "b.txt"), Token));
    }

    [Theory]
    [InlineData(false, "tree", "empty")]
    [InlineData(false, "a.txt", "empty")]
    [InlineData(false, "tree", "b.txt")]
    [InlineData(true, "tree", "empty")]
    [InlineData(true, "a.txt", "empty")]
    [InlineData(true, "tree", "b.txt")]
    public async Task MoveAsync_OverwriteWithDirectoryInvolved_ThrowsFileAlreadyExistsAndKeepsBoth(
        bool confined,
        string source,
        string destination
    )
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "tree"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "tree", "file.txt"), "tree", Token);
        Directory.CreateDirectory(Path.Combine(Workspace, "empty"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "a.txt"), "source", Token);
        await File.WriteAllTextAsync(Path.Combine(Workspace, "b.txt"), "destination", Token);
        var fileSystem = Create(confined);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.MoveAsync(source, destination, true, Token)
        );

        Assert.Equal(ErrorCodes.FileAlreadyExists.Code, exception.Code);
        Assert.Equal("tree", await File.ReadAllTextAsync(Path.Combine(Workspace, "tree", "file.txt"), Token));
        Assert.True(Directory.Exists(Path.Combine(Workspace, "empty")));
        Assert.Equal("source", await File.ReadAllTextAsync(Path.Combine(Workspace, "a.txt"), Token));
        Assert.Equal("destination", await File.ReadAllTextAsync(Path.Combine(Workspace, "b.txt"), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveAsync_MissingSource_ThrowsFileNotFound(bool confined)
    {
        var fileSystem = Create(confined);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.MoveAsync("missing.txt", "moved.txt", false, Token)
        );

        Assert.Equal(ErrorCodes.FileNotFound.Code, exception.Code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveAsync_MissingDestinationParent_ThrowsDirectoryNotFoundAndKeepsSource(bool confined)
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "a.txt"), "source", Token);
        var fileSystem = Create(confined);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.MoveAsync("a.txt", "missing/a.txt", false, Token)
        );

        Assert.Equal(ErrorCodes.DirectoryNotFound.Code, exception.Code);
        Assert.True(File.Exists(Path.Combine(Workspace, "a.txt")));
        Assert.False(Directory.Exists(Path.Combine(Workspace, "missing")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MoveAsync_DestinationOutsideRoot_ThrowsFilePathOutsideRootAndKeepsSource(bool confined)
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "a.txt"), "source", Token);
        var fileSystem = Create(confined);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.MoveAsync("a.txt", "../outside/a.txt", false, Token)
        );

        Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
        Assert.True(File.Exists(Path.Combine(Workspace, "a.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Outside));
    }

    [Fact]
    public async Task MoveAsync_ConfinedDestinationThroughSymlinkOutsideRoot_ThrowsAndKeepsSource()
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "a.txt"), "source", Token);
        Directory.CreateSymbolicLink(Path.Combine(Workspace, "link"), Outside);
        var fileSystem = Create(confined: true);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.MoveAsync("a.txt", "link/a.txt", false, Token)
        );

        Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
        Assert.True(File.Exists(Path.Combine(Workspace, "a.txt")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Outside));
    }

    [Fact]
    public async Task MoveAsync_ConfinedSourceSymlink_MovesLinkItself()
    {
        await File.WriteAllTextAsync(Path.Combine(Outside, "secret.txt"), "secret", Token);
        File.CreateSymbolicLink(Path.Combine(Workspace, "link.txt"), Path.Combine(Outside, "secret.txt"));
        var fileSystem = Create(confined: true);

        await fileSystem.MoveAsync("link.txt", "renamed.txt", false, Token);

        var moved = new FileInfo(Path.Combine(Workspace, "renamed.txt"));
        Assert.Equal(Path.Combine(Outside, "secret.txt"), moved.LinkTarget);
        Assert.False(File.Exists(Path.Combine(Workspace, "link.txt")));
        Assert.Equal("secret", await File.ReadAllTextAsync(Path.Combine(Outside, "secret.txt"), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAsync_NonRecursiveEmptyDirectory_RemovesDirectory(bool confined)
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "empty"));
        var fileSystem = Create(confined);

        await fileSystem.DeleteAsync("empty", recursive: false, Token);

        Assert.False(Directory.Exists(Path.Combine(Workspace, "empty")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAsync_NonRecursiveFile_RemovesFile(bool confined)
    {
        await File.WriteAllTextAsync(Path.Combine(Workspace, "a.txt"), "content", Token);
        var fileSystem = Create(confined);

        await fileSystem.DeleteAsync("a.txt", recursive: false, Token);

        Assert.False(File.Exists(Path.Combine(Workspace, "a.txt")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAsync_NonRecursiveNonEmptyDirectory_ThrowsDirectoryNotEmptyAndKeepsEntries(bool confined)
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "docs", "nested"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "docs", "a.md"), "a", Token);
        await File.WriteAllTextAsync(Path.Combine(Workspace, "docs", "nested", "b.md"), "b", Token);
        var fileSystem = Create(confined);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.DeleteAsync("docs", recursive: false, Token)
        );

        Assert.Equal(ErrorCodes.DirectoryNotEmpty.Code, exception.Code);
        Assert.Equal(ErrorCodes.DirectoryNotEmpty.StatusCode, exception.StatusCode);
        Assert.Equal("a", await File.ReadAllTextAsync(Path.Combine(Workspace, "docs", "a.md"), Token));
        Assert.Equal("b", await File.ReadAllTextAsync(Path.Combine(Workspace, "docs", "nested", "b.md"), Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeleteAsync_RecursiveNonEmptyDirectory_RemovesTree(bool confined)
    {
        Directory.CreateDirectory(Path.Combine(Workspace, "docs", "nested"));
        await File.WriteAllTextAsync(Path.Combine(Workspace, "docs", "nested", "b.md"), "b", Token);
        var fileSystem = Create(confined);

        await fileSystem.DeleteAsync("docs", recursive: true, Token);

        Assert.False(Directory.Exists(Path.Combine(Workspace, "docs")));
    }

    [Fact]
    public async Task DeleteAsync_ConfinedNonRecursiveDirectorySymlinkOutsideRoot_RemovesLinkOnly()
    {
        await File.WriteAllTextAsync(Path.Combine(Outside, "secret.txt"), "secret", Token);
        Directory.CreateSymbolicLink(Path.Combine(Workspace, "link"), Outside);
        var fileSystem = Create(confined: true);

        await fileSystem.DeleteAsync("link", recursive: false, Token);

        Assert.False(Directory.Exists(Path.Combine(Workspace, "link")));
        Assert.Equal("secret", await File.ReadAllTextAsync(Path.Combine(Outside, "secret.txt"), Token));
    }

    private IAgwFileSystem Create(bool confined, params string[] additionalRoots) =>
        confined
            ? new LocalFileSystem(Workspace).Confine([Workspace, .. additionalRoots])
            : new LocalFileSystem(Workspace);
}
