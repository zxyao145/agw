using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Agw.Files.Abstracts;
using Agw.Shared.Exceptions;

namespace Agw.Files.Tests;

/// <summary>
/// WriteAsync 的写入前提（expectedSha256）、原子提交与按文件身份串行。参数 confined 为 false 时测试词法文件系统
/// （LocalFileSystem），为 true 时测试当前平台的受限文件系统（Unix 上的 UnixConfinedFileSystem，Windows 上的
/// WindowsConfinedFileSystem）。
/// The write precondition (expectedSha256), atomic commit and per-file-identity serialization of WriteAsync. With
/// confined false the lexical file system (LocalFileSystem) is tested; with confined true the confined file system of
/// the current platform (UnixConfinedFileSystem on Unix, WindowsConfinedFileSystem on Windows).
/// </summary>
public sealed class FileSystemAtomicWriteTests : IDisposable
{
    private const string Original = "original content";
    private const int BlockSize = 4;

    private static readonly DateTimeOffset FixedModifiedTime = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly string _root = Directory
        .CreateDirectory(
            Path.Combine(AppContext.BaseDirectory, "file-system-atomic-write", Guid.CreateVersion7().ToString("N"))
        )
        .FullName;

    public FileSystemAtomicWriteTests()
    {
        Directory.CreateDirectory(Workspace);
        Directory.CreateDirectory(Outside);
    }

    public static bool IsWindows => OperatingSystem.IsWindows();
    public static bool IsUnix => !OperatingSystem.IsWindows();

