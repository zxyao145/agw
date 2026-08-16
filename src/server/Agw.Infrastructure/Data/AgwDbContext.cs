using Agw.Infrastructure.Data.Encryption;
using Agw.Shared.Data.Abstractions;
using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Auth;
using Agw.Shared.Data.Entities.Executions;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Entities.Tools;

using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agw.Infrastructure.Data;

public class AgwDbContext : EFContext
{
    private static readonly IEncryptedDataProtector DefaultEncryptedDataProtector =
        new DataProtectionEncryptedDataProtector(new EphemeralDataProtectionProvider());
    private static readonly EncryptedMaterializationInterceptor MaterializationInterceptor = new();

    private readonly EncryptedPropertyProcessor _encryptedPropertyProcessor;

    public AgwDbContext(DbContextOptions<AgwDbContext> options)
        : this(options, DefaultEncryptedDataProtector)
    {
    }

    public AgwDbContext(
        DbContextOptions<AgwDbContext> options,
        IEncryptedDataProtector encryptedDataProtector)
        : base(options)
    {
        _encryptedPropertyProcessor = new EncryptedPropertyProcessor(encryptedDataProtector);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PruneDeletedRelations();
        StampJobRowVersions();
        return SaveChangesWithEncryption(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PruneDeletedRelations();
        StampJobRowVersions();
        return SaveChangesWithEncryptionAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<ProviderAuthConfig> ProviderAuthConfigs => Set<ProviderAuthConfig>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();

    public DbSet<AgwAiModel> Models => Set<AgwAiModel>();
    public DbSet<ModelProviderRelation> ModelProviders => Set<ModelProviderRelation>();

    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentConnectionRelation> AgentConnectionRelations => Set<AgentConnectionRelation>();
    public DbSet<AgentSessionStateEntry> AgentSessionStates => Set<AgentSessionStateEntry>();

    /// <summary>
    /// 获取 distributed execution 的 PostgreSQL 单行状态机集合。
    /// </summary>
    public DbSet<DurableExecutionRecord> DurableExecutions => Set<DurableExecutionRecord>();

    public DbSet<AgentflowCheckpointRecord> AgentflowCheckpoints =>
        Set<AgentflowCheckpointRecord>();

    /// <summary>
    /// 获取 PostgreSQL event stream 实现的 append-only execution 消息集合。
    /// </summary>
    public DbSet<DurableExecutionEventRecord> DurableExecutionEvents =>
        Set<DurableExecutionEventRecord>();

    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<RemoteSkillCache> RemoteSkillCaches => Set<RemoteSkillCache>();
    public DbSet<AgentSkillRelation> AgentSkillRelations => Set<AgentSkillRelation>();

    public DbSet<McpServer> McpToolServers => Set<McpServer>();
    public DbSet<AgentMcpServerRelation> AgentMcpToolServers => Set<AgentMcpServerRelation>();

    public DbSet<Agentflow> Agentflows => Set<Agentflow>();
    public DbSet<AgentflowNode> AgentflowNodes => Set<AgentflowNode>();
    public DbSet<AgentflowEdge> AgentflowEdges => Set<AgentflowEdge>();
    public DbSet<AgentflowTrace> AgentflowNodeExecutionTraces => Set<AgentflowTrace>();

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectMemoryEntry> ProjectMemories => Set<ProjectMemoryEntry>();
    public DbSet<UserMemory> UserMemories => Set<UserMemory>();
    public DbSet<ProjectSkillRelation> ProjectSkillRelations => Set<ProjectSkillRelation>();
    public DbSet<ProjectMcpServerRelation> ProjectMcpToolServers => Set<ProjectMcpServerRelation>();
    public DbSet<ProjectConnectionRelation> ProjectConnectionRelations => Set<ProjectConnectionRelation>();
    public DbSet<ProjectConversation> ProjectConversations => Set<ProjectConversation>();
    public DbSet<AgentUsage> AgentUsages => Set<AgentUsage>();
    public DbSet<TaskSessionBinding> TaskSessionBindings => Set<TaskSessionBinding>();
    public DbSet<ProjectConversationChatHistory> ProjectConversationChatHistories => Set<ProjectConversationChatHistory>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobLog> JobLogs => Set<JobLog>();

    public DbSet<PluginInstallation> PluginInstallations => Set<PluginInstallation>();
    public DbSet<PluginInstallationCredential> PluginInstallationCredentials => Set<PluginInstallationCredential>();
    public DbSet<Connection> Connections => Set<Connection>();
    public DbSet<ConnectionCredential> ConnectionCredentials => Set<ConnectionCredential>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.AddInterceptors(MaterializationInterceptor);
        optionsBuilder.ReplaceService<IMigrationsModelDiffer, NoForeignKeyModelDiffer>();
        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        ConfigureVersion7GuidKeys(modelBuilder);
        ConfigureProviderSpecificColumnTypes(modelBuilder);
        EncryptedEntityMetadata.Validate(modelBuilder);
        modelBuilder.ApplySoftDeleteQueryFilters();
    }

    internal void DecryptMaterializedEntity(object entity)
    {
        _encryptedPropertyProcessor.DecryptMaterializedEntity(this, entity);
    }

    private int SaveChangesWithEncryption(bool acceptAllChangesOnSuccess)
    {
        var autoDetectChangesEnabled = ChangeTracker.AutoDetectChangesEnabled;
        if (autoDetectChangesEnabled)
        {
            ChangeTracker.DetectChanges();
        }

        var restores = _encryptedPropertyProcessor.EncryptPendingChanges(ChangeTracker);
        var plaintextRestored = false;
        try
        {
            ChangeTracker.AutoDetectChangesEnabled = false;
            var result = base.SaveChanges(acceptAllChangesOnSuccess: false);
            _encryptedPropertyProcessor.RestorePlaintext(restores);
            plaintextRestored = true;

            if (acceptAllChangesOnSuccess)
            {
                ChangeTracker.AcceptAllChanges();
            }

            return result;
        }
        finally
        {
            if (!plaintextRestored)
            {
                _encryptedPropertyProcessor.RestorePlaintext(restores);
            }

            ChangeTracker.AutoDetectChangesEnabled = autoDetectChangesEnabled;
        }
    }

    private async Task<int> SaveChangesWithEncryptionAsync(
        bool acceptAllChangesOnSuccess,
        CancellationToken cancellationToken)
    {
        var autoDetectChangesEnabled = ChangeTracker.AutoDetectChangesEnabled;
        if (autoDetectChangesEnabled)
        {
            ChangeTracker.DetectChanges();
        }

        var restores = _encryptedPropertyProcessor.EncryptPendingChanges(ChangeTracker);
        var plaintextRestored = false;
        try
        {
            ChangeTracker.AutoDetectChangesEnabled = false;
            var result = await base.SaveChangesAsync(
                acceptAllChangesOnSuccess: false,
                cancellationToken);
            _encryptedPropertyProcessor.RestorePlaintext(restores);
            plaintextRestored = true;

            if (acceptAllChangesOnSuccess)
            {
                ChangeTracker.AcceptAllChanges();
            }

            return result;
        }
        finally
        {
            if (!plaintextRestored)
            {
                _encryptedPropertyProcessor.RestorePlaintext(restores);
            }

            ChangeTracker.AutoDetectChangesEnabled = autoDetectChangesEnabled;
        }
    }

    private void StampJobRowVersions()
    {
        foreach (var entry in ChangeTracker.Entries<Job>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.RowVersion = Guid.CreateVersion7().ToByteArray();
            }
        }
    }

