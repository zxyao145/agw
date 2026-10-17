using System.Text.Json;
using Agw.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Agw.Jobs.Tests;

internal sealed class PostgresJobTestDatabase : IAsyncDisposable
{
    private readonly string _adminConnectionString;
    private readonly string _databaseName;

    private PostgresJobTestDatabase(
        string adminConnectionString,
        string databaseName,
        string connectionString,
        DbContextOptions<AgwDbContext> options
    )
    {
        _adminConnectionString = adminConnectionString;
        _databaseName = databaseName;
        ConnectionString = connectionString;
        Options = options;
    }

    public string ConnectionString { get; }

    public DbContextOptions<AgwDbContext> Options { get; }

    public static string RequireAdminConnectionString()
    {
        var path = Path.Combine(JobTestPaths.RepositoryRoot, ".scratch", "postgres-tests.json");
        if (!File.Exists(path))
        {
            Assert.Skip("PostgreSQL test configuration is not present.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var connectionString = document.RootElement.GetProperty("ConnectionString").GetString();
        Assert.False(string.IsNullOrWhiteSpace(connectionString));
        return connectionString;
    }

    public static async Task<PostgresJobTestDatabase> CreateAsync(
        string adminConnectionString,
        CancellationToken cancellationToken,
        params IInterceptor[] interceptors
    )
    {
        var adminSettings = new NpgsqlConnectionStringBuilder(adminConnectionString) { Pooling = false };
        var databaseName = $"agw_jobs_test_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(adminSettings.ConnectionString))
        {
            await admin.OpenAsync(cancellationToken);
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync(cancellationToken);
        }

        var databaseSettings = new NpgsqlConnectionStringBuilder(adminSettings.ConnectionString)
        {
            Database = databaseName,
        };
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseNpgsql(databaseSettings.ConnectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(interceptors)
            .Options;
        var database = new PostgresJobTestDatabase(
            adminSettings.ConnectionString,
            databaseName,
            databaseSettings.ConnectionString,
            options
        );

        try
        {
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(cancellationToken);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    public AgwDbContext CreateContext() => new(Options);

    public ValueTask DisposeAsync() => DropDatabaseAsync(_adminConnectionString, _databaseName);

    private static async ValueTask DropDatabaseAsync(string adminConnectionString, string databaseName)
    {
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync(CancellationToken.None);
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
