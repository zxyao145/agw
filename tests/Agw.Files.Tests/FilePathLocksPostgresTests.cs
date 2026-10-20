using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agw.Files.Infrastructure.Storage;
using Agw.Infrastructure.Configuration;
using Agw.Infrastructure.Coordination;
using Agw.Shared.Configuration;
using Agw.Shared.Contracts.Coordination;
using Agw.Shared.Coordination;
using Agw.Shared.Exceptions;
using Medallion.Threading.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Agw.Files.Tests;

/// <summary>
/// 分开部署的多个 Host 通过 PostgreSQL 分布式锁按文件身份串行写入：每个 Host 各自创建 ApplicationLockRouter 与
/// FilePathLocks，写同一个工作区中的同一个文件。
/// Separately deployed Hosts serialize writes per file identity through the PostgreSQL distributed lock: each Host
/// builds its own ApplicationLockRouter and FilePathLocks and writes the same file in the same workspace.
/// </summary>
[Trait("Category", "Postgres")]
public sealed class FilePathLocksPostgresTests : IDisposable
{
    private const string Original = "original content";

    private readonly string _workspace = Directory
        .CreateDirectory(
            Path.Combine(AppContext.BaseDirectory, "file-path-locks-postgres", Guid.CreateVersion7().ToString("N"))
        )
        .FullName;

    private string Target => Path.Combine(_workspace, "notes.md");
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public async Task WriteAsync_TwoHostsWithSameExpectedSha256_SerializesAndOnlyOneSucceeds()
    {
        var connectionString = RequireConnectionString();
        await File.WriteAllTextAsync(Target, Original, Token);
        using var firstSettings = CreateSettings(connectionString, $"agw-files-first-{Guid.CreateVersion7():N}");
        using var secondSettings = CreateSettings(connectionString, $"agw-files-second-{Guid.CreateVersion7():N}");
        var firstHost = CreateHost(CreateRouter(firstSettings));
        var secondHost = CreateHost(CreateRouter(secondSettings));
        var expectedSha256 = Sha256(Original);
        var (content, entered, release) = PausedContent("first host");

        // 第一个 Host 通过哈希检查后停在复制内容的阶段并持有锁；第二个 Host 必须等待，之后因哈希不同而失败。
        // The first Host passes the hash check and pauses in the copy stage while holding the lock; the second Host
        // must wait and then fails because the hash differs.
        var firstWrite = firstHost.WriteAsync("notes.md", content, true, false, expectedSha256, Token);
        Task secondWrite;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            secondWrite = secondHost.WriteAsync("notes.md", Content("second host"), true, false, expectedSha256, Token);
            await Task.Delay(TimeSpan.FromMilliseconds(500), Token);
            Assert.False(secondWrite.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
        }

        await firstWrite;
        var exception = await Assert.ThrowsAsync<AgwException>(() => secondWrite);
        Assert.Equal(ErrorCodes.FileChanged.Code, exception.Code);
        Assert.Equal("first host", await File.ReadAllTextAsync(Target, Token));
        AssertNoTemporaryFiles();
    }

    [Fact]
    public async Task WriteAsync_LeaseLostBeforeCommit_StaleHolderFailsAndKeepsNewContent()
    {
        var connectionString = RequireConnectionString();
        await File.WriteAllTextAsync(Target, Original, Token);
        var staleApplicationName = $"agw-files-stale-{Guid.CreateVersion7():N}";
        using var staleSettings = CreateSettings(connectionString, staleApplicationName);
        using var currentSettings = CreateSettings(connectionString, $"agw-files-current-{Guid.CreateVersion7():N}");
        var staleHost = CreateHost(CreateRouter(staleSettings));
        var currentHost = CreateHost(CreateRouter(currentSettings));
        var expectedSha256 = Sha256(Original);
        var (content, entered, release) = PausedContent("stale host");

        // 写入方 A 通过哈希检查后停在复制阶段；断开它的锁连接后数据库释放 A 的锁，写入方 B 取得锁并写入成功，B 写入成功本身
        // 说明 A 的锁已经释放。测试不读取 A 的租约，也不放行 A：A 的复制必须因租约丢失而自行中止。
        // Writer A pauses in the copy stage after the hash check; terminating its lock connection makes the database
        // release A's lock, so writer B acquires it and writes successfully, which itself shows A's lock was released.
        // The test neither reads A's lease nor releases A: A's copy must abort on its own because the lease was lost.
        var staleWrite = staleHost.WriteAsync("notes.md", content, true, false, expectedSha256, Token);
        Exception? staleFailure;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            await TerminateLockConnectionAsync(connectionString, staleApplicationName, Token);
            await currentHost
                .WriteAsync("notes.md", Content("current host"), true, false, expectedSha256, Token)
                .WaitAsync(TimeSpan.FromSeconds(10), Token);
            staleFailure = await Record.ExceptionAsync(() => staleWrite.WaitAsync(TimeSpan.FromSeconds(10), Token));
        }
        finally
        {
            release.TrySetResult();
        }

