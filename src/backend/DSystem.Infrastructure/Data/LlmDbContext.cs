using DSystem.Domain.Entities;
using DSystem.SessionRecords.Entities;
using DSystem.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Text.Json;

namespace DSystem.Infrastructure.Data;

public class LlmDbContext : DbContext
{
    public LlmDbContext(DbContextOptions<LlmDbContext> options) : base(options)
    {
    }

    public DbSet<LlmModel> Models => Set<LlmModel>();
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<ModelProvider> ModelProviders => Set<ModelProvider>();
    public DbSet<ProviderAuthConfig> ProviderAuthConfigs => Set<ProviderAuthConfig>();
    public DbSet<Agent> Agents => Set<Agent>();

    public DbSet<Agentflow> Agentflows => Set<Agentflow>();
    public DbSet<AgentflowNode> AgentflowNodes => Set<AgentflowNode>();
    public DbSet<AgentflowEdge> AgentflowEdges => Set<AgentflowEdge>();

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectTask> ProjectTasks => Set<ProjectTask>();
    public DbSet<ProjectLease> ProjectLeases => Set<ProjectLease>();
    public DbSet<AgentSessionRecord> AgentSessionRecords => Set<AgentSessionRecord>();
    public DbSet<McpToolServer> McpToolServers => Set<McpToolServer>();
    public DbSet<AgentMcpToolServer> AgentMcpToolServers => Set<AgentMcpToolServer>();

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
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000);
        });

        modelBuilder.Entity<Provider>(entity =>
        {
            entity.HasKey(e => e.Id);
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
            entity.HasOne(e => e.ModelProvider)
                .WithMany(p => p.Agents)
                .HasForeignKey(e => e.ModelProviderId)
                .OnDelete(DeleteBehavior.Cascade)
                .IsRequired(false);
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
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.Workspace).HasMaxLength(1000);
            entity.Property(e => e.ExtraSetting).HasMaxLength(16000);
        });

        modelBuilder.Entity<ProjectTask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ProjectId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.AgentType).HasConversion<int>();
            entity.Property(e => e.Description).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.SessionId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200).HasDefaultValue(string.Empty);
            entity.Property(e => e.Input).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);

            entity.HasIndex(e => new { e.ProjectId, e.Status, e.UpdateTime });
            entity.HasIndex(e => new { e.ProjectId, e.SessionId }).IsUnique();

        });

        modelBuilder.Entity<ProjectLease>(entity =>
        {
            entity.HasKey(e => e.ProjectId);
            entity.Property(e => e.LockedBy).IsRequired().HasMaxLength(200);
            entity.HasIndex(e => e.LockedUntilUtc);

            entity.HasOne(e => e.Project)
                .WithOne()
                .HasForeignKey<ProjectLease>(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentSessionRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.ProjectId, e.SessionId, e.MessageId }).IsUnique();
            entity.Property(e => e.ProjectId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.SessionId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.MessageId).IsRequired().HasMaxLength(128);
            entity.Property(e => e.Author).HasMaxLength(200);
            entity.Property(e => e.Role).IsRequired().HasMaxLength(64);
            entity.Property(e => e.Error).HasColumnType("text");

            entity.Property(e => e.Metadata)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => string.IsNullOrWhiteSpace(v)
                        ? null
                        : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(v, (JsonSerializerOptions?)null));

            entity.Property(e => e.Contents)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                    v => string.IsNullOrWhiteSpace(v)
                        ? new List<AiMessageContent>()
                        : JsonSerializer.Deserialize<List<DSystem.Shared.Models.AiMessageContent>>(v, (JsonSerializerOptions?)null) ?? new List<AiMessageContent>());

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
    }
}
