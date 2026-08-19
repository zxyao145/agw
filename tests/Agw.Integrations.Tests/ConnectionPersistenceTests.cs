using Agw.Infrastructure.Data;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Projects;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using IntegrationConnection = Agw.Shared.Data.Entities.Integrations.Connection;

namespace Agw.Integrations.Tests;

public class ConnectionPersistenceTests
{
    [Fact]
    public void Model_ConnectionPersistence_ConfiguresTablesKeysAndUniqueIndexes()
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite("Data Source=:memory:")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var dbContext = new AgwDbContext(options);

        AssertEntity(dbContext.Model, typeof(PluginInstallation), "plugin_installation", ["Id"]);
        AssertEntity(dbContext.Model, typeof(PluginInstallationCredential), "plugin_installation_credential", ["Id"]);
        AssertEntity(dbContext.Model, typeof(IntegrationConnection), "integration_connection", ["Id"]);
        AssertEntity(dbContext.Model, typeof(ConnectionCredential), "integration_connection_credential", ["Id"]);
        AssertEntity(
            dbContext.Model,
            typeof(AgentConnectionRelation),
            "agent_connection_relation",
            ["AgentId", "ConnectionId"]
        );
        AssertEntity(
            dbContext.Model,
            typeof(ProjectConnectionRelation),
            "project_connection_relation",
            ["ProjectId", "ConnectionId"]
        );

        AssertUniqueIndex(dbContext.Model, typeof(PluginInstallation), ["PluginId"]);
        AssertUniqueIndex(dbContext.Model, typeof(IntegrationConnection), ["Alias"]);
        AssertUniqueIndex(dbContext.Model, typeof(PluginInstallationCredential), ["PluginInstallationId", "Slot"]);
        AssertUniqueIndex(dbContext.Model, typeof(ConnectionCredential), ["ConnectionId", "Slot"]);
        AssertIndex(dbContext.Model, typeof(AgentConnectionRelation), ["ConnectionId"]);
        AssertIndex(dbContext.Model, typeof(ProjectConnectionRelation), ["ConnectionId"]);

        AssertCascadeRelations(dbContext.Model, typeof(PluginInstallationCredential));
        AssertCascadeRelations(dbContext.Model, typeof(ConnectionCredential));
        AssertCascadeRelations(dbContext.Model, typeof(AgentConnectionRelation));
        AssertCascadeRelations(dbContext.Model, typeof(ProjectConnectionRelation));

