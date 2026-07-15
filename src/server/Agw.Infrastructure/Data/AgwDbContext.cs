using Agw.Shared.Data.Entities.Agentflows;
using Agw.Shared.Data.Entities.Agents;
using Agw.Shared.Data.Entities.Integrations;
using Agw.Shared.Data.Entities.Jobs;
using Agw.Shared.Data.Entities.Projects;
using Agw.Shared.Data.Entities.Providers;
using Agw.Shared.Data.Entities.Skills;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Agw.Infrastructure.Data;

public class AgwDbContext : DbContext
{
    public AgwDbContext(DbContextOptions<AgwDbContext> options) : base(options)
    {
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        PruneDeletedRelations();
        StampJobRowVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PruneDeletedRelations();
        StampJobRowVersions();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<ProviderAuthConfig> ProviderAuthConfigs => Set<ProviderAuthConfig>();

    public DbSet<LlmModel> Models => Set<LlmModel>();
    public DbSet<ModelProviderRelation> ModelProviders => Set<ModelProviderRelation>();

    public DbSet<Agent> Agents => Set<Agent>();
    public DbSet<AgentConnectionRelation> AgentConnectionRelations => Set<AgentConnectionRelation>();

    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<AgentSkillRelation> AgentSkillRelations => Set<AgentSkillRelation>();

    public DbSet<McpServer> McpToolServers => Set<McpServer>();
    public DbSet<AgentMcpServerRelation> AgentMcpToolServers => Set<AgentMcpServerRelation>();

    public DbSet<Agentflow> Agentflows => Set<Agentflow>();
    public DbSet<AgentflowNode> AgentflowNodes => Set<AgentflowNode>();
    public DbSet<AgentflowEdge> AgentflowEdges => Set<AgentflowEdge>();
    public DbSet<AgentflowTrace> AgentflowNodeExecutionTraces => Set<AgentflowTrace>();

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectSkillRelation> ProjectSkillRelations => Set<ProjectSkillRelation>();
    public DbSet<ProjectMcpServerRelation> ProjectMcpToolServers => Set<ProjectMcpServerRelation>();
    public DbSet<ProjectConnectionRelation> ProjectConnectionRelations => Set<ProjectConnectionRelation>();
    public DbSet<ProjectContext> ProjectContexts => Set<ProjectContext>();
    public DbSet<AgentUsage> AgentUsages => Set<AgentUsage>();
    public DbSet<TaskSessionBinding> TaskSessionBindings => Set<TaskSessionBinding>();
    public DbSet<TaskRecord> TaskRecords => Set<TaskRecord>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobLog> JobLogs => Set<JobLog>();

    public DbSet<PluginInstallation> PluginInstallations => Set<PluginInstallation>();
    public DbSet<PluginInstallationCredential> PluginInstallationCredentials => Set<PluginInstallationCredential>();
    public DbSet<Connection> Connections => Set<Connection>();
    public DbSet<ConnectionCredential> ConnectionCredentials => Set<ConnectionCredential>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ReplaceService<IMigrationsModelDiffer, NoForeignKeyModelDiffer>();
        base.OnConfiguring(optionsBuilder);
    }

    private void StampJobRowVersions()
    {
        foreach (var entry in ChangeTracker.Entries<Job>())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                entry.Entity.RowVersion = Guid.NewGuid().ToByteArray();
            }
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
