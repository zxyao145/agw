using System.Text.Json;

using Agw.Agents.Domain.Entities;
using Agw.Integrations.Domain.Entities;
using Agw.Jobs.Domain.Entities;
using Agw.Providers.Domain.Entities;
using Agw.Shared.Data.Entities.Skills;
using Agw.Shared.Data.Entities.Tasks;

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
        PruneDeletedAgentAppRelations();
        StampJobRowVersions();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        PruneDeletedAgentAppRelations();
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

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectTask> ProjectTasks => Set<ProjectTask>();
    public DbSet<ProjectTaskSessionBinding> ProjectTaskSessionBindings => Set<ProjectTaskSessionBinding>();
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
            entity.Property(e => e.Type).HasConversion<int>();
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
            entity.Property(e => e.ConfigurationJson).HasMaxLength(16000);
        });

        modelBuilder.Entity<AgentflowNode>(entity =>
        {
            entity.HasKey(e => new { e.AgentflowId, e.NodeId });
            entity.HasIndex(e => new { e.AgentflowId, e.Type, e.RelateId })
                .IsUnique(true);
        });

        modelBuilder.Entity<AgentflowEdge>(entity =>
        {
            entity.HasKey(e => new { e.AgentflowId, e.EdgeId });

            entity.HasOne(e => e.SourceNode)
                .WithMany(n => n.SourceEdges)
                .HasForeignKey(e => new { e.AgentflowId, e.SourceNodeId })
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.TargetNode)
                .WithMany(n => n.TargetEdges)
                .HasForeignKey(e => new { e.AgentflowId, e.TargetNodeId })
                .OnDelete(DeleteBehavior.Cascade);
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
        });

        modelBuilder.Entity<ProjectTask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ContextId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.JobId);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200).HasDefaultValue("Untitled");
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);

            entity.HasIndex(e => e.ContextId).IsUnique();
            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.UpdateTime });

            entity.HasOne(e => e.Project)
                .WithMany(project => project.Tasks)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProjectTaskSessionBinding>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ExternalAgentName).IsRequired().HasMaxLength(200);
            entity.Property(e => e.ProviderSessionId).IsRequired().HasMaxLength(200);

            entity.HasIndex(e => new { e.TaskId, e.AgentId, e.ExternalAgentName }).IsUnique();
            entity.HasIndex(e => new { e.ExternalAgentName, e.ProviderSessionId });

            entity.HasOne(e => e.Task)
                .WithMany()
                .HasForeignKey(e => e.TaskId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.TaskId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.AgentName).HasMaxLength(200);
            entity.Property(e => e.ConversationPayload).HasColumnType("text");
            entity.Property(e => e.Error).HasColumnType("text");
            entity.HasIndex(e => new { e.TaskId, e.CreateTime });
            entity.HasIndex(e => new { e.TaskId, e.ConversationSequence }).IsUnique(false);

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

    private void PruneDeletedAgentAppRelations()
    {
        var deletedAgentIds = ChangeTracker.Entries<Agent>()
            .Where(entry => entry.State == EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();
        var deletedAppInstanceIds = ChangeTracker.Entries<AppInstance>()
            .Where(entry => entry.State == EntityState.Deleted)
            .Select(entry => entry.Entity.Id)
            .ToHashSet();

        if (deletedAgentIds.Count == 0 && deletedAppInstanceIds.Count == 0)
        {
            return;
        }

        var relationsToRemove = AgentAppRelations
            .Where(relation =>
                deletedAgentIds.Contains(relation.AgentId)
                || deletedAppInstanceIds.Contains(relation.AppInstanceId))
            .ToList();

        if (relationsToRemove.Count > 0)
        {
            AgentAppRelations.RemoveRange(relationsToRemove);
        }
    }
}
