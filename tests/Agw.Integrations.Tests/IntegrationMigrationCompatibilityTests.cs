using Agw.Infrastructure.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agw.Integrations.Tests;

public sealed class IntegrationMigrationCompatibilityTests
{
    private const string PreviousMigration = "20260714024630_SeparateAgentUsage";
    private const string IntegrationMigration = "20260715074414_RefactorIntegrationsToPluginConnections";
    private const string EncryptedCredentialMigration =
        "20260715130104_EnforceEncryptedIntegrationCredentialStorage";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerateScript_SqliteAndPostgres_ContainsOnlyIntegrationTables(bool usePostgres)
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>();
        if (usePostgres)
        {
            options.UseNpgsql("Host=localhost;Database=agw;Username=agw;Password=unused");
        }
        else
        {
            options.UseSqlite("Data Source=:memory:");
        }

        options.UseSnakeCaseNamingConvention();
        using var dbContext = new AgwDbContext(options.Options);

        var script = dbContext.GetService<IMigrator>().GenerateScript(
            PreviousMigration,
            IntegrationMigration,
            MigrationsSqlGenerationOptions.NoTransactions);

        Assert.Contains("integration_connection", script, StringComparison.Ordinal);
        Assert.Contains("plugin_installation", script, StringComparison.Ordinal);
        Assert.Contains("agent_connection_relation", script, StringComparison.Ordinal);
        Assert.Contains("project_connection_relation", script, StringComparison.Ordinal);
        Assert.Contains("environment_variable_name", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("source", script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("display_hint", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE \"agent\"", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE \"project\"", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE \"job\"", script, StringComparison.OrdinalIgnoreCase);
        if (usePostgres)
        {
            Assert.Contains("id uuid", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("enabled boolean", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("protected_value text", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("timestamp with time zone", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("enabled INTEGER", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Stores platform-wide plugin installation configuration.", script, StringComparison.Ordinal);
            Assert.Contains("Stores protected credentials owned by a plugin installation.", script, StringComparison.Ordinal);
            Assert.Contains("Represents an external account or service endpoint available to agents.", script, StringComparison.Ordinal);
            Assert.Contains("Stores protected credentials owned by an integration connection.", script, StringComparison.Ordinal);
            Assert.Contains("Binds an agent to an integration connection.", script, StringComparison.Ordinal);
            Assert.Contains("Binds a project to an integration connection.", script, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("\"id\" TEXT", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"enabled\" INTEGER", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"protected_value\" TEXT", script, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GenerateScript_EncryptedCredentialMigration_RemovesLegacyCredentialColumns(bool usePostgres)
    {
        var options = new DbContextOptionsBuilder<AgwDbContext>();
        if (usePostgres)
        {
            options.UseNpgsql("Host=localhost;Database=agw;Username=agw;Password=unused");
        }
        else
        {
            options.UseSqlite("Data Source=:memory:");
        }

        options.UseSnakeCaseNamingConvention();
        using var dbContext = new AgwDbContext(options.Options);

        var script = dbContext.GetService<IMigrator>().GenerateScript(
            IntegrationMigration,
            EncryptedCredentialMigration,
            MigrationsSqlGenerationOptions.NoTransactions);

        Assert.Contains(
            "DELETE FROM plugin_installation_credential WHERE protected_value IS NULL OR protected_value = ''",
            script,
            StringComparison.Ordinal);
        Assert.Contains(
            "DELETE FROM integration_connection_credential WHERE protected_value IS NULL OR protected_value = ''",
            script,
            StringComparison.Ordinal);
        Assert.Contains(EncryptedCredentialMigration, script, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER TABLE \"agent\"", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE \"project\"", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ALTER TABLE \"job\"", script, StringComparison.OrdinalIgnoreCase);

        if (usePostgres)
        {
            Assert.Contains("DROP COLUMN display_hint", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DROP COLUMN environment_variable_name", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("DROP COLUMN source", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("protected_value SET NOT NULL", script, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Contains("ef_temp_plugin_installation_credential", script, StringComparison.Ordinal);
            Assert.Contains("ef_temp_integration_connection_credential", script, StringComparison.Ordinal);
            Assert.DoesNotContain("\"display_hint\" TEXT", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"environment_variable_name\" TEXT", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("\"source\" TEXT", script, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"protected_value\" TEXT NOT NULL", script, StringComparison.OrdinalIgnoreCase);
        }
    }
}
