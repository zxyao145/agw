using DSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DSystem.Infrastructure.Data;

public class LlmDbContext : DbContext
{
    public LlmDbContext(DbContextOptions<LlmDbContext> options) : base(options)
    {
    }

    public DbSet<LlmModel> Models => Set<LlmModel>();
    public DbSet<Provider> Providers => Set<Provider>();
    public DbSet<ModelProvider> ModelProviders => Set<ModelProvider>();
    public DbSet<ModelProviderApiKey> ModelProviderApiKeys => Set<ModelProviderApiKey>();
    public DbSet<Agent> Agents => Set<Agent>();

    public DbSet<Agentflow> Agentflows => Set<Agentflow>();
    public DbSet<AgentflowNode> AgentflowNodes => Set<AgentflowNode>();
    public DbSet<AgentflowEdge> AgentflowEdges => Set<AgentflowEdge>();

    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectTask> ProjectTasks => Set<ProjectTask>();
    public DbSet<ProjectLease> ProjectLeases => Set<ProjectLease>();
    public DbSet<AgentSessionRecord> AgentSessionRecords => Set<AgentSessionRecord>();

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

        modelBuilder.Entity<ModelProviderApiKey>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ApiKey).IsRequired().HasMaxLength(2000);

            entity.HasOne(e => e.ModelProvider)
                .WithMany(mp => mp.ApiKeys)
                .HasForeignKey(e => e.ModelProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Agent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(200);
            entity.Property(e => e.SystemPrompt).HasMaxLength(4000);
            entity.Property(e => e.Tools).HasMaxLength(4000);  // JSON array of tool names

            // ModelProviderApiKeyId is optional - required for System agents, optional for External agents
            entity.HasOne(e => e.ModelProviderApiKey)
                .WithMany(k => k.Agents)
                .HasForeignKey(e => e.ModelProviderApiKeyId)
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
            entity.Property(e => e.ExtraSetting).HasMaxLength(16000);
        });

        modelBuilder.Entity<ProjectTask>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Description).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.Input).IsRequired().HasMaxLength(4000);
            entity.Property(e => e.OutputJson).HasMaxLength(16000);
            entity.Property(e => e.ErrorMessage).HasMaxLength(2000);

            entity.HasIndex(e => new { e.ProjectId, e.Status, e.UpdateTime });

            entity.HasOne(e => e.Project)
                .WithMany(p => p.Tasks)
                .HasForeignKey(e => e.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

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
            entity.HasIndex(e => new { e.ProjectId, e.SessionId }).IsUnique();
            entity.Property(e => e.SessionId).IsRequired().HasMaxLength(64);
            entity.Property(e => e.ProjectId).HasDefaultValue(Guid.Empty);
            entity.Property(e => e.Messages).HasColumnType("text");
        });
    }
}
