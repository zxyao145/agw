using System.Security.Cryptography;
using System.Text;
using Agw.Infrastructure.Data;
using Agw.Setup.Services;
using Agw.Shared;
using Agw.Shared.Data.Entities.Auth;
using Agw.Shared.Exceptions;
using Agw.Shared.Runtime;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agw.Setup.Tests;

public sealed class LegacyApiTokenMigratorTests
{
    private static readonly Guid LegacyTokenId = Guid.Parse("0198b7b8-a50c-7f6e-a50d-b46e722a6622");
    private static readonly DateTimeOffset LegacyCreatedAt = new(2026, 7, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MigrateAsync_ImportsLegacyHashAndMetadataThenRemovesStateTokens()
    {
        var paths = CreatePaths();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        try
        {
            var token = "agw_legacy-secret";
            await WriteLegacyStateAsync(paths, token);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            var stateStore = new JsonInitializationStateStore(paths);
            var migrator = new LegacyApiTokenMigrator(stateStore, context, NullLogger<LegacyApiTokenMigrator>.Instance);

            var migratedCount = await migrator.MigrateAsync(TestContext.Current.CancellationToken);
            context.ChangeTracker.Clear();

            var persisted = await context.ApiTokens.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, migratedCount);
            Assert.Equal(LegacyTokenId, persisted.Id);
            Assert.Equal("Legacy desktop", persisted.Name);
            Assert.Equal(Constants.AdminUserId, persisted.CreateBy);
            Assert.Equal(LegacyCreatedAt, persisted.CreateTime);
            Assert.Equal(Hash(token), persisted.SecretHash);
            Assert.Empty(stateStore.GetLegacyApiTokens());
            Assert.DoesNotContain(
                "\"tokens\"",
                await File.ReadAllTextAsync(paths.StateFile, TestContext.Current.CancellationToken)
            );
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task MigrateAsync_AfterDatabaseWriteWasAlreadyCompleted_ClearsLegacyState()
    {
        var paths = CreatePaths();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        try
        {
            var token = "agw_legacy-secret";
            await WriteLegacyStateAsync(paths, token);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            context.ApiTokens.Add(
                new ApiToken
                {
                    Id = LegacyTokenId,
                    Name = "Legacy desktop",
                    NormalizedName = "LEGACY DESKTOP",
                    Prefix = token[..12],
                    SecretHash = Hash(token),
                    CreateBy = Constants.AdminUserId,
                    CreateTime = LegacyCreatedAt,
                }
            );
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            var stateStore = new JsonInitializationStateStore(paths);
            var migrator = new LegacyApiTokenMigrator(stateStore, context, NullLogger<LegacyApiTokenMigrator>.Instance);

            var migratedCount = await migrator.MigrateAsync(TestContext.Current.CancellationToken);

            Assert.Equal(1, migratedCount);
            Assert.Empty(stateStore.GetLegacyApiTokens());
            Assert.Equal(1, await context.ApiTokens.CountAsync(TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    [Fact]
    public async Task MigrateAsync_WhenPersistedTokenConflictsWithLegacyState_ThrowsConflict()
    {
        var paths = CreatePaths();
        await using var connection = new SqliteConnection("Data Source=:memory:");
        try
        {
            var token = "agw_legacy-secret";
            await WriteLegacyStateAsync(paths, token);
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<AgwDbContext>()
                .UseSqlite(connection)
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var context = new AgwDbContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            context.ApiTokens.Add(
                new ApiToken
                {
                    Id = LegacyTokenId,
                    Name = "Conflicting token",
                    NormalizedName = "CONFLICTING TOKEN",
                    Prefix = token[..12],
                    SecretHash = Hash(token),
                    CreateBy = Constants.AdminUserId,
                    CreateTime = LegacyCreatedAt,
                }
            );
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
            var stateStore = new JsonInitializationStateStore(paths);
            var migrator = new LegacyApiTokenMigrator(stateStore, context, NullLogger<LegacyApiTokenMigrator>.Instance);

            var exception = await Assert.ThrowsAsync<AgwException>(() =>
                migrator.MigrateAsync(TestContext.Current.CancellationToken)
            );

            Assert.Equal(ErrorCodes.LegacyApiTokenConflict.Code, exception.Code);
            Assert.NotEmpty(stateStore.GetLegacyApiTokens());
        }
        finally
        {
            Directory.Delete(paths.Root, recursive: true);
        }
    }

    private static async Task WriteLegacyStateAsync(AgwDataPaths paths, string token)
    {
        await File.WriteAllTextAsync(
            paths.StateFile,
            $$"""
            {
              "schemaVersion": 1,
              "isInitialized": true,
              "database": {
                "provider": "sqlite",
                "connectionString": "Data Source=agw.db"
              },
              "passwordHash": "hash",
              "sessionVersion": 1,
              "tokens": [
                {
                  "id": "{{LegacyTokenId}}",
                  "name": "Legacy desktop",
                  "prefix": "{{token[..12]}}",
                  "secretHash": "{{Hash(token)}}",
                  "createdAt": "{{LegacyCreatedAt:O}}"
                }
              ]
            }
            """,
            TestContext.Current.CancellationToken
        );
    }

    private static AgwDataPaths CreatePaths()
    {
        var root = Path.Combine(Path.GetTempPath(), $"agw-legacy-token-{Guid.CreateVersion7():N}");
        var paths = AgwDataPaths.Resolve(root, "/unused");
        paths.EnsureCreated();
        return paths;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