    private string Workspace => Path.Combine(_root, "workspace");
    private string Outside => Path.Combine(_root, "outside");
    private string Target => Path.Combine(Workspace, "notes.md");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_ExpectedSha256Matches_WritesContent(bool confined)
    {
        await File.WriteAllTextAsync(Target, Original, Token);
        var fileSystem = Create(confined);

        await fileSystem.WriteAsync("notes.md", Content("replaced"), true, false, Sha256(Original), Token);

        Assert.Equal("replaced", await File.ReadAllTextAsync(Target, Token));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_ExpectedSha256Differs_ThrowsFileChanged(bool confined)
    {
        await File.WriteAllTextAsync(Target, Original, Token);
        var fileSystem = Create(confined);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.WriteAsync("notes.md", Content("replaced"), true, false, Sha256("other content"), Token)
        );

        Assert.Equal(ErrorCodes.FileChanged.Code, exception.Code);
        Assert.Equal(Original, await File.ReadAllTextAsync(Target, Token));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_ExpectedSha256AndFileMissing_ThrowsFileChanged(bool confined)
    {
        var fileSystem = Create(confined);

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            fileSystem.WriteAsync("notes.md", Content("replaced"), true, false, Sha256(Original), Token)
        );

        Assert.Equal(ErrorCodes.FileChanged.Code, exception.Code);
        Assert.False(File.Exists(Target));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_ConcurrentWritesWithSameExpectedSha256_OnlyOneSucceeds(bool confined)
    {
        await File.WriteAllTextAsync(Target, Original, Token);
        var expectedSha256 = Sha256(Original);
        string[] contents = ["first writer", "second writer"];

        var failures = await Task.WhenAll(
            contents.Select(content =>
                Record
                    .ExceptionAsync(() =>
                        Create(confined).WriteAsync("notes.md", Content(content), true, false, expectedSha256, Token)
                    )
                    .AsTask()
            )
        );

        var winner = Assert.Single(contents.Where((_, index) => failures[index] == null));
        var failure = Assert.IsType<AgwException>(Assert.Single(failures, item => item != null));
        Assert.Equal(ErrorCodes.FileChanged.Code, failure.Code);
        Assert.Equal(winner, await File.ReadAllTextAsync(Target, Token));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_ContentStreamFailsMidway_KeepsOriginalFile(bool confined)
    {
        await File.WriteAllTextAsync(Target, Original, Token);
        var content = Blocks(
            "replacement content",
            (index, _, _) => index == 1 ? throw new IOException("The content stream failed.") : Task.CompletedTask
        );

        await Assert.ThrowsAsync<IOException>(() =>
            Create(confined).WriteAsync("notes.md", content, true, false, null, Token)
        );

        Assert.Equal(Original, await File.ReadAllTextAsync(Target, Token));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_CanceledMidway_KeepsOriginalFile(bool confined)
    {
        await File.WriteAllTextAsync(Target, Original, Token);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var content = Blocks(
            "replacement content",
            async (index, _, _) =>
            {
                if (index == 0)
                {
                    await cancellation.CancelAsync();
                }
            }
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Create(confined).WriteAsync("notes.md", content, true, false, null, cancellation.Token)
        );

        Assert.Equal(Original, await File.ReadAllTextAsync(Target, Token));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_TargetChangedBeforeCommit_ThrowsFileChanged(bool confined)
    {
        await File.WriteAllTextAsync(Target, Original, Token);
        var content = Blocks(
            "replacement content",
            async (_, last, ct) =>
            {
                if (last)
                {
                    await File.WriteAllTextAsync(Target, "external change", ct);
                }
            }
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            Create(confined).WriteAsync("notes.md", content, true, false, Sha256(Original), Token)
        );

        Assert.Equal(ErrorCodes.FileChanged.Code, exception.Code);
        Assert.Equal("external change", await File.ReadAllTextAsync(Target, Token));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_TargetReplacedWithSameSizeAndTime_ThrowsFileChanged(bool confined)
    {
        const string external = "external content";
        Assert.Equal(Original.Length, external.Length);
        await File.WriteAllTextAsync(Target, Original, Token);
        File.SetLastWriteTimeUtc(Target, FixedModifiedTime.UtcDateTime);
        var replacement = Path.Combine(Workspace, "replacement.md");
        var content = Blocks(
            "replacement content",
            async (_, last, ct) =>
            {
                if (!last)
                {
                    return;
                }

                // 通过重命名替换目标，新文件的大小与修改时间与原文件相同，只有文件标识不同。
                // Replace the target by a rename; the new file has the same size and modification time, and only its
                // identity differs.
                await File.WriteAllTextAsync(replacement, external, ct);
                File.SetLastWriteTimeUtc(replacement, FixedModifiedTime.UtcDateTime);
                File.Move(replacement, Target, overwrite: true);
            }
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            Create(confined).WriteAsync("notes.md", content, true, false, Sha256(Original), Token)
        );

        Assert.Equal(ErrorCodes.FileChanged.Code, exception.Code);
        Assert.Equal(external, await File.ReadAllTextAsync(Target, Token));
        Assert.Equal(FixedModifiedTime, new DateTimeOffset(File.GetLastWriteTimeUtc(Target)));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_TargetRewrittenInPlaceWithSameSize_ThrowsFileChanged(bool confined)
    {
        const string external = "external content";
        Assert.Equal(Original.Length, external.Length);
        var rewrittenTime = FixedModifiedTime.AddSeconds(1);
        await File.WriteAllTextAsync(Target, Original, Token);
        File.SetLastWriteTimeUtc(Target, FixedModifiedTime.UtcDateTime);
        var content = Blocks(
            "replacement content",
            async (_, last, ct) =>
            {
                if (!last)
                {
                    return;
                }

                // 在原文件上截断并写入长度相同的内容：文件标识与大小不变，只有修改时间不同。
                // Truncate the original file and write content of the same length in place: the identity and size
                // stay the same and only the modification time differs.
                await using (var stream = new FileStream(Target, FileMode.Truncate, FileAccess.Write))
                {
                    await stream.WriteAsync(Encoding.UTF8.GetBytes(external), ct);
                }

                File.SetLastWriteTimeUtc(Target, rewrittenTime.UtcDateTime);
            }
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            Create(confined).WriteAsync("notes.md", content, true, false, Sha256(Original), Token)
        );

        Assert.Equal(ErrorCodes.FileChanged.Code, exception.Code);
        Assert.Equal(external, await File.ReadAllTextAsync(Target, Token));
        Assert.Equal(rewrittenTime, new DateTimeOffset(File.GetLastWriteTimeUtc(Target)));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_ReadOnlyTarget_ThrowsFileNotWritable(bool confined)
    {
        await File.WriteAllTextAsync(Target, Original, Token);
        MakeReadOnly(Target);
        try
        {
            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                Create(confined).WriteAsync("notes.md", Content("replaced"), true, false, null, Token)
            );

            Assert.Equal(ErrorCodes.FileNotWritable.Code, exception.Code);
            Assert.Equal(Original, await File.ReadAllTextAsync(Target, Token));
            AssertNoTemporaryFiles();
        }
        finally
        {
            File.SetAttributes(Target, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_NoOverwriteAndTargetCreatedBeforeCommit_ThrowsFileAlreadyExists(bool confined)
    {
        var content = Blocks(
            "replacement content",
            async (_, last, ct) =>
            {
                if (last)
                {
                    await File.WriteAllTextAsync(Target, "created meanwhile", ct);
                }
            }
        );

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            Create(confined).WriteAsync("notes.md", content, false, false, null, Token)
        );

        Assert.Equal(ErrorCodes.FileAlreadyExists.Code, exception.Code);
        Assert.Equal("created meanwhile", await File.ReadAllTextAsync(Target, Token));
        AssertNoTemporaryFiles();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_StaleTemporaryFile_IsRemoved(bool confined)
    {
        await File.WriteAllTextAsync(Target, Original, Token);
        var stale = Path.Combine(Workspace, $".notes.md.agw-{Guid.NewGuid():N}.tmp");
        var otherFileTemporary = Path.Combine(Workspace, $".other.md.agw-{Guid.NewGuid():N}.tmp");
        var unrelated = Path.Combine(Workspace, ".notes.md.agw-backup.tmp");
        await File.WriteAllTextAsync(stale, "stale", Token);
        await File.WriteAllTextAsync(otherFileTemporary, "other", Token);
        await File.WriteAllTextAsync(unrelated, "unrelated", Token);

        await Create(confined).WriteAsync("notes.md", Content("replaced"), true, false, null, Token);

        Assert.Equal("replaced", await File.ReadAllTextAsync(Target, Token));
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(otherFileTemporary));
        Assert.True(File.Exists(unrelated));
    }

    [Theory(SkipUnless = nameof(IsUnix), Skip = "Unix file modes exist on Linux and macOS only.")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [UnsupportedOSPlatform("windows")]
    public async Task WriteAsync_UnixFileMode_IsPreserved(bool confined, bool withExpectedSha256)
    {
        const UnixFileMode mode =
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead;
        await File.WriteAllTextAsync(Target, Original, Token);
        File.SetUnixFileMode(Target, mode);
        var expectedSha256 = withExpectedSha256 ? Sha256(Original) : null;

        await Create(confined).WriteAsync("notes.md", Content("replaced"), true, false, expectedSha256, Token);

        Assert.Equal("replaced", await File.ReadAllTextAsync(Target, Token));
        Assert.Equal(mode, File.GetUnixFileMode(Target));
    }

    [Theory(SkipUnless = nameof(IsUnix), Skip = "Unix file modes exist on Linux and macOS only.")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [UnsupportedOSPlatform("windows")]
    public async Task WriteAsync_SetUserIdTarget_ReplacementHasNoSetUserId(bool confined, bool withExpectedSha256)
    {
        const UnixFileMode permissions =
            UnixFileMode.UserRead
            | UnixFileMode.UserWrite
            | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead
            | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead
            | UnixFileMode.OtherExecute;
        await File.WriteAllTextAsync(Target, Original, Token);
        File.SetUnixFileMode(Target, permissions | UnixFileMode.SetUser);
        Assert.Equal(permissions | UnixFileMode.SetUser, File.GetUnixFileMode(Target));
        var expectedSha256 = withExpectedSha256 ? Sha256(Original) : null;

        // 非特权进程写入文件内容时内核会清除 setuid 位；新内容为空，不写入任何字节，结果只取决于复制给新文件的权限位。
        // The kernel clears the setuid bit when an unprivileged process writes file content; the new content is empty
        // so no byte is written, and the result depends only on the permission bits copied to the new file.
        await Create(confined).WriteAsync("notes.md", Content(""), true, false, expectedSha256, Token);

        Assert.Equal("", await File.ReadAllTextAsync(Target, Token));
        Assert.Equal(permissions, File.GetUnixFileMode(Target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WriteAsync_DanglingSymbolicLink_CreatesTargetAndKeepsLink(bool confined)
    {
        var target = Path.Combine(Workspace, "created.md");
        var link = Path.Combine(Workspace, "link.md");
        File.CreateSymbolicLink(link, target);

        await Create(confined).WriteAsync("link.md", Content("replaced"), true, false, null, Token);

        Assert.Equal(target, new FileInfo(link).LinkTarget);
        Assert.Equal("replaced", await File.ReadAllTextAsync(target, Token));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task WriteAsync_SymbolicLinkTarget_KeepsLink()
    {
        var real = Path.Combine(Workspace, "real.md");
        var link = Path.Combine(Workspace, "link.md");
        await File.WriteAllTextAsync(real, Original, Token);
        File.CreateSymbolicLink(link, real);

        await TestFileSystems
            .Local(Workspace)
            .WriteAsync("link.md", Content("replaced"), true, false, Sha256(Original), Token);

        Assert.Equal(real, new FileInfo(link).LinkTarget);
        Assert.Equal("replaced", await File.ReadAllTextAsync(real, Token));
        AssertNoTemporaryFiles();
    }

    [Fact(SkipUnless = nameof(IsWindows), Skip = "Junctions and directory share modes exist on Windows only.")]
    [SupportedOSPlatform("windows")]
    public async Task WriteAsync_ConfinedParentReplacedWithJunction_DoesNotWriteOutside()
    {
        var docs = Directory.CreateDirectory(Path.Combine(Workspace, "docs")).FullName;
        var moved = Path.Combine(Workspace, "docs-moved");
        await File.WriteAllTextAsync(Path.Combine(docs, "notes.md"), Original, Token);
        Exception? replaceFailure = null;
        var content = Blocks(
            "replacement content",
            (index, _, _) =>
            {
                if (index == 0)
                {
                    // 尝试把目标所在目录移走并换成指向允许范围以外的 junction。
                    // Try to move the target's directory away and put a junction pointing outside in its place.
                    replaceFailure = Record.Exception(() =>
                    {
                        Directory.Move(docs, moved);
                        CreateJunction(docs, Outside);
                    });
                }

                return Task.CompletedTask;
            }
        );

        await Create(confined: true).WriteAsync("docs/notes.md", content, true, false, null, Token);

        Assert.True(replaceFailure is IOException or UnauthorizedAccessException, $"Unexpected: {replaceFailure}");
        Assert.False(Directory.Exists(moved));
        Assert.Equal("replacement content", await File.ReadAllTextAsync(Path.Combine(docs, "notes.md"), Token));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Outside));
    }

    [Fact]
    public async Task WriteAllTextAsync_ConfinedDuringLocalWriteAsync_RunsAfterCommit()
    {
        await File.WriteAllTextAsync(Target, Original, Token);
        var local = TestFileSystems.Local(Workspace);
        var confined = local.Confine([Workspace]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = new BlockContentStream(
            Encoding.UTF8.GetBytes("http write"),
            1024,
            async (_, _, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        );

        // HTTP 写入持有文件锁并停在复制内容的阶段，Agent 文件工具的文字写入必须等它提交之后才执行。
        // The HTTP write holds the file lock while paused in the copy stage, so the Agent file tool's text write
        // must run only after it commits.
        var httpWrite = local.WriteAsync("notes.md", content, true, false, null, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var toolWrite = confined.WriteAllTextAsync("notes.md", "tool write", Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        Assert.False(toolWrite.IsCompleted);
        release.SetResult();
        await httpWrite;
        await toolWrite;

        Assert.Equal("tool write", await File.ReadAllTextAsync(Target, Token));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task WriteAllTextAsync_LinkTargetDuringWriteThroughLink_RunsAfterCommit()
    {
        var docs = Directory.CreateDirectory(Path.Combine(Workspace, "docs")).FullName;
        var real = Path.Combine(docs, "v2.md");
        var link = Path.Combine(docs, "current.md");
        await File.WriteAllTextAsync(real, Original, Token);
        File.CreateSymbolicLink(link, real);
        var local = TestFileSystems.Local(Workspace);
        var confined = local.Confine([Workspace]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = new BlockContentStream(
            Encoding.UTF8.GetBytes("http write"),
            1024,
            async (_, _, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        );

        // HTTP 写入经过符号链接 docs/current.md，Agent 文件工具直接写链接目标 docs/v2.md；两者是同一个文件，必须依次执行。
        // The HTTP write goes through the symbolic link docs/current.md while the Agent file tool writes the link
        // target docs/v2.md directly; both are the same file, so they must run one after the other.
        var httpWrite = local.WriteAsync("docs/current.md", content, true, false, null, Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var toolWrite = confined.WriteAllTextAsync("docs/v2.md", "tool write", Token);
        await Task.Delay(TimeSpan.FromMilliseconds(200), Token);
        Assert.False(toolWrite.IsCompleted);
        release.SetResult();
        await httpWrite;
        await toolWrite;

        Assert.Equal("tool write", await File.ReadAllTextAsync(real, Token));
        Assert.Equal(real, new FileInfo(link).LinkTarget);
    }

    private IAgwFileSystem Create(bool confined) =>
        confined ? TestFileSystems.Local(Workspace).Confine([Workspace]) : TestFileSystems.Local(Workspace);

    private static MemoryStream Content(string text) => new(Encoding.UTF8.GetBytes(text));

    private static BlockContentStream Blocks(string text, Func<int, bool, CancellationToken, Task> onBlockRead) =>
        new(Encoding.UTF8.GetBytes(text), BlockSize, onBlockRead);

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private void AssertNoTemporaryFiles() =>
        Assert.DoesNotContain(
            Directory.EnumerateFiles(Workspace),
            file => Path.GetFileName(file).Contains(".agw-", StringComparison.Ordinal)
        );

    /// <summary>
    /// 让文件只读：Unix 上权限改为 0o444，Windows 上设置只读属性。
    /// Makes a file read-only: mode 0o444 on Unix, the read-only attribute on Windows.
    /// </summary>
    private static void MakeReadOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(
            new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target]) { UseShellExecute = false }
        )!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
