using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Projects;

public sealed class ProjectMemoryEntryConfiguration : IEntityTypeConfiguration<ProjectMemoryEntry>
{
    public void Configure(EntityTypeBuilder<ProjectMemoryEntry> builder)
    {
        builder.HasKey(entry => entry.Id);
        builder.Property(entry => entry.Path).IsRequired().HasMaxLength(1024);
        builder.Property(entry => entry.Content).IsRequired();
        builder.HasIndex(entry => new { entry.ProjectId, entry.Path }).IsUnique();
        builder.HasIndex(entry => entry.UpdatedAt);
    }
}
