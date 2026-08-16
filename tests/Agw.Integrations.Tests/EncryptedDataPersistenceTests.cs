using System.Reflection;
using System.Text.Json;

using Agw.Infrastructure.Data;
using Agw.Infrastructure.Data.Encryption;
using Agw.Shared.Data.Encryption;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Tools;
using Agw.Shared.Exceptions;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Agw.Integrations.Tests;

public class EncryptedDataPersistenceTests
{
    [Fact]
    public async Task SaveChanges_EncryptedFields_StoresCiphertextAndMaterializesPlaintext()
    {
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        var protector = CreateProtector();

        var providerAuthConfig = new ProviderAuthConfig
        {
            Id = Guid.CreateVersion7(),
            ProviderId = Guid.CreateVersion7(),
            ApiKey = "provider-secret"
        };
        var mcpServer = new McpServer
        {
            Id = Guid.CreateVersion7(),
            Name = "encrypted-headers",
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer header-secret",
                ["X-Api-Key"] = "header-api-key"
            }
        };
        var pluginCredential = new PluginInstallationCredential
        {
            Id = Guid.CreateVersion7(),
            PluginInstallationId = Guid.CreateVersion7(),
            Slot = "client-secret",
            Value = "plugin-secret"
        };
        var connectionCredential = new ConnectionCredential
        {
            Id = Guid.CreateVersion7(),
            ConnectionId = Guid.CreateVersion7(),
            Slot = "access-token",
            Value = "connection-secret"
        };

        await using (var context = new AgwDbContext(options, protector))
        {
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            context.AddRange(providerAuthConfig, mcpServer, pluginCredential, connectionCredential);

            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal("provider-secret", providerAuthConfig.ApiKey);
            Assert.Equal("Bearer header-secret", mcpServer.Headers["Authorization"]);
            Assert.Equal("plugin-secret", pluginCredential.Value);
            Assert.Equal("connection-secret", connectionCredential.Value);
        }

        var storedApiKey = await ReadScalarAsync(connection, "SELECT api_key FROM provider_auth_config");
        var storedHeadersJson = await ReadScalarAsync(connection, "SELECT headers FROM mcp_server");
        var storedPluginCredential = await ReadScalarAsync(
            connection,
            "SELECT protected_value FROM plugin_installation_credential");
        var storedConnectionCredential = await ReadScalarAsync(
            connection,
            "SELECT protected_value FROM integration_connection_credential");

        AssertCiphertext(storedApiKey, "provider-secret");
        AssertCiphertext(storedPluginCredential, "plugin-secret");
        AssertCiphertext(storedConnectionCredential, "connection-secret");

        var storedHeaders = JsonSerializer.Deserialize<Dictionary<string, string>>(storedHeadersJson);
        Assert.NotNull(storedHeaders);
        Assert.Equal(["Authorization", "X-Api-Key"], storedHeaders.Keys.OrderBy(key => key));
        AssertCiphertext(storedHeaders["Authorization"], "Bearer header-secret");
        AssertCiphertext(storedHeaders["X-Api-Key"], "header-api-key");

