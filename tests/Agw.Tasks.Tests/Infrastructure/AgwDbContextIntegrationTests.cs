using Agw.Infrastructure.Data;
using Agw.Shared.Contracts.Agents;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Infrastructure.Tests;

public class AgwDbContextIntegrationTests
{
    [Fact]
    public async Task AgentAppRelation_WhenDuplicatePairInserted_RejectsInsert()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var agentId = Guid.NewGuid();
        var appInstanceId = Guid.NewGuid();

        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Agents.Add(new Agent
            {
                Id = agentId,
                Name = "agent-1",
                DisplayName = "Agent 1",
                Description = "desc",
                SystemPrompt = "prompt",
                Type = AgentType.System,
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });
            seedContext.AppInstances.Add(new AppInstance
            {
                Id = appInstanceId,
                AppName = "github",
                ClientId = "client-1",
                ClientSecret = "secret-1",
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });
            seedContext.Set<AgentAppRelation>().Add(new AgentAppRelation
            {
                AgentId = agentId,
                AppInstanceId = appInstanceId
            });

            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        dbContext.Set<AgentAppRelation>().Add(new AgentAppRelation
        {
            AgentId = agentId,
            AppInstanceId = appInstanceId
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(cancellationToken));
    }

    [Fact]
    public async Task AgentAppRelation_WhenAppInstanceDeleted_RemovesRelations()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var agentId = Guid.NewGuid();
        var appInstanceId = Guid.NewGuid();

        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Agents.Add(new Agent
            {
                Id = agentId,
                Name = "agent-1",
                DisplayName = "Agent 1",
                Description = "desc",
                SystemPrompt = "prompt",
                Type = AgentType.System,
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });
            seedContext.AppInstances.Add(new AppInstance
            {
                Id = appInstanceId,
                AppName = "github",
                ClientId = "client-1",
                ClientSecret = "secret-1",
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });
            seedContext.Set<AgentAppRelation>().Add(new AgentAppRelation
            {
                AgentId = agentId,
                AppInstanceId = appInstanceId
            });

            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var deleteContext = new AgwDbContext(options))
        {
            var appInstance = await deleteContext.AppInstances.FindAsync([appInstanceId], cancellationToken);
            deleteContext.AppInstances.Remove(appInstance!);
            await deleteContext.SaveChangesAsync(cancellationToken);
        }

        await using var assertContext = new AgwDbContext(options);
        Assert.False(await assertContext.Set<AgentAppRelation>().AnyAsync(cancellationToken));
    }

    [Fact]
    public async Task AppInstance_WhenAppNameDuplicatesAndClientIdDiffers_AllowsInsert()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        await using (var dbContext = new AgwDbContext(options))
        {
            dbContext.AppInstances.AddRange(
                new AppInstance
                {
                    Id = Guid.NewGuid(),
                    AppName = "github",
                    ClientId = "client-1",
                    ClientSecret = "secret-1",
                    CreateBy = "tester",
                    CreateTime = DateTime.UtcNow
                },
                new AppInstance
                {
                    Id = Guid.NewGuid(),
                    AppName = "github",
                    ClientId = "client-2",
                    ClientSecret = "secret-2",
                    CreateBy = "tester",
                    CreateTime = DateTime.UtcNow
                });

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await using var assertContext = new AgwDbContext(options);
        var instances = await assertContext.AppInstances
            .Where(instance => instance.AppName == "github")
            .ToListAsync(cancellationToken);

        Assert.Equal(2, instances.Count);
    }

    [Fact]
    public async Task AppInstance_WhenClientIdDuplicates_RejectsInsert()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var sharedClientId = "shared-client";
        await using var dbContext = new AgwDbContext(options);
        dbContext.AppInstances.AddRange(
            new AppInstance
            {
                Id = Guid.NewGuid(),
                AppName = "github",
                ClientId = sharedClientId,
                ClientSecret = "secret-1",
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            },
            new AppInstance
            {
                Id = Guid.NewGuid(),
                AppName = "google-workspace",
                ClientId = sharedClientId,
                ClientSecret = "secret-2",
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(cancellationToken));
    }

    [Fact]
    public async Task OAuthAuthorizationToken_WhenSecondTokenUsesSameAppInstance_RejectsInsert()
    {
        var cancellationToken = TestContext.Current.CancellationToken;

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);

        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using (var setupContext = new AgwDbContext(options))
        {
            await setupContext.Database.EnsureCreatedAsync(cancellationToken);
        }

        var appInstanceId = Guid.NewGuid();
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.AppInstances.Add(new AppInstance
            {
                Id = appInstanceId,
                AppName = "github",
                ClientId = "client-1",
                ClientSecret = "secret-1",
                CreateBy = "tester",
                CreateTime = DateTime.UtcNow
            });

            seedContext.OAuthAuthorizationTokens.Add(new OAuthAuthorizationToken
            {
                Id = Guid.NewGuid(),
                AppInstanceId = appInstanceId,
                Subject = "subject-1",
                AccessToken = "access-token-1"
            });

            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using var dbContext = new AgwDbContext(options);
        dbContext.OAuthAuthorizationTokens.Add(new OAuthAuthorizationToken
        {
            Id = Guid.NewGuid(),
            AppInstanceId = appInstanceId,
            Subject = "subject-2",
            AccessToken = "access-token-2"
        });

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(cancellationToken));
    }
}
