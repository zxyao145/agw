using Agw.Domain.Entities;
using Agw.Jobs.Enums;
using Agw.Shared.Tasks.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.Json;

namespace Agw.Infrastructure.Data;

public class LlmDbContext : DbContext
{
    public LlmDbContext(DbContextOptions<LlmDbContext> options) : base(options)
    {
    }

    public DbSet<LlmModel> Models => Set<LlmModel>();
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<ModelProvider> ModelProviders => Set<ModelProvider>();
    public DbSet<ProviderAuthConfig> ProviderAuthConfigs => Set<ProviderAuthConfig>();
    public DbSet<OAuthAuthorizationToken> OAuthAuthorizationTokens => Set<OAuthAuthorizationToken>();
    public DbSet<Agent> Agents => Set<Agent>();

    public DbSet<Agentflow> Agentflows => Set<Agentflow>();
    public DbSet<AgentflowNode> AgentflowNodes => Set<AgentflowNode>();
    public DbSet<AgentflowEdge> AgentflowEdges => Set<AgentflowEdge>();

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectTask> ProjectTasks => Set<ProjectTask>();
    public DbSet<TaskRecord> TaskRecords => Set<TaskRecord>();
    public DbSet<ProjectLease> ProjectLeases => Set<ProjectLease>();
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobLog> TaskExecutionLogs => Set<JobLog>();
    public DbSet<McpToolServer> McpToolServers => Set<McpToolServer>();
    public DbSet<AgentMcpToolServer> AgentMcpToolServers => Set<AgentMcpToolServer>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<AgentSkillRelation> AgentSkillRelations => Set<AgentSkillRelation>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.ReplaceService<IMigrationsModelDiffer, NoForeignKeyModelDiffer>();
        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LlmModel>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Type).HasConversion<int>();
            entity.Property(e => e.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<Provider>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Endpoint).IsRequired().HasMaxLength(500);
            entity.Property(e => e.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<ModelProvider>(entity =>
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

        modelBuilder.Entity<OAuthAuthorizationToken>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Provider);
            entity.HasIndex(e => new { e.Provider, e.Subject });
            entity.HasIndex(e => e.ExpiresAtUtc);
            entity.Property(e => e.Provider).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Subject).IsRequired().HasMaxLength(200);
            entity.Property(e => e.AccessToken).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.RefreshToken).HasMaxLength(4000);
            entity.Property(e => e.TokenType).IsRequired().HasMaxLength(50);
            entity.Property(e => e.Scope).HasMaxLength(2000);
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

        modelBuilder.Entity<Skill>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.Name).IsUnique();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(1024);
            entity.Property(e => e.ContentPath).IsRequired().HasMaxLength(500);
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
            entity.Property(e => e.AgentType).HasConversion<int>();
            entity.Property(e => e.Description).IsRequired().HasMaxLength(1024);
            entity.Property(e => e.SystemPrompt).HasMaxLength(4000);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200).HasDefaultValue("Untitled");
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);

            entity.HasIndex(e => e.ContextId).IsUnique();
            entity.HasIndex(e => e.AgentId).IsUnique(false);
            entity.HasIndex(e => e.ProjectId);
            entity.HasIndex(e => new { e.ProjectId, e.Status, e.UpdateTime });

            entity.HasOne(e => e.Project)
                .WithMany(project => project.Tasks)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.ConversationList)
                .WithOne()
                .HasForeignKey(e => e.ContextId)
                .HasPrincipalKey(e => e.ContextId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ContextId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.SessionId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.AgentName).HasMaxLength(200);
            entity.Property(e => e.ConversationPayload).HasColumnType("text");
            entity.Property(e => e.Error).HasColumnType("text");
            entity.HasIndex(e => new { e.ContextId, e.CreateTime });
            entity.HasIndex(e => new { e.ContextId, e.SessionId, e.CreateTime });
            entity.HasIndex(e => new { e.ContextId, e.ConversationSequence }).IsUnique(false);

            entity.Property(e => e.Metadata)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => v == null ? null : JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => string.IsNullOrWhiteSpace(v)
                        ? null
                        : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(v, (JsonSerializerOptions?)null));
        });

        modelBuilder.Entity<ProjectLease>(entity =>
        {
            entity.HasKey(e => e.ProjectId);
            entity.Property(e => e.LockedBy).IsRequired().HasMaxLength(200);
            entity.HasIndex(e => e.LockedUntilUtc);

            //entity.HasOne(e => e.Project)
            //    .WithOne()
            //    .HasForeignKey<ProjectLease>(e => e.ProjectId)
            //    .OnDelete(DeleteBehavior.Cascade);
        });


        modelBuilder.Entity<Job>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Prompt).HasMaxLength(4000);
            entity.Property(e => e.TriggerType).HasConversion<int>();
            entity.Property(e => e.TriggerValue).IsRequired().HasMaxLength(200);
            entity.Property(e => e.TimeZoneId).IsRequired().HasMaxLength(100);
            entity.Property(e => e.Status).HasConversion<int>();
            entity.Property(e => e.LastError).HasMaxLength(2000);
            entity.Property(e => e.RowVersion).IsRowVersion();

            entity.HasIndex(e => new { e.IsEnabled, e.Status, e.NextRunTime })
                .HasDatabaseName("ix_task_next_run_time");
            entity.HasIndex(e => e.ProjectId)
                .HasDatabaseName("ix_task_project");
        });

        modelBuilder.Entity<JobLog>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);
            entity.HasIndex(e => new { e.TaskId, e.StartTime });
        });

        modelBuilder.Entity<McpToolServer>(entity =>
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

        modelBuilder.Entity<AgentMcpToolServer>(entity =>
        {
            entity.ToTable("agent_mcp_tool_servers");
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

        modelBuilder.Entity<AgentSkillRelation>(entity =>
        {
            entity.ToTable("agent_skill_relations");
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
    }
}
