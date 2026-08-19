using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Tools;

public sealed class UserMemoryConfiguration : IEntityTypeConfiguration<UserMemory>
{
    public void Configure(EntityTypeBuilder<UserMemory> builder)
    {
        builder.HasKey(memory => memory.Id);
        builder.Property(memory => memory.UserId).IsRequired().HasMaxLength(256);
        builder.Property(memory => memory.Name).IsRequired().HasMaxLength(64);
        builder.Property(memory => memory.NormalizedName).IsRequired().HasMaxLength(64);
        builder.Property(memory => memory.Description).HasMaxLength(300);
        builder.Property(memory => memory.Content).IsRequired();
        builder.HasIndex(memory => new { memory.UserId, memory.NormalizedName }).IsUnique();
    }
}