        var connectionCredential = dbContext.Model.FindEntityType(typeof(ConnectionCredential));
        Assert.NotNull(connectionCredential);
        var valueProperty = connectionCredential.FindProperty(nameof(ConnectionCredential.Value));
        Assert.NotNull(valueProperty);
        Assert.False(valueProperty.IsNullable);
        Assert.Equal("protected_value", valueProperty.GetColumnName());
        Assert.Null(connectionCredential.FindProperty("Source"));
        Assert.Null(connectionCredential.FindProperty("EnvironmentVariableName"));
        Assert.Null(connectionCredential.FindProperty("DisplayHint"));
        Assert.Null(connectionCredential.FindProperty("Secret"));
    }

    [Fact]
    public async Task PluginInstallation_WhenPluginIdDuplicated_RejectsInsert()
    {
        await AssertUniqueConstraintAsync(context =>
            context.PluginInstallations.AddRange(CreatePluginInstallation("github"), CreatePluginInstallation("github"))
        );
    }

    [Fact]
    public async Task Connection_WhenAliasDuplicated_RejectsInsert()
    {
        await AssertUniqueConstraintAsync(context =>
            context.Connections.AddRange(CreateConnection("work"), CreateConnection("work"))
        );
    }

    [Fact]
    public async Task PluginInstallationCredential_WhenOwnerAndSlotDuplicated_RejectsInsert()
    {
        var installation = CreatePluginInstallation("github");

        await AssertUniqueConstraintAsync(context =>
        {
            context.PluginInstallations.Add(installation);
            context.PluginInstallationCredentials.AddRange(
                CreatePluginInstallationCredential(installation.Id, "client-secret"),
                CreatePluginInstallationCredential(installation.Id, "client-secret")
            );
        });
    }

    [Fact]
    public async Task ConnectionCredential_WhenOwnerAndSlotDuplicated_RejectsInsert()
    {
        var integrationConnection = CreateConnection("work");

        await AssertUniqueConstraintAsync(context =>
        {
            context.Connections.Add(integrationConnection);
            context.ConnectionCredentials.AddRange(
                CreateConnectionCredential(integrationConnection.Id, "access-token"),
                CreateConnectionCredential(integrationConnection.Id, "access-token")
            );
        });
    }

    [Fact]
    public async Task Connection_WhenDeletedWithoutForeignKeys_RemovesCredentialsAndBindings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var agent = CreateAgent();
        var project = CreateProject();
        var integrationConnection = CreateConnection("work");
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Agents.Add(agent);
            seedContext.Projects.Add(project);
            seedContext.Connections.Add(integrationConnection);
            seedContext.ConnectionCredentials.Add(CreateConnectionCredential(integrationConnection.Id, "access-token"));
            seedContext.AgentConnectionRelations.Add(
                new AgentConnectionRelation { AgentId = agent.Id, ConnectionId = integrationConnection.Id }
            );
            seedContext.ProjectConnectionRelations.Add(
                new ProjectConnectionRelation { ProjectId = project.Id, ConnectionId = integrationConnection.Id }
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var deleteContext = new AgwDbContext(options))
        {
            var entity = await deleteContext.Connections.FindAsync([integrationConnection.Id], cancellationToken);
            deleteContext.Connections.Remove(entity!);
            await deleteContext.SaveChangesAsync(cancellationToken);
        }

        await using var assertContext = new AgwDbContext(options);
        Assert.False(await assertContext.ConnectionCredentials.AnyAsync(cancellationToken));
        Assert.False(await assertContext.AgentConnectionRelations.AnyAsync(cancellationToken));
        Assert.False(await assertContext.ProjectConnectionRelations.AnyAsync(cancellationToken));
    }

    [Fact]
    public async Task PluginInstallation_WhenDeletedWithoutForeignKeys_RemovesCredentials()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var installation = CreatePluginInstallation("github");
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.PluginInstallations.Add(installation);
            seedContext.PluginInstallationCredentials.Add(
                CreatePluginInstallationCredential(installation.Id, "client-secret")
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var deleteContext = new AgwDbContext(options))
        {
            var entity = await deleteContext.PluginInstallations.FindAsync([installation.Id], cancellationToken);
            deleteContext.PluginInstallations.Remove(entity!);
            await deleteContext.SaveChangesAsync(cancellationToken);
        }

        await using var assertContext = new AgwDbContext(options);
        Assert.False(await assertContext.PluginInstallationCredentials.AnyAsync(cancellationToken));
    }

    [Fact]
    public async Task AgentAndProject_WhenDeletedWithoutForeignKeys_RemoveConnectionBindings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        var agent = CreateAgent();
        var project = CreateProject();
        var integrationConnection = CreateConnection("work");
        await using (var seedContext = new AgwDbContext(options))
        {
            seedContext.Agents.Add(agent);
            seedContext.Projects.Add(project);
            seedContext.Connections.Add(integrationConnection);
            seedContext.AgentConnectionRelations.Add(
                new AgentConnectionRelation { AgentId = agent.Id, ConnectionId = integrationConnection.Id }
            );
            seedContext.ProjectConnectionRelations.Add(
                new ProjectConnectionRelation { ProjectId = project.Id, ConnectionId = integrationConnection.Id }
            );
            await seedContext.SaveChangesAsync(cancellationToken);
        }

        await using (var deleteContext = new AgwDbContext(options))
        {
            deleteContext.Agents.Remove((await deleteContext.Agents.FindAsync([agent.Id], cancellationToken))!);
            deleteContext.Projects.Remove((await deleteContext.Projects.FindAsync([project.Id], cancellationToken))!);
            await deleteContext.SaveChangesAsync(cancellationToken);
        }

        await using var assertContext = new AgwDbContext(options);
        Assert.False(await assertContext.AgentConnectionRelations.AnyAsync(cancellationToken));
        Assert.False(await assertContext.ProjectConnectionRelations.AnyAsync(cancellationToken));
        Assert.True(await assertContext.Connections.AnyAsync(cancellationToken));
    }

    private static async Task AssertUniqueConstraintAsync(Action<AgwDbContext> arrange)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(cancellationToken);
        var options = CreateOptions(connection);
        await EnsureCreatedAsync(options, cancellationToken);

        await using var dbContext = new AgwDbContext(options);
        arrange(dbContext);

        await Assert.ThrowsAsync<DbUpdateException>(() => dbContext.SaveChangesAsync(cancellationToken));
    }

    private static DbContextOptions<AgwDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AgwDbContext>().UseSqlite(connection).UseSnakeCaseNamingConvention().Options;

    private static async Task EnsureCreatedAsync(
        DbContextOptions<AgwDbContext> options,
        CancellationToken cancellationToken
    )
    {
        await using var setupContext = new AgwDbContext(options);
        await setupContext.Database.EnsureCreatedAsync(cancellationToken);
    }

    private static PluginInstallation CreatePluginInstallation(string pluginId) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            PluginId = pluginId,
            Enabled = true,
            ConfigurationJson = "{}",
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private static PluginInstallationCredential CreatePluginInstallationCredential(Guid ownerId, string slot) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            PluginInstallationId = ownerId,
            Slot = slot,
            Value = "protected-payload",
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private static IntegrationConnection CreateConnection(string alias) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            PluginId = "github",
            ConnectorId = "github-cloud",
            AuthSchemeId = "oauth2",
            DisplayName = alias,
            Alias = alias,
            ConfigurationJson = "{}",
            Enabled = true,
            Status = ConnectionStatus.Ready,
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private static ConnectionCredential CreateConnectionCredential(Guid connectionId, string slot) =>
        new()
        {
            Id = Guid.CreateVersion7(),
            ConnectionId = connectionId,
            Slot = slot,
            Value = "protected-payload",
            ExpiresAtUtc = TimeProvider.System.GetUtcNow().AddHours(1),
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private static Agent CreateAgent() =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = $"agent-{Guid.CreateVersion7():N}",
            DisplayName = "Agent",
            Description = "Test agent",
            SystemPrompt = "Test prompt",
            Type = AgentType.System,
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private static Project CreateProject() =>
        new()
        {
            Id = Guid.CreateVersion7(),
            Name = $"project-{Guid.CreateVersion7():N}",
            CreateBy = "tester",
            CreateTime = TimeProvider.System.GetUtcNow(),
        };

    private static void AssertEntity(IModel model, Type clrType, string tableName, string[] primaryKey)
    {
        var entity = model.FindEntityType(clrType);
        Assert.NotNull(entity);
        Assert.Equal(tableName, entity.GetTableName());
        Assert.Equal(primaryKey, entity.FindPrimaryKey()!.Properties.Select(property => property.Name).ToArray());
    }

    private static void AssertUniqueIndex(IModel model, Type clrType, string[] properties)
    {
        var entity = model.FindEntityType(clrType);
        Assert.NotNull(entity);
        Assert.Contains(
            entity.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(property => property.Name).SequenceEqual(properties)
        );
    }

    private static void AssertIndex(IModel model, Type clrType, string[] properties)
    {
        var entity = model.FindEntityType(clrType);
        Assert.NotNull(entity);
        Assert.Contains(
            entity.GetIndexes(),
            index => index.Properties.Select(property => property.Name).SequenceEqual(properties)
        );
    }

    private static void AssertCascadeRelations(IModel model, Type clrType)
    {
        var entity = model.FindEntityType(clrType);
        Assert.NotNull(entity);
        Assert.NotEmpty(entity.GetForeignKeys());
        Assert.All(
            entity.GetForeignKeys(),
            foreignKey => Assert.Equal(DeleteBehavior.Cascade, foreignKey.DeleteBehavior)
        );
    }
}
