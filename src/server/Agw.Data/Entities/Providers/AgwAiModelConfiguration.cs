using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Agw.Shared.Data.Entities.Providers;

public class AgwAiModelConfiguration : IEntityTypeConfiguration<AgwAiModel>
{
    public void Configure(EntityTypeBuilder<AgwAiModel> builder)
    {
        builder.HasKey(e => e.Id);
        builder.HasIndex(e => e.Name).IsUnique();
        builder.Property(e => e.Name).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Description).HasMaxLength(1000);
    }
}