    private static void ConfigureVersion7GuidKeys(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var primaryKey = entityType.FindPrimaryKey();
            if (primaryKey?.Properties.Count != 1 || primaryKey.Properties[0].ClrType != typeof(Guid))
            {
                continue;
            }

            modelBuilder.Entity(entityType.ClrType)
                .Property(primaryKey.Properties[0].Name)
                .HasValueGenerator<GuidVersion7ValueGenerator>();
        }
    }

    private void ConfigureProviderSpecificColumnTypes(ModelBuilder modelBuilder)
    {
        if (Database.IsNpgsql())
        {
            modelBuilder.Entity<ProjectConversationChatHistory>()
                .Property(history => history.Metadata)
                .HasColumnType("jsonb");
        }
    }

    private void PruneDeletedRelations()
    {
        var deletedAgentIds = ChangeTracker.Entries<Agent>()
            .Where(entry => entry.State == EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();
        var deletedProjectIds = ChangeTracker.Entries<Project>()
            .Where(entry => entry.State == EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();
        var deletedSkillIds = ChangeTracker.Entries<Skill>()
            .Where(entry => entry.State == EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();
        var deletedMcpToolServerIds = ChangeTracker.Entries<McpServer>()
            .Where(entry => entry.State == EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();
        var deletedPluginInstallationIds = ChangeTracker.Entries<PluginInstallation>()
            .Where(entry => entry.State == EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();
        var deletedConnectionIds = ChangeTracker.Entries<Connection>()
            .Where(entry => entry.State == EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();

        if (deletedProjectIds.Count > 0 || deletedSkillIds.Count > 0)
        {
            var projectSkillRelationsToRemove = ProjectSkillRelations
                .Where(relation =>
                    deletedProjectIds.Contains(relation.ProjectId)
                    || deletedSkillIds.Contains(relation.SkillId))
                .ToList();

            if (projectSkillRelationsToRemove.Count > 0)
            {
                ProjectSkillRelations.RemoveRange(projectSkillRelationsToRemove);
            }
        }

        if (deletedProjectIds.Count > 0 || deletedMcpToolServerIds.Count > 0)
        {
            var projectMcpRelationsToRemove = ProjectMcpToolServers
                .Where(relation =>
                    deletedProjectIds.Contains(relation.ProjectId)
                    || deletedMcpToolServerIds.Contains(relation.McpToolServerId))
                .ToList();

            if (projectMcpRelationsToRemove.Count > 0)
            {
                ProjectMcpToolServers.RemoveRange(projectMcpRelationsToRemove);
            }
        }

        if (deletedPluginInstallationIds.Count > 0)
        {
            var pluginInstallationCredentialsToRemove = PluginInstallationCredentials
                .Where(credential => deletedPluginInstallationIds.Contains(credential.PluginInstallationId))
                .ToList();

            if (pluginInstallationCredentialsToRemove.Count > 0)
            {
                PluginInstallationCredentials.RemoveRange(pluginInstallationCredentialsToRemove);
            }
        }

        if (deletedConnectionIds.Count > 0)
        {
            var connectionCredentialsToRemove = ConnectionCredentials
                .Where(credential => deletedConnectionIds.Contains(credential.ConnectionId))
                .ToList();

            if (connectionCredentialsToRemove.Count > 0)
            {
                ConnectionCredentials.RemoveRange(connectionCredentialsToRemove);
            }
        }

        if (deletedAgentIds.Count > 0 || deletedConnectionIds.Count > 0)
        {
            var agentConnectionRelationsToRemove = AgentConnectionRelations
                .Where(relation =>
                    deletedAgentIds.Contains(relation.AgentId)
                    || deletedConnectionIds.Contains(relation.ConnectionId))
                .ToList();

            if (agentConnectionRelationsToRemove.Count > 0)
            {
                AgentConnectionRelations.RemoveRange(agentConnectionRelationsToRemove);
            }
        }

        if (deletedProjectIds.Count > 0 || deletedConnectionIds.Count > 0)
        {
            var projectConnectionRelationsToRemove = ProjectConnectionRelations
                .Where(relation =>
                    deletedProjectIds.Contains(relation.ProjectId)
                    || deletedConnectionIds.Contains(relation.ConnectionId))
                .ToList();

            if (projectConnectionRelationsToRemove.Count > 0)
            {
                ProjectConnectionRelations.RemoveRange(projectConnectionRelationsToRemove);
            }
        }
    }
}