        await using var verifyContext = new AgwDbContext(options, protector);
        var trackedAuthConfig = await verifyContext.ProviderAuthConfigs
            .SingleAsync(TestContext.Current.CancellationToken);
        var untrackedMcpServer = await verifyContext.McpToolServers
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        var reloadedPluginCredential = await verifyContext.PluginInstallationCredentials
            .SingleAsync(TestContext.Current.CancellationToken);
        var reloadedConnectionCredential = await verifyContext.ConnectionCredentials
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal("provider-secret", trackedAuthConfig.ApiKey);
        Assert.Equal("Bearer header-secret", untrackedMcpServer.Headers["Authorization"]);
        Assert.Equal("header-api-key", untrackedMcpServer.Headers["X-Api-Key"]);
        Assert.Equal("plugin-secret", reloadedPluginCredential.Value);
        Assert.Equal("connection-secret", reloadedConnectionCredential.Value);
    }

    [Fact]
    public async Task SaveChanges_ModifiedStringAndDictionary_StoresNewCiphertextAndKeepsPlaintextTracked()
    {
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        var protector = CreateProtector();
        var authConfig = new ProviderAuthConfig
        {
            Id = Guid.CreateVersion7(),
            ProviderId = Guid.CreateVersion7(),
            ApiKey = "old-api-key"
        };
        var mcpServer = new McpServer
        {
            Id = Guid.CreateVersion7(),
            Name = "modified-headers",
            Headers = new Dictionary<string, string> { ["Authorization"] = "old-header" }
        };

        await using (var context = new AgwDbContext(options, protector))
        {
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            context.AddRange(authConfig, mcpServer);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            authConfig.ApiKey = "new-api-key";
            mcpServer.Headers["Authorization"] = "new-header";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);

            Assert.Equal("new-api-key", authConfig.ApiKey);
            Assert.Equal("new-header", mcpServer.Headers["Authorization"]);
        }

        AssertCiphertext(
            await ReadScalarAsync(connection, "SELECT api_key FROM provider_auth_config"),
            "new-api-key");
        var storedHeadersJson = await ReadScalarAsync(connection, "SELECT headers FROM mcp_server");
        var storedHeaders = JsonSerializer.Deserialize<Dictionary<string, string>>(storedHeadersJson);
        Assert.NotNull(storedHeaders);
        AssertCiphertext(storedHeaders["Authorization"], "new-header");

        await using var verifyContext = new AgwDbContext(options, protector);
        Assert.Equal(
            "new-api-key",
            (await verifyContext.ProviderAuthConfigs.SingleAsync(TestContext.Current.CancellationToken)).ApiKey);
        Assert.Equal(
            "new-header",
            (await verifyContext.McpToolServers.SingleAsync(TestContext.Current.CancellationToken))
                .Headers["Authorization"]);
    }

    [Fact]
    public async Task SaveChanges_AcceptAllChangesFalse_KeepsPlaintextAndEntityState()
    {
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        var protector = CreateProtector();
        var credential = new ConnectionCredential
        {
            Id = Guid.CreateVersion7(),
            ConnectionId = Guid.CreateVersion7(),
            Slot = "access-token",
            Value = "plaintext-token"
        };

        await using var context = new AgwDbContext(options, protector);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        context.ConnectionCredentials.Add(credential);

        await context.SaveChangesAsync(
            acceptAllChangesOnSuccess: false,
            TestContext.Current.CancellationToken);

        Assert.Equal("plaintext-token", credential.Value);
        Assert.Equal(EntityState.Added, context.Entry(credential).State);
        AssertCiphertext(
            await ReadScalarAsync(connection, "SELECT protected_value FROM integration_connection_credential"),
            "plaintext-token");
    }

    [Fact]
    public async Task SaveChanges_WhenDatabaseWriteFails_RestoresPlaintext()
    {
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        var protector = CreateProtector();
        var ownerId = Guid.CreateVersion7();
        var first = new PluginInstallationCredential
        {
            Id = Guid.CreateVersion7(),
            PluginInstallationId = ownerId,
            Slot = "duplicate-slot",
            Value = "first-secret"
        };
        var second = new PluginInstallationCredential
        {
            Id = Guid.CreateVersion7(),
            PluginInstallationId = ownerId,
            Slot = "duplicate-slot",
            Value = "second-secret"
        };

        await using var context = new AgwDbContext(options, protector);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        context.PluginInstallationCredentials.AddRange(first, second);

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            context.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal("first-secret", first.Value);
        Assert.Equal("second-secret", second.Value);
        Assert.Equal(EntityState.Added, context.Entry(first).State);
        Assert.Equal(EntityState.Added, context.Entry(second).State);
    }

    [Fact]
    public async Task SaveChanges_EmptyEntityId_ThrowsBeforeWritingAndKeepsPlaintext()
    {
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        var validCredential = new ConnectionCredential
        {
            Id = Guid.CreateVersion7(),
            ConnectionId = Guid.CreateVersion7(),
            Slot = "refresh-token",
            Value = "valid-plaintext-token"
        };
        var invalidCredential = new ConnectionCredential
        {
            Id = Guid.CreateVersion7(),
            ConnectionId = Guid.CreateVersion7(),
            Slot = "access-token",
            Value = "invalid-plaintext-token"
        };

        await using var context = new AgwDbContext(options, CreateProtector());
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        context.ConnectionCredentials.AddRange(validCredential, invalidCredential);
        invalidCredential.Id = Guid.Empty;

        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.SaveChangesAsync(TestContext.Current.CancellationToken));

        Assert.Equal(ErrorCodes.EncryptedModelInvalid.Code, exception.Code);
        Assert.Contains("non-empty Guid", exception.Message, StringComparison.Ordinal);
        Assert.Equal("valid-plaintext-token", validCredential.Value);
        Assert.Equal("invalid-plaintext-token", invalidCredential.Value);
        Assert.Equal(0, await CountRowsAsync(connection, "integration_connection_credential"));
    }

    [Fact]
    public void Model_EncryptedAttributeInventory_IsExact()
    {
        var encryptedProperties = typeof(ProviderAuthConfig).Assembly.GetTypes()
            .SelectMany(type => type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(property => property.GetCustomAttribute<EncryptedAttribute>() != null)
                .Select(property => $"{type.FullName}.{property.Name}"))
            .OrderBy(name => name)
            .ToArray();

        Assert.Equal(
            new[]
            {
                $"{typeof(AgentflowCheckpointRecord).FullName}.{nameof(AgentflowCheckpointRecord.CheckpointJson)}",
                $"{typeof(McpServer).FullName}.{nameof(McpServer.Headers)}",
                $"{typeof(DurableExecutionRecord).FullName}.{nameof(DurableExecutionRecord.CheckpointJson)}",
                $"{typeof(DurableExecutionRecord).FullName}.{nameof(DurableExecutionRecord.ErrorMessage)}",
                $"{typeof(DurableExecutionRecord).FullName}.{nameof(DurableExecutionRecord.ManifestJson)}",
                $"{typeof(DurableExecutionRecord).FullName}.{nameof(DurableExecutionRecord.PendingInteractionsJson)}",
                $"{typeof(DurableExecutionRecord).FullName}.{nameof(DurableExecutionRecord.ResponsesJson)}",
                $"{typeof(DurableExecutionEventRecord).FullName}.{nameof(DurableExecutionEventRecord.PayloadJson)}",
                $"{typeof(ConnectionCredential).FullName}.{nameof(ConnectionCredential.Value)}",
                $"{typeof(PluginInstallationCredential).FullName}.{nameof(PluginInstallationCredential.Value)}",
                $"{typeof(ProviderAuthConfig).FullName}.{nameof(ProviderAuthConfig.ApiKey)}",
                $"{typeof(UserMemory).FullName}.{nameof(UserMemory.Content)}"
            }.OrderBy(name => name),
            encryptedProperties);
    }

    [Fact]
    public void Model_EncryptedUnsupportedType_Throws()
    {
        using var context = new ValidationDbContext<UnsupportedEncryptedEntity>(
            configure: builder => builder.HasKey(entity => entity.Id));

        var exception = Assert.Throws<AgwException>(() => _ = context.Model);

        Assert.Equal(ErrorCodes.EncryptedModelInvalid.Code, exception.Code);
        Assert.Contains("unsupported type", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Model_EncryptedIndexedProperty_Throws()
    {
        using var context = new ValidationDbContext<IndexedEncryptedEntity>(configure: builder =>
        {
            builder.HasKey(entity => entity.Id);
            builder.HasIndex(entity => entity.Secret);
        });

        var exception = Assert.Throws<AgwException>(() => _ = context.Model);

        Assert.Equal(ErrorCodes.EncryptedModelInvalid.Code, exception.Code);
        Assert.Contains("cannot participate in a key or index", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_EncryptedEntityWithoutSingleGuidKey_Throws()
    {
        using var context = new ValidationDbContext<CompositeKeyEncryptedEntity>(configure: builder =>
            builder.HasKey(entity => new { entity.Id, entity.Sequence }));

        var exception = Assert.Throws<AgwException>(() => _ = context.Model);

        Assert.Equal(ErrorCodes.EncryptedModelInvalid.Code, exception.Code);
        Assert.Contains("single Guid primary key", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Materialization_InvalidEnvelope_ThrowsWithoutPlaintextFallback()
    {
        await using var connection = await OpenConnectionAsync();
        var options = CreateOptions(connection);
        var id = Guid.CreateVersion7();

        await using (var setupContext = new AgwDbContext(options, CreateProtector()))
        {
            await setupContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        await ExecuteAsync(
            connection,
            "INSERT INTO provider_auth_config (id, provider_id, auth_type, api_key, enable, create_time, update_time) "
            + "VALUES ($id, $providerId, 0, 'legacy-plaintext', 1, $now, $now)",
            ("$id", id),
            ("$providerId", Guid.CreateVersion7()),
            ("$now", DateTimeOffset.UtcNow));

        await using var context = new AgwDbContext(options, CreateProtector());
        var exception = await Assert.ThrowsAsync<AgwException>(() =>
            context.ProviderAuthConfigs.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken));
        Assert.Equal(ErrorCodes.EncryptedDataInvalid.Code, exception.Code);
    }

    private static DbContextOptions<AgwDbContext> CreateOptions(SqliteConnection connection) =>
        new DbContextOptionsBuilder<AgwDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

    private static DataProtectionEncryptedDataProtector CreateProtector() =>
        new(new EphemeralDataProtectionProvider());

    private static async Task<SqliteConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=False");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task<string> ReadScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken)
            ?? throw new InvalidOperationException("No value was stored."));
    }

    private static async Task<long> CountRowsAsync(SqliteConnection connection, string tableName)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {tableName}";
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? 0L);
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static void AssertCiphertext(string storedValue, string plaintext)
    {
        Assert.StartsWith(DataProtectionEncryptedDataProtector.EnvelopePrefix, storedValue, StringComparison.Ordinal);
        Assert.DoesNotContain(plaintext, storedValue, StringComparison.Ordinal);
    }

    private sealed class ValidationDbContext<TEntity> : DbContext
        where TEntity : class
    {
        private readonly Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity>> _configure;

        public ValidationDbContext(
            Action<Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity>> configure)
            : base(new DbContextOptionsBuilder<ValidationDbContext<TEntity>>()
                .UseSqlite("Data Source=:memory:")
                .Options)
        {
            _configure = configure;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            var builder = modelBuilder.Entity<TEntity>();
            builder.ToTable(typeof(TEntity).Name);
            _configure(builder);
            EncryptedEntityMetadata.Validate(modelBuilder);
        }
    }

    private sealed class UnsupportedEncryptedEntity
    {
        public Guid Id { get; set; }
        [Encrypted]
        public int Secret { get; set; }
    }

    private sealed class IndexedEncryptedEntity
    {
        public Guid Id { get; set; }
        [Encrypted]
        public string Secret { get; set; } = string.Empty;
    }

    private sealed class CompositeKeyEncryptedEntity
    {
        public Guid Id { get; set; }
        public int Sequence { get; set; }
        [Encrypted]
        public string Secret { get; set; } = string.Empty;
    }
}
