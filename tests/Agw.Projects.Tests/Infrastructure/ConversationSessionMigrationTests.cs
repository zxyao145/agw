using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agents;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agw.Infrastructure.Tests;

public sealed partial class InitialMigrationTests
{
    [Fact]
    public async Task MigrateAsync_ConversationGeneration_PreservesLegacyConversationAndEncryptedSession()
    {
        var token = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(token);
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(
                connection,
                builder => builder.MigrationsAssembly(AgwDbContextOptionsConfigurator.SqliteMigrationsAssembly)
            )
            .UseSnakeCaseNamingConvention()
            .Options;
        await using var context = new AgwDbContext(options);
        var migrator = context.GetService<IMigrator>();
        var migrations = context.Database.GetMigrations().ToArray();
        var addedGeneration = Array.FindIndex(
            migrations,
            name => name.EndsWith("_ConversationSessionGeneration", StringComparison.Ordinal)
        );
        await migrator.MigrateAsync(migrations[addedGeneration - 1], token);
        var projectId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO project (id,name,type,tools,environment_variables,create_by,create_time) VALUES ({projectId},'legacy',0,'[]','{{}}','tester',{TimeProvider.System.GetUtcNow()})",
            token
        );
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO project_conversation (id,project_id,context_id,title,create_by,create_time) VALUES ({conversationId},{projectId},'legacy-context','legacy-title','tester',{TimeProvider.System.GetUtcNow()})",
            token
        );
        context.AgentSessionStates.Add(
            new AgentSessionStateEntry
            {
                ProjectConversationId = conversationId,
                AgentId = Guid.NewGuid(),
                SerializedSession = "old-sdk-state",
            }
        );
        await context.SaveChangesAsync(token);
        await using var state = connection.CreateCommand();
        state.CommandText = "SELECT serialized_session FROM agent_session_state";
        var encrypted = await state.ExecuteScalarAsync(token);

        await migrator.MigrateAsync(migrations[addedGeneration], token);
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT generation || '|' || context_id || '|' || title FROM project_conversation";
        Assert.Equal("0|legacy-context|legacy-title", await inspect.ExecuteScalarAsync(token));
        Assert.False(await ColumnExistsAsync(connection, "project_conversation", "session_generation", token));
        Assert.Equal(encrypted, await state.ExecuteScalarAsync(token));

        await context.Database.ExecuteSqlRawAsync("UPDATE project_conversation SET generation = 7", token);
        Assert.Equal("7|legacy-context|legacy-title", await inspect.ExecuteScalarAsync(token));
        await migrator.MigrateAsync(migrations[addedGeneration - 1], token);
        Assert.False(await ColumnExistsAsync(connection, "project_conversation", "generation", token));
        Assert.Equal(encrypted, await state.ExecuteScalarAsync(token));
        await migrator.MigrateAsync(migrations[addedGeneration], token);
        Assert.Equal("0|legacy-context|legacy-title", await inspect.ExecuteScalarAsync(token));
    }
}
