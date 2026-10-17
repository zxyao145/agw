using Agw.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Agw.Jobs.Tests;

internal sealed class SqliteJobTestDatabase : IAsyncDisposable
{
    private readonly string _directory;
    private readonly string _path;

    private SqliteJobTestDatabase(string directory, string path, DbContextOptions<AgwDbContext> options)
    {
        _directory = directory;
        _path = path;
        Options = options;
    }

    public DbContextOptions<AgwDbContext> Options { get; }

    public static Task<SqliteJobTestDatabase> CreateAsync()
    {
        var scratchDirectory = Path.Combine(JobTestPaths.RepositoryRoot, ".scratch", "jobs-tests");
        Directory.CreateDirectory(scratchDirectory);
        var databaseDirectory = Path.Combine(scratchDirectory, $"jobs-{Guid.CreateVersion7():N}");
        Directory.CreateDirectory(databaseDirectory);
        var path = Path.Combine(databaseDirectory, "jobs.db");
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite($"Data Source={path};Pooling=False;Foreign Keys=False;Default Timeout=30")
            .UseSnakeCaseNamingConvention()
            .Options;
        return Task.FromResult(new SqliteJobTestDatabase(databaseDirectory, path, options));
    }

    public AgwDbContext CreateContext() => new(Options);

    public ValueTask DisposeAsync()
    {
        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
        Directory.Delete(_directory, recursive: true);
        return ValueTask.CompletedTask;
    }
}