        Assert.IsAssignableFrom<OperationCanceledException>(staleFailure);
        Assert.Equal("current host", await File.ReadAllTextAsync(Target, Token));
        AssertNoTemporaryFiles();
    }

    private LocalFileSystem CreateHost(IApplicationLock applicationLock) =>
        new(_workspace, new FilePathLocks(applicationLock), NullLogger.Instance);

    private void AssertNoTemporaryFiles() =>
        Assert.DoesNotContain(
            Directory.EnumerateFiles(_workspace),
            file => Path.GetFileName(file).Contains(".agw-", StringComparison.Ordinal)
        );

    private static MemoryStream Content(string text) => new(Encoding.UTF8.GetBytes(text));

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    /// <summary>
    /// 读出第一块后通知 entered 并等待 release 的内容流。
    /// A content stream that signals entered after its first block and waits for release.
    /// </summary>
    private static (
        BlockContentStream Content,
        TaskCompletionSource Entered,
        TaskCompletionSource Release
    ) PausedContent(string text)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = new BlockContentStream(
            Encoding.UTF8.GetBytes(text),
            1024,
            async (_, _, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
            }
        );
        return (content, entered, release);
    }

    private static string RequireConnectionString()
    {
        var path = Path.Combine(FindRepositoryRoot(), ".scratch", "postgres-tests.json");
        if (!File.Exists(path))
        {
            Assert.Skip("PostgreSQL test configuration is not present.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var connectionString = document.RootElement.GetProperty("ConnectionString").GetString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        return connectionString;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Agw.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find the Agw solution root.");
    }

    private static ServiceProvider CreateSettings(string connectionString, string applicationName) =>
        new ServiceCollection()
            .Configure<DatabaseSettings>(settings =>
            {
                settings.Provider = DatabaseProvider.Postgres;
                settings.ConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
                {
                    ApplicationName = applicationName,
                    Pooling = false,
                }.ConnectionString;
            })
            .AddOptions<DistributedLockSettings>()
            .Services.BuildServiceProvider();

    /// <summary>
    /// 与生产环境相同的锁提供方：<see cref="PostgresDistributedSynchronizationProvider"/> 使用默认选项。
    /// The same lock provider as production: <see cref="PostgresDistributedSynchronizationProvider"/> with default
    /// options.
    /// </summary>
    private static ApplicationLockRouter CreateRouter(IServiceProvider settings) =>
        new(
            settings.GetRequiredService<IOptionsMonitor<DatabaseSettings>>(),
            settings.GetRequiredService<IOptionsMonitor<DistributedLockSettings>>(),
            new InMemoryApplicationLock(),
            (_, connectionString) => new PostgresDistributedSynchronizationProvider(connectionString)
        );

    private static async Task TerminateLockConnectionAsync(
        string connectionString,
        string applicationName,
        CancellationToken token
    )
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(token);
        await using var command = new NpgsqlCommand(
            "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE application_name = @name AND pid <> pg_backend_pid()",
            connection
        );
        command.Parameters.AddWithValue("name", applicationName);
        Assert.Equal(true, await command.ExecuteScalarAsync(token));
    }
}
