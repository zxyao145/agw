using DSystem.Domain.Entities;
using Microsoft.EntityFrameworkCore;

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
    public DbSet<Workflow> Workflows => Set<Workflow>();
    public DbSet<WorkflowAgent> WorkflowAgents => Set<WorkflowAgent>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<ProjectTask> ProjectTasks => Set<ProjectTask>();
    public DbSet<ProjectLease> ProjectLeases => Set<ProjectLease>();

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
            entity.HasKey(e => new { e.ModelId, e.ProviderId });
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
                .HasForeignKey(e => new { e.ModelId, e.ProviderId })
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Agent>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Instructions).HasMaxLength(4000);
            entity.Property(e => e.SystemPrompt).HasMaxLength(4000);

            entity.HasOne(e => e.ModelProviderApiKey)
                .WithMany(k => k.Agents)
                .HasForeignKey(e => e.ModelProviderApiKeyId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Workflow>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000);
            entity.Property(e => e.ConfigurationJson).HasMaxLength(16000);
        });

        modelBuilder.Entity<WorkflowAgent>(entity =>
        {
            entity.HasKey(e => new { e.WorkflowId, e.AgentId });
            entity.Property(e => e.Role).HasMaxLength(200);

            entity.HasIndex(e => new { e.WorkflowId, e.Order }).IsUnique();

            entity.HasOne(e => e.Workflow)
                .WithMany(w => w.Agents)
                .HasForeignKey(e => e.WorkflowId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(e => e.Agent)
                .WithMany(a => a.Workflows)
                .HasForeignKey(e => e.AgentId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Project>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Description).HasMaxLength(1000);
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

            entity.HasOne(e => e.Workflow)
                .WithMany()
                .HasForeignKey(e => e.WorkflowId)
                .OnDelete(DeleteBehavior.Restrict);
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
    }
}
