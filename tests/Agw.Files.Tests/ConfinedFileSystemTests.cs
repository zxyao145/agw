using Agw.Files.Abstracts;
using Agw.Files.Abstracts.Dtos;
using Agw.Files.Infrastructure.Storage;
using Agw.Shared.Exceptions;

namespace Agw.Files.Tests;

/// <summary>
/// 受限文件系统的行为：符号链接只能落在允许的目录内，且每个操作都绑定到解析时打开的句柄。
/// Behavior of the confined file system: symbolic links may only land inside the allowed directories, and every
/// operation is bound to the handles opened while resolving.
/// </summary>
public sealed class ConfinedFileSystemTests
{
    [Fact]
    public async Task ReadAllText_FileSymlinkOutsideRoot_ThrowsFilePathOutsideRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTestRoot();
        try
        {
            var workspace = CreateDirectory(root, "workspace");
            var outside = CreateDirectory(root, "outside");
            await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "secret", ct);
            File.CreateSymbolicLink(Path.Combine(workspace, "link.txt"), Path.Combine(outside, "secret.txt"));
            var fileSystem = Confine(workspace);

            var exception = await Assert.ThrowsAsync<AgwException>(() => fileSystem.ReadAllTextAsync("link.txt", ct));

            Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAllText_DirectorySymlinkOutsideRoot_ThrowsAndCreatesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTestRoot();
        try
        {
            var workspace = CreateDirectory(root, "workspace");
            var outside = CreateDirectory(root, "outside");
            Directory.CreateSymbolicLink(Path.Combine(workspace, "link"), outside);
            var fileSystem = Confine(workspace);

            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                fileSystem.WriteAllTextAsync("link/escaped.txt", "escaped", ct)
            );

            Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
            Assert.False(File.Exists(Path.Combine(outside, "escaped.txt")));
            Assert.False(File.Exists(Path.Combine(workspace, "escaped.txt")));
            Assert.Empty(Directory.GetFiles(workspace, ".agw-write-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAllText_SymlinkIntoAdditionalRoot_ReturnsContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTestRoot();
        try
        {
            var workspace = CreateDirectory(root, "workspace");
            var additional = CreateDirectory(root, "additional");
            await File.WriteAllTextAsync(Path.Combine(additional, "shared.md"), "shared", ct);
            Directory.CreateSymbolicLink(Path.Combine(workspace, "link"), additional);
            var fileSystem = Confine(workspace, additional);

            Assert.Equal("shared", await fileSystem.ReadAllTextAsync("link/shared.md", ct));
            Assert.True(await fileSystem.ExistsFileAsync("link/shared.md", ct));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAllText_RelativeLinkClimbingOutOfRoot_ThrowsFilePathOutsideRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTestRoot();
        try
        {
            var workspace = CreateDirectory(root, "workspace");
            await File.WriteAllTextAsync(Path.Combine(root, "secret.txt"), "secret", ct);
            File.CreateSymbolicLink(Path.Combine(workspace, "link.txt"), "../secret.txt");
            var fileSystem = Confine(workspace);

            var exception = await Assert.ThrowsAsync<AgwException>(() => fileSystem.ReadAllTextAsync("link.txt", ct));

            Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAllText_LinkTargetSpelledWithDifferentCase_OnCaseInsensitiveVolume_ReturnsContent()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTestRoot();
        try
        {
            var workspace = CreateDirectory(root, "workspace");
            var upperCasedWorkspace = Path.Combine(root, "WORKSPACE");
            if (!Directory.Exists(upperCasedWorkspace))
            {
                Assert.Skip("The test volume is case-sensitive, so no differently cased alias exists.");
            }

            await File.WriteAllTextAsync(Path.Combine(workspace, "notes.md"), "notes", ct);
            File.CreateSymbolicLink(Path.Combine(workspace, "link.md"), Path.Combine(upperCasedWorkspace, "notes.md"));
            var fileSystem = Confine(workspace);

            Assert.Equal("notes", await fileSystem.ReadAllTextAsync("link.md", ct));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAllText_LinkSwappedWhileWaitingForPathLock_RejectsAndLeavesOutsideFileIntact()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTestRoot();
        try
        {
            var workspace = CreateDirectory(root, "workspace");
            var outside = CreateDirectory(root, "outside");
            var outsideFile = Path.Combine(outside, "secret.txt");
            await File.WriteAllTextAsync(outsideFile, "secret", ct);
            await File.WriteAllTextAsync(Path.Combine(workspace, "inside.txt"), "inside", ct);
            var link = Path.Combine(workspace, "link.txt");
            File.CreateSymbolicLink(link, Path.Combine(workspace, "inside.txt"));
            var fileSystem = Confine(workspace);

            // 持有目标路径的写锁，让 WriteAllTextAsync 在锁上等待，期间把链接改为指向允许范围之外。
            // Hold the write lock of the target path so WriteAllTextAsync waits on it, and repoint the link outside
            // the allowed directories meanwhile.
            Task write;
            using (await LocalFilePathLocks.AcquireAsync(link, ct))
            {
                write = fileSystem.WriteAllTextAsync("link.txt", "payload", ct);
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
                Assert.False(write.IsCompleted);
                File.Delete(link);
                File.CreateSymbolicLink(link, outsideFile);
            }

            var exception = await Assert.ThrowsAsync<AgwException>(() => write);

            Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
            Assert.Equal("secret", await File.ReadAllTextAsync(outsideFile, ct));
            Assert.Equal("inside", await File.ReadAllTextAsync(Path.Combine(workspace, "inside.txt"), ct));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAllText_MissingFile_CreatesFileAndParentDirectories()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTestRoot();
        try
        {
            var workspace = CreateDirectory(root, "workspace");
            var fileSystem = Confine(workspace);

            await fileSystem.WriteAllTextAsync("nested/deeper/notes.md", "first", ct);
            await fileSystem.WriteAllTextAsync("nested/deeper/notes.md", "second", ct);

            Assert.Equal(
                "second",
                await File.ReadAllTextAsync(Path.Combine(workspace, "nested", "deeper", "notes.md"), ct)
            );
            Assert.Empty(Directory.GetFiles(workspace, ".agw-write-*"));
            Assert.False(await fileSystem.CreateTextFileAsync("nested/deeper/notes.md", "third", ct));
            Assert.True(await fileSystem.CreateTextFileAsync("nested/created.md", "created", ct));
            Assert.Equal("created", await fileSystem.ReadAllTextAsync("nested/created.md", ct));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReplaceText_ExistingFile_RewritesContentThroughOneHandle()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTestRoot();
        try
        {
            var workspace = CreateDirectory(root, "workspace");
            await File.WriteAllTextAsync(Path.Combine(workspace, "notes.md"), "alpha beta alpha", ct);
            var fileSystem = Confine(workspace);

            var count = await fileSystem.ReplaceTextAsync("notes.md", "alpha", "gamma", replaceAll: true, ct);
            await fileSystem.ReplaceLinesAsync(
                "notes.md",
                [new AgwFileLineEdit { LineNumber = 1, NewLine = "delta\n" }],
                ct
            );

            Assert.Equal(2, count);
            Assert.Equal("delta\n", await File.ReadAllTextAsync(Path.Combine(workspace, "notes.md"), ct));
            Assert.Equal(new[] { "delta\n" }, await fileSystem.ReadLinesAsync("notes.md", 1, null, ct));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Enumerate_And_Search_ShowOnlyEntriesInsideAllowedRoots()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTestRoot();
        try
        {
            var workspace = CreateDirectory(root, "workspace");
            var additional = CreateDirectory(root, "additional");
            var outside = CreateDirectory(root, "outside");
            await File.WriteAllTextAsync(Path.Combine(workspace, "plain.txt"), "needle plain", ct);
            await File.WriteAllTextAsync(Path.Combine(additional, "shared.txt"), "needle shared", ct);
            await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "needle secret", ct);
            Directory.CreateDirectory(Path.Combine(workspace, "sub"));
            await File.WriteAllTextAsync(Path.Combine(workspace, "sub", "inner.txt"), "needle inner", ct);
            File.CreateSymbolicLink(Path.Combine(workspace, "inside-link.txt"), Path.Combine(additional, "shared.txt"));
            File.CreateSymbolicLink(Path.Combine(workspace, "outside-link.txt"), Path.Combine(outside, "secret.txt"));
            Directory.CreateSymbolicLink(Path.Combine(workspace, "outside-dir"), outside);
            var fileSystem = Confine(workspace, additional);

            var entries = new List<FileEntry>();
            await foreach (var entry in fileSystem.EnumerateAsync(string.Empty, "*", recursive: false, ct))
            {
                entries.Add(entry);
            }

            var results = new List<AgwFileSearchResult>();
            await foreach (
                var result in fileSystem.SearchAsync(
                    string.Empty,
                    new SearchOptions("needle", CaseInsensitive: true, Recursive: true),
                    ct
                )
            )
            {
                results.Add(result);
            }

            Assert.Equal(
                new[] { "inside-link.txt", "plain.txt", "sub" },
                entries.Select(static entry => entry.Path).Order(StringComparer.Ordinal).ToArray()
            );
            Assert.Equal(
                new[] { "plain.txt", "sub/inner.txt" },
                results.Select(static result => result.FileName).Order(StringComparer.Ordinal).ToArray()
            );
            var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            {
                await foreach (var _ in fileSystem.EnumerateAsync("outside-dir", "*", recursive: false, ct)) { }
            });
            Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Delete_SymlinkAndDirectory_RemovesLinkItselfAndDirectoryTree()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTestRoot();
        try
        {
            var workspace = CreateDirectory(root, "workspace");
            var outside = CreateDirectory(root, "outside");
            await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "secret", ct);
            Directory.CreateSymbolicLink(Path.Combine(workspace, "outside-dir"), outside);
            Directory.CreateDirectory(Path.Combine(workspace, "tree", "leaf"));
            await File.WriteAllTextAsync(Path.Combine(workspace, "tree", "leaf", "file.txt"), "leaf", ct);
            File.CreateSymbolicLink(Path.Combine(workspace, "tree", "escape"), outside);
            var fileSystem = Confine(workspace);

            await fileSystem.DeleteAsync("outside-dir", ct);
            await fileSystem.DeleteAsync("tree", ct);

            Assert.False(Directory.Exists(Path.Combine(workspace, "outside-dir")));
            Assert.False(Directory.Exists(Path.Combine(workspace, "tree")));
            Assert.Equal("secret", await File.ReadAllTextAsync(Path.Combine(outside, "secret.txt"), ct));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetSubFileSystem_SubRootSymlinkOutsideRoot_ThrowsFilePathOutsideRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTestRoot();
        try
        {
            var workspace = CreateDirectory(root, "workspace");
            var outside = CreateDirectory(root, "outside");
            Directory.CreateDirectory(Path.Combine(workspace, ".agw"));
            Directory.CreateSymbolicLink(Path.Combine(workspace, ".agw", "memory"), outside);
            var memory = Confine(workspace).GetSubFileSystem(".agw/memory");

            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                memory.WriteAllTextAsync("notes.md", "memory", ct)
            );

            Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
            Assert.False(File.Exists(Path.Combine(outside, "notes.md")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Stat_RootReachedThroughSymlinkedPath_ReportsDirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTestRoot();
        try
        {
            var physicalWorkspace = CreateDirectory(root, "physical");
            await File.WriteAllTextAsync(Path.Combine(physicalWorkspace, "notes.md"), "notes", ct);
            var linkedWorkspace = Path.Combine(root, "linked");
            Directory.CreateSymbolicLink(linkedWorkspace, physicalWorkspace);
            var fileSystem = Confine(linkedWorkspace);

            var stat = await fileSystem.StatAsync(string.Empty, ct);
            var file = await fileSystem.StatAsync("notes.md", ct);

            Assert.NotNull(stat);
            Assert.True(stat.IsDirectory);
            Assert.NotNull(file);
            Assert.Equal("notes.md", file.Path);
            Assert.Equal(5, file.Size);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ReadAllText_PathWithNulCharacter_IsRejectedBeforeNativeCalls()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTestRoot();
        try
        {
            var workspace = CreateDirectory(root, "workspace");
            var outside = CreateDirectory(root, "outside");
            await File.WriteAllTextAsync(Path.Combine(outside, "secret.txt"), "secret", ct);
            var fileSystem = Confine(workspace);

            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                fileSystem.ReadAllTextAsync("..\0ignored/outside/secret.txt", ct)
            );

            Assert.Equal(ErrorCodes.InvalidParam.Code, exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Search_LinkWhoseTargetIsParentDirectory_ThrowsFilePathOutsideRoot()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTestRoot();
        try
        {
            var workspace = CreateDirectory(root, "workspace");
            await File.WriteAllTextAsync(Path.Combine(root, "secret.txt"), "needle outside", ct);
            Directory.CreateSymbolicLink(Path.Combine(workspace, "parent"), "..");
            var fileSystem = Confine(workspace);

            var exception = await Assert.ThrowsAsync<AgwException>(async () =>
            {
                await foreach (
                    var _ in fileSystem.SearchAsync("parent", new SearchOptions("needle", Recursive: false), ct)
                ) { }
            });

            Assert.Equal(ErrorCodes.FilePathOutsideRoot.Code, exception.Code);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Enumerate_LinkDirectlyToAdditionalRoot_ListsItsEntries()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTestRoot();
        try
        {
            var workspace = CreateDirectory(root, "workspace");
            var additional = CreateDirectory(root, "additional");
            await File.WriteAllTextAsync(Path.Combine(additional, "shared.md"), "shared", ct);
            Directory.CreateSymbolicLink(Path.Combine(workspace, "link"), additional);
            var fileSystem = Confine(workspace, additional);

            var entries = new List<FileEntry>();
            await foreach (var entry in fileSystem.EnumerateAsync("link", "*", recursive: false, ct))
            {
                entries.Add(entry);
            }

            var single = Assert.Single(entries);
            Assert.Equal("link/shared.md", single.Path);
            Assert.True(await fileSystem.ExistsDirectoryAsync("link", ct));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task WriteAllText_NewFileThroughLinkIntoAdditionalRoot_CreatesItUnderTheTargetDirectory()
    {
        var ct = TestContext.Current.CancellationToken;
        var root = CreateTestRoot();
        try
        {
            var workspace = CreateDirectory(root, "workspace");
            var additional = CreateDirectory(root, "additional");
            Directory.CreateSymbolicLink(Path.Combine(workspace, "link"), additional);
            var fileSystem = Confine(workspace, additional);

            await fileSystem.WriteAllTextAsync("link/created.md", "created", ct);

            Assert.Equal("created", await File.ReadAllTextAsync(Path.Combine(additional, "created.md"), ct));
            Assert.Empty(Directory.GetFiles(workspace));
            Assert.Empty(Directory.GetFiles(additional, ".agw-write-*"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static IAgwFileSystem Confine(string workspace, params string[] additionalRoots) =>
        new LocalFileSystem(workspace).Confine([workspace, .. additionalRoots]);

    private static string CreateDirectory(string root, string name) =>
        Directory.CreateDirectory(Path.Combine(root, name)).FullName;

    /// <summary>
    /// 在测试输出目录下创建一个独立的根目录，符号链接测试的所有目录都放在其中。
    /// Creates an isolated root under the test output directory that holds every directory of a symbolic-link test.
    /// </summary>
    private static string CreateTestRoot() =>
        Directory
            .CreateDirectory(
                Path.Combine(AppContext.BaseDirectory, "confined-file-system", Guid.CreateVersion7().ToString("N"))
            )
            .FullName;
}
