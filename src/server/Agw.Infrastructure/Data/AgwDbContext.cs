using System.Text.Json;

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
    public DbSet<AgentAppRelation> AgentAppRelations => Set<AgentAppRelation>();

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
    public DbSet<ProjectAppRelation> ProjectAppRelations => Set<ProjectAppRelation>();
    public DbSet<ProjectContext> ProjectContexts => Set<ProjectContext>();
    public DbSet<AgentUsage> AgentUsages => Set<AgentUsage>();
    public DbSet<TaskSessionBinding> TaskSessionBindings => Set<TaskSessionBinding>();
    public DbSet<TaskRecord> TaskRecords => Set<TaskRecord>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobLog> JobLogs => Set<JobLog>();

    public DbSet<AppInstance> AppInstances => Set<AppInstance>();

    public DbSet<OAuthAuthorizationToken> OAuthAuthorizationTokens => Set<OAuthAuthorizationToken>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ReplaceService<IMigrationsModelDiffer, NoForeignKeyModelDiffer>();
        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Provider>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.Name, e.ProviderType }).IsUnique();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Endpoint).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<ProviderAuthConfig>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.AuthType).HasConversion<int>();
            entity.Property(e => e.ApiKey).HasMaxLength(2000);
            entity.Property(e => e.EnvName).HasMaxLength(200);

            entity.HasOne(e => e.Provider)
                .WithMany(p => p.AuthConfigs)
                .HasForeignKey(e => e.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<LlmModel>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<ModelProviderRelation>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.InputPrice).HasColumnType("decimal(18,4)");
            entity.Property(e => e.OutputPrice).HasColumnType("decimal(18,4)");
            entity.Property(e => e.CacheRead).HasColumnType("decimal(18,4)");
            entity.Property(e => e.CacheWrite).HasColumnType("decimal(18,4)");

            entity.HasOne(e => e.Model)
                .WithMany(m => m.Providers)
                .HasForeignKey(e => e.ModelId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Provider)
                .WithMany(p => p.Models)
                .HasForeignKey(e => e.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Agent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.DisplayName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(200);
            entity.Property(e => e.SystemPrompt).HasMaxLength(4000);
            entity.Property(e => e.Tools).HasMaxLength(4000);  // JSON array of tool names
            entity.Property(e => e.EnvironmentVariables).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v)
                    ? new Dictionary<string, string>()
                    : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null)
                    ?? new Dictionary<string, string>());

            // ModelProviderId is optional - required for System agents, optional for External agents
            //entity.HasOne(e => e.ModelProvider)
            //    .WithMany(p => p.Agents)
            //    .HasForeignKey(e => e.ModelProviderId)
            //    .OnDelete(DeleteBehavior.Cascade)
            //    .IsRequired(false);
        });

        modelBuilder.Entity<AgentAppRelation>(entity =>
        {
            entity.HasKey(e => new { e.AgentId, e.AppInstanceId });

            entity.HasOne(e => e.Agent)
                .WithMany(agent => agent.AgentAppRelations)
                .HasForeignKey(e => e.AgentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.AppInstance)
                .WithMany()
                .HasForeignKey(e => e.AppInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.AppInstanceId);
        });

        modelBuilder.Entity<Skill>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(1024);
            entity.Property(e => e.ContentPath).IsRequired().HasMaxLength(500);
        });

        modelBuilder.Entity<AgentSkillRelation>(entity =>
        {
            entity.HasKey(e => new { e.AgentId, e.SkillId });

            entity.HasOne(e => e.Agent)
                .WithMany(a => a.AgentSkillRelations)
                .HasForeignKey(e => e.AgentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<Skill>()
                .WithMany()
                .HasForeignKey(e => e.SkillId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.SkillId);
        });

        modelBuilder.Entity<McpServer>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.TransportType).IsRequired().HasMaxLength(20);
            entity.Property(e => e.Command).HasMaxLength(200);
            entity.Property(e => e.WorkingDirectory).HasMaxLength(500);
            entity.Property(e => e.Url).HasMaxLength(1000);
            entity.Property(e => e.Arguments)
            .HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v)
                    ? new List<string>()
                    : System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null)
                    ?? new List<string>()
                    );
            entity.Property(e => e.EnvironmentVariables).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v)
                    ? new Dictionary<string, string>()
                    : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new Dictionary<string, string>());
            entity.Property(e => e.Headers).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v)
                    ? new Dictionary<string, string>()
                    : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null) ?? new Dictionary<string, string>());
        });

        modelBuilder.Entity<AgentMcpServerRelation>(entity =>
        {
            entity.HasKey(e => new { e.AgentId, e.McpToolServerId });

            entity.HasOne(e => e.Agent)
                .WithMany(a => a.AgentMcpToolServers)
                .HasForeignKey(e => e.AgentId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.McpToolServer)
                .WithMany(s => s.AgentMcpToolServers)
                .HasForeignKey(e => e.McpToolServerId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.McpToolServerId);
        });

        modelBuilder.Entity<Agentflow>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.SystemPrompt).HasMaxLength(4000);
        });

        modelBuilder.Entity<AgentflowNode>(entity =>
        {
            entity.HasKey(e => new { e.AgentflowId, e.NodeId });
            entity.Property(e => e.Kind).HasConversion<int>();
            entity.Property(e => e.Name).HasMaxLength(200);
            entity.Property(e => e.PositionJson).HasMaxLength(1000);
            entity.Property(e => e.Instructions).HasMaxLength(8000);
            entity.Property(e => e.ConfigJson).HasMaxLength(16000);
            entity.HasIndex(e => new { e.AgentflowId, e.Kind, e.RelateId });
        });

        modelBuilder.Entity<AgentflowEdge>(entity =>
        {
            entity.HasKey(e => new { e.AgentflowId, e.EdgeId });
            entity.Property(e => e.Kind).HasConversion<int>();
            entity.Property(e => e.Label).HasMaxLength(200);
            entity.Property(e => e.ConditionJson).HasMaxLength(8000);
            entity.Property(e => e.ConfigJson).HasMaxLength(16000);

            entity.HasOne(e => e.SourceNode)
                .WithMany(n => n.SourceEdges)
                .HasForeignKey(e => new { e.AgentflowId, e.SourceNodeId })
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.TargetNode)
                .WithMany(n => n.TargetEdges)
                .HasForeignKey(e => new { e.AgentflowId, e.TargetNodeId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentflowTrace>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ContextId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.NodeId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.NodeName).HasMaxLength(200);
            entity.Property(e => e.NodeKind).HasConversion<string>().HasMaxLength(32);
            entity.Property(e => e.AgentName).HasMaxLength(200);
            entity.Property(e => e.Input).IsRequired().HasColumnType("text");
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.Error).HasColumnType("text");
            entity.HasIndex(e => new { e.ProjectId, e.ContextId, e.TaskId, e.StartTimeUtc });
            entity.HasIndex(e => new { e.AgentflowId, e.NodeId, e.StartTimeUtc });
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Type).HasConversion<int>();
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.Workspace).HasMaxLength(1000);
            entity.Property(e => e.ExtraSetting).HasMaxLength(16000);
            entity.Property(e => e.Tools).HasMaxLength(4000);
            entity.Property(e => e.EnvironmentVariables).HasConversion(
                v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                v => string.IsNullOrWhiteSpace(v)
                    ? new Dictionary<string, string>()
                    : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null)
                    ?? new Dictionary<string, string>());
        });

        modelBuilder.Entity<ProjectSkillRelation>(entity =>
        {
            entity.HasKey(e => new { e.ProjectId, e.SkillId });

            entity.HasOne(e => e.Project)
                .WithMany(project => project.ProjectSkillRelations)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Skill)
                .WithMany()
                .HasForeignKey(e => e.SkillId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.SkillId);
        });

        modelBuilder.Entity<ProjectMcpServerRelation>(entity =>
        {
            entity.HasKey(e => new { e.ProjectId, e.McpToolServerId });

            entity.HasOne(e => e.Project)
                .WithMany(project => project.ProjectMcpToolServers)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.McpToolServer)
                .WithMany()
                .HasForeignKey(e => e.McpToolServerId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.McpToolServerId);
        });

        modelBuilder.Entity<ProjectAppRelation>(entity =>
        {
            entity.HasKey(e => new { e.ProjectId, e.AppInstanceId });

            entity.HasOne(e => e.Project)
                .WithMany(project => project.ProjectAppRelations)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.AppInstance)
                .WithMany()
                .HasForeignKey(e => e.AppInstanceId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(e => e.AppInstanceId);
        });

        modelBuilder.Entity<ProjectContext>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.JobId);
            entity.Property(e => e.ContextId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200).HasDefaultValue("Untitled");

            entity.HasIndex(e => new { e.ProjectId, e.ContextId }).IsUnique();
            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => e.JobId);
            entity.HasIndex(e => e.UpdateTime);

            entity.HasOne(e => e.Project)
                .WithMany(project => project.Contexts)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentUsage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ContextId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.AgentName).IsRequired().HasMaxLength(200);

            entity.HasIndex(e => new { e.ProjectId, e.ContextId });
            entity.HasIndex(e => e.AgentName);
            entity.HasIndex(e => e.RecordedAt);
        });

        modelBuilder.Entity<TaskSessionBinding>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ExternalAgentName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ProviderSessionId).IsRequired().HasMaxLength(200);

            entity.HasIndex(e => new { e.ProjectContextId, e.AgentId, e.ExternalAgentName }).IsUnique();
            entity.HasIndex(e => new { e.ExternalAgentName, e.ProviderSessionId });

            entity.HasOne(e => e.ProjectContext)
                .WithMany()
                .HasForeignKey(e => e.ProjectContextId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TaskId).IsRequired();
            entity.Property(e => e.JobId);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.TaskErrorMessage).HasMaxLength(2000);
            entity.Property(e => e.AgentName).HasMaxLength(200);
            entity.Property(e => e.ConversationPayload).HasColumnType("text");
            entity.Property(e => e.Error).HasColumnType("text");
            entity.HasIndex(e => e.ProjectContextId);
            entity.HasIndex(e => new { e.ProjectContextId, e.ConversationSequence });
            entity.HasIndex(e => new { e.TaskId, e.CreateTime });
            entity.HasIndex(e => new { e.TaskId, e.ConversationSequence }).IsUnique(false);

            entity.HasOne(e => e.ProjectContext)
                .WithMany(context => context.Records)
                .HasForeignKey(e => e.ProjectContextId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.Property(e => e.Metadata)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => string.IsNullOrWhiteSpace(v)
                        ? null
                        : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(v, (JsonSerializerOptions?)null));
        });

        modelBuilder.Entity<Job>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Prompt).HasMaxLength(4000);
            entity.Property(e => e.TriggerType).HasConversion<int>();
            entity.Property(e => e.TriggerValue).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.RowVersion)
                .IsRequired()
                .IsConcurrencyToken()
                .ValueGeneratedNever();

            entity.HasIndex(e => new { e.IsEnabled, e.Status, e.NextRunTime })
                .HasDatabaseName("ix_task_next_run_time");
            entity.HasIndex(e => e.ProjectId)
                .HasDatabaseName("ix_task_project");
        });

        modelBuilder.Entity<JobLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.JobId);
            entity.Property(e => e.TaskId);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            entity.HasIndex(e => new { e.JobId, e.StartTime });
        });

        modelBuilder.Entity<AppInstance>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AppName).IsUnique(false);
            entity.HasIndex(e => e.ClientId).IsUnique();
            entity.Property(e => e.AppName).IsRequired().HasMaxLength(128);
            entity.Property(e => e.ClientId).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ClientSecret).IsRequired().HasMaxLength(2000);

            entity.HasOne(e => e.AuthorizationToken)
                .WithOne()
                .HasForeignKey<OAuthAuthorizationToken>(e => e.AppInstanceId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OAuthAuthorizationToken>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.AppInstanceId).IsUnique();
            entity.HasIndex(e => e.ExpiresAtUtc);
            entity.Property(e => e.AppInstanceId).IsRequired();
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AccessToken).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.RefreshToken).HasMaxLength(4000);
            entity.Property(e => e.TokenType).IsRequired().HasMaxLength(50);
        });
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
        var deletedAppInstanceIds = ChangeTracker.Entries<AppInstance>()
            .Where(entry => entry.State == EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();

        if (deletedAgentIds.Count > 0 || deletedAppInstanceIds.Count > 0)
        {
            var agentAppRelationsToRemove = AgentAppRelations
                .Where(relation =>
                    deletedAgentIds.Contains(relation.AgentId)
                    || deletedAppInstanceIds.Contains(relation.AppInstanceId))
                .ToList();

            if (agentAppRelationsToRemove.Count > 0)
            {
                AgentAppRelations.RemoveRange(agentAppRelationsToRemove);
            }
        }

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

        if (deletedProjectIds.Count > 0 || deletedAppInstanceIds.Count > 0)
        {
            var projectAppRelationsToRemove = ProjectAppRelations
                .Where(relation =>
                    deletedProjectIds.Contains(relation.ProjectId)
                    || deletedAppInstanceIds.Contains(relation.AppInstanceId))
                .ToList();

            if (projectAppRelationsToRemove.Count > 0)
            {
                ProjectAppRelations.RemoveRange(projectAppRelationsToRemove);
            }
        }
    }
}
